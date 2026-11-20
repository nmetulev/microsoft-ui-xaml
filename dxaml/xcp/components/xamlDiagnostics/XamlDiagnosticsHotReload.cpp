// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

// IXamlHotReloadService (XamlOM.WinUI.idl): lets the diagnostics tool this instance is sited with give the app new
// compiled markup while it runs. Every method refuses unless the app carries the hot reload stamp.

#include "precomp.h"
#include "XamlDiagnostics.h"
#include "MetadataAPI.h"
#include <RuntimeEnabledFeatures.h>
#include <XamlHotReload.h>
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

    HRESULT EnsureAllowed()
    {
        return XamlHotReload::IsAllowed()
            ? S_OK
            : Refuse(E_XAMLHOTRELOAD_NOTENABLED,
                L"XAML hot reload is not enabled for this app. Build it in a debug configuration, or set the "
                L"EnableXamlHotReload project property to true.");
    }
}

bool XamlHotReload::IsAllowed()
{
    return RuntimeFeatureBehavior::GetRuntimeEnabledFeatureDetector()->IsFeatureEnabled(
               RuntimeFeatureBehavior::RuntimeEnabledFeature::XamlDiagnostics) &&
           IsEnabledForApp();
}

void XamlHotReload::InvokeLoadCallbacks(_In_ IInspectable* component, _In_z_ const wchar_t* uri)
{
    if (!s_hasListeners.load(std::memory_order_acquire))
    {
        return;
    }

    for (const auto& listener : Listeners().Snapshot())
    {
        IGNOREHR(listener->OnComponentLoaded(component, uri));
    }
}

IFACEMETHODIMP XamlDiagnostics::SetComponentXbf(
    _In_z_ LPCWSTR uri,
    UINT32 size,
    _In_reads_(size) const BYTE* xbf)
{
    IFC_RETURN(EnsureAllowed());
    if (uri == nullptr || xbf == nullptr || size == 0)
    {
        return E_INVALIDARG;
    }

    const WCHAR* reason = nullptr;
    const HRESULT hr = XamlHotReload::SetComponentXbf(uri, xbf, size, &reason);
    return FAILED(hr) && reason ? Refuse(hr, reason) : hr;
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
    IFC_RETURN(EnsureAllowed());
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
    IFC_RETURN(EnsureAllowed());
    if (!Listeners().Remove(cookie))
    {
        return E_INVALIDARG;
    }
    s_hasListeners.store(!Listeners().Empty(), std::memory_order_release);
    return S_OK;
}

IFACEMETHODIMP XamlDiagnostics::InvalidateTypeCaches()
{
    IFC_RETURN(EnsureAllowed());
    DirectUI::MetadataAPI::InvalidateCustomTypeCaches();
    XamlHotReload::g_typeCacheGeneration.fetch_add(1);
    return S_OK;
}
