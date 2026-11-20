// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

#pragma once

#include <atomic>
#include <cstdint>
#include <vector>

// XAML hot reload, framework side. The only entry point is IXamlHotReloadService on the XamlDiagnostics instance a
// diagnostics tool is sited with (XamlOM.WinUI.idl). It works only in apps built with the hot reload stamp
// (XamlHotReloadRegistry.h) while the tool is attached; until a tool replaces markup, nothing here changes behavior.
//
// The counters and the probe are inline so the parser, which also ships in binaries without the rest (GenXbf), can
// read them without linking anything. Each UI thread's caches remember the values they last saw and refresh
// themselves on their next use, so no thread touches another thread's caches.
struct IPALUri;
struct IPALResource;
struct IInspectable;

namespace XamlHotReload
{
    // Bumped by every replaced .xbf.
    inline std::atomic<std::uint32_t> g_markupGeneration{ 0 };

    // Bumped when the tool says types changed (new types, or new members on types already used).
    inline std::atomic<std::uint32_t> g_typeCacheGeneration{ 0 };

    // Set when the first .xbf is replaced: whether a component URI (.xaml or .xbf) has a replaced .xbf.
    using ReplacedXbfProbe = bool (*)(const wchar_t* uri, std::size_t count);
    inline std::atomic<ReplacedXbfProbe> g_replacedXbfProbe{ nullptr };

    // True once a tool replaced any .xbf in this process.
    inline bool IsActive()
    {
        return g_replacedXbfProbe.load(std::memory_order_relaxed) != nullptr;
    }

    inline bool HasReplacedXbf(const wchar_t* uri, std::size_t count)
    {
        const auto probe = g_replacedXbfProbe.load(std::memory_order_relaxed);
        return probe != nullptr && probe(uri, count);
    }

    // Set by Application.LoadComponent while it parses a component's markup, when hot reload is allowed: the parser
    // adds the index (KnownPropertyIndex) of every property it sets on Root, so a later load of replaced markup can
    // clear the ones it no longer sets.
    struct RootPropertyRecording
    {
        const void* Root = nullptr;
        std::vector<std::uint16_t>* Properties = nullptr;
    };
    inline thread_local RootPropertyRecording* t_rootRecording = nullptr;

    // True when the app carries the hot reload stamp (computed once).
    bool IsEnabledForApp();

    // True when the app carries the stamp and a diagnostics tool is attached.
    bool IsAllowed();

    // IXamlHotReloadService::SetComponentXbf. *reason receives why the URI or content was refused.
    _Check_return_ HRESULT SetComponentXbf(
        _In_z_ const wchar_t* uri,
        _In_reads_(size) const std::uint8_t* content,
        std::uint32_t size,
        _Outptr_result_maybenull_z_ const wchar_t** reason);

    // Sets *ppResource to the replaced .xbf for pUri, or nullptr when it has none.
    _Check_return_ HRESULT TryGetReplacedXbf(_In_ IPALUri* pUri, _Outptr_result_maybenull_ IPALResource** ppResource);

    // Called by Application.LoadComponent after each successful load.
    void InvokeLoadCallbacks(_In_ IInspectable* component, _In_z_ const wchar_t* uri);
}
