// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

// IXamlHotReloadService: lets the diagnostics tool this instance is sited with replace the app's markup while it runs.
// Every method refuses unless the app was built with the hot reload stamp (XamlHotReloadOverrides::IsEnabledForApp).

#include "precomp.h"
#include "XamlDiagnostics.h"
#include "MetadataAPI.h"
#include <XamlHotReloadOverrides.h>
#include <XamlHotReloadRegistry.h>
#include <roerrorapi.h>

namespace
{
    using ListenerList = XamlHotReload::CookieList<wrl::ComPtr<IXamlComponentLoadedCallback>>;

    // Intentionally leaked: tool callbacks must not be released during process shutdown.
    ListenerList& Listeners()
    {
        static auto* listeners = new ListenerList();
        return *listeners;
    }

    std::atomic<bool> s_hasListeners{ false };

    HRESULT Refuse(HRESULT hr, _In_z_ const WCHAR* message)
    {
        RoOriginateErrorW(hr, 0, message);
        return hr;
    }

    HRESULT EnsureEnabled()
    {
        return XamlHotReloadOverrides::IsEnabledForApp()
            ? S_OK
            : Refuse(E_XAMLHOTRELOAD_NOTENABLED,
                L"XAML hot reload is not enabled for this app. Build it in the Debug configuration, or set the "
                L"EnableXamlHotReload project property to true.");
    }
}

void XamlHotReloadOverrides::InvokeLoadCallbacks(_In_ IInspectable* component, _In_z_ const WCHAR* uri)
{
    if (!s_hasListeners.load(std::memory_order_acquire))
    {
        return;
    }

    // Snapshot first: a listener may unsubscribe from inside its own callback.
    for (const auto& listener : Listeners().Snapshot())
    {
        IGNOREHR(listener->OnComponentLoaded(component, uri));
    }
}

IFACEMETHODIMP XamlDiagnostics::SetComponentMarkup(
    _In_z_ LPCWSTR uri,
    XamlMarkupKind kind,
    UINT32 size,
    _In_reads_(size) const BYTE* content)
{
    IFC_RETURN(EnsureEnabled());
    if (uri == nullptr || content == nullptr || size == 0)
    {
        return E_INVALIDARG;
    }

    XamlHotReloadOverrides::OverrideKind overrideKind;
    switch (kind)
    {
        case XamlMarkupKind_Text: overrideKind = XamlHotReloadOverrides::OverrideKind::Text; break;
        case XamlMarkupKind_Binary: overrideKind = XamlHotReloadOverrides::OverrideKind::Binary; break;
        default: return E_INVALIDARG;
    }

    const WCHAR* reason = nullptr;
    const HRESULT hr = XamlHotReloadOverrides::SetMarkup(uri, overrideKind, content, size, &reason);
    return FAILED(hr) && reason ? Refuse(hr, reason) : hr;
}

IFACEMETHODIMP XamlDiagnostics::RemoveComponentMarkup(_In_opt_z_ LPCWSTR uri)
{
    IFC_RETURN(EnsureEnabled());
    return XamlHotReloadOverrides::RemoveMarkup(uri);
}

IFACEMETHODIMP XamlDiagnostics::AdviseComponentLoaded(
    _In_ IXamlComponentLoadedCallback* callback,
    _Out_ DWORD* cookie)
{
    if (cookie == nullptr)
    {
        return E_POINTER;
    }
    *cookie = 0;
    IFC_RETURN(EnsureEnabled());
    if (callback == nullptr)
    {
        return E_INVALIDARG;
    }

    *cookie = Listeners().Add(wrl::ComPtr<IXamlComponentLoadedCallback>(callback));
    s_hasListeners.store(true, std::memory_order_release);
    return S_OK;
}

IFACEMETHODIMP XamlDiagnostics::UnadviseComponentLoaded(DWORD cookie)
{
    if (!Listeners().Remove(cookie))
    {
        return E_INVALIDARG;
    }
    s_hasListeners.store(!Listeners().Empty(), std::memory_order_release);
    return S_OK;
}

IFACEMETHODIMP XamlDiagnostics::RegisterMetadataProvider(_In_ IInspectable* provider)
{
    IFC_RETURN(EnsureEnabled());
    if (provider == nullptr)
    {
        return E_INVALIDARG;
    }

    wrl::ComPtr<xaml_markup::IXamlMetadataProvider> metadataProvider;
    IFC_RETURN(provider->QueryInterface(IID_PPV_ARGS(&metadataProvider)));

    const auto result = DirectUI::MetadataAPI::RegisterSideMetadataProvider(metadataProvider.Get());
    if (result.Status != DirectUI::XamlMetadataProviderRegistrationStatus::Registered &&
        result.Status != DirectUI::XamlMetadataProviderRegistrationStatus::AlreadyRegistered)
    {
        return Refuse(E_FAIL, L"The metadata provider was refused (the registry is full or the provider is invalid).");
    }

    return InvalidateTypeCaches();
}

IFACEMETHODIMP XamlDiagnostics::InvalidateTypeCaches()
{
    IFC_RETURN(EnsureEnabled());
    DirectUI::MetadataAPI::InvalidateUnresolvedTypeCache();
    XamlHotReloadOverrides::g_typeGeneration.fetch_add(1);
    return S_OK;
}
