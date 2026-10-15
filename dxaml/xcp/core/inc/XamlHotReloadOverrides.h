// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

#pragma once

#include <atomic>
#include <cstdint>
#include <vector>

// Experimental XAML hot reload support. The only entry point is IXamlHotReloadService on the XamlDiagnostics
// instance a diagnostics tool is sited with (see XamlOM.WinUI.idl); this header is the framework-internal side.
struct IPALUri;
struct IPALResource;
struct IInspectable;
class CDependencyObject;

namespace XamlHotReloadOverrides
{
    enum class OverrideKind : std::uint8_t { None = 0, Text = 1, Binary = 2 };

    // Bumped whenever replaced markup changes. Each UI thread's caches remember the value they last saw and refresh
    // themselves on their next use, so no thread ever touches another thread's caches. Inline (not in the registry)
    // so the parser, which also ships in binaries without the registry (GenXbf), can read it without linking it.
    inline std::atomic<std::uint32_t> g_markupGeneration{ 0 };

    // Bumped when types become available after startup; parser "type not found" caches compare against it.
    inline std::atomic<std::uint32_t> g_typeGeneration{ 0 };

    // Set the first time markup is replaced. Same link-free reasoning as above.
    using KindProbe = OverrideKind (*)(const WCHAR* uri, size_t count);
    inline std::atomic<KindProbe> g_kindProbe{ nullptr };

    inline OverrideKind ProbeKind(const WCHAR* uri, size_t count)
    {
        auto probe = g_kindProbe.load();
        return probe ? probe(uri, count) : OverrideKind::None;
    }

    // True once any markup was replaced in this process. Until then nothing hot reload adds changes behavior.
    inline bool IsActive()
    {
        return g_kindProbe.load(std::memory_order_relaxed) != nullptr;
    }

    // Set by Application.LoadComponent (stamped apps only) while it parses a component's markup: the parser adds the
    // index (KnownPropertyIndex) of every property it sets on Root. Thread-local and link-free for the same reason as
    // above; the parser only reads it.
    struct RootPropertyRecording
    {
        const void* Root = nullptr;
        std::vector<std::uint16_t>* Properties = nullptr;
    };
    inline thread_local RootPropertyRecording* t_rootRecording = nullptr;

    // Makes IsActive() true: replaced markup, or a tool asking to load components again.
    void Activate();

    // Sets *ppResource to a resource holding the replaced markup for pUri, or nullptr when it has none.
    _Check_return_ HRESULT TryGetOverrideResource(_In_ IPALUri* pUri, _Outptr_result_maybenull_ IPALResource** ppResource);

    // IXamlHotReloadService (XamlDiagnostics) entry points. *reason receives why a URI was refused.
    _Check_return_ HRESULT SetMarkup(
        _In_z_ const WCHAR* uri,
        OverrideKind kind,
        _In_reads_(size) const BYTE* content,
        UINT32 size,
        _Outptr_result_maybenull_z_ const WCHAR** reason);
    _Check_return_ HRESULT RemoveMarkup(_In_opt_z_ const WCHAR* uri);

    // Called by Application.LoadComponent after each successful load (implemented with the service).
    void InvokeLoadCallbacks(_In_ IInspectable* component, _In_z_ const WCHAR* uri);

    // Called by Application.LoadComponent after each successful load in a stamped app, before the load callbacks:
    // remembers the component so ReloadComponents can find it, and, when this load ran replaced markup on a component
    // loaded before, clears the root properties the previous markup set and this one didn't
    // (XamlHotReloadComponents.cpp).
    void OnComponentLoaded(
        _In_ IInspectable* component,
        _In_ CDependencyObject* root,
        _In_z_ const WCHAR* uri,
        std::int32_t resourceLocation,
        std::vector<std::uint16_t> rootProperties);

    // IXamlHotReloadService::ReloadComponents: runs Application.LoadComponent again for every live component loaded from
    // uri, each on its own thread. *count receives how many loaded; the first failure is returned.
    _Check_return_ HRESULT ReloadComponents(_In_z_ const WCHAR* uri, _Out_ UINT32* count);

    // IXamlHotReloadService::GetLiveComponents: the calling thread's live components loaded from uri, AddRef'd, in a
    // CoTaskMemAlloc'd array.
    _Check_return_ HRESULT GetLiveComponents(_In_z_ const WCHAR* uri, _Out_ UINT32* count, _Outptr_result_buffer_maybenull_(*count) IInspectable*** components);

    // True when the app was built with the hot reload stamp. Computed once.
    bool IsEnabledForApp();
}
