# LiveBindScopeAttachProbe

A standalone, self-driving Windows App SDK fixture that exercises the semantics of the proposed live
`{x:Bind}` scope attach contract against a cold-built `{x:Bind}` oracle in the same process.

**This is an app-level simulation.** It runs on the stock, unmodified shipping `Microsoft.UI.Xaml.dll`.
It proves two things:

* the proposed API semantics are sound and complete enough to make an already-constructed, already-loaded
  page behave identically to a cold-built one, and
* an externally generated, strongly typed connector living in a separate assembly can drive that page
  exactly like a compiler-generated `{x:Bind}` scope.

It does **not** prove that a public runtime hook exists. `Probe\LiveBindScopeAttach.cs` is a managed
mirror of the native reference implementation in `dxaml\xcp\dxaml\lib\XamlBindScopeAttach.cpp`; the two
places where it stands in for a runtime-internal facility are marked `RUNTIME BOUNDARY`.

## Why the runtime boundary is real

The probe found this the hard way. On the first run, the "cached, never-parented page" case was refused
as an out-of-namescope target, because an app-level reachability check has to walk the visual tree and an
unrealized tree has no visual parents. The runtime does not have that problem:
`CDependencyObject::GetStandardNameScopeOwner()` walks the namescope owner chain, which exists from parse
time. Both implementations were corrected so reachability is only checked for rows with no stable name —
a row resolved through the root's namescope has already proven membership. The cached-instance scenario
is not soundly implementable above the runtime boundary.

## Layout

| Path | Role |
|---|---|
| `Probe\OraclePage.xaml` | Cold-built `{x:Bind}`. The reference behaviour every assertion compares against. |
| `Probe\SubjectPage.xaml` | Same element tree, deliberately **no** bindings — what a page inflated from an older XBF looks like. Two adjacent same-typed `TextBlock`s are the identity negative control. |
| `Probe\Contracts.cs` | Local mirror of the proposed `Microsoft.UI.Xaml.Markup` types, with identical shape and identical numeric values. `IComponentConnector` is the real runtime interface. |
| `Probe\LiveBindScopeAttach.cs` | Managed mirror of the native algorithm: preflight, fixed fault precedence, then mutation. |
| `Probe\ProbeRunner.cs` | The matrix. Negative controls run first, against a root that owns no scope. |
| `ProbeScope\SubjectScope.cs` | The "newly generated" scope, in a side assembly compiled **after** the app and loaded by reflection at runtime. |

The side assembly compiles against the app's already-built output (`ProbeAssemblyPath`), and the app has
no compile-time dependency on it. That models a tool-generated assembly and sidesteps the Edit-and-Continue
restriction on inserting new nested types into an existing assembly. The tradeoff versus applying generated
code with `MetadataUpdater` is that a side assembly cannot add members to the existing page type, so the
scope reaches the page through its public surface instead of a generated `Bindings` field; in exchange it
needs no EnC capability at all and is a plain, fully verifiable, strongly typed assembly.

## Building and running

Requires the .NET 8 (or later) SDK and an installed Windows App Runtime 1.8. Nothing here participates in
the repository build; `Directory.Build.props` next to this file stops property inheritance from the repo root.

```powershell
cd Probe
dotnet build -c Release -p:Platform=x64

cd ..\ProbeScope
$probeDll = "..\Probe\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\LiveBindScopeProbe.dll"
dotnet build -c Release -p:Platform=x64 -p:ProbeAssemblyPath=$probeDll

cd ..
.\run.ps1
```

`run.ps1` copies the side assembly next to the app, launches it, waits with a watchdog bounded to its own
exact PID, and reports the exit code. The app opens a window, runs the matrix once the tree is live, writes
`probe-results.txt` next to the executable, and closes itself. It never waits for input.

## Recorded result

`probe-results.txt` in this folder is the run that accompanied the prototype: 20 cases, 20 passed, 196 ms,
single process, current page instance never rebuilt.

Revisions in that run are **test generation tokens**, not content hashes. The two dimensions are kept
strictly separate and are never compared to each other, so real values substitute directly:
`baseTreeRevision` = hash of the XBF, source checksum, compiler/schema version and connection-map;
`scopeRevision` = hash of the generated scope and its binding manifest.
