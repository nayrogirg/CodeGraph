using System.Text.Json.Serialization;
using CodeGraph.Core;
using CodeGraph.Web;

// MSBuild must be located before any Roslyn workspace type is loaded.
SolutionAnalyzer.EnsureMsBuildRegistered();

// The content root is the build output, not the working directory, so the UI is found wherever the tool is started from.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.WebHost.UseStaticWebAssets(); // serves wwwroot straight from the project when running from bin/
if (string.IsNullOrEmpty(builder.Configuration["urls"]) && string.IsNullOrEmpty(builder.Configuration["ASPNETCORE_URLS"]))
    builder.WebHost.UseUrls("http://localhost:5080"); // loopback only: the API reads local source files

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddSingleton<SolutionAnalyzer>();
// Analyses persist on disk so a restarted server shows an unchanged code base immediately; --disk-cache false disables it.
builder.Services.AddSingleton(sp => new AnalysisCache(
    sp.GetRequiredService<SolutionAnalyzer>(),
    builder.Configuration.GetValue("disk-cache", true) ? AnalysisCache.DefaultDiskCacheDirectory : null));
builder.Services.AddSingleton<GraphState>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // Local dev tool: always revalidate so UI updates show up without a hard refresh.
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});

var api = app.MapGroup("/api");

api.MapGet("/status", (GraphState state) => Results.Ok(state.Status()));

api.MapGet("/graph", (GraphState state) =>
    state.Current is { } graph ? Results.Ok(graph) : Results.NoContent());

api.MapPost("/analyze", async (AnalyzeRequest request, GraphState state, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Path))
        return Results.Problem("A path to a .sln, .slnx, .csproj or folder is required.", statusCode: 400);
    try
    {
        return Results.Ok(await state.AnalyzeAsync(request.Path.Trim().Trim('"'), request.Force, ct));
    }
    catch (Exception ex) when (ex is FileNotFoundException or ArgumentException or InvalidOperationException)
    {
        return Results.Problem(ex.Message, statusCode: 400);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        return Results.Problem($"{ex.GetType().Name}: {ex.Message}", statusCode: 500);
    }
});

api.MapGet("/source", (string nodeId, int? context, GraphState state) =>
    state.ReadSource(nodeId, Math.Clamp(context ?? 12, 2, 200)) is { } snippet
        ? Results.Ok(snippet)
        : Results.NotFound());

var startupPath = builder.Configuration["path"];
if (!string.IsNullOrWhiteSpace(startupPath))
{
    var state = app.Services.GetRequiredService<GraphState>();
    _ = Task.Run(async () =>
    {
        try { await state.AnalyzeAsync(startupPath, force: false, CancellationToken.None); }
        catch (Exception ex) { app.Logger.LogError(ex, "Initial analysis of {Path} failed", startupPath); }
    });
}

app.Lifetime.ApplicationStarted.Register(() =>
    app.Logger.LogInformation("CodeGraph UI: {Urls}", string.Join(", ", app.Urls)));

app.Run();

public sealed record AnalyzeRequest(string Path, bool Force = false);
