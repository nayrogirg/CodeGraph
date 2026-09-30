using System.Text;
using CodeGraph.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace CodeGraph.Core.Messaging;

/// <summary>
/// Detects RabbitMQ interactions (raw RabbitMQ.Client and MassTransit) and adds exchanges, queues and
/// publish/bind/consume edges to the graph.
/// </summary>
/// <remarks>
/// Facts are collected while the syntax trees are walked, and resolved in <see cref="Complete"/>, because
/// MassTransit endpoint configuration usually lives in a different project than the consumers and publishers.
/// </remarks>
internal sealed class MessagingCollector
{
    public const string BrokerId = "mq:rabbitmq";

    private static readonly HashSet<string> RabbitClientNames = new(StringComparer.Ordinal)
    {
        "ExchangeDeclare", "QueueDeclare", "QueueBind", "BasicPublish", "BasicConsume",
    };

    private static readonly HashSet<string> MassTransitNames = new(StringComparer.Ordinal)
    {
        "Publish", "Send", "ReceiveEndpoint", "ConfigureConsumer", "Consumer", "Map",
        "SetKebabCaseEndpointNameFormatter", "SetSnakeCaseEndpointNameFormatter",
    };

    /// <summary>The name roles each RabbitMQ.Client operation takes, by parameter name.</summary>
    private static readonly Dictionary<string, string[]> OperationRoles = new(StringComparer.Ordinal)
    {
        ["ExchangeDeclare"] = ["exchange", "type"],
        ["QueueDeclare"] = ["queue"],
        ["QueueBind"] = ["queue", "exchange", "routingKey"],
        ["BasicPublish"] = ["exchange", "routingKey"],
        ["BasicConsume"] = ["queue"],
    };

    private readonly GraphBuilder _graph;
    private readonly Func<ISymbol?, string?> _ensureNode;

    private readonly List<ConsumerFact> _consumers = new();
    private readonly Dictionary<string, Name> _endpointByConsumer = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Name> _conventionByMessage = new(StringComparer.Ordinal);
    private readonly List<PendingSend> _pendingSends = new();
    private string _endpointFormatter = "default";

    /// <summary>
    /// Methods that perform a RabbitMQ operation with names taken from their own parameters (e.g. an in-house
    /// IQueueClient.PublishMessage(message, exchange, routingKey)), keyed by documentation id — including the
    /// interface/base members they implement, so calls through an interface match too.
    /// </summary>
    private readonly Dictionary<string, List<WrapperSpec>> _wrappers = new(StringComparer.Ordinal);
    /// <summary>Calls to source methods with string/enum arguments; the ones that turn out to target wrappers become edges.</summary>
    private readonly List<CallFact> _calls = new();

    // Diagnostics, reported as analysis warnings so users can see why something is (not) linked.
    private readonly SortedSet<string> _wrapperDescriptions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _wrapperCallCounts = new(StringComparer.Ordinal);
    private readonly List<string> _unboundCalls = new();
    private int _directRabbitCalls, _massTransitCalls;

    /// <summary>Human-readable notes about what messaging detection found; added to the analysis warnings.</summary>
    public List<string> Diagnostics { get; } = new();

    public MessagingCollector(GraphBuilder graph, Func<ISymbol?, string?> ensureNode)
    {
        _graph = graph;
        _ensureNode = ensureNode;
    }

    // ---------------------------------------------------------------- collection

    public void OnInvocation(InvocationExpressionSyntax invocation, SemanticModel model, Func<string?> owner, CancellationToken ct)
    {
        var name = InvokedName(invocation);
        if (name is null) return;
        if (name.EndsWith("Async", StringComparison.Ordinal)) name = name[..^5];
        // Binding is cached by the semantic model (the name was already bound while walking), so this is cheap.
        if (model.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol target)
        {
            if (RabbitClientNames.Contains(name) && _unboundCalls.Count < 10)
            {
                var where = At(invocation.GetLocation());
                _unboundCalls.Add(where is null ? name : $"{name} at {Path.GetFileName(where.File)}:{where.Line}");
            }
            return;
        }
        target = target.ReducedFrom ?? target;

        var ns = target.ContainingType?.ContainingNamespace?.ToDisplayString() ?? "";
        if (ns.StartsWith("RabbitMQ.Client", StringComparison.Ordinal))
        {
            if (RabbitClientNames.Contains(name) && model.GetOperation(invocation, ct) is IInvocationOperation op)
            {
                _directRabbitCalls++;
                OnRabbitClient(name, op, invocation, owner);
            }
        }
        else if (ns.StartsWith("MassTransit", StringComparison.Ordinal))
        {
            if (MassTransitNames.Contains(name) && model.GetOperation(invocation, ct) is IInvocationOperation op)
            {
                _massTransitCalls++;
                OnMassTransit(name, op, invocation, model, owner, ct);
            }
        }
        else if (target.Parameters.Any(p => IsNameType(p.Type)) &&
                 (target.Locations.Any(l => l.IsInSource) || LooksLikeMessagingApi(target)) &&
                 model.GetOperation(invocation, ct) is IInvocationOperation call)
        {
            OnPossibleWrapperCall(call, invocation, model, owner, ct);
        }
    }

