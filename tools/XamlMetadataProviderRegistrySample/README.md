# XamlMetadataProviderRegistry plugin sample

Proves `Microsoft.UI.Xaml.Markup.XamlMetadataProviderRegistry` end to end
([spec](../../docs/api-specs/XamlMetadataProviderRegistry/XamlMetadataProviderRegistry.md)):

- `PluginControls` is a WinUI control library with two controls (`Badge`, `StarRating`). Its XAML compiler-generated
  `XamlMetaDataProvider` describes them.
- `PluginHost` has **no reference** to `PluginControls`. At runtime it loads `plugins\PluginControls.dll`, finds the
  generated provider, registers it, and creates the plugin's controls with `XamlReader.Load`. Then it unregisters the
  provider and checks that the same markup fails to parse, while the controls it already created keep working.

The host exits with 0 only if every check passes, and writes `plugin-host-results.txt` next to `PluginHost.exe`.

```powershell
.\run.ps1 -WinUIPackageSource <folder with Microsoft.WindowsAppSDK.WinUI.3.0.0-dev.nupkg built from this branch>
```

It is standalone: `Directory.Build.props`/`.targets` stop inheritance from the repository build, and nothing in the
repository references it.

Keep `-OutDir` short (the default is under `%TEMP%`): the XAML compiler fails with WMC1006 when package paths under `NUGET_PACKAGES` exceed MAX_PATH. Use `-NuGetFeed` where nuget.org isn't reachable.
