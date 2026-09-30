using System.Diagnostics;
using System.Runtime.CompilerServices;
using CodeGraph.Core.Model;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace CodeGraph.Core;

/// <summary>Loads a .sln/.slnx/.csproj with Roslyn and turns it into a <see cref="GraphDocument"/>.</summary>
public sealed class SolutionAnalyzer
{
    private static readonly object RegistrationLock = new();

    /// <summary>
    /// Registers the installed .NET SDK's MSBuild. Must run before any method that touches MSBuild/Workspace
    /// types is JIT-compiled, which is why the analysis itself lives in a separate, non-inlined method.
    /// </summary>
    public static void EnsureMsBuildRegistered()
    {
        lock (RegistrationLock)
        {
            if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();
        }
    }

    /// <param name="path">A .sln, .slnx or .csproj file, or a folder: every solution (and stray project) under it is analyzed into one graph.</param>
    public Task<GraphDocument> AnalyzeAsync(string path, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        EnsureMsBuildRegistered();
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (Directory.Exists(fullPath)) return AnalyzeFolderCoreAsync(fullPath, progress, ct);
        if (!File.Exists(fullPath)) throw new FileNotFoundException($"Solution, project or folder not found: {fullPath}", fullPath);
        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        if (ext is not (".sln" or ".slnx" or ".csproj"))
            throw new ArgumentException($"Expected a .sln, .slnx or .csproj file or a folder, got '{ext}'.", nameof(path));
        return AnalyzeCoreAsync(fullPath, progress, ct);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<GraphDocument> AnalyzeCoreAsync(string path, IProgress<string>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var warnings = new List<string>();
        using var workspace = CreateWorkspace(warnings);

        var run = new AnalysisRun(warnings, progress);
        progress?.Report($"Loading {Path.GetFileName(path)}...");
        Solution solution;
        using (run.Timer.Measure("load"))
            solution = path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                ? (await workspace.OpenProjectAsync(path, cancellationToken: ct)).Solution
                : await workspace.OpenSolutionAsync(path, cancellationToken: ct);

        await run.AddSolutionAsync(solution, solutionNodeId: null, ct);
        run.Complete();
        var doc = run.Build(path, Path.GetFileNameWithoutExtension(path));
        doc.DurationSeconds = Math.Round(sw.Elapsed.TotalSeconds, 2);
        return doc;
    }

    /// <summary>
    /// Analyzes every solution under a folder into one graph. Solutions are loaded one at a time (each workspace is
    /// disposed before the next) to keep memory bounded; shared projects are analyzed once.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<GraphDocument> AnalyzeFolderCoreAsync(string folder, IProgress<string>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var warnings = new List<string>();
        progress?.Report($"Scanning {folder} for solutions...");
        var discoverTimer = Stopwatch.StartNew();
        var layout = WorkspaceLayout.Discover(folder, warnings);
        if (layout.Solutions.Count == 0 && layout.OrphanProjects.Count == 0)
            throw new InvalidOperationException($"No .sln, .slnx or .csproj files found under {folder}.");

        var run = new AnalysisRun(warnings, progress, layout.ProjectNames, layout.AssemblyToProject);
        run.Timer.Add("discover", discoverTimer.Elapsed);

        // MSBuild loading dominates; each workspace evaluates in its own build host process, so a few solutions are
        // loaded ahead in parallel while earlier ones are analyzed (analysis itself stays in order).
        var loadAhead = Math.Clamp(Environment.ProcessorCount / 4, 2, 4);
        var loads = new Task<LoadedSolution>?[layout.Solutions.Count];
        void StartLoad(int index)
        {
            if (index < loads.Length) loads[index] ??= LoadSolutionAsync(layout.Solutions[index], ct);
        }
        for (var i = 0; i < loadAhead; i++) StartLoad(i);
        // Projects outside any solution load in parallel with the solutions and are analyzed last.
        var orphans = layout.OrphanProjects.Count > 0 ? LoadProjectsAsync(layout.OrphanProjects, ct) : null;

        try
        {
            for (var i = 0; i < layout.Solutions.Count; i++)
            {
                var solutionPath = layout.Solutions[i];
                progress?.Report($"Loading {Path.GetFileName(solutionPath)} ({i + 1}/{layout.Solutions.Count})...");
                LoadedSolution loaded;
                using (run.Timer.Measure("load")) loaded = await loads[i]!;
                loads[i] = null;
                StartLoad(i + loadAhead);
                using (loaded.Workspace)
                {
                    warnings.AddRange(loaded.Warnings);
                    if (loaded.Solution is null)
                    {
                        warnings.Add($"Could not load {Path.GetRelativePath(folder, solutionPath)}: {loaded.Error}");
                        continue;
                    }
                    await run.AddSolutionAsync(loaded.Solution, run.AddSolutionNode(solutionPath), ct);
                }
            }

            if (orphans is not null)
            {
                progress?.Report($"Loading {layout.OrphanProjects.Count} project(s) outside any solution...");
                LoadedSolution loaded;
                using (run.Timer.Measure("load")) loaded = await orphans;
                orphans = null;
                using (loaded.Workspace)
                {
                    warnings.AddRange(loaded.Warnings);
                    if (loaded.Solution is not null) await run.AddSolutionAsync(loaded.Solution, solutionNodeId: null, ct);
                }
            }
        }
        finally
        {
            // On failure/cancellation, don't leak workspaces (and their build host processes) that were loading ahead.
            foreach (var pending in loads.Append(orphans).OfType<Task<LoadedSolution>>())
                _ = pending.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Workspace.Dispose(); }, TaskScheduler.Default);
        }

        run.Complete();
        var doc = run.Build(folder, Path.GetFileName(folder));
        doc.DurationSeconds = Math.Round(sw.Elapsed.TotalSeconds, 2);
        return doc;
    }

    /// <summary>A solution loaded in its own workspace, with the warnings raised while loading it.</summary>
    private sealed record LoadedSolution(MSBuildWorkspace Workspace, Solution? Solution, string? Error, List<string> Warnings);

    private static async Task<LoadedSolution> LoadSolutionAsync(string path, CancellationToken ct)
    {
        // Own warning list: loads run concurrently with the analysis, which writes to the shared one.
        var warnings = new List<string>();
        var workspace = CreateWorkspace(warnings);
        try
        {
            return new LoadedSolution(workspace, await workspace.OpenSolutionAsync(path, cancellationToken: ct), null, warnings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new LoadedSolution(workspace, null, ex.Message, warnings);
        }
    }

    /// <summary>Loads projects that no solution includes into one workspace.</summary>
    private static async Task<LoadedSolution> LoadProjectsAsync(IReadOnlyList<string> projects, CancellationToken ct)
    {
        var warnings = new List<string>();
        var workspace = CreateWorkspace(warnings);
        foreach (var project in projects)
        {
            // Opening a project also loads its references, so it may already be part of the workspace.
            if (workspace.CurrentSolution.Projects.Any(p => string.Equals(p.FilePath, project, StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                await workspace.OpenProjectAsync(project, cancellationToken: ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (warnings) warnings.Add($"Could not load {project}: {ex.Message}");
            }
        }
        return new LoadedSolution(workspace, workspace.CurrentSolution, null, warnings);
    }

    private static MSBuildWorkspace CreateWorkspace(List<string> warnings)
    {
        var workspace = MSBuildWorkspace.Create();
        workspace.RegisterWorkspaceFailedHandler(e =>
        {
            lock (warnings) warnings.Add($"{e.Diagnostic.Kind}: {e.Diagnostic.Message}");
        });
        return workspace;
    }
}