    public void OnType(INamedTypeSymbol type, string typeId)
    {
        if (type.TypeKind != TypeKind.Class || type.IsAbstract) return;
        foreach (var iface in type.AllInterfaces)
        {
            if (iface.OriginalDefinition.ToDisplayString() != "MassTransit.IConsumer<TMessage>") continue;
            if (iface.TypeArguments[0] is not INamedTypeSymbol message) continue;
            var consume = iface.GetMembers("Consume").FirstOrDefault() is { } member ? type.FindImplementationForInterfaceMember(member) : null;
            _consumers.Add(new ConsumerFact(typeId, type.Name, _ensureNode(consume) ?? typeId,
                _ensureNode(message), message.Name, MassTransitExchangeName(message), At(type.Locations.FirstOrDefault())));
        }
    }

    private void OnRabbitClient(string operation, IInvocationOperation op, SyntaxNode syntax, Func<string?> owner)
    {
        var names = OperationRoles[operation].ToDictionary(role => role, role => ResolveName(Arg(op, role)));
        var handler = operation == "BasicConsume" ? ConsumerHandler(Arg(op, "consumer")) : null;

        // Names that come from the enclosing method's parameters are only known at that method's call sites.
        if (names.Values.FirstOrDefault(n => n.ParamOwner is not null)?.ParamOwner is { } wrapper)
        {
            RegisterWrapper(wrapper, new WrapperSpec(operation, names, handler));
            return;
        }
        Emit(operation, names, owner(), handler, null, null, At(syntax.GetLocation()), via: null);
    }

    /// <summary>Adds the nodes/edges for one RabbitMQ operation whose names are resolved (or known to be unresolvable).</summary>
    private void Emit(string operation, IReadOnlyDictionary<string, Name> names, string? from, string? handler,
                      string? messageName, string? messageTypeId, Where? at, string? via)
    {
        var name = (string role) => names.GetValueOrDefault(role) ?? Name.Unresolved(role);
        switch (operation)
        {
            case "ExchangeDeclare":
            {
                var exchange = EnsureExchange(name("exchange"), "declared", at);
                if (names.GetValueOrDefault("type") is { Resolved: true } type && exchange is not null)
                    exchange.Meta["exchangeType"] = type.Value;
                break;
            }
            case "QueueDeclare":
                EnsureQueue(name("queue"), "declared", at);
                break;
            case "QueueBind":
            {
                var queue = EnsureQueue(name("queue"), "declared", at);
                var exchange = EnsureExchange(name("exchange"), "declared", null);
                var edge = _graph.AddEdge(exchange?.Id, queue?.Id, EdgeKind.Binds);
                edge?.AddMeta("routingKeys", names.GetValueOrDefault("routingKey")?.Display);
                edge?.AddMeta("via", via);
                break;
            }
            case "BasicPublish":
            {
                var exchangeName = name("exchange");
                var routingKey = names.GetValueOrDefault("routingKey");
                // The default exchange ("") routes straight to the queue named by the routing key.
                var target = exchangeName is { Resolved: true, Value: "" } && routingKey is not null
                    ? EnsureQueue(routingKey, "inferred", null)
                    : EnsureExchange(exchangeName, "inferred", null);
                var edge = _graph.AddEdge(from, target?.Id, EdgeKind.Publishes);
                edge?.AddMeta("routingKeys", routingKey?.Display);
                edge?.AddMeta("mode", "publish");
                edge?.AddMeta("messageTypes", messageName);
                edge?.AddMeta("via", via);
                _graph.AddEdge(target?.Id, messageTypeId, EdgeKind.Carries);
                break;
            }
            case "BasicConsume":
            {
                var queue = EnsureQueue(name("queue"), "inferred", null);
                var edge = _graph.AddEdge(queue?.Id, handler ?? from, EdgeKind.Consumes);
                edge?.AddMeta("messageTypes", messageName);
                edge?.AddMeta("via", via);
                _graph.AddEdge(queue?.Id, messageTypeId, EdgeKind.Carries);
                break;
            }
        }
    }

