XamlMetadataProviderRegistry
===

> **Status:** proposal, implemented as an experimental API (`WinUIContract` experimental version) on the fork
> branch `nmetulev-metadata-provider-api`. Sample: `tools\XamlMetadataProviderRegistrySample`.

# Background

XAML creates objects by name. When the parser sees `<local:Widget/>`, WinUI asks for the type
`Contoso.Controls.Widget`. Built-in WinUI types come from WinUI's own tables. Everything else comes from an
[`IXamlMetadataProvider`](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.markup.ixamlmetadataprovider):
the XAML compiler generates one for the app (`XamlTypeInfo.g.cs` / `XamlTypeInfo.g.cpp`), the app's `Application`
implements it, and the generated provider keeps a list of the providers of the libraries the app references
(`OtherProviders`). That list is fixed when the app is compiled.

So a library that the app didn't reference at build time can't be used from markup, even if the app loads it while
running. That rules out:

* **Plugins.** An app that loads a XAML control library at runtime (an extension, a downloaded module, a feature
  that ships separately) can create the library's controls from code but not from markup.
* **Tools.** A designer, a XAML previewer or a hot reload host that compiles new types while the app runs has no
  supported way to make them resolvable. The hot reload prototype added a hot-reload-only
  `IXamlHotReloadService.RegisterMetadataProvider`; review feedback (Evelyn Wu) was that registering and
  unregistering providers should be a general platform API rather than part of the hot reload service.

Apps work around this today by modifying the generated provider's list in memory (managed apps only, relying on
generated code that isn't a contract) or by rewriting markup to avoid the library's types.

This spec adds `Microsoft.UI.Xaml.Markup.XamlMetadataProviderRegistry`: a process-wide list of extra metadata
providers that WinUI consults after the app's own provider, with `Register` and `Unregister`.

# Conceptual pages (How To)

## Using controls from a library loaded at runtime

1. Load the library.
2. Create its metadata provider. A WinUI class library built with the XAML compiler has a generated, public
   `XamlMetaDataProvider` (`<RootNamespace>.<RootNamespace>_XamlTypeInfo.XamlMetaDataProvider` in C#,
   `<RootNamespace>.XamlMetaDataProvider` in C++/WinRT) that describes every type its markup uses. A library can
   also implement `IXamlMetadataProvider` itself.
