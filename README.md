# CodeGraph

Interactive browser visualization of a .NET solution: projects, NuGet packages, namespaces, classes and method calls.
It analyzes C# source with Roslyn (MSBuildWorkspace) and serves the UI from a local ASP.NET Core server.

## Run

```powershell
dotnet run --project src/CodeGraph.Web -- --path samples/SampleShop/SampleShop.sln
```

Open http://localhost:5080. You can also start it without `--path` and enter any `.sln`, `.slnx` or `.csproj` in the top bar.

### Speed and caching

- Results are cached in memory and on disk (`%LOCALAPPDATA%\CodeGraph\cache`), keyed by a fingerprint of every source/project
  file (path, size, timestamp). An unchanged code base loads instantly, even after a restart; any edit, addition or deletion
  triggers a fresh analysis. Check **fresh** to force one; start with `--disk-cache false` to disable the disk cache.
- Hover the stats in the top bar for a per-phase timing breakdown (load, compile, bind, walk...).
- Solutions in a folder are loaded several at a time, compilations are created in parallel, and method bodies are bound on all CPU cores.

### Whole code base (several solutions)

Pass a **folder** instead of a solution (`--path C:\src\MyCompany`, or type it in the top bar). CodeGraph finds every `.sln`/`.slnx`
under it (skipping `bin`, `obj`, `.git`, `node_modules`…) and every `.csproj` not in a solution, and merges them into one graph:

- Each solution is a box around its projects in the Architecture view; projects outside any solution stand alone.
- Solutions are loaded one at a time; a project shared by several solutions is analyzed once (it belongs to the first solution found).
- Projects with the same file name get their solution as a prefix (`Billing/Api`, `Shipping/Api`).
- Types from projects in the folder count as your own code even when another service references them as a compiled/NuGet package,
  so shared contracts and RabbitMQ flows connect across services.
The target solution must be restored (`dotnet restore`) so that NuGet packages can be resolved.

## Views

| Tab | Shows |
| --- | --- |
| Architecture | Projects and NuGet packages with project/package references and rolled-up code dependencies |
| Namespaces | Namespaces grouped by project, with dependencies between them |
| Classes | Types grouped by project and namespace: inheritance, interfaces, calls, `new`, type usage |
| Methods | Call graph around a class or method: callers and/or callees, 1–5 hops (follows messages through exchanges and queues) |
| Messaging | RabbitMQ flows: publishers → exchanges → queues → consumers, with message types, grouped by project |

### RabbitMQ detection

Found statically in source; nothing connects to a broker.

- **RabbitMQ.Client** (v6 `IModel` and v7 `IChannel`): `ExchangeDeclare`, `QueueDeclare`, `QueueBind`, `BasicPublish`, `BasicConsume` (and `*Async`).
  Publishing to the default exchange (`""`) is drawn straight to the queue named by the routing key.
- **MassTransit**: `IConsumer<T>` classes, `Publish`, `Send` (via `GetSendEndpoint(new Uri("queue:…"))` or `EndpointConvention.Map`),
  `ReceiveEndpoint("queue", e => e.ConfigureConsumer<X>())`, and the endpoint name formatter convention for consumers without an explicit endpoint.
  Exchanges follow MassTransit's `Namespace:MessageType` naming.
- **In-house wrappers** (e.g. `_queueClient.PublishMessage(msg, ExchangeNames.Brand, RoutingKey.Brand.Update)`): when a method passes its
  own parameters to `BasicPublish`/`BasicConsume`/`QueueBind`/`QueueDeclare`/`ExchangeDeclare`, the names are taken from each call site,
  including calls through interfaces and wrappers of wrappers. A handler passed as a method group becomes the consumer.
  Wrappers from compiled packages are recognized by convention: `Publish*`/`Send*`/`Subscribe*`/`Consume*` methods with
  exchange/queue parameters or on queue/bus/broker types.
- Names are read from constants, enum members, `const`/readonly fields and literal initializers. Names computed at run time (config, interpolation) appear
  as dashed nodes labelled with the source expression.

- **Double-click** a node to drill down (project → namespaces → classes → methods). **Backspace** or the browser Back button goes back.
- **Click** a node to see its details: signature, file:line, incoming and outgoing relationships, members and a source snippet.
- **`/`** focuses search. Filters on the left toggle relationship kinds, external packages, the .NET framework and public-only API.
- **PNG** exports the current view.

## Layout

- `src/CodeGraph.Core`: Roslyn analyzer that produces the graph (`SolutionAnalyzer`, `AnalysisRun`, `NuGetAssets`, `AnalysisCache`).
- `src/CodeGraph.Web`: minimal API (`/api/analyze`, `/api/graph`, `/api/status`, `/api/source`) and the static UI in `wwwroot` (Cytoscape.js + ELK, vendored).
- `samples/SampleShop`: demo solution with three projects and two NuGet packages.
- `tests/CodeGraph.Core.Tests`: analyzer tests against SampleShop (`dotnet test`).

The server listens on localhost only. `/api/source` returns only files that belong to the analyzed solution.
