using System.Text.RegularExpressions;
using CodeGraph.Core.Messaging;
using CodeGraph.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeGraph.Core;

/// <summary>One analysis pass over a loaded solution.</summary>
internal sealed partial class AnalysisRun
{
    public const string FrameworkPackageId = "pkg:.NET";

    private static readonly SymbolDisplayFormat TypeNameFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static readonly SymbolDisplayFormat FullTypeNameFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static readonly SymbolDisplayFormat MemberNameFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static readonly SymbolDisplayFormat SignatureFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeTypeConstraints,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType |
                       SymbolDisplayMemberOptions.IncludeModifiers | SymbolDisplayMemberOptions.IncludeAccessibility,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName |
                          SymbolDisplayParameterOptions.IncludeParamsRefOut | SymbolDisplayParameterOptions.IncludeDefaultValue,
        propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
                              SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private readonly List<string> _warnings;
    private readonly IProgress<string>? _progress;
    private readonly GraphBuilder _graph = new();

    /// <summary>Assembly name → project display name, for every C# project in the solution.</summary>
    private readonly Dictionary<string, string> _projectByAssembly = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Referenced (metadata) assembly name → package node id.</summary>
    private readonly Dictionary<string, string> _packageByAssembly = new(StringComparer.OrdinalIgnoreCase);

    private readonly MessagingCollector _messaging;

    /// <summary>Display names by project file, when analyzing a folder (disambiguates e.g. several "Api" projects).</summary>
    private readonly IReadOnlyDictionary<string, string> _projectNames;
    /// <summary>Project files already analyzed; projects shared by several solutions are analyzed once.</summary>
    private readonly HashSet<string> _analyzedProjects = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="knownAssemblies">
    /// Assembly name → project name for every project in the analyzed folder. Types from those assemblies are
    /// treated as part of the code base even when a service consumes them as a (internal) NuGet package.
    /// </param>
    public AnalysisRun(List<string> warnings, IProgress<string>? progress,
                       IReadOnlyDictionary<string, string>? projectNames = null,
                       IReadOnlyDictionary<string, string>? knownAssemblies = null)
    {
        _warnings = warnings;
        _progress = progress;
        _projectNames = projectNames ?? new Dictionary<string, string>();
        foreach (var (assembly, project) in knownAssemblies ?? new Dictionary<string, string>())
            _projectByAssembly[assembly] = project;
        _messaging = new MessagingCollector(_graph, EnsureNode);
    }

    /// <summary>Adds a node for a solution file (folder analysis); its projects are nested in it.</summary>
    public string AddSolutionNode(string solutionPath)
    {
        var name = Path.GetFileNameWithoutExtension(solutionPath);
        var id = $"sln:{name}";
        for (var i = 2; _graph.TryGet(id, out _); i++) id = $"sln:{name}-{i}";
        _graph.GetOrAdd(id, () => new GraphNode
        {
            Id = id,
            Kind = NodeKind.Solution,
            Name = name,
            FullName = solutionPath,
            File = solutionPath,
        });
        return id;
    }

    /// <summary>Analyzes the solution's projects that were not analyzed yet (as part of an earlier solution).</summary>
    public async Task AddSolutionAsync(Solution solution, string? solutionNodeId, CancellationToken ct)
    {
        var projects = SelectProjects(solution);

        foreach (var project in projects)
        {
            var name = ProjectName(project);
            _projectByAssembly[project.AssemblyName] = name;
            _graph.GetOrAdd(ProjectId(name), () => new GraphNode
            {
                Id = ProjectId(name),
                Kind = NodeKind.Project,
                Name = name,
                FullName = project.AssemblyName,
                ParentId = solutionNodeId,
                File = project.FilePath,
                Meta =
                {
                    ["assembly"] = project.AssemblyName,
                    ["outputKind"] = project.CompilationOptions?.OutputKind.ToString() ?? "",
                    ["documents"] = project.Documents.Count().ToString(),
                },
            });
        }

        foreach (var project in projects)
        {
            var projectId = ProjectId(ProjectName(project));
            foreach (var reference in project.ProjectReferences)
                if (solution.GetProject(reference.ProjectId) is { } target)
                    _graph.AddEdge(projectId, ProjectId(ProjectName(target)), EdgeKind.ProjectRef);
        }

        // Creating compilations (parsing + declarations) is independent per project: do all of them concurrently.
        using (Timer.Measure("compile"))
        {
            _progress?.Report($"Compiling {projects.Count} project(s)...");
            await Task.WhenAll(projects.Select(p => p.GetCompilationAsync(ct)));
        }

        var index = 0;
        foreach (var project in projects)
        {
            ct.ThrowIfCancellationRequested();
            _progress?.Report($"Analyzing {ProjectName(project)} ({++index}/{projects.Count})...");
            await AnalyzeProjectAsync(project, ct);
        }
    }

