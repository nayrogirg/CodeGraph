using CodeGraph.Core.Model;

namespace CodeGraph.Core.Tests;

/// <summary>Analyzes samples/Microservices as a folder: two solutions sharing a project, plus a project outside any solution.</summary>
public sealed class MicroservicesFixture : IAsyncLifetime
{
    public GraphDocument Graph { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var sample = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(SampleShopFixture.SamplePath))!, "Microservices");
        Graph = await new SolutionAnalyzer().AnalyzeAsync(sample);
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

public class MultiSolutionTests : IClassFixture<MicroservicesFixture>
{
    private readonly GraphDocument _graph;

    public MultiSolutionTests(MicroservicesFixture fixture) => _graph = fixture.Graph;

    private GraphNode Node(string id) =>
        _graph.Nodes.SingleOrDefault(n => n.Id == id)
        ?? throw new Xunit.Sdk.XunitException($"Node '{id}' not found. Projects/solutions: {string.Join(", ", _graph.Nodes.Where(n => n.Kind is NodeKind.Project or NodeKind.Solution).Select(n => n.Id))}");

    private bool HasEdge(string source, string target, EdgeKind kind) =>
        _graph.Edges.Any(e => e.Source == source && e.Target == target && e.Kind == kind);

    [Fact]
    public void Every_solution_becomes_a_node_and_owns_its_projects()
    {
        Assert.Equal(NodeKind.Solution, Node("sln:Billing").Kind);
        Assert.Equal(NodeKind.Solution, Node("sln:Shipping").Kind);
        Assert.Equal("sln:Billing", Node("proj:Billing/Api").ParentId);
        Assert.Equal("sln:Shipping", Node("proj:Shipping/Api").ParentId);
        Assert.Equal("sln:Shipping", Node("proj:Worker").ParentId);
    }

    [Fact]
    public void Clashing_project_names_are_prefixed_with_their_solution()
    {
        var projects = _graph.Nodes.Where(n => n.Kind == NodeKind.Project).Select(n => n.Name).Order().ToArray();
        Assert.Equal(["Billing/Api", "Contracts", "Seeder", "Shipping/Api", "Worker"], projects);
    }

    [Fact]
    public void Projects_shared_by_several_solutions_are_analyzed_once()
    {
        // Contracts is in both Billing.sln and Shipping.sln; it belongs to the first one found.
        Assert.Equal("sln:Billing", Node("proj:Contracts").ParentId);
        Assert.True(HasEdge("proj:Worker", "proj:Contracts", EdgeKind.ProjectRef));
        Assert.Equal("ns:Contracts:Billing.Contracts", Node("T:Billing.Contracts.InvoicePaid").ParentId);
    }

    [Fact]
    public void Projects_outside_any_solution_are_included()
    {
        var seeder = Node("proj:Seeder");
        Assert.Null(seeder.ParentId);
    }

    [Fact]
    public void Messaging_flows_connect_across_solutions()
    {
        Assert.True(HasEdge("M:Billing.Api.InvoiceService.PayAsync(System.Guid,System.Decimal)", "mq:x:billing", EdgeKind.Publishes));
        Assert.True(HasEdge("mq:x:billing", "mq:q:shipping-invoices", EdgeKind.Binds));
        Assert.Contains(_graph.Edges, e => e.Source == "mq:q:shipping-invoices" && e.Kind == EdgeKind.Consumes &&
                                           e.Target.StartsWith("M:Shipping.Worker.InvoicePaidConsumer.HandleBasicDeliverAsync"));
    }

    [Fact]
    public void Graph_is_named_after_the_folder()
    {
        Assert.Equal("Microservices", _graph.Name);
        Assert.All(_graph.Edges, e => Assert.Contains(_graph.Nodes, n => n.Id == e.Source));
    }
}