3. Call `XamlMetadataProviderRegistry.Register(provider)` **before** you use any of the library's types (see
   [Register before first use](#register-before-first-use)).
4. Markup can now name the library's types: `XamlReader.Load`, `Application.LoadComponent`, and templates and
   resource dictionaries parsed from now on.
5. When you're done with the library, call `XamlMetadataProviderRegistry.Unregister(provider)`. Markup parsed after
   that fails for its types exactly as for any unknown type. Objects already created keep working.

## Lookup order

For every type name the parser resolves (and every `TypeName`-based lookup, such as `Frame.Navigate(typeof(T))`):

1. WinUI's built-in types.
2. The application's provider, including the providers of the libraries it referenced at build time.
3. Registered providers. All of them are asked.

Registered providers are a fallback only. A type that the app's provider describes always resolves through the app's
provider, whether or not a registered provider also describes it. Registering a provider can never change which type
an already-resolvable name means.

## When registered providers agree or disagree

Two providers "describe the same type" when the `IXamlType.UnderlyingType` they return for a name is the same
`TypeName` (name and kind). For WinRT types the name is the identity. For .NET types it's the assembly-qualified
name, so two different libraries (or two versions of one) that both define `Contoso.Card` describe different types.

| Situation | Result |
|---|---|
| One registered provider describes the name | The name resolves to its type. |
| Several describe the same type (two plugins that reference one control library; every library provider that also describes WinUI's own controls) | The name resolves to that type, once. All of them own it, and it stays resolvable until the last one is unregistered. The `IXamlType` used is the earliest registered provider's. |
| Several describe different types, and the name hasn't been used yet | The name is **ambiguous** and resolves to nothing: markup fails as for an unknown type. The outcome never depends on registration order. Unregistering all but one settles it. |
| A provider being registered describes a name already in use as a different type | `Register` **fails** (`HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS)`) and names the type. Nothing changes. Unregister the provider that supplied it first. |

"In use" means the name has resolved through a registered provider. WinUI can't enumerate a provider's types
(`IXamlMetadataProvider` has no such method), so a conflict over a name nobody has used yet shows up as an
ambiguity rather than as a failed `Register`.

## What Unregister does

* The provider is released and no longer asked about anything.
* Every name that resolved to a type **only that provider** supplied is forgotten, in WinUI's metadata caches and,
  on their next use, in every UI thread's parser caches. Markup parsed afterwards that names such a type fails as for
  any unknown type (`XamlParseException` with the line and column of the element).
* Types that another still-registered provider also supplies stay resolvable and keep their identity.
* **Objects already created keep working**, and so does markup already parsed into templates (`DataTemplate`,
  `ControlTemplate`), styles and resources: they refer to WinUI's entry for the type, not to its name, and WinUI keeps
  that entry, and the `IXamlType` behind it, for the life of the process.
* Unregistering is "hide", not "unload". Because those entries keep the library's `IXamlType` objects alive, a
  library whose types were used can't be unloaded (for .NET, a collectible `AssemblyLoadContext` won't be collected).
  See [Open questions](#open-questions).
* Registering the provider again makes its names resolve again, as new type entries.

## Register before first use

Register the provider before anything touches the library's types: before you create its controls in code and before
its static constructors run. A library registers its dependency properties in static constructors
(`DependencyProperty.Register(..., typeof(Badge), ...)`). If WinUI is asked about `typeof(Badge)` before the provider is
registered, it records the type as unknown and attaches those properties to that unknown entry; markup that sets
`Badge.Label` later won't find them as dependency properties. The same is true today for a type the app's own
provider doesn't describe.

## Resources the library needs

The registry makes a library's **types** resolvable. It doesn't make the library's **resources** available: a
`UserControl` or `Page` in the library loads its markup from `ms-appx:///<Library>/<Page>.xaml` (or `.xbf`) in
`InitializeComponent`, and a templated control's default style comes from `ms-appx:///<Library>/Themes/Generic.xaml`.
Those resolve only if the app's package (or, unpackaged, its folder and `resources.pri`) contains them. Loading a
library's resources at runtime is a separate problem (MRT Core) and out of scope here.

# Examples

## C#: load a plugin, use its controls, stop using it

```csharp
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;

public sealed class PluginHost
{
    private IXamlMetadataProvider? _provider;

    public void Load(string pluginPath)
    {
        Assembly plugin = AssemblyLoadContext.Default.LoadFromAssemblyPath(pluginPath);

        // The XAML compiler generates one public provider per library.
        Type providerType = plugin.GetExportedTypes()
            .Single(t => typeof(IXamlMetadataProvider).IsAssignableFrom(t) && !t.IsAbstract);
        _provider = (IXamlMetadataProvider)Activator.CreateInstance(providerType)!;

        // Throws if the plugin redefines a type another plugin already supplied.
        XamlMetadataProviderRegistry.Register(_provider);
    }

    public UIElement CreateBadge() =>
        (UIElement)XamlReader.Load(
            "<p:Badge xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
            "xmlns:p='using:PluginControls' Label='hello' Count='3'/>");

    public void Unload()
    {
        if (_provider != null)
        {
            // From now on, markup that names PluginControls types fails to parse.
            // Badges created above keep working.
            XamlMetadataProviderRegistry.Unregister(_provider);
            _provider = null;
        }
    }
}
```

## C++/WinRT: register the provider of a component loaded at runtime

```cpp
#include <winrt/Microsoft.UI.Xaml.h>
#include <winrt/Microsoft.UI.Xaml.Markup.h>

using namespace winrt;
using namespace winrt::Microsoft::UI::Xaml;
using namespace winrt::Microsoft::UI::Xaml::Markup;

// A C++/WinRT component built with the XAML compiler exposes its generated provider as an activatable class,
// <RootNamespace>.XamlMetaDataProvider. Activate it from the DLL the app just loaded.
IXamlMetadataProvider LoadPluginProvider(HMODULE plugin, hstring const& providerClass)
{
    using GetFactory = HRESULT(__stdcall*)(HSTRING, ::IActivationFactory**);
    auto getFactory = reinterpret_cast<GetFactory>(::GetProcAddress(plugin, "DllGetActivationFactory"));
    check_bool(getFactory != nullptr);

    com_ptr<::IActivationFactory> factory;
    check_hresult(getFactory(static_cast<HSTRING>(get_abi(providerClass)), factory.put()));

    com_ptr<::IInspectable> instance;
    check_hresult(factory->ActivateInstance(instance.put()));
    return instance.as<IXamlMetadataProvider>();
}

void UsePlugin(HMODULE plugin)
{
    auto provider = LoadPluginProvider(plugin, L"Contoso.Widgets.XamlMetaDataProvider");
    XamlMetadataProviderRegistry::Register(provider);

    auto gauge = XamlReader::Load(
        LR"(<w:Gauge xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                     xmlns:w='using:Contoso.Widgets' Value='42'/>)").as<UIElement>();

    // ...

    XamlMetadataProviderRegistry::Unregister(provider);
    // Don't FreeLibrary(plugin): objects it created, and the type entries WinUI keeps for them, still use its code.
}
```

## Plugin walkthrough

The sample in `tools\XamlMetadataProviderRegistrySample` does this end to end with two projects:

* `PluginControls`: a WinUI class library with two controls, `Badge` (a `ContentControl` with `Label` and `Count`
  dependency properties) and `StarRating` (a `StackPanel` whose `Stars` property creates children). A resource
  dictionary names both, so the XAML compiler puts them and their members in the library's generated
  `PluginControls.PluginControls_XamlTypeInfo.XamlMetaDataProvider`.
* `PluginHost`: an unpackaged WinUI app with **no reference** to `PluginControls`. `run.ps1` builds both and drops
  `PluginControls.dll` into `plugins\` next to the host.

The host, without user input:

1. Parses `<p:Badge xmlns:p="using:PluginControls" Label="hello" Count="3"/>`: fails (unknown type).
2. Loads `plugins\PluginControls.dll`, finds and creates the generated provider, and calls `Register`.
3. Parses the same markup: a `PluginControls.Badge` with `Label == "hello"` and `Count == 3`. Adds it to the window,
   waits for `Loaded`, and checks it renders `hello (3)`. Parses
   `<StackPanel><p:StarRating Stars="4"/></StackPanel>`: four children.
4. Calls `Register` again: no error, still registered.
5. Calls `Unregister`: `true`; `IsRegistered` is now `false`.
6. Parses both snippets again: both fail with `XamlParseException`.
7. Sets `Label` on the badge created in step 3: it still updates and is still in the tree.
8. `Unregister` again returns `false`; `Register(null)` throws `ArgumentException`.
9. Registers again: the markup parses again.

# Remarks

* **Scope.** The registry is process-wide: one list for every window, UI thread and XAML island in the process.
* **Threading.** All three methods can be called from any thread, including one with no XAML on it (the class is
  free-threaded). A change is visible to every UI thread on its next lookup: each thread's parser compares process-wide
  counters with the values its caches were filled under and refreshes them itself, so no thread touches another
  thread's caches. Markup being parsed on another thread during the call may or may not see the change.
* **Calls into the provider.** `Register` asks the new provider about every name registered providers have already
  supplied (to detect conflicts), without holding WinUI's metadata lock, so the provider may call back into XAML. On
  the lookup path providers are called with WinUI's metadata lock held, exactly like the app's own provider today, so a
  provider must not block on other threads while it answers. A provider that fails is treated as not describing the
  name.
* **Identity.** Providers are compared by COM identity: registering the same object twice is a no-op, even through
  different interface pointers.
* **Lifetime.** The registry holds a strong reference to each provider until it's unregistered. Type entries for types
  that resolved through a provider hold that type's `IXamlType` for the life of the process.
* **Limits.** At most 256 providers can be registered at once. Every name the app's provider doesn't know is offered to
  every registered provider, so keep the number small.
* **Hot reload.** `IXamlHotReloadService.RegisterMetadataProvider` (XAML diagnostics) is a thin call into `Register`,
  gated on the hot reload build stamp: same registry, same rules, same errors. Unregister with
  `XamlMetadataProviderRegistry.Unregister`.

# API Pages

## XamlMetadataProviderRegistry class

Process-wide list of extra `IXamlMetadataProvider`s that WinUI asks about type names the application's own provider
doesn't know.

```csharp
public static class XamlMetadataProviderRegistry
```

### XamlMetadataProviderRegistry.Register method

```csharp
public static void Register(IXamlMetadataProvider provider);
```

Adds `provider` to the list. Names that failed to resolve before may resolve from now on. Registering a provider that
is already registered does nothing.

| Error | When |
|---|---|
| `ArgumentException` (`E_INVALIDARG`) | `provider` is null. |
| `HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS)` | `provider` describes a name already in use as a different type. The message names the type. |
| `E_BOUNDS` | 256 providers are already registered. |
| `E_FAIL` | `provider` failed when asked about a name already in use. |
| `E_CHANGED_STATE` | Other threads kept changing the list while this call validated `provider` (it retries internally first). Safe to retry. |

### XamlMetadataProviderRegistry.Unregister method

```csharp
public static bool Unregister(IXamlMetadataProvider provider);
```

Removes `provider` and releases it. Returns `false` if it wasn't registered. Names that only `provider` supplied stop
resolving; objects already created, and markup already parsed into templates, styles and resources, keep working.

### XamlMetadataProviderRegistry.IsRegistered method

```csharp
public static bool IsRegistered(IXamlMetadataProvider provider);
```

Returns whether `provider` (compared by COM identity) is registered.

# API Details

```c# (but really MIDL3)
namespace Microsoft.UI.Xaml.Markup
{
    [contract(Microsoft.UI.Xaml.WinUIContract, 12)]   // the experimental contract version
    [webhosthidden]
    static runtimeclass XamlMetadataProviderRegistry
    {
        static void Register(IXamlMetadataProvider provider);
        static Boolean Unregister(IXamlMetadataProvider provider);
        static Boolean IsRegistered(IXamlMetadataProvider provider);
    };
}
```

# Appendix

## Implementation notes

* **Rules.** `dxaml\xcp\core\inc\XamlMetadataProviderRegistry.h`, standard C++ only, so the rules above are unit
  tested without the XAML build (`dxaml\xcp\components\metadata\unittests\run-portable.ps1`). It never calls a
  provider. Registration is begin (under the lock), probe (outside it), commit (under the lock; refused if the list or
  the set of names in use changed meanwhile, and `MetadataAPI` then retries up to 8 times).
* **Claims.** Each type resolved through registered providers gets a claim: its key (the underlying `TypeName`), the
  providers that own it, and every way it was looked up (by full name from markup; by `TypeName` for .NET types,
  whose `TypeName` is assembly-qualified). A new provider is probed with those same questions. Unregistering removes
  the provider from every claim and reports the claims left with no owner ("orphaned").
* **Metadata caches** (`MetadataAPI`, `DynamicMetadataStorage`). A name that fails to resolve is cached as a
  placeholder type entry; `Register` evicts placeholders. Each type entry created from a registered provider is tagged
  with its claim, and `Unregister` evicts the name mappings (by full name and by assembly-qualified name) of orphaned
  claims, plus placeholders (an ambiguity may have been settled). The entries themselves stay: `KnownTypeIndex` values
  are dense indices into the entry vector, and live objects refer to them.
* **Parser caches** (one set per UI thread). Two process-wide counters in `XamlTypeCacheGenerations.h`. `g_typesAdded`
  (bumped by `Register`, `Unregister` and the hot reload service's `InvalidateTypeCaches`) makes each
  `XamlXmlNamespace` drop its "type not found" set. `g_typesRemoved` (bumped by `Unregister`) also makes it drop its
  name-to-`XamlType` map, makes `XamlSchemaContext` drop its fully qualified property cache, and makes the node stream
  cache drop cached node lists and XBF readers (readers are kept alive, as on a metadata reset, because they own string
  storage). `XamlSchemaContext`'s token-keyed type list isn't touched: a re-resolved name gets a new type entry and so a
  new token.
* **Unchanged.** Built-in types, the app's provider and everything it resolves. When nothing is registered a miss
  costs one extra count check under the existing lock.

## Alternatives considered

* **On the hot reload service only** (the prototype). Rejected in review: the need isn't specific to hot reload.
* **`Application.RegisterMetadataProvider`.** The registry is process-wide, not per `Application`, and registering
  before the `Application` exists, or from a background thread, is useful. A static class says that.
* **First registered wins** instead of failing closed on ambiguity. It makes behavior depend on load order, which for
  plugins is often nondeterministic. Agreement on the same type is allowed, which covers the common shared-dependency
  case, so failing closed only hits real ambiguities.
* **Registered providers before the app's provider.** Would let a plugin replace an app type and break the app's own
  markup in ways the app can't see.
* **A registration token (`IClosable`) instead of `Unregister(provider)`.** Equivalent. A `Register`/`Unregister` pair
  matches the request ("register and unregister") and needs no extra type.
* **Return a status instead of throwing on conflict.** A conflict is a programming error in the plugin set, and WinRT
  reports those with exceptions.

## Open questions

1. **Truly unloading a library.** Should `Unregister` (or a new method) drop the `IXamlType` references held by type
   entries no live object uses, so a collectible `AssemblyLoadContext` can unload? That needs a way to know an entry is
   unused (objects, templates, styles and DP registrations all refer to it by index).
2. **Register after first use.** Should WinUI repair a placeholder that already has dependency properties attached
   (move them to the real type) instead of documenting "register before first use"?
3. **Generated providers.** Should the XAML compiler generate a helper on libraries (for example static
   `Register()`/`Unregister()` methods) so hosts don't find the generated provider by reflection?
4. **Diagnostics.** An ambiguous name fails as an unknown type today. Should the parse error say which providers
   disagree?
5. **Generated app providers.** Generated providers aggregate `OtherProviders` from referenced libraries. Should that
   list go through the registry too (one set of rules for build-time and runtime libraries), or stay as it is?
