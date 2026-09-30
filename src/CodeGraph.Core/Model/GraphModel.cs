namespace CodeGraph.Core.Model;

public enum NodeKind
{
    /// <summary>A .sln/.slnx file; only present when a folder with several solutions is analyzed.</summary>
    Solution,
    Project,
    Package,
    Namespace,
    Type,
    Method,
    /// <summary>The message broker (RabbitMQ); parent of all exchanges and queues.</summary>
    Broker,
    Exchange,
    Queue,
}

public enum EdgeKind
{
    /// <summary>Method/property/event invocation or access.</summary>
    Calls,
    /// <summary>Member creates an instance of a type (<c>new T()</c>).</summary>
    Instantiates,
    /// <summary>Type derives from a base class (or interface extends interface).</summary>
    Inherits,
    /// <summary>Type implements an interface.</summary>
    Implements,
    /// <summary>Member overrides a base member or implements an interface member.</summary>
    Overrides,
    /// <summary>Member or type references another type (fields, parameters, locals, static access...).</summary>
    UsesType,
    /// <summary>Project references another project.</summary>
    ProjectRef,
    /// <summary>Project (or package) depends on a NuGet package.</summary>
    PackageRef,
    /// <summary>Member publishes/sends to an exchange (or directly to a queue).</summary>
    Publishes,
    /// <summary>Exchange routes to a queue.</summary>
    Binds,
    /// <summary>Queue is consumed by a member.</summary>
    Consumes,
    /// <summary>Exchange or queue carries a message type.</summary>
    Carries,
}

public sealed class GraphNode
{
    public required string Id { get; init; }
    public required NodeKind Kind { get; init; }
    public required string Name { get; init; }
    public string FullName { get; init; } = "";
    public string? ParentId { get; init; }
    public string? File { get; set; }
    public int? Line { get; set; }
    /// <summary>True for symbols that live outside the analyzed source (NuGet packages, the framework).</summary>
    public bool External { get; init; }
    public Dictionary<string, string> Meta { get; init; } = new();
}

public sealed class GraphEdge
{
    public required string Source { get; init; }
    public required string Target { get; init; }
    public required EdgeKind Kind { get; init; }
    public int Count { get; set; } = 1;
    /// <summary>Extra facts, e.g. messageTypes / routingKeys on messaging edges (comma-separated values).</summary>
    public Dictionary<string, string>? Meta { get; set; }

    /// <summary>Adds a value to a comma-separated meta list, ignoring duplicates.</summary>
    public void AddMeta(string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        Meta ??= new();
        if (!Meta.TryGetValue(key, out var existing) || existing.Length == 0) Meta[key] = value;
        else if (!existing.Split(", ").Contains(value)) Meta[key] = existing + ", " + value;
    }
}

public sealed class GraphDocument
{
    public required string SourcePath { get; init; }
    public required string Name { get; init; }
    public DateTime AnalyzedAtUtc { get; init; } = DateTime.UtcNow;
    public double DurationSeconds { get; set; }
    /// <summary>Seconds spent per analysis phase (discover, load, compile, diagnostics, walk, messaging).</summary>
    public Dictionary<string, double> Timings { get; set; } = new();
    /// <summary>True when this result was loaded from the disk cache instead of being analyzed now.</summary>
    public bool FromCache { get; set; }
    public List<GraphNode> Nodes { get; init; } = new();
    public List<GraphEdge> Edges { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
    public Dictionary<string, int> Stats { get; init; } = new();
}
