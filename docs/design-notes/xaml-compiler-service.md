# XAML compiler as a service

Status: spike (branch `nmetulev-xaml-compiler-service`), for review by the WinUI team.

## Table of Contents

- [Why](#why)
- [What it is](#what-it-is)
- [API sketch](#api-sketch)
- [State the service keeps](#state-the-service-keeps)
- [Per-save flow](#per-save-flow)
- [Correctness](#correctness)
- [Measurements](#measurements)
- [Compiler changes](#compiler-changes)
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

        // .g.i.cs (C#) or .g.h (C++) for the given pages. Doesn't need the project's assembly.
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

        // Generated source and edited XAML by full path. The file on disk is this text, UTF-8 with a
        // BOM, plus a final newline.
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
- Add a way to tell the service that references changed (today: dispose it and create a new one).

## State the service keeps

| State | Kept between calls | Replaced when |
|---|---|---|
| Type universe: referenced assemblies and winmds, LMR caches, type-name map | Yes | Never; recreate the service if references change |
| The project's own assembly | Yes | On every pass 2. The old one is unloaded and every cache that points at it is dropped |
| Saved state (x:Class per file, generated file names) | Yes, in memory | Updated after each call |
| Set of types and members in XamlTypeInfo | Yes | After each pass 2 with `regenerateTypeInfo: true` |
| Schema context, XAML DOMs, code generators | No | Created per call (cheap once the universe is warm) |

## Per-save flow

C# (with .NET hot reload applying the generated code):

1. `CompilePass1([page])` gives the new `.g.i.cs`.
2. If any user-written C# changed since the last image, emit the app's compilation metadata-only
   (`EmitOptions(metadataOnly: true, includePrivateMembers: true)`) and pass the bytes. Otherwise reuse
   the previous image: in our tests the changes pass 1 makes to `.g.i.cs` (new fields) didn't affect
   pass 2, because it resolves named elements from the XAML.
3. `CompilePass2([page], image, regenerateTypeInfo: false)` gives `.g.cs` and `.xbf`.
4. If `XamlTypeInfoMayBeStale`, run `CompilePass2(AllXamlFiles, image, regenerateTypeInfo: true)`.
5. Hand `.g.i.cs`, `.g.cs` (and `XamlTypeInfo.g.cs` if regenerated) to Roslyn, and the `.xbf` to
   WinUI.

C++/WinRT (no general native hot reload):

1. `CompilePass1([page])` and `CompilePass2([page], winmd, false)`, the same as for C#. The local
   assembly is the project's merged `.winmd` (midl output).
2. Compare the generated `.g.h`/`.g.hpp` with the previous compile. If they're unchanged (markup-only
   edits: property values, text, unnamed elements), apply the `.xbf` live. If they changed (`x:Name`,
   events, `x:Bind`), the C++ code must be rebuilt, so report "restart".

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

One thing is not identical: regenerating XamlTypeInfo from a single-page pass 2 with
`regenerateTypeInfo: true` changes the order of members in `XamlTypeInfo.g.cs`. That order follows
the order in which System.Xaml first looked members up, which depends on every page being harvested.
That's why the service uses a full pass 2 when type info is stale.

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
- `Tools/XamlCompilerServiceHost`: benchmarks, the Roslyn emit and the edit scenarios used above.

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

## Open questions and risks

- XamlTypeInfo member order depends on lookup history. If the team agrees to a canonical order
  (for example declaration order), XamlTypeInfo could be regenerated from cached per-page type info
  instead of a 2 s full pass 2. That changes the order in every project's `XamlTypeInfo.g.cs`
  once.
- The stale check detects added types and members, not removed ones. A type that is no longer used
  stays in XamlTypeInfo until the next full compile. That's harmless at run time but isn't
  byte-identical to a build.
- Reusing the previous project image for XAML-only edits was correct in every case we tried. Pass 2
  does read the project's types (for example `x:Bind` paths), so whenever user C# changes, a new
  image is required. The conservative rule is to always emit one (about 0.4 to 0.65 s on Gallery).
- `Environment.CurrentDirectory` is set to the project folder during a compile, because some inputs
  are relative. A host shouldn't run other work that depends on the current directory concurrently.
- One project per process, until the type universe is per-instance.
- The unit tests in `XamlCompilerTests.sln` weren't run: they need the full product build
  (MergedWinMD and genxbf from source). The regression checks above compare against the unmodified
  compiler instead.
- C#: `.g.cs` starts with `#pragma checksum` of the XAML file, so it changes on every edit, even when
  nothing else does. The C++ `.g.h`/`.g.hpp` carry no checksum and didn't change for markup-only
  edits, which is what makes the "unchanged, apply the .xbf live" test possible there.
