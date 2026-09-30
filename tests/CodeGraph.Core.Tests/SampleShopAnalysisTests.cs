using CodeGraph.Core.Model;

namespace CodeGraph.Core.Tests;

public sealed class SampleShopFixture : IAsyncLifetime
{
    public GraphDocument Graph { get; private set; } = null!;

    public static string SamplePath
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CodeGraph.sln"))) dir = dir.Parent;
            if (dir is null) throw new InvalidOperationException("Could not locate the repository root.");
            return Path.Combine(dir.FullName, "samples", "SampleShop", "SampleShop.sln");
        }
    }

    public async Task InitializeAsync() => Graph = await new SolutionAnalyzer().AnalyzeAsync(SamplePath);

    public Task DisposeAsync() => Task.CompletedTask;
}

public class SampleShopAnalysisTests : IClassFixture<SampleShopFixture>
{
    private readonly GraphDocument _graph;

    public SampleShopAnalysisTests(SampleShopFixture fixture) => _graph = fixture.Graph;

    private bool HasEdge(string source, string target, EdgeKind kind) =>
        _graph.Edges.Any(e => e.Source == source && e.Target == target && e.Kind == kind);

    private GraphNode Node(string id) =>
        _graph.Nodes.SingleOrDefault(n => n.Id == id) ?? throw new Xunit.Sdk.XunitException(
            $"Node '{id}' not found. Similar: {string.Join(", ", _graph.Nodes.Where(n => n.Id.Contains(id.Split('.').Last().Split('(')[0])).Select(n => n.Id))}");

    [Fact]
    public void Loads_all_projects_without_errors()
    {
        var projects = _graph.Nodes.Where(n => n.Kind == NodeKind.Project).Select(n => n.Name).Order().ToArray();
        Assert.Equal(["Shop.Api", "Shop.Data", "Shop.Domain", "Shop.Worker"], projects);
        Assert.DoesNotContain(_graph.Warnings, w => w.Contains("compile error"));
    }

    [Fact]
    public void Project_references_are_edges()
    {
        Assert.True(HasEdge("proj:Shop.Api", "proj:Shop.Data", EdgeKind.ProjectRef));
        Assert.True(HasEdge("proj:Shop.Api", "proj:Shop.Domain", EdgeKind.ProjectRef));
        Assert.True(HasEdge("proj:Shop.Data", "proj:Shop.Domain", EdgeKind.ProjectRef));
    }

    [Fact]
    public void NuGet_packages_are_mapped()
    {
        Assert.True(HasEdge("proj:Shop.Data", "pkg:Newtonsoft.Json", EdgeKind.PackageRef));
        Assert.True(HasEdge("proj:Shop.Api", "pkg:Microsoft.Extensions.Logging.Abstractions", EdgeKind.PackageRef));

        // JsonConvert lives under the Newtonsoft.Json package node and is called from OrderSerializer.
        var jsonConvert = Node("T:Newtonsoft.Json.JsonConvert");
        Assert.Equal("pkg:Newtonsoft.Json", jsonConvert.ParentId);
        Assert.True(jsonConvert.External);
        Assert.Contains(_graph.Edges, e => e.Source == "M:Shop.Data.OrderSerializer.Serialize(Shop.Domain.Order)"
                                           && e.Target.StartsWith("M:Newtonsoft.Json.JsonConvert.SerializeObject") && e.Kind == EdgeKind.Calls);
    }

    [Fact]
    public void Namespaces_are_grouped_per_project()
    {
        Assert.Equal("proj:Shop.Domain", Node("ns:Shop.Domain:Shop.Domain.Pricing").ParentId);
        Assert.Equal("ns:Shop.Domain:Shop.Domain.Pricing", Node("T:Shop.Domain.Pricing.BulkDiscount").ParentId);
    }

