using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeGraph.Core.Model;

namespace CodeGraph.Core;

/// <summary>
/// Caches analysis results per path, in memory and on disk, and re-analyzes only when source/project files changed.
/// The disk cache makes a restarted server show an unchanged code base immediately.
/// Analyses are serialized: MSBuildWorkspace loading is heavy and not worth running concurrently.
/// </summary>
public sealed class AnalysisCache
{
    private static readonly HashSet<string> WatchedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".sln", ".slnx", ".props", ".targets", ".json",
    };
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", ".idea", "node_modules", "packages", "artifacts", "TestResults",
    };

    /// <summary>Changes with every build of the analyzer, so results from older analyzer versions are never reused.</summary>
    private static readonly string AnalyzerVersion = typeof(AnalysisCache).Assembly.ManifestModule.ModuleVersionId.ToString("N");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SolutionAnalyzer _analyzer;
    private readonly string? _diskCacheDirectory;
    private readonly ConcurrentDictionary<string, (string Stamp, GraphDocument Graph)> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="diskCacheDirectory">Where analyses are persisted; null disables the disk cache.</param>
    public AnalysisCache(SolutionAnalyzer analyzer, string? diskCacheDirectory = null)
    {
        _analyzer = analyzer;
        _diskCacheDirectory = diskCacheDirectory;
    }

    public static string DefaultDiskCacheDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodeGraph", "cache");

    public GraphDocument? TryGet(string path) =>
        _entries.TryGetValue(Path.GetFullPath(path), out var entry) ? entry.Graph : null;

    public async Task<GraphDocument> GetOrAnalyzeAsync(string path, bool force = false, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        await _gate.WaitAsync(ct);
        try
        {
            progress?.Report("Checking for changes...");
            var stamp = Fingerprint(fullPath);
            if (!force)
            {
                if (_entries.TryGetValue(fullPath, out var entry) && entry.Stamp == stamp) return entry.Graph;
                if (await ReadDiskAsync(fullPath, stamp, ct) is { } cached)
                {
                    _entries[fullPath] = (stamp, cached);
                    return cached;
                }
            }

            var graph = await _analyzer.AnalyzeAsync(fullPath, progress, ct);
            _entries[fullPath] = (stamp, graph);
            await WriteDiskAsync(fullPath, stamp, graph);
            return graph;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------------------------------------------------------------- disk cache

    private sealed record DiskEntry(string Version, string Stamp, GraphDocument Graph);

    private string? CacheFile(string fullPath)
    {
        if (_diskCacheDirectory is null) return null;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullPath.ToLowerInvariant())))[..32];
        return Path.Combine(_diskCacheDirectory, key + ".json");
    }

    private async Task<GraphDocument?> ReadDiskAsync(string fullPath, string stamp, CancellationToken ct)
    {
        if (CacheFile(fullPath) is not { } file || !File.Exists(file)) return null;
        try
        {
            await using var stream = File.OpenRead(file);
            var entry = await JsonSerializer.DeserializeAsync<DiskEntry>(stream, Json, ct);
            if (entry is null || entry.Version != AnalyzerVersion || entry.Stamp != stamp) return null;
            entry.Graph.FromCache = true;
            return entry.Graph;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null; // a broken cache file just means analyzing again
        }
    }

    private async Task WriteDiskAsync(string fullPath, string stamp, GraphDocument graph)
    {
        if (CacheFile(fullPath) is not { } file) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temp = file + ".tmp";
            await using (var stream = File.Create(temp))
                await JsonSerializer.SerializeAsync(stream, new DiskEntry(AnalyzerVersion, stamp, graph), Json);
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Caching is an optimization; never fail an analysis because of it.
        }
    }

    // ---------------------------------------------------------------- change detection

    /// <summary>
    /// Hash of path, size and write time of every source/project file under the solution (or folder), so edits,
    /// additions, deletions and renames all invalidate the cache. bin/obj/.git/node_modules are never entered.
    /// </summary>
    private static string Fingerprint(string path)
    {
        var root = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (root is not null && Directory.Exists(root))
        {
            var files = new List<string>();
            Collect(root, files);
            files.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                var info = new FileInfo(file);
                hash.AppendData(Encoding.UTF8.GetBytes($"{Path.GetRelativePath(root, file)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}\n"));
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void Collect(string directory, List<string> files)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory))
                if (WatchedExtensions.Contains(Path.GetExtension(file))) files.Add(file);
            foreach (var sub in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(sub);
                if (!SkippedDirectories.Contains(name) && !name.StartsWith('.')) Collect(sub, files);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
        }
    }
}