    // ---------------------------------------------------------------- wrappers

    private void RegisterWrapper(ParamOwner method, WrapperSpec spec)
    {
        var parameters = spec.Names.Where(n => n.Value.Param is not null).Select(n => $"{n.Key}←{n.Value.Value}");
        _wrapperDescriptions.Add($"{method.DisplayName} wraps {spec.Operation} ({string.Join(", ", parameters)})");
        foreach (var key in method.Keys)
        {
            if (!_wrappers.TryGetValue(key, out var specs)) _wrappers[key] = specs = new();
            specs.Add(spec); // a wrapper can do several things, e.g. QueueDeclare then QueueBind
        }
    }

    /// <summary>A call that may target a wrapper: resolved string arguments by parameter ordinal, plus call-site context.</summary>
    private void OnPossibleWrapperCall(IInvocationOperation call, SyntaxNode syntax, SemanticModel model, Func<string?> owner, CancellationToken ct)
    {
        var target = call.TargetMethod.ReducedFrom ?? call.TargetMethod;
        var args = new Dictionary<int, Name>();
        IOperation? delegateArg = null;
        foreach (var argument in call.Arguments)
        {
            if (argument.Parameter is not { } parameter) continue;
            if (IsNameType(parameter.Type)) args[parameter.Ordinal] = ResolveName(argument.Value);
            else if (parameter.Type.TypeKind == TypeKind.Delegate) delegateArg ??= argument.Value;
        }
        var message = CallMessageType(call);
        var fact = new CallFact(
            SymbolKey(target), target.Parameters.Select(p => p.Name).ToArray(), args,
            owner(), HandlerFrom(delegateArg),
            message?.Name, _ensureNode(message), At(syntax.GetLocation()),
            $"{target.ContainingType?.Name}.{target.Name}", LooksLikeMessagingApi(target) ? target.Name : null);

        // Compiled (NuGet) wrappers have no source to learn from; fall back to their parameter names.
        if (!target.Locations.Any(l => l.IsInSource)) EmitByConvention(fact);
        else _calls.Add(fact);
    }