    [Fact]
    public void Cross_project_calls_are_resolved()
    {
        Assert.True(HasEdge("M:Shop.Api.Services.OrderService.PlaceOrder(System.String,System.Collections.Generic.IEnumerable{System.ValueTuple{Shop.Domain.Product,System.Int32}})",
            "M:Shop.Domain.IOrderRepository.Save(Shop.Domain.Order)", EdgeKind.Calls));
        Assert.True(HasEdge("M:Shop.Api.Controllers.OrderController.Get(System.Guid)",
            "M:Shop.Domain.IOrderRepository.Find(System.Guid)", EdgeKind.Calls));
    }

    [Fact]
    public void Inheritance_and_interfaces()
    {
        Assert.True(HasEdge("T:Shop.Data.OrderRepository", "T:Shop.Domain.IOrderRepository", EdgeKind.Implements));
        Assert.True(HasEdge("T:Shop.Domain.Order", "T:Shop.Domain.EntityBase", EdgeKind.Inherits));
        Assert.True(HasEdge("T:Shop.Domain.Pricing.BulkDiscount", "T:Shop.Domain.Pricing.DiscountPolicy", EdgeKind.Inherits));
        Assert.True(HasEdge("T:Shop.Domain.Pricing.DiscountPolicy", "T:Shop.Domain.Pricing.IDiscountPolicy", EdgeKind.Implements));
    }

    [Fact]
    public void Overrides_and_interface_implementations_link_members()
    {
        Assert.True(HasEdge("M:Shop.Data.OrderRepository.Save(Shop.Domain.Order)", "M:Shop.Domain.IOrderRepository.Save(Shop.Domain.Order)", EdgeKind.Overrides));
        Assert.True(HasEdge("M:Shop.Domain.Pricing.BulkDiscount.Calculate(Shop.Domain.Order)", "M:Shop.Domain.Pricing.DiscountPolicy.Calculate(Shop.Domain.Order)", EdgeKind.Overrides));
    }

    [Fact]
    public void Calls_inside_lambdas_belong_to_the_enclosing_method()
    {
        const string repriceAll = "M:Shop.Api.Services.OrderService.RepriceAll(System.Collections.Generic.IEnumerable{Shop.Domain.Product},System.Decimal)";
        Assert.True(HasEdge(repriceAll, "M:Shop.Domain.Product.ChangePrice(System.Decimal)", EdgeKind.Calls));
        Assert.True(HasEdge(repriceAll, "P:Shop.Domain.Product.Price", EdgeKind.Calls));
    }

    [Fact]
    public void Anonymous_types_are_not_nodes_and_their_initializers_belong_to_the_member()
    {
        Assert.DoesNotContain(_graph.Nodes, n => n.Name.Contains("anonymous", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(_graph.Nodes, n => n.Kind == NodeKind.Namespace && n.Name == "<global>");
        // `new { order.Id, ... Total = order.Total() }` inside OrderSerializer.Serialize
        Assert.True(HasEdge("M:Shop.Data.OrderSerializer.Serialize(Shop.Domain.Order)", "M:Shop.Domain.Order.Total", EdgeKind.Calls));
    }

    [Fact]
    public void Object_creation_is_recorded()
    {
        Assert.True(HasEdge("M:Shop.Api.Composition.BuildController", "T:Shop.Data.OrderRepository", EdgeKind.Instantiates));
        Assert.True(HasEdge("M:Shop.Api.Services.OrderService.ChoosePolicy(Shop.Domain.Order)", "T:Shop.Domain.Pricing.BulkDiscount", EdgeKind.Instantiates));
    }

    [Fact]
    public void Source_nodes_have_locations()
    {
        var orderService = Node("T:Shop.Api.Services.OrderService");
        Assert.EndsWith("OrderService.cs", orderService.File);
        Assert.Equal(9, orderService.Line);
        Assert.False(orderService.External);
    }

    [Fact]
    public void All_edges_point_to_existing_nodes()
    {
        var ids = _graph.Nodes.Select(n => n.Id).ToHashSet();
        Assert.All(_graph.Edges, e => Assert.True(ids.Contains(e.Source) && ids.Contains(e.Target)));
        Assert.All(_graph.Nodes.Where(n => n.ParentId is not null), n => Assert.Contains(n.ParentId!, ids));
    }
}
