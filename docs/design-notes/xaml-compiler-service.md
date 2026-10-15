# XAML compiler as a service

Status: spike (branch `nmetulev-xaml-compiler-service`), for review by the WinUI team. Part 2 adds
[hot reload code generation](#hot-reload-code-generation): what the compiler generates in hot reload
(Debug) builds so that .NET hot reload can apply every recompile.

## Table of Contents

- [Why](#why)
- [What it is](#what-it-is)
- [API sketch](#api-sketch)
- [State the service keeps](#state-the-service-keeps)
- [Per-save flow](#per-save-flow)
- [Correctness](#correctness)
- [Measurements](#measurements)
- [Compiler changes](#compiler-changes)
- [Hot reload code generation](#hot-reload-code-generation)
- [Roslyn issues to file](#roslyn-issues-to-file)
- [Open questions and risks](#open-questions-and-risks)

## Why

Hot reload has to produce exactly what a clean build produces. The way to guarantee that is to run
the real XAML compiler on every save, not a second implementation of it. A full compile of WinUI
Gallery takes about 8 s per pass out of process, so the compiler has to stay warm and compile only
the page that changed.

## What it is

`XamlCompilerService` (in `src/XamlCompiler/BuildTasks/XamlCompilerService.cs`) runs
`CompileXamlInternal` in-process with the same code paths a build uses, and keeps the type universe
loaded between calls. Each call compiles only the XAML files it is given.

Its inputs are the CompileXaml inputs the build already writes next to the generated files
(`obj\...\input.json`, the file `XamlCompiler.exe` reads). Nothing needs to be captured from a
binlog.

There are two ways to host it:

| Host | How | Use when |
|---|---|---|
| In-process | Reference `Microsoft.UI.Xaml.Markup.Compiler.dll` (net8.0 or net472) and create `XamlCompilerService` | A .NET tool such as winapp's hot reload helper |
| Out of process | `XamlCompiler.exe --serve <input.json> [<output folder>]`, one JSON request per line on stdin, one JSON response per line on stdout | Native tooling, or a host that doesn't want the compiler's dependencies in its process |

The service works for both C# and C++/WinRT projects. The compiler is managed in both cases (C++
builds already run it as an MSBuild task), so the language of the app doesn't matter. What differs
is what the caller does with the output.

## API sketch

This is the shape of the spike, with the changes we would make before shipping noted in comments.

```csharp
namespace Microsoft.UI.Xaml.Markup.Compiler
{
    public sealed class XamlCompilerService : IDisposable
    {
        // buildInputs: the pass 2 CompileXaml inputs of one project (input.json).
        // outputPath:  where generated files go; null writes into the build's own folder.
        // One service per process: the compiler's type universe is static today.
        public XamlCompilerService(CompilerInputs buildInputs, string outputPath = null);

        public IReadOnlyList<string> AllXamlFiles { get; }

        // A page the build didn't compile (a new file), as a Page item. Compile calls add any .xaml file
        // they're given that isn't in the project yet, so a host normally doesn't need it.
        public bool AddXamlPage(string xamlFile);

        // A deleted page. Its generated files are left alone.
        public bool RemoveXamlPage(string xamlFile);

        // New inputs from a build of the same project (a reference added, removed or updated). Unchanged
        // references stay warm; rebuilt, deleted and removed ones are unloaded. A framework or WinUI reference
        // change releases everything (FullReset). RefreshReferences: same inputs, files rebuilt on disk.
        public XamlReloadResult ReloadInputs(CompilerInputs newInputs);
        public XamlReloadResult RefreshReferences();

        // .g.i.cs (C#; .i.g.cs in hot reload builds) or .g.h (C++) for the given pages.
        // Doesn't need the project's assembly.
        public XamlCompileResult CompilePass1(IEnumerable<string> xamlFiles);

        // .g.cs / .g.hpp, the edited XAML and the .xbf for the given pages.
        //   localAssemblyImage: the project's assembly or .winmd as a PE image (for example a Roslyn
        //       metadata-only emit). Null reads LocalAssembly from disk.
        //   regenerateTypeInfo: false reads only the given pages and leaves XamlTypeInfo alone (fast);
        //       true reads every page and regenerates XamlTypeInfo exactly as a build does.
        public XamlCompileResult CompilePass2(
            IEnumerable<string> xamlFiles,
            byte[] localAssemblyImage = null,
            bool regenerateTypeInfo = true,
            string localAssemblyPath = null);

        public void Dispose();
    }

    public sealed class XamlCompileResult
    {
        public bool Success { get; }
        public TimeSpan Elapsed { get; }

        // Generated source and edited XAML by full path: exactly the text of the file on disk (UTF-8
        // with a BOM).
        public IDictionary<string, string> GeneratedText { get; }

        // .xbf bytes for the requested pages, by full path.
        public IDictionary<string, byte[]> GeneratedXbf { get; }

        // Pass 2 with regenerateTypeInfo: false only. True when the pages use a type or member that
        // the last full compile didn't put in XamlTypeInfo; call CompilePass2(AllXamlFiles, ...,
        // regenerateTypeInfo: true) before relying on XamlTypeInfo.
        public bool XamlTypeInfoMayBeStale { get; }

        public IList<XamlCompileDiagnostic> Diagnostics { get; }  // file, line, column, code, message
        public IList<XamlCompileMarker> Markers { get; }          // the compiler's perf markers
    }
}
```

The `--serve` protocol carries the same request and result as JSON:

```json
{"Id":1,"Pass":2,"Files":["Samples\\Button\\ButtonPage.xaml"],"RegenerateTypeInfo":false,"LocalAssemblyImage":"<base64 or null>"}
{"Id":1,"Success":true,"ElapsedMs":61.0,"XamlTypeInfoMayBeStale":false,"GeneratedText":{"...ButtonPage.g.cs":"..."},"GeneratedXbf":{"...ButtonPage.xbf":"<base64>"},"Diagnostics":[]}
```

Before shipping we would:

- Return results without writing to disk (today the compiler writes, and the service also returns
  the content).
- Make the type universe per-instance instead of static, so one process can serve several projects.
- References: done in API v3 (`ReloadInputs`, `RefreshReferences`; see [References](#references)).

## State the service keeps

| State | Kept between calls | Replaced when |
|---|---|---|
| Type universe: referenced assemblies and winmds, LMR caches, type-name map | Yes, read fully into memory (no files held open) | Never; recreate the service if references change |
| The project's own assembly | Yes | On every pass 2. The old one is unloaded and every cache that points at it is dropped |
| Saved state (x:Class per file, generated file names) | Yes, in memory | Updated after each call |
| Set of types and members in XamlTypeInfo | Yes | After each pass 2 with `regenerateTypeInfo: true` |
| Schema context, XAML DOMs, code generators | No | Created per call (cheap once the universe is warm) |

## Per-save flow

C# (with .NET hot reload applying the generated code):

1. `CompilePass1([page])` gives the new `.g.i.cs` (`.i.g.cs` in hot reload builds).
2. If any user-written C# changed since the last image, emit the app's compilation metadata-only
   (`EmitOptions(metadataOnly: true, includePrivateMembers: true)`) and pass the bytes. Otherwise reuse
   the previous image: in our tests the changes pass 1 makes to `.g.i.cs` (new fields) didn't affect
   pass 2, because it resolves named elements from the XAML.
3. `CompilePass2([page], image, regenerateTypeInfo: false)` gives `.g.cs` and `.xbf`.
4. If `XamlTypeInfoMayBeStale`, run `CompilePass2([page], image, regenerateTypeInfo: true)` (hot reload builds; see
   [XamlTypeInfo](#4-xamltypeinfo)). Normal builds need `CompilePass2(AllXamlFiles, image, regenerateTypeInfo: true)`.
5. Hand `.g.i.cs`, `.g.cs` (and `XamlTypeInfo.g.cs` if regenerated) to Roslyn, and the `.xbf` to
   WinUI.

C++/WinRT (no general native hot reload):

1. `CompilePass1([page])` and `CompilePass2([page], winmd, false)`, the same as for C#. The local
   assembly is the project's merged `.winmd` (midl output).
2. Compare the generated `.g.h`/`.g.hpp` with the running build's. If they're unchanged, apply the `.xbf` live. In
   hot reload builds that covers property values, text, unnamed elements, and moving, wrapping or reordering
   elements, named or bound ([section 6](#6-cwinrt)). If they changed (a new `x:Name`, event or `x:Bind`, a changed
   binding), the C++ code must be rebuilt, so report "restart".

## Correctness

Every comparison is byte-for-byte against the out-of-process `XamlCompiler.exe` from the same branch
running a normal two-pass compile on the same inputs (what `dotnet build` runs).

| Check | Result |
|---|---|
| WinUI Gallery (194 pages), full compile through the service, cold and warm | 777/777 files identical |
| Every Gallery page recompiled one at a time through the service | 777/777 identical, 0 failures |
| Same, feeding pass 2 a Roslyn metadata-only image of the app instead of the build's DLL | 777/777 identical |
| Same, temp DLL on disk instead of an image | 777/777 identical |
| Edit: insert an element with `x:Name` and an `x:Bind` to it; edit, revert, edit | Page outputs identical in every round; whole folder 777/777 identical to a full build of the edited project |
| Edit: add a `Click` handler in XAML and C# | 777/777 identical to a full build |
| Edit: `x:Bind` to a new C# property (changes XamlTypeInfo) | Service flags `XamlTypeInfoMayBeStale`; after the full pass 2, 777/777 identical |
| C++/WinRT project (regression project `Basic\CppWinRT\Simple`), per page | 27/27 identical |
| C++ edits: property value, unnamed element, `x:Name` | 27/27 identical to a full build each time |
| `XamlCompiler.exe --serve` on Gallery | 777/777 identical |
| This branch's compiler vs. the unmodified compiler, normal build | Gallery 777/777, C++ 27/27 identical |

One thing is not identical in normal builds: regenerating XamlTypeInfo from a single-page pass 2 with
`regenerateTypeInfo: true` changes the order of members in `XamlTypeInfo.g.cs`. That order follows
the order in which System.Xaml first looked members up, which depends on every page being harvested.
That's why the service uses a full pass 2 when type info is stale. Hot reload builds sort the tables
instead (see [XamlTypeInfo](#4-xamltypeinfo)), and there the service's XamlTypeInfo is identical.

## Measurements

WinUI Gallery, x64, Release build of the compiler, warm OS file cache.

| Step | Time |
|---|---|
| Out of process, `XamlCompiler.exe` (net472), all pages | pass 1: 5.4 to 8.4 s, pass 2: 8.1 to 8.4 s |
| Service, first compile in the process, all pages | pass 1: 4.2 to 5.8 s, pass 2: 4.7 to 6.7 s |
| Service, warm, all pages | pass 1: 0.92 to 0.97 s, pass 2: 1.96 to 2.39 s |
| Service, warm, one page, pass 1 + pass 2 (all 194 pages) | p50 56 to 61 ms, p90 82 to 92 ms, p99 115 to 138 ms, max 148 to 179 ms |
| Service, warm, one page, pass 2 with type info for all pages | 760 to 770 ms (and not byte-identical, see above) |
| `--serve` (net472), warm, one page | pass 1: 60 to 122 ms, pass 2: 61 to 122 ms |
| Roslyn, metadata-only emit of the app, nothing changed / one tree changed | 0.39 to 0.43 s / 0.58 to 0.66 s |
| Roslyn, full emit of the app | 2.8 to 4.0 s |
| Loading the project's assembly in pass 2: image / same file / new temp file | 2 to 8 ms / 2 to 14 ms / 59 to 69 ms (+1.5 to 2.2 ms to write it) |
| End to end per save: XAML-only edit (image reused) | about 75 to 125 ms |
| End to end per save: with a new metadata-only image (needed when C# changed) | 460 to 810 ms |
| XamlTypeInfo stale, full pass 2 | 2.07 s |

Where the time goes in one warm single-page call: the page itself (parse, validate, harvest, code
generation) is 5 to 30 ms, genxbf 4 to 10 ms, App.xaml (always read for the application name) about
5 ms, and creating the schema context and loading the project's assembly 7 to 20 ms.

Before the service-specific trims, about 100 ms per call went to bookkeeping for files that weren't
being compiled: timestamp and size probes for every page, generated-file lists for MSBuild, and
rewriting the saved-state file.

The C++/WinRT project has 5 pages and 97 winmd references. Out of process it takes pass 1: 4.3 s,
pass 2: 2.6 s. Through the service, warm, one page takes 27 to 53 ms, and one save takes 114 to
135 ms.

## Compiler changes

Service support (no effect on builds):

- `CompileXamlInternal`: compile only the requested files, take the project's assembly as an image,
  report generated text, and skip build-only bookkeeping (up-to-date checks, fingerprints,
  timestamp probes, MSBuild file lists, the C# pass 1 XamlTypeInfo stub and pass 2 backups).
- LMR: load an assembly from an image; unload one assembly and drop every cache that points at it
  (universe name and type caches, factory type cache, per-assembly and per-module type caches, type
  resolver, attribute cache).
- `XamlCompilerService`, `XamlCompiler.exe --serve`, `perfXC_Checkpoint` markers.
- In the service, assembly files are read completely when loaded and not kept open, so a build can
  overwrite the project's intermediate assembly and its references while the service is alive.
  A build still reads them lazily, as before.
- `Tools/XamlCompilerServiceHost`: benchmarks, the Roslyn emit and the edit scenarios used above,
  `--hot-reload` (read inputs as a hot reload build), and the `tombstones` self-test.
- `Tools/XamlHotReloadEncHarness`: replays a journey through the service and .NET hot reload (see
  [Evidence](#evidence)).

Fixes that matter for builds too:

- Generated `x:Bind` names (function bindings, map indexers, `ApiInformation` conditions) used
  `string.GetHashCode()`. On .NET that's randomized per process, and on .NET Framework it differs
  between 32-bit and 64-bit. So the in-process .NET task produced different generated code on every
  build. They now use a stable hash equal to the 64-bit .NET Framework one, so the output of Visual
  Studio builds and `XamlCompiler.exe` doesn't change.
- `TypeForCodeGen`'s cache was a static dictionary keyed by `XamlType` that was never cleared. It
  could return types from an earlier compile in a reused MSBuild node. It's now cleared after every
  compile, like the other code generation caches.
- Native projects under the .NET compiler couldn't resolve `mscorlib` (the fallback was
  `System.Private.CoreLib`). The .NET build now uses the .NET Framework `mscorlib`, as the
  .NET Framework compiler does.
- References are no longer re-read from disk when they're loaded a second time in the same universe.

## Hot reload code generation

The real compiler's output can be applied live with .NET hot reload (spike S1/S2) if the generated C# changes only
in ways edit and continue supports. In hot reload builds the compiler now generates code shaped for that. A normal
(Release) build is unchanged.

### The gate

`EnableXamlHotReload` (a CompileXaml input, from the targets) turns it on. The targets default it to `true` for
Debug builds (C#: `Optimize != true`; C++: `UseDebugLibraries`). `EnableHotReloadStableConnectionIds` now defaults
to `$(EnableXamlHotReload)`. The service passes the build's value through, so a hot reload Debug build and every
service compile generate the same shape.

- Stable connection IDs (Prototype A) apply to every language: C++ also stops renumbering when an element is inserted.
- Hot reload builds always leave the pass 2 compiler inputs (`input.json`, next to the generated files: C#
  `obj\...\input.json`, C++ `Generated Files\input.json`) for a host to start the service from. `dotnet build` (the
  executable compiler) always wrote them; Visual Studio's MSBuild (the in-process compiler) now writes them too when
  `EnableXamlHotReload` is true. Check: `src\XamlCompiler\Tools\CheckXamlHotReloadInputs.ps1`.
- Sections 2 to 5 below are C# only (`IsHotReloadCodeGen`). C++/WinRT gets its own changes (`IsCppHotReloadCodeGen`,
  [section 6](#6-cwinrt)) and a deterministic lookup in its `static_assert` list (see [Other changes](#other-changes)).

### 1. Stable, append-only connection IDs and tombstones

**Settings check.** The build also records the code generation settings that shape its output
(`XamlHotReload\codegen.json`, `{"enableXamlHotReload":…}`). A service compile, either pass, whose inputs differ fails
with WMC9998 at that file instead of generating code the running build doesn't have. A host whose `input.json` left
out `EnableXamlHotReload` used to get, for example, C++ `static_assert`s and `.g.h` members that differ from the build.

**Where the state lives.** The connection ID ledger and the member inventories are in the build's output folder
(`obj\...\XamlHotReload\`), even when the service writes its generated files elsewhere. A build and the service
compiles that follow it continue one history. Clean deletes the folder.

The files are named by a 12-hex hash of the class name (`<hash>.connids.json`, `<hash>.hotreload.json`,
`<hash>.i.g.cs.members.json`, at most 40 characters), and atomic writes go through `~<n>.tmp` in the same folder, so a
temporary path is never longer than the file's own. When the folder is deeper than 218 characters, the compiler
addresses it as `\\?\<path>`: XamlCompiler.exe runs on .NET Framework, which can't open longer paths otherwise, even
with LongPathsEnabled. A state path that still fails is reported as WMC9998 with its length, not as an internal error.

- **Bound-signature keys** for unnamed elements that bind or handle events (the ones that get connection IDs): their
  namescope, type, and canonical `x:Bind` and handler text, plus their order among identical elements in the namescope.
  Inserting other elements, or wrapping or moving the element, keeps its ID; editing its binding gives it a new one.
- **Indexed structural keys** for other unnamed elements that aren't unique among their siblings (see the risks
  section).
- **Deterministic transient IDs.** An element without a stable key (no `x:Name`, `x:Uid` or `x:Key`, and not unique
  by structure) used to get a new ID on every compile, so recompiling the same markup changed the output.
  - Each such element now has a fingerprint: scope, type and collision key.
  - The k-th element with a given fingerprint reuses the k-th ID it had last time; unused IDs are retired.
  - A rebuild of unchanged markup is now a fixed point.
- **Service compiles.** They regenerate only the requested pages. Builds still regenerate every page, so that every
  page's ledger is republished. Ledger files whose bytes didn't change aren't rewritten.

**Tombstones.** .NET hot reload can't delete a field or a type (ENC0033). After codegen, a declaration-level scanner
of the generated C# records the fields, methods and nested types of each class, per generated file, in
`<hash>.<ext>.members.json`. On a service compile, any recorded member the new code no longer declares is put
back before the closing brace of its container:

- A field keeps its declaration without its initializer (constants stay as they are).
- A method keeps its header with an inert body (below), when no live member of the same name is left. .NET hot reload
  can delete a method, but Roslyn can't delete one that an earlier update of the session added (issue 4): an
  `x:Load` element added and then removed during a session took `UpdateUnloadedElement`, `CompleteUpdate`,
  `Update_<path>` and the page's `UnloadObject` implementation with it. Not kept: constructors, operators, explicit
  interface implementations, generic, abstract, extern and partial methods, except `UnloadObject`, whose defining
  declaration pass 1 emits for every page. A dropped implementation leaves a partial method unimplemented, which
  Roslyn also deletes. `InitializeXProperties` (`x:Property`) is not covered.
- A type keeps its header, fields and nested types. Its method, constructor and accessor bodies are inert, because
  live objects created before the update can still call them: a list calls `IDataTemplateComponent.Recycle`,
  `ProcessBindings` and `Update` on the old Bindings object of every container it recycles or reuses. Void members,
  constructors and `set`/`add`/`remove` accessors get `{ }`. Other members get `return default;` (`=> default;` for
  expression bodies), with `out` parameters assigned first: `nextPhase = -1` for `ProcessBindings` ("no more
  phases"), `default` otherwise. An `int ProcessBindings` returns -1.
- The block is wrapped in `#pragma warning disable` / `restore`, with a comment.
- A live member with the same name always wins.
- A build records the inventory but puts nothing back: it is the new baseline, and the app restarts from it.

The order of the restored members is deterministic (sorted by key).

**Template Bindings classes.** A template's Bindings classes are named with a hash of their data type
(`Page_obj20_Bindings_1a2b3c4d`), so an `x:DataType` change adds new classes instead of changing field types
(ENC0009). The old classes become tombstones. The `obj20` is the connection ID of the template's root element, so
wrapping that root (for example a `TextBlock` into a `StackPanel`) also renames the class. The wrapped element keeps
its own ID.

### 2. No redundant default initializers

`initialized`, `removedDataContextHandler`, the static `isobjN...Disabled` flags, `cache_X` and `tokenDPC_X` are
declared without `= false`, `= null` or `= 0`. A static initializer gives the class a static constructor, and
Roslyn 5.9 throws when an update changes the static constructor of a type that an earlier update added (see
[Roslyn issues](#roslyn-issues-to-file)). Hot reload output now has no static initializers and no static
constructors.

### 3. `.g.i.cs` is `.i.g.cs`

Roslyn's edit and continue ignores documents named `*.g.i.cs` (`IsWpfDesignTimeOnlyDocument`), and that's where
WinUI puts the `x:Name` fields, `InitializeComponent` and the `Bindings` field. Changing
`<TextBlock x:Name="Later">` to `<TextBox x:Name="Later">` was silently "applied" while the field kept its old type.

Hot reload builds name the C# pass 1 file `<Page>.i.g.cs`.

- It still ends in `.g.cs`, so analyzers and the IDE treat it as generated (it also carries the `<auto-generated>`
  header).
- MSBuild takes the file names from the task's outputs, so nothing in the targets changes.
- Design-time builds and IntelliSense see the same file.

Options considered:

| Option | Verdict |
|---|---|
| (a) Rename the file in the compiler (chosen) | Small, and the build and the service agree. |
| (b) The host gives Roslyn the same text under another document name | Breaks the PDB's document paths and checksums for the running baseline. |
| (c) A Roslyn change, so that only WPF projects get the `.g.i.cs` rule | Right long term. Filed as an issue; (a) doesn't depend on it. |

### 4. XamlTypeInfo

- **Reset hook.** `[assembly: MetadataUpdateHandler(typeof(XamlTypeInfoProvider))]`. Its `ClearCache` bumps a
  static generation counter. On its next lookup each provider clears its caches and type tables, so a type added
  by an update is found.
- **Canonical order.** The type and member tables are sorted by name (ordinal) before code generation. A service
  compile that regenerates XamlTypeInfo (`regenerateTypeInfo: true`) is then byte-identical to a build, whatever
  the warm schema context looked up before.
- **Stable method names.** `Activate_`, `StaticInitializer_`, `VectorAdd_`, `MapAdd_`, `get_` and `set_` use a hash
  of the type or member name instead of the table index. Inserting a type no longer renames, and therefore deletes,
  every method after it, and delegates cached by WinUI keep pointing at live methods.
- **Per save, XamlTypeInfo is not regenerated** (`regenerateTypeInfo: false`) unless `XamlTypeInfoMayBeStale` says a
  page uses a type or member that isn't in it; then `CompilePass2([page], image, regenerateTypeInfo: true)`. A removal
  leaves a harmless superset until the next regeneration.
- **Per-page type info cache** (`PageTypeInfoCache`, service only). Regenerating XamlTypeInfo reads every page for
  type information.
  - The service records each page's calls into the schema info (types and members by name), keyed by the page's
    content hash. On later compiles it replays them for the pages it isn't compiling, instead of parsing,
    validating and collecting them.
  - A replay re-resolves every name against the current compile, applies each distinct call once, and falls back to
    collecting the page when a name doesn't resolve or a project type it names changed shape (base type or declared
    members).
  - Reference reloads clear the cache. Hot reload (C#) only, where the sorted tables make the order of the calls
    irrelevant. `XAML_COMPILER_SERVICE_NO_TYPEINFO_CACHE=1` turns it off.
  - A page without a valid entry (never collected, cleared by a reload, added with `AddXamlPage`, or edited) is
    collected, and every collection stores its entry again. The content hash is recomputed only when the file's
    size or write time changed, so an edit that keeps both is missed (editors always change the write time).
  - Check: `XamlCompilerService.VerifyTypeInfo` (`XAML_COMPILER_SERVICE_VERIFY_TYPEINFO=1`) collects every page
    again after each replay and fails the compile (WMC9998) if XamlTypeInfo differs. The harness turns it on with
    `--verify-build`. Journey `journeys\reload-typeinfo.json` (TodoBench) covers this: session-added pages, a reference
    reload, then a page added with `AddXamlPage` and a type from the new reference, each regenerated per page and
    identical to a full collection. A negative control (a page changed behind the cache) fails as expected.
  - Gallery, every page compiled one at a time with `regenerateTypeInfo: true`: 1522/1522 identical to a build.
    Button, ComboBox and ScrollViewer back to back, pass 2 with type info p50: 407 to 459 ms with the cache, 804 to
    866 ms without it.
  - What's left: replaying 473 distinct calls (140 to 160 ms, mostly member enumeration of user types), metadata
    providers (55 ms), XamlTypeInfo generation (75 ms).
  - Pages that are replayed don't report their binding AOT warnings again; their type information is unchanged.

### 5. Re-running InitializeComponent

The agent resets `_contentLoaded` and calls `InitializeComponent` again on the live object. The generated code now
does the rest:

- **Before `LoadComponent`:** the previous `Bindings` stop (`StopTracking()`).
- **After `LoadComponent`:** if the root (or a Window's content) is already loaded, it calls `Bindings.Update()`,
  because `Loading` (or `Activated`) won't fire again.
- **In a root Bindings class**, `Loading` and `Activated` return early unless the object is still the owner's
  current `Bindings`. Otherwise a replaced Bindings object, still subscribed to the root, starts again on the next
  event: a Window raises `Activated` often, and `StopTracking` resets `initialized`.
- **`StopTracking` is null-safe** (`bindingsTracking?.`).
- **Tracking may be missing on live objects.** .NET hot reload adds fields without running constructors, so a
  Bindings object created before the update that gave its scope its first OneWay or TwoWay binding has no
  `bindingsTracking`. `Recycle` (called when a list recycles containers, also on scroll) and `SetDataRoot` skip it,
  and the paths that need it (TwoWay listener registration, `UpdateChildListeners_*`) call `EnsureBindingsTracking()`,
  which creates it.

The host must still call the old `StopTracking` before it applies the update. After the delta, the old object's
methods are the new code running against old fields.

### 6. C++/WinRT

A C++ app can't take new code while it runs. A host applies a page's new markup live only when the generated C++ is
byte-identical to the running build's, so in C++/WinRT hot reload builds (`IsCppHotReloadCodeGen`) that text depends on
what the markup means, not on where elements sit in the file:

- **Declarations in connection ID order.** Switch cases (`Connect`, `GetBindingConnector`, `DisconnectUnloadedObject`,
  the Bindings classes' `Connect` and `UpdateUnloadedElement`), Bindings struct definitions and their element fields
  follow connection IDs; `Update_`, `UpdateFallback_`, `Invoke_` and `TryGet_` methods and setter helpers follow their
  names. The `.g.h` (pass 1, before stable IDs exist) orders fields, accessors and Bindings forward declarations by
  name. Statement order inside a method (which binding updates first) stays in document order, as in every other
  build: reordering it could change behavior, for example `ItemsSource` before `SelectedItem`.
- **No source line numbers.** `// MainPage.xaml line 14` becomes `// MainPage.xaml CountText` (the element's name, or
  its type).
- **Re-initializing in place.** The host loads new markup into a live instance with `Application::LoadComponent`,
  which calls `GetBindingConnector` for the root again. The root case now stops the previous `Bindings`
  (`StopTracking()`), revokes its `Loading` (or `Activated`) subscription (that delegate holds a raw pointer to the
  object, which is released when replaced), and stores the new token in `_hotReloadBindingsStartToken` (`.g.h`). If
  the root (a Window: its content) is already loaded, it queues `Initialize()` on the element's `DispatcherQueue`,
  because the elements the bindings target are connected after this call and `Loading` won't fire again. The `.g.h`
  includes `winrt/Microsoft.UI.Dispatching.h` for that.

Edit matrix on a C++/WinRT app (a page with a named Button with Click, named TextBlocks and an unnamed `x:Bind`
TextBlock), warm service vs. a Debug build of this compiler:

| Edit | Before | After |
|---|---|---|
| unchanged page, unnamed element at top, line inserted, bound element moved or wrapped, everything wrapped in a Grid | `.g.hpp` changed (line comments; the bound element's ID) | identical |
| named element wrapped in a Border, two named elements swapped | `.g.h`/`.g.hpp` changed (order) | identical |
| binding mode changed, named element added | changed | changed (real code changes) |

### Other changes

- C++/WinRT: `static_assert`s for projected primitives were decided with `XamlType.ToString()`. That string includes
  the XAML namespace once something has initialized it, and whether something has depends on which other pages the
  same compile looked at. A full build could then emit `static_assert(is_type_complete_v<::winrt::hstring>)` that a
  one-page compile didn't. Hot reload builds look up the CLR name. Normal builds keep the old behavior; it is
  probably worth fixing for them too, since an incremental build can differ from a full one the same way.
- `XamlCompileResult.GeneratedText` is now exactly the file's text. The file service writes with `WriteLine`, so
  the old value lacked the final line break, and a host that gave it to Roslyn compiled slightly different text
  than the build did.
- `XamlCompilerService.AddXamlPage` and `RemoveXamlPage` (new and deleted pages).

### Evidence

`Tools/XamlHotReloadEncHarness` replays a winapp journey through the service and the SDK's edit-and-continue engine
(dotnet watch's `HotReloadService`, Roslyn 5.9 from SDK 10.0.401).

- **What a save is:** apply the edit, run pass 1, emit the app metadata-only, run pass 2, update the Roslyn
  solution, then `GetUpdatesAsync`.
- **Builds:** `XamlCompiler.exe` pass 1, an intermediate compile, pass 2, then a full C# compile that becomes the
  baseline. A build happens at the start and after every rude edit or exception, where the app would restart.
- **`--verify-build`:** after every XAML save it builds the same sources and ledger state into a scratch folder and
  compares every generated file.

TodoBench, the S1/S2 journey (24 saves) plus one extra (`TextBlock` to `TextBox` with the same `x:Name`):

| Mode | Applied live | Rude edits | Roslyn exceptions | App restarts |
|---|---|---|---|---|
| off (today's Debug build) | 12 of 25 | 6 (ENC0020 renames, ENC0033, ENC0009) | 5 (NRE) | 12 |
| stable IDs only (the S1/S2 spike) | 15 of 25 (the extra step "applies" wrongly) | 2 (s12 ENC0033, s17 ENC0009) | 6 (NRE) | 9 |
| hot reload code generation | 22 of 25 | 1 (the extra step: ENC0009, correct) | 0 | 2 (initial, and after that step) |

The three that don't apply in hot reload mode are expected:

- s03a: a `Click` handler that doesn't exist yet (CS1061).
- s18a: the new UserControl's `x:Bind` to a property that its code-behind (s18b) adds (WMC1110). The page is
  compiled again when the code arrives, and s18b applies.
- The extra step, which must restart.

With `--verify-build`, every XAML save was identical to a build of the same state:

- s01 to s11: 13 of 13 files identical.
- s12 to x1: two files (the page's `.g.cs` and `.i.g.cs`) differ only by the tombstone blocks; everything else is
  identical, including XBF, edited XAML and XamlTypeInfo.

WinUI Gallery, hot reload mode:

- **Fixed point:** build, rebuild, then every page recompiled through the service into the same folder. 1523 of 1523
  files identical (generated code, XBF, ledgers, manifests, member inventories), with
  `--typeinfo skip` and with `--typeinfo all`.
- **Tombstone self-test** (`XamlCompilerServiceHost tombstones`): 388 generated files, 10716 recorded members. Each
  file is stripped of every field, nested type and keepable method and restored from tombstones: 370 files rebuilt,
  0 failures. The tombstones compile on their own (no CS0161, CS0177, CS0127, CS0126 or CS1622) and contain no
  `throw`. Gallery compiled with all 373 rebuilt generated files in place of the build's (csc with the build's
  arguments): 0 errors.
- **C++ (Simple):** the fixed point is 38 of 38 identical.

Normal builds (flags off) are unchanged: Gallery 777 of 777 and C++ 27 of 27 identical to the unmodified compiler's
output.

Cost: warm, one Gallery page (Button, ComboBox, ScrollViewer; image; type info skipped), measured back to back on a
loaded machine:

| Mode | Pass 1 p50 | Pass 2 p50 |
|---|---|---|
| Off | 27 to 37 ms | 38 to 52 ms |
| Hot reload | 27 to 29 ms | 47 to 52 ms |

The ledger, manifest and inventory work is 4 to 17 ms of that.

## References

`ReloadInputs(newInputs)` and `RefreshReferences()` (API v3) change the references of a warm service:

- The universe records each loaded file's size and write time.
- A reload unloads every loaded file that was rewritten or deleted, and every former reference that the new inputs
  no longer list. It uses the same steps as the per-save swap of the project's assembly: the type name map, the
  reflection helper caches, and the LMR universe and factory caches.
- The next compile loads the new references and adds them to the type name map.
- If a framework or WinUI reference changed, a reload releases the whole universe instead (`FullReset`), because
  those types are cached in too many places. The next compile is then a cold one.

WinUI Gallery, `XamlCompilerServiceHost reload` (CommunityToolkit.WinUI.Converters; every full compile is compared
with a normal build), verbatim:

```
1. service created without CommunityToolkit.WinUI.Converters (cold)
  compile: ... 10318 ms, ok=False, errors=7 (first: WMC0001 Unknown type 'EmptyObjectToObjectConverter' ...)
2. ReloadInputs(with CommunityToolkit.WinUI.Converters): reload 25.0 ms, added 1, removed 0, released [], full reset False
  compile: ... ok=True, errors=0; vs baseline: identical=777 differ=0 missing=0
3. ReloadInputs(without ...): released [CommunityToolkit.WinUI.Converters.dll]
  compile: ... ok=False, errors=7 (first: WMC0001 Unknown type ...)
4. ReloadInputs(... -> a build of it with no types, at a temp path)
  compile: ... ok=False, errors=7 (first: WMC0001 Unknown type ...)
5. that file rebuilt in place, RefreshReferences(): reload 28.4 ms, released [CommunityToolkit.WinUI.Converters.dll]
  compile: ... ok=True, errors=0; vs baseline: identical=777 differ=0 missing=0
6. RefreshReferences() with nothing changed: released []
  compile: ... ok=True; vs baseline: identical=777
7. ReloadInputs(without Microsoft.WinUI.dll): released [Microsoft.WinUI.dll, ...], full reset True
8. ReloadInputs(back to the build's inputs)
  compile (cold: the universe was released): ... ok=True; vs baseline: identical=777 differ=0 missing=0
```

A reload takes 4 to 56 ms. The first full compile after it reads the new reference: 5.5 s (with type info, all pages,
on a loaded machine), against 10 to 13.5 s for a cold service. A host still needs a build to get the new input.json
(restore and reference resolution).

## Roslyn issues to file

1. **NullReferenceException in `DefinitionMap.GetPreviousMethodHandle`** (Roslyn 5.9.0, SDK 10.0.401, through
   `HotReloadService.GetUpdatesAsync`). Plain C#, verified with the harness:
   - Save 1 adds `public static class Helper { private static bool s_a = false; public static bool A => s_a; }`.
     It applies.
   - Save 2 adds `private static bool s_b = false;` to `Helper`, and `GetUpdatesAsync` throws:

     ```
     NullReferenceException at Microsoft.CodeAnalysis.Emit.DefinitionMap.GetPreviousMethodHandle(IMethodSymbolInternal oldProperty, IMethodSymbolInternal& peMethod)
     ```

   - Control: if save 1 declares `s_a` without an initializer (no static constructor), the later saves apply,
     including one that adds `s_c = false`.

   The trigger is an update to the synthesized static constructor of a type that an earlier update added.
2. **Edit and continue ignores `*.g.i.cs` in every project type.** `IsWpfDesignTimeOnlyDocument` (in
   `EditAndContinue/Utilities/Extensions.cs`) excludes it, so a change to a field's type in that file (WinUI's
   `x:Name` fields) applies with no rude edit, and the field keeps its old type at run time. Ask: limit the rule to
   WPF projects, or report changes in such documents as rude edits. WinUI hot reload builds no longer depend on it.
3. **A no-change update breaks the next update of members an earlier update added** (same NullReferenceException in
   `DefinitionMap.GetPreviousMethodHandle`). When `GetUpdatesAsync` finds no semantic change
   (`ModuleUpdateStatus.None`), `DebuggingSession` commits the new solution (`LastCommittedSolution.CommitChanges`)
   without a new `EmitBaseline`. The next update then maps old symbols from that solution's compilation, while
   `Baseline.MethodsAdded` is keyed by the compilation that emitted the delta, so a method added during the session
   isn't found. Harness repro (TodoBench): add a TwoWay `x:Bind` (applies), edit a text property (the `.g.cs` changes
   only in its `#pragma checksum`: no change), change the binding's `UpdateSourceTrigger`: NRE. Without the middle
   save, or with the old lambda codegen, it applies, so lambdas aren't the trigger. Hosts can avoid it for generated
   code: don't hand Roslyn a compile whose generated files changed only in `#pragma checksum` lines (hold them back
   and send them with the next real change, so one `.xaml`'s checksums stay consistent, CS1697). User edits that
   change only comments or whitespace still hit it.
4. **Deleting a method that an earlier update added throws the same NullReferenceException** (from
   `DeltaMetadataWriter.CreateDeletedMemberDefs` → `getDeletedMemberDefs`). Harness repro (the pipeline's
   parity-xbind-divergences journey): save 1 adds `<Border x:Load="{x:Bind Vm.Flag, Mode=OneWay}">` (applies; the
   update adds `UpdateUnloadedElement`, `CompleteUpdate`, `Update_Vm_Flag` and the `UnloadObject` implementation),
   save 2 removes it: NRE. Removing it after a build instead of an update applies. Generated code avoids it by keeping
   removed methods as inert tombstones (§1); user code that deletes a method it added in the same session still
   hits it.
5. **`GetDeletedSynthesizedMethods` NullReferenceException after a no-change update that added documents** (pipeline:
   todo-app-extended x14, `restart MainPage.xaml ... NullReferenceException`). This is issue 3 again, through added
   documents instead of changed ones:
   - x12 creates `Themes\Brand.xaml`, a ResourceDictionary without `x:Class`. Its generated `Brand.g.cs` and
     `Brand.i.g.cs` contain only `" \r\n"` (as in a build). A host that adds them to the workspace makes a solution
     change with no semantic change, and `DebuggingSession` commits it without a new baseline (`ModuleUpdateStatus.None`).
   - The next update that touches a method an earlier update of the session added then throws. x14 adds a named
     element to MainPage, which updates the template Bindings class whose TwoWay listener a22 added.
   - The frame depends on which method it hits. `DeltaMetadataWriter.CreateDeletedMemberDefs` calls
     `DefinitionMap.GetDeletedSynthesizedMethods` for every updated method, lambdas or not. It throws in
     `GetPreviousMethodHandle` when the old method isn't found in `MethodsAdded`, and in `GetDeletedSynthesizedMethods`
     itself when it is found but has no `AddedOrChangedMethods` entry (`peMethod` is null).
   - Lambdas aren't the trigger. Hot reload codegen with named methods instead of the TwoWay lambdas moved the NRE to
     another session-added method in the same save. With the original codegen and the host rule below, the journey
     applies every save (harness: 41 of 41 from the baseline after a12, including x14). Without the rule, x14 throws.
   - Host rule (the same as issue 3's): don't hand Roslyn an added generated file that has no code (whitespace only).
     Hold it back and add it with the next real change. The EnC harness does this. To replay a winapp journey in the
     harness, use `Tools\XamlHotReloadEncHarness\New-HarnessJourney.ps1`.

## Open questions and risks

- XamlTypeInfo member order depends on lookup history in normal builds. Hot reload builds now sort the tables
  (canonical, ordinal). Doing the same for every build would change every project's `XamlTypeInfo.g.cs` once.
- Hot reload code generation:
  - Tombstones exist only between builds; a build is a new baseline. A tombstone whose field type is a user type
    that was since deleted or renamed fails to compile. That edit is a rude edit in user code anyway, but the error
    shows up in generated code. The same holds for a kept method's parameter and return types.
  - A method whose signature changes (same name) isn't kept: Roslyn deletes the old overload, and issue 4 applies if
    the session added it. Generated code changes a signature when an `x:Bind` path's type changes.
  - Unnamed elements that aren't unique among their siblings get an indexed structural key (parent, member, type,
    position among same-type unnamed siblings), so an unchanged file keeps its IDs (Gallery: 543 ambiguous identities
    before, 0 after). Inserting an unnamed sibling of the same type before one shifts the IDs after it, among fields of
    the same type: not a rude edit. Transient IDs remain only under a parent without a stable identity.
  - WinUI's native metadata cache keeps the `IXamlType` objects it already resolved. The provider reset makes new
    types and members resolvable; a changed member on a type WinUI has already cached (for example a new dependency
    property on a user control that's on screen) may still need a restart. Not tested with a running app.
  - The re-init changes (`StopTracking`, `Update`, the guard against retired root Bindings) were checked for the
    shape of the code and for edit-and-continue acceptance, not in a running app.
  - The C++/WinRT `static_assert` determinism fix is gated on hot reload. Normal incremental C++ builds can still
    differ from full builds in that list.
- The stale check detects added types and members, not removed ones. A type that is no longer used
  stays in XamlTypeInfo until the next full compile. That's harmless at run time but isn't
  byte-identical to a build.
- Reusing the previous project image for XAML-only edits was correct in every case we tried. Pass 2
  does read the project's types (for example `x:Bind` paths), so whenever user C# changes, a new
  image is required. The conservative rule is to always emit one (about 0.4 to 0.65 s on Gallery).
- `Environment.CurrentDirectory` is set to the project folder during a compile, because some inputs
  are relative. A host shouldn't run other work that depends on the current directory concurrently.
- One project per process, until the type universe is per-instance.
- References are loaded once. If a referenced library is rebuilt, the host must recreate the
  service; it doesn't notice by itself.
- Memory: the warm Gallery service sits at about 400 to 570 MB working set, mostly reference metadata.
- The unit tests in `XamlCompilerTests.sln` weren't run: they need the full product build
  (MergedWinMD and genxbf from source). The regression checks above compare against the unmodified
  compiler instead.
- C#: `.g.cs` starts with `#pragma checksum` of the XAML file, so it changes on every edit, even when
  nothing else does. The C++ `.g.h`/`.g.hpp` carry no checksum and didn't change for markup-only
  edits, which is what makes the "unchanged, apply the .xbf live" test possible there.
