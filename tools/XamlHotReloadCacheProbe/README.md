# XAML hot reload cache probe

Checks what a WinUI 3 app shows after its markup is replaced through `IXamlHotReloadService`. The cases are resource
dictionaries, default styles, page resources, templates, `x:Load`, loose XAML, the Frame page cache, and resource
deferral. Each check compares against what a clean build of the edited markup shows. See
`docs/design-notes/xaml-hot-reload-caches.md` for the inventory and results.

- `tap\`: a minimal XAML diagnostics tap (`IObjectWithSite`). It keeps the `XamlDiagnostics` site so the app can query
  `IXamlHotReloadService` from it.
- `probe\`: an unpackaged, self-contained WinUI 3 app built Debug, so it carries the hot reload stamp. It loads the tap
  into its own process with `InitializeXamlDiagnosticsEx`, runs one scenario, writes `CHECK`/`SCENARIO` lines and exits.
  `Variants\V2`, `V3` and `Broken` hold the edited markup. The build compiles each one to `.xbf` for binary mode; text
  mode reads the `.xaml` source.

```powershell
.\run.ps1 -PackageSource <folder with Microsoft.WindowsAppSDK.WinUI.3.0.0-dev.nupkg>
.\run.ps1 -SkipBuild -Scenarios page-reinit,merged-swap -Modes text,binary
.\run.ps1 -SkipBuild -Scenarios implicit-code-swap,reinit-no-hotreload -Modes nodiag   # controls, no diagnostics
```

Each scenario runs in its own process and opens a window. On a shared desktop, pass `-DesktopLock` to serialize runs.
