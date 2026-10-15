# XAML hot reload: runtime caches and resources

Status: experimental, for review by the WinUI team. Branch `nmetulev-hr-caches` (on top of the hot reload prototype,
`nmetulev-hot-reload-poc`).

## Why this note exists

The hot reload prototype adds `IXamlHotReloadService` to XAML diagnostics (`XamlOM.WinUI.idl`). A tool replaces the
markup for an `ms-appx:///` URI (`SetComponentMarkup`, text or compiled `.xbf`) and then makes the app load it again.
Depending on the tool, that means re-running a live component's `InitializeComponent`, creating a new instance, or
loading a `ResourceDictionary` from that `Source` again. Re-running markup works for most pages. In review, the WinUI team
pointed at the edge cases: resource dictionaries, cached data, loose XAML, and caches in general.

This note lists every place the runtime keeps data derived from markup or resources. For each one it says whether a
hot reload can leave the app showing old content, and what the branch changes. Each claim marked *measured* comes
from the probe described below. Claims marked *code* come from reading the source.

The bar is the one the WinUI team set: after any number of hot reloads, the app shows what a clean build of the same
markup shows. Apps that never use hot reload must pay nothing.

## How the claims were checked

`tools/XamlHotReloadCacheProbe` is a small unpackaged WinUI 3 app plus a 60-line diagnostics tap.

1. At startup the app calls `InitializeXamlDiagnosticsEx` on its own process, which loads its own tap.
2. It queries `IXamlHotReloadService` from the site the tap receives.
3. It replaces markup exactly as a tool would, either as text or as the `.xbf` the XAML compiler built from the edited
   file.
4. It performs the tool's next step: creating a new instance, re-running `InitializeComponent`, swapping a merged
   dictionary, navigating, and so on.
5. It reads what the live visual tree shows: text, `Tag`, applied templates, and resource values after layout.

Each check's expected value is what a clean build of the edited markup shows. Every scenario runs in its own process,
in `text` and `binary` modes. A `nodiag` mode runs the controls with no diagnostics attached.

```powershell
tools\XamlHotReloadCacheProbe\run.ps1 -PackageSource <folder with Microsoft.WindowsAppSDK.WinUI.3.0.0-dev.nupkg>
```

## Results

Baseline: the prototype package at c7cbf40ee. Fixed: this branch's runtime change (01bddb720) built into an integration
package on top of c7cbf40ee (`C:\hr-shared\winui\int-01bddb720`). Both columns come from the same probe build, with the
package cache isolated for each.

