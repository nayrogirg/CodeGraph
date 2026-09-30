using CodeGraph.Core.Model;

namespace CodeGraph.Core.Tests;

public class MessagingTests : IClassFixture<SampleShopFixture>
{
    private const string OrderPlacedExchange = "mq:x:Shop.Domain.Events:OrderPlaced";
    private const string OrderPlacedConsume = "M:Shop.Worker.Consumers.OrderPlacedConsumer.Consume(MassTransit.ConsumeContext{Shop.Domain.Events.OrderPlaced})";

    private readonly GraphDocument _graph;

    public MessagingTests(SampleShopFixture fixture) => _graph = fixture.Graph;

    private GraphEdge Edge(string source, string target, EdgeKind kind) =>
        _graph.Edges.SingleOrDefault(e => e.Source == source && e.Target == target && e.Kind == kind)
        ?? throw new Xunit.Sdk.XunitException(
            $"Missing {kind} edge {source} -> {target}. Edges from source: {string.Join(", ", _graph.Edges.Where(e => e.Source == source).Select(e => $"{e.Kind}->{e.Target}"))}");

    private GraphNode Node(string id) =>
        _graph.Nodes.SingleOrDefault(n => n.Id == id)
        ?? throw new Xunit.Sdk.XunitException($"Node '{id}' not found. Messaging nodes: {string.Join(", ", _graph.Nodes.Where(n => n.Id.StartsWith("mq:")).Select(n => n.Id))}");

    [Fact]
    public void Exchanges_and_queues_live_under_the_broker()
    {
        Assert.Equal(NodeKind.Broker, Node("mq:rabbitmq").Kind);
        Assert.All(_graph.Nodes.Where(n => n.Kind is NodeKind.Exchange or NodeKind.Queue), n => Assert.Equal("mq:rabbitmq", n.ParentId));
    }

    [Fact]
    public void Raw_client_publish_bind_and_consume()
    {
        var audit = Node("mq:x:audit");
        Assert.Equal(NodeKind.Exchange, audit.Kind);
        Assert.Equal("fanout", audit.Meta["exchangeType"]);
        Assert.Equal("declared", audit.Meta["source"]);
        Assert.EndsWith("AuditPublisher.cs", audit.File);

        Edge("M:Shop.Data.AuditPublisher.PublishAsync(Shop.Domain.Order)", "mq:x:audit", EdgeKind.Publishes);
        Edge("mq:x:audit", "mq:q:audit-log", EdgeKind.Binds);
        // The handler is a lambda on an AsyncEventingBasicConsumer, so the consuming member is the one calling BasicConsumeAsync.
        Edge("mq:q:audit-log", "M:Shop.Worker.AuditListener.StartAsync", EdgeKind.Consumes);
    }

    [Fact]
    public void Runtime_computed_names_become_unresolved_nodes()
    {
        var edge = _graph.Edges.Single(e => e.Source == "M:Shop.Data.AuditPublisher.PublishToEnvironmentAsync(Shop.Domain.Order)" && e.Kind == EdgeKind.Publishes);
        var target = Node(edge.Target);
        Assert.Equal("true", target.Meta["unresolved"]);
        Assert.Contains("_options.Environment", target.Name);
        Assert.Equal("orders", edge.Meta!["routingKeys"]);
    }

    [Fact]
    public void MassTransit_publish_reaches_the_consumer()
    {
        var publish = Edge("M:Shop.Api.Services.OrderService.PlaceOrderAndNotifyAsync(MassTransit.IPublishEndpoint,System.String,System.Collections.Generic.IEnumerable{System.ValueTuple{Shop.Domain.Product,System.Int32}})",
            OrderPlacedExchange, EdgeKind.Publishes);
        Assert.Equal("OrderPlaced", publish.Meta!["messageTypes"]);
        Edge(OrderPlacedExchange, "T:Shop.Domain.Events.OrderPlaced", EdgeKind.Carries);
        Edge(OrderPlacedExchange, "mq:q:order-notifications", EdgeKind.Binds);
        Edge("mq:q:order-notifications", OrderPlacedConsume, EdgeKind.Consumes);
    }