    /// <summary>Finishes cross-solution work (messaging topology) once every solution has been added.</summary>
    public void Complete()
    {
        using var _ = Timer.Measure("messaging");
        _messaging.Complete();
        _warnings.AddRange(_messaging.Diagnostics);
    }

    public PhaseTimer Timer { get; } = new();

    public GraphDocument Build(string path, string name)
    {
        var doc = _graph.Build(path, name, _warnings);
        doc.Timings = Timer.ToSeconds();
        return doc;
    }

    private List<Project> SelectProjects(Solution solution)
    {
        var result = new List<Project>();
        foreach (var project in solution.Projects.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (project.Language != LanguageNames.CSharp)
            {
                _warnings.Add($"Skipped {project.Name}: only C# projects are analyzed ({project.Language}).");
                continue;
            }
            // Multi-targeted projects load once per target framework, and shared projects appear in several solutions.
            if (project.FilePath is { } file && !_analyzedProjects.Add(file)) continue;
            result.Add(project);
        }
        return result;
    }

    private async Task AnalyzeProjectAsync(Project project, CancellationToken ct)
    {
        var projectName = ProjectName(project);
        var projectNodeId = ProjectId(projectName);

        var assets = project.FilePath is { } projectFile ? NuGetAssets.Load(projectFile) : null;
        if (assets is null)
            _warnings.Add($"{projectName}: obj/project.assets.json not found; run 'dotnet restore' to see NuGet packages.");
        else
        {
            foreach (var package in assets.DirectDependencies)
                _graph.AddEdge(projectNodeId, EnsurePackage(package, assets.Versions.GetValueOrDefault(package)), EdgeKind.PackageRef);
            foreach (var (package, deps) in assets.PackageDependencies)
                foreach (var dep in deps)
                    _graph.AddEdge(EnsurePackage(package, assets.Versions.GetValueOrDefault(package)),
                        EnsurePackage(dep, assets.Versions.GetValueOrDefault(dep)), EdgeKind.PackageRef);
        }

        Compilation? compilation;
        using (Timer.Measure("compile")) compilation = await project.GetCompilationAsync(ct);
        if (compilation is null)
        {
            _warnings.Add($"{projectName}: could not create a compilation.");
            return;
        }

        using (Timer.Measure("diagnostics"))
        {
            // Declaration-level diagnostics catch what matters here (missing references/usings, unresolved types in
            // signatures) without binding every method body a second time, which full GetDiagnostics() would do.
            var errors = compilation.GetDeclarationDiagnostics(ct).Count(d => d.Severity == DiagnosticSeverity.Error);
            if (errors > 0)
                _warnings.Add($"{projectName}: {errors} compile error(s); some references may be unresolved.");
        }

        foreach (var reference in compilation.References.OfType<PortableExecutableReference>())
        {
            if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly) continue;
            if (_packageByAssembly.ContainsKey(assembly.Name)) continue;
            var package = reference.FilePath is { } refPath ? assets?.PackageForPath(refPath) : null;
            _packageByAssembly[assembly.Name] = package is not null
                ? EnsurePackage(package, assets!.Versions.GetValueOrDefault(package))
                : IsFrameworkPath(reference.FilePath) ? EnsureFrameworkPackage() : EnsureAssemblyPackage(assembly.Name, reference.FilePath);
        }

