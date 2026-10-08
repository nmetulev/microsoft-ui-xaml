// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

#pragma once

#include <atomic>
#include <cstdint>

// Experimental XAML hot reload support. The only entry point is IXamlHotReloadService on the XamlDiagnostics
// instance a diagnostics tool is sited with (see XamlOM.WinUI.idl); this header is the framework-internal side.
struct IPALUri;
struct IPALResource;
struct IInspectable;

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

    // True when the app was built with the hot reload stamp. Computed once.
    bool IsEnabledForApp();
}
