using System.Text.Json;

namespace CodeGraph.Core;

/// <summary>Reads the parts of obj/project.assets.json needed to map references to NuGet packages.</summary>
internal sealed class NuGetAssets
{
    private readonly List<string> _packageFolders = new();
    /// <summary>"newtonsoft.json/13.0.3" (package-folder relative path) → (Newtonsoft.Json, 13.0.3).</summary>
    private readonly Dictionary<string, (string Name, string Version)> _librariesByPath = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> Versions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> DirectDependencies { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, HashSet<string>> PackageDependencies { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static NuGetAssets? Load(string projectFilePath)
    {
        var dir = Path.GetDirectoryName(projectFilePath);
        if (dir is null) return null;
        var file = Path.Combine(dir, "obj", "project.assets.json");
        if (!File.Exists(file)) return null;

        using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
        var root = doc.RootElement;
        var assets = new NuGetAssets();

        if (root.TryGetProperty("packageFolders", out var folders))
            foreach (var f in folders.EnumerateObject())
                assets._packageFolders.Add(Normalize(f.Name));

        if (root.TryGetProperty("libraries", out var libraries))
            foreach (var lib in libraries.EnumerateObject())
            {
                if (!IsPackage(lib.Value)) continue;
                var (name, version) = SplitKey(lib.Name);
                assets.Versions[name] = version;
                if (lib.Value.TryGetProperty("path", out var path) && path.GetString() is { } p)
                    assets._librariesByPath[p.Replace('\\', '/').TrimEnd('/')] = (name, version);
            }

        if (root.TryGetProperty("project", out var project) &&
            project.TryGetProperty("frameworks", out var frameworks))
            foreach (var fw in frameworks.EnumerateObject())
                if (fw.Value.TryGetProperty("dependencies", out var deps))
                    foreach (var d in deps.EnumerateObject())
                        assets.DirectDependencies.Add(d.Name);

        if (root.TryGetProperty("targets", out var targets))
            foreach (var target in targets.EnumerateObject())
                foreach (var lib in target.Value.EnumerateObject())
                {
                    if (!IsPackage(lib.Value)) continue;
                    var (name, _) = SplitKey(lib.Name);
                    if (!assets.PackageDependencies.TryGetValue(name, out var set))
                        assets.PackageDependencies[name] = set = new(StringComparer.OrdinalIgnoreCase);
                    if (lib.Value.TryGetProperty("dependencies", out var deps))
                        foreach (var d in deps.EnumerateObject()) set.Add(d.Name);
                }

        return assets;
    }

    /// <summary>Returns the package a referenced assembly file belongs to, if it lives in a NuGet package folder.</summary>
    public string? PackageForPath(string assemblyPath)
    {
        var path = Normalize(assemblyPath);
        foreach (var folder in _packageFolders)
        {
            if (!path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) continue;
            var segments = path[folder.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2) continue;
            return _librariesByPath.TryGetValue($"{segments[0]}/{segments[1]}", out var lib) ? lib.Name : segments[0];
        }
        return null;
    }

    private static bool IsPackage(JsonElement lib) =>
        lib.TryGetProperty("type", out var type) && type.GetString() == "package";

    private static (string Name, string Version) SplitKey(string key)
    {
        var slash = key.IndexOf('/');
        return slash < 0 ? (key, "") : (key[..slash], key[(slash + 1)..]);
    }

    private static string Normalize(string path)
    {
        var p = path.Replace('\\', '/');
        return p.EndsWith('/') ? p : p + "/";
    }
}