    /// <summary>
    /// Resolves wrapper calls to edges. Runs to a fixed point: a call that passes its own parameters to a wrapper
    /// makes the calling method a wrapper as well (wrappers of wrappers).
    /// </summary>
    private void ResolveWrapperCalls()
    {
        var pending = new List<CallFact>(_calls);
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var call in pending.ToList())
            {
                if (!_wrappers.TryGetValue(call.TargetKey, out var specs)) continue;
                pending.Remove(call);
                foreach (var spec in specs.ToList())
                {
                    var names = spec.Names.ToDictionary(kv => kv.Key, kv => kv.Value.Param is int ordinal
                        ? kv.Value.Bind(call.Args.GetValueOrDefault(ordinal) ?? Name.Unresolved(call.ParameterNames.ElementAtOrDefault(ordinal) ?? kv.Value.Value))
                        : kv.Value);
                    _wrapperCallCounts[call.Via] = _wrapperCallCounts.GetValueOrDefault(call.Via) + 1;
                    if (names.Values.FirstOrDefault(n => n.ParamOwner is not null)?.ParamOwner is { } caller)
                    {
                        RegisterWrapper(caller, spec with { Names = names });
                        changed = true;
                    }
                    else
                    {
                        Emit(spec.Operation, names, call.From, call.Handler ?? spec.Handler, call.MessageName, call.MessageTypeId, call.At, call.Via);
                    }
                }
            }
        }
        // Source methods that look like a messaging API but whose body we could not follow: only trust explicit
        // exchange/queue parameter names here, since the method is ours and simply didn't reach RabbitMQ.Client.
        foreach (var call in pending)
            if (call.ParameterNames.Any(n => n.Contains("exchange", StringComparison.OrdinalIgnoreCase) || n.Contains("queue", StringComparison.OrdinalIgnoreCase)))
                EmitByConvention(call);
    }

    /// <summary>Publish*/Send* and Subscribe*/Consume* methods of queue/bus/message types with exchange/queue-like string parameters.</summary>
    private void EmitByConvention(CallFact call)
    {
        if (call.ConventionName is not { } methodName) return;
        int? Find(Func<string, bool> match) =>
            call.ParameterNames.Select((n, i) => (n, i)).Where(p => call.Args.ContainsKey(p.i) && match(p.n.ToLowerInvariant())).Select(p => (int?)p.i).FirstOrDefault();
        var stringParams = call.Args.Keys.Order().ToList();
        if (stringParams.Count == 0) return;

        if (IsPublishName(methodName))
        {
            var exchange = Find(n => n.Contains("exchange") || n.Contains("topic")) ?? stringParams[0];
            var routing = Find(n => n.Contains("rout") || n.Contains("key")) ?? stringParams.FirstOrDefault(i => i != exchange, -1);
            var names = new Dictionary<string, Name> { ["exchange"] = call.Args[exchange] };
            if (routing >= 0) names["routingKey"] = call.Args[routing];
            Emit("BasicPublish", names, call.From, null, call.MessageName, call.MessageTypeId, call.At, call.Via);
        }
        else if (IsConsumeName(methodName))
        {
            var queue = Find(n => n.Contains("queue")) ?? stringParams[0];
            Emit("BasicConsume", new Dictionary<string, Name> { ["queue"] = call.Args[queue] },
                call.From, call.Handler, call.MessageName, call.MessageTypeId, call.At, call.Via);
        }
    }

    /// <summary>Parameter types that can carry an exchange/queue/routing-key name.</summary>
    private static bool IsNameType(ITypeSymbol type) =>
        type.SpecialType == SpecialType.System_String || type.TypeKind == TypeKind.Enum;

    private void WriteDiagnostics()
    {
        if (_directRabbitCalls + _massTransitCalls + _wrapperDescriptions.Count + _unboundCalls.Count == 0) return;
        Diagnostics.Add($"RabbitMQ: found {_directRabbitCalls} RabbitMQ.Client call(s), {_massTransitCalls} MassTransit call(s), " +
                        $"{_wrapperDescriptions.Count} wrapper operation(s).");
        foreach (var wrapper in _wrapperDescriptions) Diagnostics.Add($"RabbitMQ: wrapper {wrapper}.");
        foreach (var (via, count) in _wrapperCallCounts.OrderBy(kv => kv.Key)) Diagnostics.Add($"RabbitMQ: {count} call site(s) linked through {via}.");
        foreach (var call in _unboundCalls)
            Diagnostics.Add($"RabbitMQ: could not resolve {call}; the project probably has compile errors in CodeGraph (see the other warnings).");
    }

    private static bool IsPublishName(string name) =>
        name.StartsWith("Publish", StringComparison.Ordinal) || name.StartsWith("Send", StringComparison.Ordinal) || name.StartsWith("Produce", StringComparison.Ordinal);

    private static bool IsConsumeName(string name) =>
        name.StartsWith("Subscribe", StringComparison.Ordinal) || name.StartsWith("Consume", StringComparison.Ordinal) || name.StartsWith("Listen", StringComparison.Ordinal);

    private static bool HasEndpointNamedParameter(IMethodSymbol method) =>
        method.Parameters.Any(p => p.Type.SpecialType == SpecialType.System_String &&
                                   (p.Name.Contains("exchange", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("queue", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// A command-like Publish*/Send*/Subscribe*/Consume* method (returns void/Task) with an exchange/queue parameter,
    /// or declared on a queue/bus/broker type.
    /// </summary>
    private static bool LooksLikeMessagingApi(IMethodSymbol method)
    {
        if (!IsPublishName(method.Name) && !IsConsumeName(method.Name)) return false;
        if (!method.ReturnsVoid && method.ReturnType.Name is not ("Task" or "ValueTask")) return false;
        if (HasEndpointNamedParameter(method)) return true;
        var owner = $"{method.ContainingType?.ContainingNamespace}.{method.ContainingType?.Name}";
        return new[] { "Queue", "Rabbit", "Bus", "Messag", "Broker", "Publisher", "Mq" }.Any(w => owner.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The message type of a wrapper call: its first concrete generic argument, or the type of its first "payload" argument.</summary>
    private static INamedTypeSymbol? CallMessageType(IInvocationOperation call)
    {
        var method = call.TargetMethod;
        if (method.TypeArguments.OfType<INamedTypeSymbol>().FirstOrDefault(IsMessageType) is { } generic) return generic;
        foreach (var argument in call.Arguments)
            // Payload parameters are often `object`; the argument's own type (before the conversion) is the message.
            if (argument.Parameter?.Type is { TypeKind: not TypeKind.Delegate } parameterType && !IsNameType(parameterType) &&
                Unwrap(argument.Value)?.Type is INamedTypeSymbol type && IsMessageType(type))
                return type;
        return null;
    }

    private static bool IsMessageType(INamedTypeSymbol type) =>
        type.SpecialType == SpecialType.None && type.TypeKind is TypeKind.Class or TypeKind.Struct &&
        !type.IsAnonymousType && !type.IsTupleType &&
        !(type.ContainingNamespace?.ToDisplayString() ?? "").StartsWith("System", StringComparison.Ordinal);

    /// <summary>A handler passed as a method group is the consumer; a lambda's code belongs to the calling member.</summary>
    private string? HandlerFrom(IOperation? delegateArg) => Unwrap(delegateArg) switch
    {
        IDelegateCreationOperation { Target: IMethodReferenceOperation reference } => _ensureNode(reference.Method),
        IMethodReferenceOperation reference => _ensureNode(reference.Method),
        _ => null,
    };


    /// <summary>The method itself plus every interface member it implements and base member it overrides.</summary>
    private static IEnumerable<string> KeysFor(IMethodSymbol method)
    {
        method = method.OriginalDefinition;
        yield return SymbolKey(method);
        for (var overridden = method.OverriddenMethod; overridden is not null; overridden = overridden.OverriddenMethod)
            yield return SymbolKey(overridden.OriginalDefinition);
        if (method.ContainingType is { } type)
            foreach (var iface in type.AllInterfaces)
                foreach (var member in iface.GetMembers(method.Name).OfType<IMethodSymbol>())
                    if (SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(member)?.OriginalDefinition, method))
                        yield return SymbolKey(member.OriginalDefinition);
    }

    private static string SymbolKey(IMethodSymbol method) =>
        method.OriginalDefinition.GetDocumentationCommentId() ?? method.OriginalDefinition.ToDisplayString();

    private void OnMassTransit(string name, IInvocationOperation op, InvocationExpressionSyntax syntax, SemanticModel model, Func<string?> owner, CancellationToken ct)
    {
        var at = At(syntax.GetLocation());
        switch (name)
        {
            case "Publish":
            {
                if (MessageType(op) is not { } message) return;
                var exchange = EnsureMassTransitExchange(message);
                var edge = _graph.AddEdge(owner(), exchange.Id, EdgeKind.Publishes);
                edge?.AddMeta("messageTypes", message.Name);
                edge?.AddMeta("mode", "publish");
                break;
            }
            case "Send":
            {
                if (MessageType(op) is not { } message) return;
                var address = SendAddress(op.Instance, model, ct);
                var from = owner();
                if (address is { Resolved: true }) AddSend(from, EnsureQueue(address, "inferred", null), message);
                else _pendingSends.Add(new PendingSend(from, message.Name, _ensureNode(message)));
                break;
            }
            case "Map" when op.TargetMethod.ContainingType?.Name == "EndpointConvention" && op.TargetMethod.TypeArguments.Length == 1:
            {
                var address = ResolveName(Arg(op, "destinationAddress"));
                if (address.Resolved && _ensureNode(op.TargetMethod.TypeArguments[0]) is { } messageId)
                    _conventionByMessage[messageId] = address;
                break;
            }
            case "ReceiveEndpoint":
                EnsureQueue(ResolveName(Arg(op, "queueName")), "masstransit-endpoint", at);
                break;
            case "ConfigureConsumer" or "Consumer" when op.TargetMethod.TypeArguments.Length > 0:
            {
                var consumerId = _ensureNode(op.TargetMethod.TypeArguments[0]);
                var endpoint = syntax.Ancestors().OfType<InvocationExpressionSyntax>().FirstOrDefault(i => InvokedName(i) == "ReceiveEndpoint");
                if (consumerId is null || endpoint is null) return;
                if (model.GetOperation(endpoint, ct) is IInvocationOperation endpointOp && ResolveName(Arg(endpointOp, "queueName")) is { } queue)
                    _endpointByConsumer[consumerId] = queue;
                break;
            }
            case "SetKebabCaseEndpointNameFormatter":
                _endpointFormatter = "kebab";
                break;
            case "SetSnakeCaseEndpointNameFormatter":
                _endpointFormatter = "snake";
                break;
        }
    }

    // ---------------------------------------------------------------- resolution

    /// <summary>Adds the MassTransit topology: consumer queues bound to message exchanges, and convention-routed sends.</summary>
    public void Complete()
    {
        ResolveWrapperCalls();
        WriteDiagnostics();

        foreach (var consumer in _consumers)
        {
            var explicitQueue = _endpointByConsumer.GetValueOrDefault(consumer.TypeId);
            var queue = explicitQueue is not null
                ? EnsureQueue(explicitQueue, "masstransit-endpoint", null)
                : EnsureQueue(Name.Constant(ConventionQueueName(consumer.TypeName)), "masstransit-convention", consumer.At);
            if (queue is null) continue;
            var exchange = EnsureExchange(Name.Constant(consumer.ExchangeName), "masstransit-convention", null)!;
            exchange.Meta["exchangeType"] = "fanout";
            _graph.AddEdge(exchange.Id, queue.Id, EdgeKind.Binds)?.AddMeta("messageTypes", consumer.MessageName);
            _graph.AddEdge(exchange.Id, consumer.MessageTypeId, EdgeKind.Carries);
            _graph.AddEdge(queue.Id, consumer.ConsumeId, EdgeKind.Consumes)?.AddMeta("messageTypes", consumer.MessageName);
        }

        foreach (var send in _pendingSends)
        {
            var address = send.MessageTypeId is not null ? _conventionByMessage.GetValueOrDefault(send.MessageTypeId) : null;
            var queue = address is not null
                ? EnsureQueue(address, "inferred", null)
                : EnsureQueue(Name.Unresolved($"send: {send.MessageName}"), "inferred", null);
            AddSend(send.From, queue, send.MessageName, send.MessageTypeId);
        }
    }

    private void AddSend(string? from, GraphNode? queue, INamedTypeSymbol message) => AddSend(from, queue, message.Name, _ensureNode(message));

    private void AddSend(string? from, GraphNode? queue, string messageName, string? messageTypeId)
    {
        if (queue is null) return;
        var edge = _graph.AddEdge(from, queue.Id, EdgeKind.Publishes);
        edge?.AddMeta("messageTypes", messageName);
        edge?.AddMeta("mode", "send");
        _graph.AddEdge(queue.Id, messageTypeId, EdgeKind.Carries);
    }

    private GraphNode EnsureMassTransitExchange(INamedTypeSymbol message)
    {
        var exchange = EnsureExchange(Name.Constant(MassTransitExchangeName(message)), "masstransit-convention", null)!;
        exchange.Meta["exchangeType"] = "fanout";
        _graph.AddEdge(exchange.Id, _ensureNode(message), EdgeKind.Carries);
        return exchange;
    }

    /// <summary>MassTransit's default publish topology names the exchange after the message type: "Namespace:Type".</summary>
    private static string MassTransitExchangeName(INamedTypeSymbol message)
    {
        var ns = message.ContainingNamespace is { IsGlobalNamespace: false } n ? n.ToDisplayString() : "";
        var name = message.Name;
        for (var outer = message.ContainingType; outer is not null; outer = outer.ContainingType) name = outer.Name + "+" + name;
        return $"{ns}:{name}";
    }

    private string ConventionQueueName(string consumerTypeName)
    {
        var name = consumerTypeName.EndsWith("Consumer", StringComparison.Ordinal) && consumerTypeName.Length > "Consumer".Length
            ? consumerTypeName[..^"Consumer".Length]
            : consumerTypeName;
        return _endpointFormatter switch
        {
            "kebab" => SplitWords(name, '-'),
            "snake" => SplitWords(name, '_'),
            _ => name,
        };
    }

    private static string SplitWords(string name, char separator)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1])) sb.Append(separator);
            sb.Append(char.ToLowerInvariant(name[i]));
        }
        return sb.ToString();
    }

    /// <summary>
    /// A consumer object whose class derives from (Async)DefaultBasicConsumer handles deliveries in HandleBasicDeliver(Async);
    /// otherwise (e.g. an EventingBasicConsumer with a lambda) the handler code lives in the calling member.
    /// </summary>
    private string? ConsumerHandler(IOperation? consumer)
    {
        if (Unwrap(consumer)?.Type is not INamedTypeSymbol type || !type.Locations.Any(l => l.IsInSource)) return null;
        for (var t = type; t is not null; t = t.BaseType)
        {
            var handler = t.GetMembers().FirstOrDefault(m => m.Name is "HandleBasicDeliver" or "HandleBasicDeliverAsync" && m.Locations.Any(l => l.IsInSource));
            if (handler is not null) return _ensureNode(handler);
        }
        return _ensureNode(type);
    }

    private static INamedTypeSymbol? MessageType(IInvocationOperation op)
    {
        if (op.TargetMethod.TypeArguments.FirstOrDefault() is INamedTypeSymbol generic && generic.SpecialType != SpecialType.System_Object)
            return generic;
        return Unwrap(Arg(op, "message"))?.Type as INamedTypeSymbol is { SpecialType: not SpecialType.System_Object } type ? type : null;
    }

    /// <summary>Finds the address of `await provider.GetSendEndpoint(new Uri("queue:x"))` that produced the endpoint a Send is called on.</summary>
    private static Name? SendAddress(IOperation? instance, SemanticModel model, CancellationToken ct)
    {
        var receiver = Unwrap(instance);
        if (receiver is ILocalReferenceOperation local &&
            local.Local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(ct) is VariableDeclaratorSyntax { Initializer.Value: { } init } &&
            init.SyntaxTree == model.SyntaxTree)
            receiver = model.GetOperation(init, ct);
        if (Unwrap(receiver) is IAwaitOperation awaited) receiver = awaited.Operation;
        return Unwrap(receiver) is IInvocationOperation { TargetMethod.Name: "GetSendEndpoint" } getEndpoint
            ? ResolveName(Arg(getEndpoint, "address"))
            : null;
    }

    // ---------------------------------------------------------------- names

    /// <summary>An exchange/queue name: a compile-time value, or the source text of an expression we cannot evaluate.</summary>
    private sealed record Name(string Value, bool Resolved)
    {
        /// <summary>Set when the name is a parameter of an ordinary method: resolved later at that method's call sites.</summary>
        public int? Param { get; init; }
        public ParamOwner? ParamOwner { get; init; }
        /// <summary>Case changes applied to the parameter inside the wrapper ("lower"/"upper"), replayed on the call-site value.</summary>
        public string? Case { get; init; }

        /// <summary>This (parameter) name bound to a call-site value, with the wrapper's case change applied.</summary>
        public Name Bind(Name callSite) => Case is null || !callSite.Resolved
            ? callSite with { Case = callSite.Case ?? (callSite.Param is not null ? Case : null) }
            : Constant(Case == "lower" ? callSite.Value.ToLowerInvariant() : callSite.Value.ToUpperInvariant());

        public static Name Constant(string value) => new(value, true);
        public static Name Unresolved(string text) => new(text, false);
        public string Display => Resolved ? Value : $"{{{Value}}}";
    }

    private static Name ResolveName(IOperation? operation)
    {
        var value = Unwrap(operation);
        if (value is null) return Name.Unresolved("?");
        if (value.ConstantValue is { HasValue: true, Value: string constant }) return Name.Constant(constant);

        switch (value)
        {
            case IParameterReferenceOperation { Parameter: { ContainingSymbol: IMethodSymbol { MethodKind: not (MethodKind.LambdaMethod or MethodKind.AnonymousFunction or MethodKind.LocalFunction) } method } parameter }:
                return new Name(parameter.Name, false)
                {
                    Param = parameter.Ordinal,
                    ParamOwner = new ParamOwner(KeysFor(method).ToArray(), $"{method.ContainingType?.Name}.{method.Name}"),
                };
            case IFieldReferenceOperation { Field: { Name: "Empty", ContainingType.SpecialType: SpecialType.System_String } }:
                return Name.Constant("");
            // ExchangeNames.Brand as an enum member (possibly with .ToString()) is named after the member.
            case IFieldReferenceOperation { Field: { ContainingType.TypeKind: TypeKind.Enum } enumMember }:
                return Name.Constant(enumMember.Name);
            case IInvocationOperation { TargetMethod.Name: "ToString", Arguments.Length: 0, Instance: { } instance }:
                return ResolveName(instance);
            case IInvocationOperation { TargetMethod.Name: "ToLower" or "ToLowerInvariant" or "ToUpper" or "ToUpperInvariant", Instance: { } cased }:
            {
                var lower = ((IInvocationOperation)value).TargetMethod.Name.StartsWith("ToLower", StringComparison.Ordinal);
                return ResolveName(cased) switch
                {
                    { Resolved: true } r => Name.Constant(lower ? r.Value.ToLowerInvariant() : r.Value.ToUpperInvariant()),
                    { Param: not null } p => p with { Case = lower ? "lower" : "upper" },
                    _ => Name.Unresolved(value.Syntax.ToString()),
                };
            }
            case IFieldReferenceOperation { Field: { IsReadOnly: true } field } when LiteralInitializer(field) is { } literal:
                return Name.Constant(literal);
            case IPropertyReferenceOperation { Property: { SetMethod: null } property } when LiteralInitializer(property) is { } literal:
                return Name.Constant(literal);
            case IObjectCreationOperation creation when creation.Type?.ToDisplayString() == "System.Uri":
            {
                var uri = ResolveName(creation.Arguments.FirstOrDefault()?.Value);
                return uri.Resolved ? Name.Constant(EndpointNameFromUri(uri.Value)) : uri;
            }
        }
        return Name.Unresolved(value.Syntax.ToString());
    }

    private static string? LiteralInitializer(ISymbol symbol)
    {
        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            var expression = reference.GetSyntax() switch
            {
                VariableDeclaratorSyntax v => v.Initializer?.Value,
                PropertyDeclarationSyntax p => p.Initializer?.Value ?? p.ExpressionBody?.Expression,
                _ => null,
            };
            if (expression is LiteralExpressionSyntax literal && literal.Token.Value is string s) return s;
        }
        return null;
    }

    /// <summary>"queue:orders" / "exchange:orders" / "rabbitmq://host/vhost/orders?durable=true" → "orders".</summary>
    private static string EndpointNameFromUri(string uri)
    {
        var path = uri.Split('?')[0];
        if (path.Contains("://", StringComparison.Ordinal)) return path.TrimEnd('/').Split('/').Last();
        var colon = path.IndexOf(':');
        return colon >= 0 ? path[(colon + 1)..] : path;
    }

    // ---------------------------------------------------------------- nodes

    private GraphNode? EnsureExchange(Name name, string source, Where? at) => EnsureEndpoint(NodeKind.Exchange, "x", name, source, at);

    private GraphNode? EnsureQueue(Name name, string source, Where? at) => EnsureEndpoint(NodeKind.Queue, "q", name, source, at);

    private GraphNode? EnsureEndpoint(NodeKind kind, string prefix, Name name, string source, Where? at)
    {
        if (kind == NodeKind.Queue && name is { Resolved: true, Value: "" }) return null; // server-named queue
        EnsureBroker();
        var id = $"mq:{prefix}:{(name.Resolved ? name.Value : "?" + name.Value)}";
        var display = name.Resolved ? (name.Value.Length == 0 ? "(default exchange)" : name.Value) : name.Value;
        var node = _graph.GetOrAdd(id, () => new GraphNode
        {
            Id = id,
            Kind = kind,
            Name = display,
            FullName = $"{(kind == NodeKind.Exchange ? "exchange" : "queue")} {display}",
            ParentId = BrokerId,
            Meta = { ["source"] = source },
        });
        if (!name.Resolved) node.Meta["unresolved"] = "true";
        // Declarations are the most informative location and source; they win over inferred usages.
        if (at is not null && (node.File is null || source is "declared" && node.Meta["source"] != "declared"))
        {
            node.File = at.File;
            node.Line = at.Line;
        }
        if (source == "declared" || node.Meta["source"] is "inferred") node.Meta["source"] = source;
        return node;
    }

    private void EnsureBroker() =>
        _graph.GetOrAdd(BrokerId, () => new GraphNode
        {
            Id = BrokerId,
            Kind = NodeKind.Broker,
            Name = "RabbitMQ",
            FullName = "RabbitMQ message broker",
        });

    // ---------------------------------------------------------------- helpers

    private sealed record Where(string File, int Line);

    private static Where? At(Location? location) =>
        location?.SourceTree?.FilePath is { Length: > 0 } file
            ? new Where(file, location.GetLineSpan().StartLinePosition.Line + 1)
            : null;

    private sealed record ConsumerFact(string TypeId, string TypeName, string ConsumeId, string? MessageTypeId, string MessageName, string ExchangeName, Where? At);

    private sealed record PendingSend(string? From, string MessageName, string? MessageTypeId);

    /// <summary>
    /// The method a parameter belongs to, as documentation-id keys (itself + implemented interface/base members).
    /// Kept as strings, not symbols, so analyzed solutions' compilations can be freed.
    /// </summary>
    private sealed record ParamOwner(string[] Keys, string DisplayName);

    /// <summary>What a wrapper method does: the operation, and its names (constants or references to its own parameters).</summary>
    private sealed record WrapperSpec(string Operation, IReadOnlyDictionary<string, Name> Names, string? Handler);

    private sealed record CallFact(
        string TargetKey, string[] ParameterNames, IReadOnlyDictionary<int, Name> Args,
        string? From, string? Handler,
        string? MessageName, string? MessageTypeId, Where? At, string Via, string? ConventionName);

    private static IOperation? Arg(IInvocationOperation op, string parameterName) =>
        op.Arguments.FirstOrDefault(a => a.Parameter?.Name == parameterName)?.Value;

    private static IOperation? Unwrap(IOperation? operation)
    {
        while (operation is IConversionOperation or IParenthesizedOperation)
            operation = operation switch
            {
                IConversionOperation c => c.Operand,
                IParenthesizedOperation p => p.Operand,
                _ => operation,
            };
        return operation;
    }

    private static string? InvokedName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        _ => null,
    };
}
