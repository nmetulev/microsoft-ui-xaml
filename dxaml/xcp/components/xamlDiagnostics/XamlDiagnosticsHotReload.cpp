// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

// IXamlHotReloadService: lets the diagnostics tool this instance is sited with replace the app's markup while it runs.
// Every method refuses unless the app was built with the hot reload stamp (XamlHotReloadOverrides::IsEnabledForApp).

#include "precomp.h"
#include "XamlDiagnostics.h"
#include "MetadataAPI.h"
#include <XamlHotReloadOverrides.h>
#include <XamlHotReloadRegistry.h>
#include <XamlTypeCacheGenerations.h>
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

IFACEMETHODIMP XamlDiagnostics::ReloadComponents(_In_z_ LPCWSTR uri, _Out_ UINT32* count)
{
    if (count == nullptr)
    {
        return E_POINTER;
    }
    *count = 0;
    IFC_RETURN(EnsureEnabled());
    if (uri == nullptr)
    {
        return E_INVALIDARG;
    }
    return XamlHotReloadOverrides::ReloadComponents(uri, count);
}

IFACEMETHODIMP XamlDiagnostics::GetLiveComponents(
    _In_z_ LPCWSTR uri,
    _Out_ UINT32* count,
    _Outptr_result_buffer_maybenull_(*count) IInspectable*** components)
{
    if (count == nullptr || components == nullptr)
    {
        return E_POINTER;
    }
    *count = 0;
    *components = nullptr;
    IFC_RETURN(EnsureEnabled());
    if (uri == nullptr)
    {
        return E_INVALIDARG;
    }
    return XamlHotReloadOverrides::GetLiveComponents(uri, count, components);
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

// A thin gate over the general API, Microsoft.UI.Xaml.Markup.XamlMetadataProviderRegistry.Register: same registry, same
// rules, same errors. The service only adds the hot reload stamp check.
IFACEMETHODIMP XamlDiagnostics::RegisterMetadataProvider(_In_ IInspectable* provider)
{
    IFC_RETURN(EnsureEnabled());
    if (provider == nullptr)
    {
        return E_INVALIDARG;
    }

    wrl::ComPtr<xaml_markup::IXamlMetadataProvider> metadataProvider;
    IFC_RETURN(provider->QueryInterface(IID_PPV_ARGS(&metadataProvider)));

    std::wstring message;
    const HRESULT hr = DirectUI::DescribeRegistrationResult(
        DirectUI::MetadataAPI::RegisterSideMetadataProvider(metadataProvider.Get()), message);
    return FAILED(hr) ? Refuse(hr, message.c_str()) : S_OK;
}

IFACEMETHODIMP XamlDiagnostics::InvalidateTypeCaches()
{
    IFC_RETURN(EnsureEnabled());
    DirectUI::MetadataAPI::InvalidateUnresolvedTypeCache();
    XamlTypeCacheGenerations::g_typesAdded.fetch_add(1, std::memory_order_relaxed);
    // Types that already resolved may have new members too: every resolved custom type is asked for again on its next
    // member miss.
    DirectUI::MetadataAPI::MarkCustomTypesChanged(nullptr, 0);
    XamlTypeCacheGenerations::g_typesChanged.fetch_add(1, std::memory_order_relaxed);
    return S_OK;
}

IFACEMETHODIMP XamlDiagnostics::InvalidateTypes(
    UINT32 count,
    _In_reads_(count) LPCWSTR* fullNames,
    _Out_ UINT32* marked)
{
    if (marked == nullptr)
    {
        return E_POINTER;
    }
    *marked = 0;
    IFC_RETURN(EnsureEnabled());
    if (count > 0 && fullNames == nullptr)
    {
        return E_INVALIDARG;
    }

    std::vector<xstring_ptr> names;
    names.reserve(count);
    for (UINT32 i = 0; i < count; ++i)
    {
        if (fullNames[i] == nullptr || fullNames[i][0] == L'\0')
        {
            return E_INVALIDARG;
        }
        xstring_ptr name;
        IFC_RETURN(xstring_ptr::CloneBuffer(fullNames[i], &name));
        names.push_back(std::move(name));
    }
    if (names.empty())
    {
        return S_OK;
    }

    *marked = static_cast<UINT32>(DirectUI::MetadataAPI::MarkCustomTypesChanged(names.data(), names.size()));
    if (*marked > 0)
    {
        XamlTypeCacheGenerations::g_typesChanged.fetch_add(1, std::memory_order_relaxed);
    }
    return S_OK;
}