        var documents = new List<(SyntaxTree Tree, SemanticModel Model)>();
        foreach (var document in project.Documents)
        {
            if (document.FilePath is { } docPath && IsGeneratedPath(docPath)) continue;
            if (await document.GetSyntaxTreeAsync(ct) is { } tree) documents.Add((tree, compilation.GetSemanticModel(tree)));
        }

        // Binding method bodies is the expensive part and semantic models are thread-safe, so bind every document in
        // parallel first; the graph walk below is single-threaded but then only reads the models' binding caches.
        using (Timer.Measure("bind"))
            await Parallel.ForEachAsync(documents, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct },
                (doc, token) => { Bind(doc.Tree, doc.Model, token); return ValueTask.CompletedTask; });

        using (Timer.Measure("walk"))
            foreach (var (tree, model) in documents)
                ProcessTree(tree, model, ct);
        _nodeIdCache.Clear(); // don't keep this project's symbols (and so its compilation) alive
    }

    /// <summary>Warms the semantic model's binding caches for the nodes <see cref="ProcessTree"/> will query.</summary>
    private static void Bind(SyntaxTree tree, SemanticModel model, CancellationToken ct)
    {
        foreach (var node in tree.GetRoot(ct).DescendantNodes())
        {
            if (node is SimpleNameSyntax or BaseObjectCreationExpressionSyntax or ConstructorInitializerSyntax or InvocationExpressionSyntax)
                model.GetSymbolInfo(node, ct);
            else if (node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax or BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax)
                model.GetDeclaredSymbol(node, ct);
        }
    }

    // ---------------------------------------------------------------- syntax walk

    private void ProcessTree(SyntaxTree tree, SemanticModel model, CancellationToken ct)
    {
        var root = tree.GetRoot(ct);
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case BaseTypeDeclarationSyntax or DelegateDeclarationSyntax:
                    ProcessTypeDeclaration(node, model, ct);
                    break;
                case BaseObjectCreationExpressionSyntax creation:
                    if (model.GetSymbolInfo(creation, ct).Symbol is IMethodSymbol { ContainingType: { } created })
                        _graph.AddEdge(Owner(model, creation, ct), EnsureNode(created), EdgeKind.Instantiates);
                    break;
                case InvocationExpressionSyntax invocation:
                    _messaging.OnInvocation(invocation, model, () => Owner(model, invocation, ct), ct);
                    break;
                case ConstructorInitializerSyntax initializer:
                    if (model.GetSymbolInfo(initializer, ct).Symbol is IMethodSymbol baseCtor)
                        _graph.AddEdge(Owner(model, initializer, ct), EnsureNode(baseCtor), EdgeKind.Calls);
                    break;
                case SimpleNameSyntax name:
                    ProcessName(name, model, ct);
                    break;
            }
        }
    }

    private void ProcessTypeDeclaration(SyntaxNode declaration, SemanticModel model, CancellationToken ct)
    {
        if (model.GetDeclaredSymbol(declaration, ct) is not INamedTypeSymbol type) return;
        var typeId = EnsureNode(type);
        if (typeId is null) return;

        // Partial types have several declarations; relationships are recorded once, from the first one.
        var first = type.DeclaringSyntaxReferences.FirstOrDefault();
        if (first is null || first.SyntaxTree != declaration.SyntaxTree || first.Span != declaration.Span) return;

        _messaging.OnType(type, typeId);

        if (type.TypeKind != TypeKind.Interface && type.BaseType is { } baseType && baseType.SpecialType == SpecialType.None)
            _graph.AddEdge(typeId, EnsureNode(baseType), EdgeKind.Inherits);
        foreach (var iface in type.Interfaces)
            _graph.AddEdge(typeId, EnsureNode(iface), type.TypeKind == TypeKind.Interface ? EdgeKind.Inherits : EdgeKind.Implements);

        foreach (var member in type.GetMembers())
        {
            if (!IsDeclaredMember(member)) continue;
            var memberId = EnsureNode(member);
            ISymbol? overridden = member switch
            {
                IMethodSymbol m => m.OverriddenMethod,
                IPropertySymbol p => p.OverriddenProperty,
                IEventSymbol e => e.OverriddenEvent,
                _ => null,
            };
            if (overridden is not null) _graph.AddEdge(memberId, EnsureNode(overridden), EdgeKind.Overrides);
        }

        if (type.TypeKind is TypeKind.Class or TypeKind.Struct)
            foreach (var iface in type.AllInterfaces)
                foreach (var ifaceMember in iface.GetMembers())
                {
                    if (ifaceMember is IMethodSymbol { MethodKind: not MethodKind.Ordinary } || !ifaceMember.IsAbstract && !ifaceMember.IsVirtual) continue;
                    var impl = type.FindImplementationForInterfaceMember(ifaceMember);
                    if (impl is not null && SymbolEqualityComparer.Default.Equals(impl.ContainingType, type))
                        _graph.AddEdge(EnsureNode(impl), EnsureNode(ifaceMember), EdgeKind.Overrides);
                }
    }

    private void ProcessName(SimpleNameSyntax name, SemanticModel model, CancellationToken ct)
    {
        if (name.Ancestors().Any(a => a is UsingDirectiveSyntax || IsInNamespaceName(name, a))) return;

        var info = model.GetSymbolInfo(name, ct);
        var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
        switch (symbol)
        {
            case IMethodSymbol { MethodKind: MethodKind.LocalFunction or MethodKind.LambdaMethod or MethodKind.AnonymousFunction }:
                return; // local functions are rolled up into their containing member
            case IMethodSymbol { MethodKind: MethodKind.Constructor } ctor:
                // Attribute usage ([Foo]) binds to the constructor; creations are handled separately.
                if (name.Parent is AttributeSyntax || name.Parent?.Parent is AttributeSyntax)
                    _graph.AddEdge(Owner(model, name, ct), EnsureNode(ctor.ContainingType), EdgeKind.UsesType);
                return;
            case IMethodSymbol or IPropertySymbol or IEventSymbol:
                _graph.AddEdge(Owner(model, name, ct), EnsureNode(symbol), EdgeKind.Calls);
                return;
            case INamedTypeSymbol type:
                if (name.Ancestors().Any(a => a is BaseListSyntax)) return; // recorded as Inherits/Implements
                if (IsCreationTypeName(name)) return;                        // recorded as Instantiates
                if (IsNoiseType(type)) return;
                _graph.AddEdge(Owner(model, name, ct), EnsureNode(type), EdgeKind.UsesType);
                return;
        }
    }

    private static bool IsInNamespaceName(SyntaxNode name, SyntaxNode ancestor) =>
        ancestor is BaseNamespaceDeclarationSyntax ns && ns.Name.Span.Contains(name.Span);

    private static bool IsCreationTypeName(SimpleNameSyntax name)
    {
        SyntaxNode current = name;
        while (current.Parent is QualifiedNameSyntax or AliasQualifiedNameSyntax) current = current.Parent;
        return current.Parent is ObjectCreationExpressionSyntax creation && creation.Type == current;
    }

    /// <summary>Primitive/special types and compiler-generated shapes add noise without insight.</summary>
    private static bool IsNoiseType(INamedTypeSymbol type) =>
        type.SpecialType != SpecialType.None || type.OriginalDefinition.SpecialType != SpecialType.None ||
        type.IsAnonymousType || type.IsTupleType || type.TypeKind == TypeKind.Error;

    /// <summary>The graph node that "owns" code at this position: the enclosing member, or the type for field initializers.</summary>
    /// <remarks>
    /// Walks the syntax (not <see cref="SemanticModel.GetEnclosingSymbol"/>), so code inside lambdas, local functions
    /// and anonymous-object initializers is attributed to the declared member that contains it.
    /// </remarks>
    private string? Owner(SemanticModel model, SyntaxNode node, CancellationToken ct)
    {
        foreach (var ancestor in node.AncestorsAndSelf())
        {
            switch (ancestor)
            {
                case BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax:
                    return model.GetDeclaredSymbol(ancestor, ct) is { } member && IsDeclaredMember(member)
                        ? EnsureNode(member)
                        : TypeOwner(model, ancestor, ct);
                case GlobalStatementSyntax:
                    return EnsureNode(model.GetEnclosingSymbol(node.SpanStart, ct)); // top-level statements' Main
                case BaseFieldDeclarationSyntax or BaseTypeDeclarationSyntax or DelegateDeclarationSyntax:
                    return TypeOwner(model, ancestor, ct);
            }
        }
        return null;
    }

    private string? TypeOwner(SemanticModel model, SyntaxNode node, CancellationToken ct)
    {
        var typeDeclaration = node.AncestorsAndSelf().FirstOrDefault(a => a is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax);
        return typeDeclaration is null ? null : EnsureNode(model.GetDeclaredSymbol(typeDeclaration, ct));
    }

    private static bool IsDeclaredMember(ISymbol member) => member switch
    {
        IMethodSymbol m => m.MethodKind is MethodKind.Ordinary or MethodKind.Constructor or MethodKind.StaticConstructor
                               or MethodKind.Destructor or MethodKind.UserDefinedOperator or MethodKind.Conversion
                               or MethodKind.ExplicitInterfaceImplementation
                           && (!m.IsImplicitlyDeclared || IsTopLevelMain(m)),
        IPropertySymbol or IEventSymbol => !member.IsImplicitlyDeclared,
        _ => false,
    };

    private static bool IsTopLevelMain(ISymbol symbol) => symbol is IMethodSymbol { Name: "<Main>$" };

    // ---------------------------------------------------------------- nodes

    /// <summary>
    /// Node ids already resolved for this project's symbols. The same types and methods are referenced over and over,
    /// and building their documentation-comment ids is surprisingly costly. Cleared after each project.
    /// </summary>
    private readonly Dictionary<ISymbol, string?> _nodeIdCache = new(SymbolEqualityComparer.Default);

    private string? EnsureNode(ISymbol? symbol)
    {
        if (symbol is null) return null;
        if (_nodeIdCache.TryGetValue(symbol, out var cached)) return cached;
        var normalized = Normalize(symbol);
        var id = normalized switch
        {
            INamedTypeSymbol type => EnsureType(type),
            IMethodSymbol or IPropertySymbol or IEventSymbol => EnsureMember(normalized),
            _ => null,
        };
        _nodeIdCache[symbol] = id;
        return id;
    }

    private static ISymbol? Normalize(ISymbol? symbol)
    {
        switch (symbol)
        {
            case null:
                return null;
            case IArrayTypeSymbol array:
                return Normalize(array.ElementType);
            case IPointerTypeSymbol pointer:
                return Normalize(pointer.PointedAtType);
            case INamedTypeSymbol type:
                if (type.IsTupleType || type.IsAnonymousType || type.TypeKind == TypeKind.Error) return null;
                return type.OriginalDefinition;
            case IMethodSymbol method:
                method = method.ReducedFrom ?? method;
                if (method.AssociatedSymbol is { } associated) return Normalize(associated);
                if (method.MethodKind is MethodKind.LambdaMethod or MethodKind.AnonymousFunction or MethodKind.LocalFunction) return null;
                return method.OriginalDefinition;
            case IPropertySymbol or IEventSymbol:
                return symbol.OriginalDefinition;
            default:
                return null;
        }
    }

    private string? EnsureType(INamedTypeSymbol type)
    {
        var id = SymbolId(type);
        var sourceLocation = type.Locations.FirstOrDefault(l => l.IsInSource && !IsGeneratedPath(l.SourceTree?.FilePath));
        if (_graph.TryGet(id, out var existing))
        {
            // First seen through a compiled reference (e.g. another service's contracts package); now we have its source.
            if (existing.File is null && !existing.External) SetLocation(existing, sourceLocation);
            return id;
        }

        string? parentId;
        bool external;
        // Any assembly built from a project in the analyzed code base is "ours", even when referenced as a DLL/package.
        if (type.ContainingAssembly is { } asm && _projectByAssembly.TryGetValue(asm.Name, out var project))
        {
            parentId = EnsureNamespace(project, type.ContainingNamespace);
            external = false;
        }
        else
        {
            parentId = PackageFor(type.ContainingAssembly);
            external = true;
            sourceLocation = null;
            if (parentId is null) return null;
        }

        var kind = type.IsRecord ? (type.TypeKind == TypeKind.Struct ? "record struct" : "record") : type.TypeKind.ToString().ToLowerInvariant();
        var node = new GraphNode
        {
            Id = id,
            Kind = NodeKind.Type,
            Name = type.ToDisplayString(TypeNameFormat),
            FullName = type.ToDisplayString(FullTypeNameFormat),
            ParentId = parentId,
            External = external,
            Meta =
            {
                ["typeKind"] = kind,
                ["accessibility"] = Accessibility(type.DeclaredAccessibility),
                ["namespace"] = type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : "",
            },
        };
        if (type.IsStatic) node.Meta["static"] = "true";
        if (type.IsAbstract && type.TypeKind == TypeKind.Class) node.Meta["abstract"] = "true";
        if (type.IsSealed && type.TypeKind == TypeKind.Class && !type.IsStatic) node.Meta["sealed"] = "true";
        if (type.GetMembers().Length > 0 && !external)
            node.Meta["members"] = type.GetMembers().Count(IsDeclaredMember).ToString();
        SetLocation(node, sourceLocation);
        _graph.GetOrAdd(id, () => node);
        return id;
    }

    private string? EnsureMember(ISymbol member)
    {
        var id = SymbolId(member);
        if (_graph.TryGet(id, out var existing))
        {
            if (existing.File is null && !existing.External)
                SetLocation(existing, member.Locations.FirstOrDefault(l => l.IsInSource && !IsGeneratedPath(l.SourceTree?.FilePath)));
            return id;
        }
        // Members of anonymous types and tuples are not graph nodes (Normalize drops those types).
        if (Normalize(member.ContainingType) is not INamedTypeSymbol containingType) return null;
        var parentId = EnsureType(containingType);
        if (parentId is null || !_graph.TryGet(parentId, out var parent)) return null;

        var memberKind = member switch
        {
            IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } => "constructor",
            IMethodSymbol { MethodKind: MethodKind.Destructor } => "destructor",
            IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator or MethodKind.Conversion } => "operator",
            IMethodSymbol => "method",
            IPropertySymbol { IsIndexer: true } => "indexer",
            IPropertySymbol => "property",
            IEventSymbol => "event",
            _ => "member",
        };

        var name = IsTopLevelMain(member) ? "Main (top-level statements)" : member.ToDisplayString(MemberNameFormat);
        var node = new GraphNode
        {
            Id = id,
            Kind = NodeKind.Method,
            Name = name,
            FullName = $"{parent.FullName}.{name}",
            ParentId = parentId,
            External = parent.External,
            Meta =
            {
                ["memberKind"] = memberKind,
                ["accessibility"] = Accessibility(member.DeclaredAccessibility),
                ["signature"] = member.ToDisplayString(SignatureFormat),
            },
        };
        if (member.IsStatic) node.Meta["static"] = "true";
        if (member.IsAbstract) node.Meta["abstract"] = "true";
        if (member.IsVirtual) node.Meta["virtual"] = "true";
        if (member.IsOverride) node.Meta["override"] = "true";
        if (member is IMethodSymbol { IsAsync: true }) node.Meta["async"] = "true";
        if (!parent.External)
            SetLocation(node, member.Locations.FirstOrDefault(l => l.IsInSource && !IsGeneratedPath(l.SourceTree?.FilePath)));
        _graph.GetOrAdd(id, () => node);
        return id;
    }

    private string EnsureNamespace(string project, INamespaceSymbol? ns)
    {
        var name = ns is null || ns.IsGlobalNamespace ? "<global>" : ns.ToDisplayString();
        var id = $"ns:{project}:{name}";
        _graph.GetOrAdd(id, () => new GraphNode
        {
            Id = id,
            Kind = NodeKind.Namespace,
            Name = name,
            FullName = name,
            ParentId = ProjectId(project),
        });
        return id;
    }

    private string EnsurePackage(string name, string? version)
    {
        // An internal package built from a project in the analyzed code base points at that project.
        if (_projectByAssembly.TryGetValue(name, out var project)) return ProjectId(project);
        var id = $"pkg:{name}";
        _graph.GetOrAdd(id, () => new GraphNode
        {
            Id = id,
            Kind = NodeKind.Package,
            Name = name,
            FullName = string.IsNullOrEmpty(version) ? name : $"{name} {version}",
            External = true,
            Meta = { ["source"] = "nuget", ["version"] = version ?? "" },
        });
        return id;
    }

    private string EnsureFrameworkPackage() =>
        _graph.GetOrAdd(FrameworkPackageId, () => new GraphNode
        {
            Id = FrameworkPackageId,
            Kind = NodeKind.Package,
            Name = ".NET",
            FullName = ".NET base class library",
            External = true,
            Meta = { ["source"] = "framework" },
        }).Id;

    private string EnsureAssemblyPackage(string assemblyName, string? path)
    {
        var id = $"pkg:{assemblyName}";
        _graph.GetOrAdd(id, () => new GraphNode
        {
            Id = id,
            Kind = NodeKind.Package,
            Name = assemblyName,
            FullName = path ?? assemblyName,
            External = true,
            Meta = { ["source"] = "assembly" },
        });
        return id;
    }

    private string? PackageFor(IAssemblySymbol? assembly)
    {
        if (assembly is null) return null;
        if (_packageByAssembly.TryGetValue(assembly.Name, out var id)) return id;
        // Source types from projects we could not map, or assemblies seen only through facades.
        return assembly.Name.StartsWith("System", StringComparison.Ordinal) || assembly.Name is "mscorlib" or "netstandard"
            ? EnsureFrameworkPackage()
            : EnsureAssemblyPackage(assembly.Name, null);
    }

    // ---------------------------------------------------------------- helpers

    private static string ProjectId(string projectName) => $"proj:{projectName}";

    /// <summary>Multi-targeted projects are named "Foo(net8.0)" by the workspace.</summary>
    private string ProjectName(Project project) =>
        project.FilePath is { } file && _projectNames.TryGetValue(file, out var name) ? name : TfmSuffix().Replace(project.Name, "");

    [GeneratedRegex(@"\([^()]*\)$")]
    private static partial Regex TfmSuffix();

    private static string SymbolId(ISymbol symbol) =>
        symbol.GetDocumentationCommentId() ?? "X:" + symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    private static void SetLocation(GraphNode node, Location? location)
    {
        if (location?.SourceTree?.FilePath is not { Length: > 0 } file) return;
        node.File = file;
        node.Line = location.GetLineSpan().StartLinePosition.Line + 1;
    }

    private static bool IsGeneratedPath(string? path) =>
        path is not null && (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                             || path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase));

    private static bool IsFrameworkPath(string? path) =>
        path is not null && (path.Contains($"{Path.DirectorySeparatorChar}packs{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                             || path.Contains($"{Path.DirectorySeparatorChar}shared{Path.DirectorySeparatorChar}Microsoft.", StringComparison.OrdinalIgnoreCase)
                             || path.Contains("Reference Assemblies", StringComparison.OrdinalIgnoreCase));

    private static string Accessibility(Microsoft.CodeAnalysis.Accessibility accessibility) => accessibility switch
    {
        Microsoft.CodeAnalysis.Accessibility.Public => "public",
        Microsoft.CodeAnalysis.Accessibility.Internal => "internal",
        Microsoft.CodeAnalysis.Accessibility.Protected => "protected",
        Microsoft.CodeAnalysis.Accessibility.Private => "private",
        Microsoft.CodeAnalysis.Accessibility.ProtectedOrInternal => "protected internal",
        Microsoft.CodeAnalysis.Accessibility.ProtectedAndInternal => "private protected",
        _ => "",
    };
}