    [Fact]
    public void Consumers_without_an_explicit_endpoint_use_the_naming_convention()
    {
        var inventory = Node("mq:q:inventory"); // kebab-case formatter, "Consumer" suffix removed
        Assert.Equal("masstransit-convention", inventory.Meta["source"]);
        Edge(OrderPlacedExchange, "mq:q:inventory", EdgeKind.Binds);
        Edge("mq:q:inventory", "M:Shop.Worker.Consumers.InventoryConsumer.Consume(MassTransit.ConsumeContext{Shop.Domain.Events.OrderPlaced})", EdgeKind.Consumes);
    }

    [Fact]
    public void Publishing_through_an_in_house_wrapper_uses_the_call_site_names()
    {
        // _queueClient.PublishMessage(brand, ExchangeNames.Brand, RoutingKey.Brand.Update) — called via the IQueueClient interface.
        var publish = Edge("M:Shop.Api.Services.BrandService.UpdateAsync(Shop.Domain.Brand)", "mq:x:brand", EdgeKind.Publishes);
        Assert.Equal("brand.update", publish.Meta!["routingKeys"]);
        Assert.Equal("Brand", publish.Meta["messageTypes"]);
        Assert.Equal("IQueueClient.PublishMessage", publish.Meta["via"]);
        Edge("mq:x:brand", "T:Shop.Domain.Brand", EdgeKind.Carries);
    }

    [Fact]
    public void Disposable_wrapper_with_enum_exchange_and_retry_lambda()
    {
        // PublishMessage(product, ExchangeName.Pricing, "price.changed"): exchange is an enum, .ToString().ToLowerInvariant()'d
        // inside a `using` channel scope and a retry lambda.
        var publish = Edge("M:Shop.Api.Services.PricingService.ChangePriceAsync(Shop.Domain.Product,System.Decimal)", "mq:x:pricing", EdgeKind.Publishes);
        Assert.Equal("price.changed", publish.Meta!["routingKeys"]);
        Assert.Equal("Product", publish.Meta["messageTypes"]);
    }

    [Fact]
    public void Messaging_diagnostics_describe_detected_wrappers()
    {
        Assert.Contains(_graph.Warnings, w => w.StartsWith("RabbitMQ:") && w.Contains("QueueClient.PublishMessage"));
    }

    [Fact]
    public void Wrappers_of_wrappers_are_followed()
    {
        // EventPublisher.Publish fixes the exchange (static readonly field) and passes the routing key through.
        var publish = Edge("M:Shop.Api.Services.BrandService.RenameAsync(Shop.Domain.Brand)", "mq:x:shop-events", EdgeKind.Publishes);
        Assert.Equal("brand.renamed", publish.Meta!["routingKeys"]);
    }

    [Fact]
    public void Binding_and_subscribing_through_a_wrapper()
    {
        var bind = Edge("mq:x:brand", "mq:q:brand-updates", EdgeKind.Binds);
        Assert.Equal("brand.update", bind.Meta!["routingKeys"]);
        Assert.EndsWith("BrandListener.cs", Node("mq:q:brand-updates").File); // declared at the wrapper's call site
        // The handler passed as a method group is the consumer.
        Edge("mq:q:brand-updates", "M:Shop.Worker.BrandListener.OnBrandUpdated(Shop.Domain.Brand)", EdgeKind.Consumes);
    }

    [Fact]
    public void Wrapper_internals_do_not_create_parameter_named_nodes()
    {
        Assert.DoesNotContain(_graph.Nodes, n => n.Id.StartsWith("mq:") && n.Name is "exchange" or "queue" or "routingKey");
    }

    [Fact]
    public void MassTransit_send_resolves_the_endpoint_address()
    {
        var send = Edge("M:Shop.Api.Messaging.StockClient.ReserveAsync(Shop.Domain.Order)", "mq:q:stock-reservations", EdgeKind.Publishes);
        Assert.Equal("send", send.Meta!["mode"]);
        Assert.Equal("ReserveStock", send.Meta["messageTypes"]);
        Edge("mq:q:stock-reservations", "M:Shop.Worker.Consumers.StockReservationConsumer.Consume(MassTransit.ConsumeContext{Shop.Domain.Events.ReserveStock})", EdgeKind.Consumes);
    }
}
