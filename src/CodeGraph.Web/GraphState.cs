using CodeGraph.Core;
using CodeGraph.Core.Model;

namespace CodeGraph.Web;

/// <summary>The currently displayed graph, plus progress of a running analysis.</summary>
public sealed class GraphState
{
    private readonly AnalysisCache _cache;
    private readonly ILogger<GraphState> _logger;
    private volatile string? _progress;
    private volatile string? _analyzingPath;
    private volatile string? _lastError;

    public GraphState(AnalysisCache cache, ILogger<GraphState> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public GraphDocument? Current { get; private set; }

    public object Status() => new
    {
        busy = _analyzingPath is not null,
        path = _analyzingPath ?? Current?.SourcePath,
        message = _progress,
        error = _lastError,
        hasGraph = Current is not null,
    };

    public async Task<GraphDocument> AnalyzeAsync(string path, bool force, CancellationToken ct)
    {
        _analyzingPath = Path.GetFullPath(path);
        _lastError = null;
        var progress = new Progress<string>(message =>
        {
            _progress = message;
            _logger.LogInformation("{Message}", message);
        });
        try
        {
            var graph = await _cache.GetOrAnalyzeAsync(path, force, progress, ct);
            Current = graph;
            _logger.LogInformation("Analyzed {Name}: {Nodes} nodes, {Edges} edges in {Seconds}s",
                graph.Name, graph.Nodes.Count, graph.Edges.Count, graph.DurationSeconds);
            return graph;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            throw;
        }
        finally
        {
            _analyzingPath = null;
            _progress = null;
        }
    }

    /// <summary>
    /// Returns lines around a node's declaration. Only files that are part of the analyzed graph can be read,
    /// so this endpoint cannot be used to read arbitrary files from disk.
    /// </summary>
    public SourceSnippet? ReadSource(string nodeId, int context)
    {
        var node = Current?.Nodes.FirstOrDefault(n => n.Id == nodeId);
        if (node?.File is not { } file || !File.Exists(file)) return null;

        var lines = File.ReadAllLines(file);
        var line = Math.Clamp(node.Line ?? 1, 1, Math.Max(1, lines.Length));
        var start = Math.Max(1, line - context / 3);
        var end = Math.Min(lines.Length, line + context * 2);
        return new SourceSnippet(file, line, start, lines[(start - 1)..end]);
    }
}

public sealed record SourceSnippet(string File, int Line, int StartLine, string[] Lines);
