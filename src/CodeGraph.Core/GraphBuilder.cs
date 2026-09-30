using CodeGraph.Core.Model;

namespace CodeGraph.Core;

/// <summary>Accumulates nodes and de-duplicated, counted edges.</summary>
internal sealed class GraphBuilder
{
    private readonly Dictionary<string, GraphNode> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<(string, string, EdgeKind), GraphEdge> _edges = new();

    public bool TryGet(string id, out GraphNode node) => _nodes.TryGetValue(id, out node!);

    public GraphNode GetOrAdd(string id, Func<GraphNode> factory)
    {
        if (!_nodes.TryGetValue(id, out var node))
        {
            node = factory();
            _nodes[id] = node;
        }
        return node;
    }

    public GraphEdge? AddEdge(string? source, string? target, EdgeKind kind, int count = 1)
    {
        if (source is null || target is null || source == target) return null;
        var key = (source, target, kind);
        if (_edges.TryGetValue(key, out var edge)) edge.Count += count;
        else _edges[key] = edge = new GraphEdge { Source = source, Target = target, Kind = kind, Count = count };
        return edge;
    }

    public GraphDocument Build(string sourcePath, string name, List<string> warnings)
    {
        var edges = _edges.Values
            .Where(e => _nodes.ContainsKey(e.Source) && _nodes.ContainsKey(e.Target))
            .OrderBy(e => e.Source, StringComparer.Ordinal).ThenBy(e => e.Target, StringComparer.Ordinal)
            .ToList();
        var nodes = _nodes.Values.OrderBy(n => n.Kind).ThenBy(n => n.Id, StringComparer.Ordinal).ToList();

        var stats = new Dictionary<string, int>();
        foreach (var g in nodes.Where(n => !n.External).GroupBy(n => n.Kind)) stats[g.Key.ToString()] = g.Count();
        stats["External"] = nodes.Count(n => n.External);
        foreach (var g in edges.GroupBy(e => e.Kind)) stats[g.Key.ToString()] = g.Count();

        return new GraphDocument
        {
            SourcePath = sourcePath,
            Name = name,
            Nodes = nodes,
            Edges = edges,
            Warnings = warnings,
            Stats = stats,
        };
    }
}
