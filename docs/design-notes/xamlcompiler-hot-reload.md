# XAML compiler: hot reload support

A hot reload tool runs the real XAML compiler on every save, so the running app gets exactly what a build would
produce. Two pieces make that practical:

1. **The compiler service**: `XamlCompiler.exe --serve` keeps the compiler loaded and compiles only the pages that
   changed.
2. **Hot reload code generation** (opt-in, C#): the generated code is shaped so that .NET hot reload can apply each
   recompile to the running app.

Builds that don't ask for either are unchanged.

## The compiler service

```
XamlCompiler.exe --serve <input.json>
```

`input.json` is the pass 2 input a build already writes next to the generated files (`obj\...\input.json`, written by
`dotnet build`). The service reads one JSON request per line on stdin and writes one JSON response per line on stdout.

```json
{"Id":1,"Pass":2,"Files":["Views\\MainPage.xaml"],"LocalAssemblyImage":null}
```

| Field | Meaning |
|---|---|
| `Pass` | 1 or 2, as in a build: pass 1 generates the code the project's C# compiles against (`.g.i.cs`), pass 2 the rest (`.g.cs`, edited `.xaml`, `.xbf`) |
| `Files` | The XAML files to compile, relative to the project or full paths. A file the project doesn't list yet is compiled as a new page |
| `LocalAssemblyImage` | Pass 2, optional: the project's assembly as base64, for example a metadata-only emit of C# the build hasn't compiled yet. Without it the service reads the build's |

```json
{"Id":1,"Success":true,"ElapsedMs":92.4,"Files":{"C:\\app\\obj\\...\\MainPage.g.cs":"..."},"Xbf":{"C:\\app\\obj\\...\\MainPage.xbf":"<base64>"},"Diagnostics":[]}
```

- `Files` and `Xbf` hold the requested pages' generated files, keyed by where the build puts them. The service writes
  them to a folder of its own and never to the build's folders.
- `Diagnostics` are the errors and warnings a build would report (`Type`, `ErrorCode`, `File`, `LineNumber`,
  `ColumnNumber`, `Message`).
- A pass 2 also returns `XamlTypeInfo.g.cs` when it had to generate it: the first pass 2, and any compile whose pages use
  a type or member the app's `XamlTypeInfo` doesn't have. Generating it means compiling every page, as a build does
  (about 2 s warm on WinUI Gallery); other compiles take about 0.1 s per pass.

### Running next to a build

The service never writes the build's files. It loads assemblies into memory instead of keeping them open, its own
output folder is under `%TEMP%`, and the only thing it writes in `obj` is its session state (below). So a build can run
while the service is running. A build starts a new session: the app is restarted from it.

## Hot reload code generation

`EnableXamlHotReloadCodegen=true` (an MSBuild property, off by default and not tied to a configuration) turns it on for
C# projects. A hot reload tool sets it on its own builds; the service uses whatever the build's `input.json` says.

| Change | Why |
|---|---|
| The pass 1 file is `<Page>.i.g.cs` instead of `<Page>.g.i.cs` | .NET hot reload ignores files named `*.g.i.cs` (it treats them as WPF design-time files), and `x:Name` fields are declared there |
| Connection IDs stay with their elements | Generated fields and Bindings classes are named after connection IDs, and .NET hot reload can't change a field's type. A build numbers elements in document order as before and records a key for each (see below); the service gives a known element its recorded ID and a new one the next unused ID |
| No `= false` initializers on static fields | A static initializer adds a static constructor, and Roslyn fails to update the static constructor of a class that an earlier update added |
| `InitializeComponent` can run again | A tool can reset `_contentLoaded` and call it again on a live object: the previous compiled bindings stop (`StopTracking`) and the new ones start (`Update`), since a loaded element doesn't raise `Loading` again. Replaced Bindings objects ignore later `Loading`/`Activated` events |
| `XamlTypeInfo` has a `MetadataUpdateHandler` | After an update, .NET hot reload calls `XamlTypeInfoProvider.ClearCache`; the provider then forgets what it looked up, so types and members an update added are found |
| The service keeps the `XamlTypeInfo` type and member order | New types and members go at the end, so adding one doesn't renumber the others, and a fresh service numbers member accessors (`get_88_...`) as the app's build did. That order depends on which pages a build regenerated, so it can't be recomputed by compiling every page |
| The service keeps members the new code no longer has | .NET hot reload can't remove a field or a type, and Roslyn fails to remove a method an earlier update added. Such members stay, with empty bodies, inside a `#pragma warning disable` block marked "Kept by XAML hot reload" |

A build with the property also writes `obj\...\XamlHotReload\`: one `<hash>.ids` file per page (connection ID keys),
`typeinfo.txt` and `members.txt` (the type and member order), and `code\<hash>.cs`, a copy of each generated C# file the build compiled into the app.
The service takes the members the app has from those copies, never from `<Page>.g.cs`: the next build's pass 1 empties
that file, and an editor's design-time builds into the same folder rewrite it. A build only writes these files and never
reads them, so its output doesn't depend on earlier builds. Turning the property on or off regenerates every page.

The build also writes `build.txt`, a new ID per build. After every compile the service writes `XamlHotReload\session\`:
everything the running app depends on that the session changed (the IDs it gave new elements, the generated code the app
now has, the type and member order and contents of the `XamlTypeInfo` it returned, the pages it added), each tagged with
the `build.txt` it continues. A service started again on the same build (after a crash or a restart) carries on exactly
where the previous one stopped; after a new build, the session files are ignored.

### What a host does

- Start the service after a build of the project with the property, from that build's `input.json`. An editor's
  design-time build rewrites `input.json` with its own pass 1 inputs, so take a copy when the build finishes; the
  service answers every request with an error if it was started from inputs that aren't a build's pass 2.
- On a save: pass 1 for the changed pages, compile the C# (the tool's own Roslyn workspace), pass 2 with the new
  assembly image, then apply the C# update and hand the `.xbf` to WinUI.
- After applying an update, call the app's `MetadataUpdateHandler`s (`ClearCache`), as `dotnet watch` does.
- After restarting the app from a new build, nothing: the service notices the build's new `build.txt`.
- After the service exits or is killed, start another on the same build's inputs; it continues the session.

### Limits

- A whole compile of every page is needed whenever `XamlTypeInfo` must change.
- Kept members only exist between builds; a build is a new starting point.
- `ApiInformation` flags for conditional XAML namespaces still have static initializers.
- C++/WinRT and VB get none of the code generation changes.