| Scenario | What it does | Baseline text / binary | Fixed text / binary |
|---|---|---|---|
| merged-new | Replace a merged dictionary's markup, then create `new ResourceDictionary { Source }` (values, ThemeDictionaries) | pass / pass | pass / pass |
| merged-swap | Replace it in `Application.Resources.MergedDictionaries`, then create a new page, and check a live page | **STALE / STALE**: the live element's implicit style is lost (`Tag` becomes null) | pass / pass |
| negcache-swap | Look up a missing key, then swap in a dictionary that defines it (the not-found cache) | pass / pass | pass / pass |
| generic | Replace `Themes/Generic.xaml`, then create a new templated control | pass / pass | pass / pass |
| page-new | Replace a page's markup, then create a new instance (Page.Resources, DataTemplate, `x:Load`) | pass / pass | pass / pass |
| page-reinit | Replace a page's markup, then run `InitializeComponent` again on the live instance | **STALE / STALE**: old Page.Resources and DataTemplate; new keys missing; root `Tag` stays | **STALE / STALE**: only the root `Tag` stays (open, see below) |
| page-reinit-reset | Same, after the tool sets `Resources = new ResourceDictionary()` (what both agents do today) | **STALE / STALE**: root `Tag` | **STALE / STALE**: root `Tag` (open) |
| page-reinit-broken | Replaced markup fails to load; the live page must keep its resources | pass / **STALE**: an existing key is lost | pass / pass |
| dict-reinit | A `ResourceDictionary` with `x:Class`, `InitializeComponent` run again | **ERROR** (duplicate key) / **STALE** (old value) | pass / pass |
| loose | A `.xaml` file shipped as content (no `.xbf`), loaded by `Source`, replaced, then removed | pass / pass | pass / pass |
| cycle | Replace a dictionary V2 → V3 → V2 → original → V3 → V2 → V3, checking each step (reader cache, storage) | pass / pass | pass / pass |
| app-reload | Replace `App.xaml`, then run `Application.LoadComponent` on the app again (text only; binary is a no-op) | **STALE**: live implicit style lost | pass |
| implicit-code-swap (control) | Swap in a dictionary built in code, with no markup involved | **STALE / STALE**; `nodiag`: pass | pass / pass; `nodiag`: pass |
| frame-cache | A `NavigationCacheMode="Required"` page: navigate away, replace its markup, then go back | **STALE / STALE**: old page | same (the tool's job, see below) |
| deferral | Count resources created by loading the replaced dictionary (clean build: 0) | **STALE**: 4 / pass: 0 | same (not changed, see below) |
| merged-resetsource, resetsource-plain (control) | Set `Source` again on the app's merged dictionary | ERROR E_FAIL; `nodiag` too | same (existing behavior, see below) |
| reinit-no-hotreload (control) | `InitializeComponent` twice with no hot reload: no markup replaced, no diagnostics | `nodiag`: loads twice, keeps `page-v1` | unchanged |

## Inventory

"Refreshed" means the cache is already refreshed on a process-wide generation counter. Each UI thread compares the
counter on its next use, so no thread touches another's caches. The counters are:

- `XamlHotReloadOverrides::g_markupGeneration`: bumped when replaced markup changes.
- `XamlTypeCacheGenerations::g_typesAdded` and `g_typesRemoved` (`core/inc/XamlTypeCacheGenerations.h`): these come
  from the metadata provider registry (branch `nmetulev-metadata-provider-api`) and replace the prototype's
  `g_typeGeneration`.
  - `g_typesAdded` is bumped by `XamlMetadataProviderRegistry.Register`, by `Unregister`, and by the service's
    `InvalidateTypeCaches`.
  - `g_typesRemoved` is bumped only by `Unregister`.

  See `docs/api-specs/XamlMetadataProviderRegistry/XamlMetadataProviderRegistry.md`, "Parser caches".

### Markup loading (parser, XBF, URIs)

| Cache | Where | Key / lifetime | Refreshed | Can it show old content? |
|---|---|---|---|---|
| Parsed node lists | `XamlNodeStreamCacheManager::m_UriToNodelistMap` (`core/Parser/NodeStreamCache.cpp`) | markup URI / per core | yes (`SyncHotReloadGeneration` → `Flush`) | No. *measured*: page-new, cycle |
| URI → `.xbf` resource | `m_UriToXbfResourceMap` | URI / per core | yes | No. *measured*: cycle, loose |
| `.xbf` storage across flushes | `m_XbfResourceStorage`, `m_fileToStoredXbfResource` | file path / per core, until shutdown | replaced markup is never stored by file path | No. Built files can't change while the app runs. Replaced `.xbf` is kept until shutdown (see "Memory") |
| XBFv2 readers | `m_XBFv2ReaderCache` | **buffer address** / until metadata reset | not needed | No. The key is an address, but every buffer stays alive in `m_XbfResourceStorage`, so an address is never reused. *measured*: cycle alternates contents 7 times |
| Negative type lookups | `XamlXmlNamespace::m_mapKnownNotFoundTypes` | type name / per namespace | yes (`g_typesAdded`) | No |
| Positive type and member lookups | `XamlXmlNamespace::m_mapNameToXamlType`, `XamlSchemaContext::m_mapFullyQualifiedProperty`; other `XamlSchemaContext` maps, `MetadataAPI` custom type and property caches | name or token / per core or process | The first two, plus node lists and XBF readers, are dropped on `g_typesRemoved` (a provider was unregistered). Other resolved types stay | Not for markup edits: markup doesn't change what a type is. A type whose shape changes in place is a .NET hot reload question, out of scope here |
| Parse errors | core error service | per core | yes (cleared on the first `LoadComponent` after a change) | No |
| Resource URI resolution | `ResourceManager::TryGetLocalResource` → `ModernResourceProvider` | URI / per call | the override wins before MRT | No XAML-side cache. MRT's own maps are opaque |

### Resources

| Cache | Where | Key / lifetime | Refreshed | Can it show old content? |
|---|---|---|---|---|
| Default styles (app and library `Themes/Generic.xaml`) | `DirectUI::StyleCache` (`dxaml/lib/DefaultStyles.cpp`) | type / per core | yes (`ClearCustomStyles`) | No for new controls (*measured*: generic). Controls that already applied a template keep it until re-created, as with any template |
| Framework theme resources | `CCoreServices::m_pThemeResources` | per core | no | Not replaceable: WinUI's own resources are refused by the service |
| Dictionary contents loaded by `Source` | `CResourceDictionary::LoadFromSource` | per dictionary instance | n/a | No for a new or re-sourced dictionary. `m_resourceDictionaryUriCache` is only a cycle check, not a content cache (*code*, `Resources.cpp` `LoadFromSource`) |
| Deferred resources (XBF) | `CResourceDictionary::m_pDeferredResources` | key / per dictionary | n/a | Only when markup is loaded **into an existing dictionary**: see "Re-running InitializeComponent" |
| Deferred resources (text) | `m_pRuntimeDeferredKeys` | key / per dictionary | n/a | Same |
| Not-found keys | `CResourceDictionary::m_keysNotFoundCache` (8 keys) | key / per dictionary | n/a | No. Adding a merged dictionary clears the caches up the chain. *measured*: negcache-swap |
| Active theme dictionary | `m_pActiveThemeDictionary` | theme / per dictionary | n/a | No. Rebuilt with the dictionary. *measured*: merged-swap toggles theme on a live page and gets the new value |
| Theme walk | `ThemeWalkResourceCache` | (dictionary, theme, key) / one theme walk | n/a | No. Cleared at the end of each walk |
| Implicit style per element | `CFrameworkElement::m_pImplicitStyle` | per element | n/a | **Yes, with diagnostics attached.** See "Implicit styles" |
| Values resolved by `{StaticResource}` | the property values themselves | per element | n/a | Yes, by design: a live element keeps the value it resolved when it loaded. The tool reloads the element (a new page, or `InitializeComponent` again). `{ThemeResource}` re-resolves on theme change (*measured*) |
| x:Uid / `.resw` | `ModernResourceProvider::GetPropertyBag` over the MRT `ResourceManager` created at startup | per process | no | **Yes.** A rebuilt `resources.pri` is never seen. No XAML-side cache. See "Not handled" |
| Images by URI | `ImageCache` (`components/imaging`) | URI / per core | no | **Yes** for an asset overwritten on disk (not markup). Today's agents reload images themselves |

### State the tool's reload step keeps (not a WinUI cache, but it shows old content)

| What | Where | Can it show old content? |
|---|---|---|
| Frame page cache | `Frame` `NavigationCache` (`dxaml/lib/NavigationCache.h`) for `NavigationCacheMode` Enabled/Required | **Yes.** `GoBack` returns the cached instance with its old markup (*measured*: frame-cache, `CachedPage` constructed once). The tool must re-run those instances' markup or drop them from the cache |
| Template content of live controls | `CTemplateContent`, per template | Controls keep the template they applied. The tool re-creates them or reloads the page |
| `x:Load` / `x:DeferLoadStrategy` | `CDeferredElement`, per page instance | A re-initialized or new page gets the new markup (*measured*: page-new, page-reinit). An old instance that the tool keeps realizes old markup |
| Visual states, storyboards | per template or element | Same as templates |
| Item containers | `ListView`/`ItemsRepeater` recycle pools | Recycled containers keep the old `DataTemplate`'s elements until the list is re-created |

## Fixes on this branch

### Implicit styles are lost from live elements when a diagnostics tool is attached

*Measured*: merged-swap, app-reload and implicit-code-swap fail in both modes. The control with no diagnostics
(`nodiag`) passes. This is a WinUI bug that hot reload exposes rather than a hot reload bug. It affects any app with
Visual Studio's Live Visual Tree or any other diagnostics tool attached, whenever a dictionary with implicit styles is
added or replaced at run time.

`CFrameworkElement::InvalidateImplicitStyles` asks the subtree to look implicit styles up again by calling
`UpdateImplicitStyle(nullptr, nullptr, forceUpdate = true)`. XamlDiagnostics uses exactly those arguments to mean
"unapply the implicit style" (`CResourceDictionary::NotifyImplicitStyleChanged` with `ForcedByDiagnotics` and no
style). With the diagnostics feature on, `UpdateImplicitStyle` takes the diagnostics meaning, skips
`EnsureImplicitStyle`, and every element in the subtree ends with no implicit style.

Fix (`core/core/elements/framework.cpp`): a per-thread flag marks the "look them up again" pass, and the diagnostics
shortcut ignores it. Without diagnostics, nothing changes.

### Re-running InitializeComponent with replaced markup kept the old resources

*Measured*: page-reinit and dict-reinit. Approach B and today's in-place reload re-run `InitializeComponent` on the
live instance (with the generated `_contentLoaded` guard reset). The parser adds markup resources to the root's
existing `Resources` dictionary, which leads to three different failures:

- Text markup fails with "The dictionary key 'PageText' is already used", or silently keeps old values and misses new
  keys.
- Compiled markup installs a second deferred-resources table into the same dictionary. That case is asserted against
  in `CResourceDictionary::SetCustomWriterRuntimeData` (`ASSERT(!m_pDeferredResources)`). In release builds the old,
  already-created values win and the old deferred keys disappear.
- A failed load can still drop existing keys (page-reinit-broken, binary).

Both agents work around this today with `Resources = new ResourceDictionary()` before the re-run.

Fix (`core/packaging/application.cpp`, `Resources.cpp/.h`):

- After every `LoadComponent`, the dictionary its markup filled is marked. That's the root's `Resources`, or the
  component itself when it is a `ResourceDictionary` with `x:Class`. The mark is one bit in an existing bitfield.
- Once hot reload has replaced any markup in the process (`XamlHotReloadOverrides::IsActive()`), a `LoadComponent`
  onto a component whose dictionary carries the mark first gives it a fresh dictionary. A dictionary component is
  emptied instead.
- If the load fails, the previous dictionary is put back.
- Before any markup is replaced, behavior is unchanged: the `nodiag` control `reinit-no-hotreload` still loads twice as
  before.

Known limit: entries that code added to the root's own `Resources` before the re-run are dropped, as with the agents'
workaround. A clean build's constructor would add them again.

`CResourceDictionary::Clear` now also resets `m_undeferredKeyCount` together with the deferred table it drops.
Otherwise a dictionary loaded again subtracts the previous load's count from the new table. This is not what makes
setting `Source` again fail with E_FAIL (see below); that still fails with the fix.

## Not handled (proposals)

- **Root attributes removed from the markup.** Re-running `InitializeComponent` on a live page leaves properties that
  only the old markup set (for example `Tag` or `Background` on `<Page>`). *Measured*: page-reinit and
  page-reinit-reset in both modes, with and without this branch. A clean build or a re-created page (approach C)
  doesn't have them. Text and compiled markup both set root properties through
  `ObjectWriterCommonRuntime::SetValue` → `ObjectWriterRuntime::SetValueImpl`, which can compare the instance with
  `m_spContext->get_RootInstance()`. The runtime could record the root properties a `LoadComponent` sets there, and
  clear them before replaced markup is loaded again. That needs per-component storage. To keep the cost near zero, the
  recording would be limited to apps that carry the hot reload stamp, which needs the stamp check moved to startup.
  This is a design decision for the WinUI team. Until then, tools that reload in place should re-create a page whose
  root element's attributes changed, or report it.
- **Text vs compiled resource deferral.** Compiled markup defers every keyed resource, ThemeDictionaries included,
  until it is looked up. Text markup creates them eagerly unless the dictionary has at least 128 nodes and only keyed
  items (`CResourceDictionary::ShouldDeferNodeStream`). *Measured*: deferral, where text creates 4 resources and
  compiled creates 0. A text-based hot reload can therefore run constructors, or report errors in unused resources,
  that a clean build never would. Delivering the compiler's `.xbf` (approach B) has none of these differences.
- **x:Uid / `.resw`.** Needs a service method that swaps the app's MRT `ResourceManager` for one built from a new
  `resources.pri` (`ModernResourceProvider::m_appResourceManager`), plus the same per-thread refresh. Until then, a
  `.resw` edit needs a restart.
- **Setting `ResourceDictionary.Source` again on a dictionary in `Application.Resources`** fails with E_FAIL even
  without hot reload or diagnostics (resetsource-plain, `nodiag`, with and without this branch). Tools must replace
  the dictionary in its collection instead, which works (merged-swap).
- **Memory.** Each replaced `.xbf` is kept until shutdown in `m_XbfResourceStorage`. That keeps the XBFv2 reader
  cache's address keys unique and the reader-owned strings valid. The cost is the size of every compiled save. Freeing
  it needs readers that no longer hand out strings pointing into their buffer.
- **Frame page cache.** This is the tool's job. Re-run the cached pages' markup, or clear them through
  `Frame.CacheSize`.

## Cost for apps that don't use hot reload

- The implicit style flag costs one thread-local read, only on the path where the diagnostics feature is enabled.
- The dictionary mark is one bit set after each `LoadComponent`.
- The reset is skipped until a tool replaces markup (`IsActive()` is an atomic pointer load).
- Everything else in the prototype was already behind the generation counters, or behind the hot reload stamp and an
  attached diagnostics tool.
