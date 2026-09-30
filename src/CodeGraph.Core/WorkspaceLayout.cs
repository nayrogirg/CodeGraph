using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CodeGraph.Core;

/// <summary>
/// The solutions and projects found under a folder, read from the .sln/.slnx/.csproj files themselves
/// (no MSBuild), so names can be assigned before any solution is loaded.
/// </summary>
internal sealed partial class WorkspaceLayout
{
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", ".idea", "node_modules", "packages", "artifacts", "TestResults",
    };

    /// <summary>Solution files, one per directory + name (.sln preferred over an equivalent .slnx).</summary>
    public List<string> Solutions { get; } = new();
    /// <summary>C# projects that no discovered solution includes.</summary>
    public List<string> OrphanProjects { get; } = new();
    /// <summary>Project file → display name; clashing names get their solution (or folder) as a prefix.</summary>
    public Dictionary<string, string> ProjectNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Assembly name → project display name.</summary>
    public Dictionary<string, string> AssemblyToProject { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static WorkspaceLayout Discover(string root, List<string> warnings)
    {
        var layout = new WorkspaceLayout();
        var solutionFiles = new List<string>();
        var projectFiles = new List<string>();
        Walk(root, solutionFiles, projectFiles);

        // Where both X.sln and X.slnx exist they describe the same solution.
        foreach (var group in solutionFiles.GroupBy(f => Path.Combine(Path.GetDirectoryName(f)!, Path.GetFileNameWithoutExtension(f)), StringComparer.OrdinalIgnoreCase))
            layout.Solutions.Add(group.OrderBy(f => f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)).First());
        layout.Solutions.Sort(StringComparer.OrdinalIgnoreCase);

        // Which solution first lists each project (used to name clashing projects and to spot orphans).
        var solutionOfProject = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var solution in layout.Solutions)
        {
            try
            {
                foreach (var project in ReadSolutionProjects(solution))
                    solutionOfProject.TryAdd(project, solution);
            }
            catch (Exception ex)
            {
                warnings.Add($"Could not read {solution}: {ex.Message}");
            }
        }
        layout.OrphanProjects.AddRange(projectFiles.Where(p => !solutionOfProject.ContainsKey(p)).Order(StringComparer.OrdinalIgnoreCase));

        var allProjects = solutionOfProject.Keys.Concat(layout.OrphanProjects).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var group in allProjects.GroupBy(p => Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase))
        {
            var clash = group.Count() > 1;
            foreach (var project in group)
            {
                var name = Path.GetFileNameWithoutExtension(project);
                if (clash)
                {
                    var prefix = solutionOfProject.TryGetValue(project, out var sln)
                        ? Path.GetFileNameWithoutExtension(sln)
                        : Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(project))) ?? "";
                    name = $"{prefix}/{name}";
                }
                // Still ambiguous (same solution name in several folders): fall back to the relative folder.
                if (layout.ProjectNames.ContainsValue(name))
                    name = Path.GetRelativePath(root, Path.GetDirectoryName(project)!).Replace('\\', '/');
                layout.ProjectNames[project] = name;

                var assembly = ReadAssemblyName(project);
                if (!layout.AssemblyToProject.TryAdd(assembly, name))
                    warnings.Add($"Projects {layout.AssemblyToProject[assembly]} and {name} both build assembly '{assembly}'; their types may be merged.");
            }
        }
        return layout;
    }

    private static void Walk(string directory, List<string> solutions, List<string> projects)
    {
        IEnumerable<string> files, directories;
        try
        {
            files = Directory.EnumerateFiles(directory);
            directories = Directory.EnumerateDirectories(directory);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return;
        }
        foreach (var file in files)
        {
            var ext = Path.GetExtension(file);
            if (ext.Equals(".sln", StringComparison.OrdinalIgnoreCase) || ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase)) solutions.Add(Path.GetFullPath(file));
            else if (ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase)) projects.Add(Path.GetFullPath(file));
        }
        foreach (var sub in directories)
            if (!SkippedDirectories.Contains(Path.GetFileName(sub)) && !Path.GetFileName(sub).StartsWith('.'))
                Walk(sub, solutions, projects);
    }

    /// <summary>C# project paths listed in a .sln (text format) or .slnx (XML).</summary>
    private static IEnumerable<string> ReadSolutionProjects(string solution)
    {
        var dir = Path.GetDirectoryName(solution)!;
        IEnumerable<string> relative = solution.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
            ? XDocument.Load(solution).Descendants().Where(e => e.Name.LocalName == "Project")
                .Select(e => (string?)e.Attribute("Path")).OfType<string>()
            : SlnProjectLine().Matches(File.ReadAllText(solution)).Select(m => m.Groups[1].Value);
        return relative
            .Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.GetFullPath(Path.Combine(dir, p.Replace('\\', Path.DirectorySeparatorChar))));
    }

    private static string ReadAssemblyName(string project)
    {
        try
        {
            var match = AssemblyNameElement().Match(File.ReadAllText(project));
            if (match.Success && !match.Groups[1].Value.Contains("$(", StringComparison.Ordinal)) return match.Groups[1].Value.Trim();
        }
        catch (IOException)
        {
        }
        return Path.GetFileNameWithoutExtension(project);
    }

    // Project("{FAE04EC0-...}") = "Name", "relative\path\Name.csproj", "{GUID}"
    [GeneratedRegex("""^Project\("\{[^}]+\}"\)\s*=\s*"[^"]*",\s*"([^"]+)"\s*,""", RegexOptions.Multiline)]
    private static partial Regex SlnProjectLine();

    [GeneratedRegex("<AssemblyName>([^<]+)</AssemblyName>")]
    private static partial Regex AssemblyNameElement();
}
