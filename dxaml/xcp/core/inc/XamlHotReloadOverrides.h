// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

#pragma once

#include <atomic>

// Experimental hot reload markup delivery channel.
//
// A hot reload agent running inside the app can redirect a local resource URI
// (for example ms-appx:///MainPage.xbf) to a file on disk that holds freshly
// compiled markup. Every local resource lookup consults this table first, so
// the next Application.LoadComponent / InitializeComponent for that URI parses
// the new markup, including URIs that did not exist when the app was packaged
// (new pages).
struct IPALUri;
struct IPALResource;
struct IInspectable;

namespace XamlHotReloadOverrides
{
    // Registers, replaces, or (when filePath is null or empty) removes an override.
    _Check_return_ HRESULT SetOverride(_In_z_ const WCHAR* uri, _In_opt_z_ const WCHAR* filePath);

    void ClearAll();

    // Sets *ppResource to a resource backed by the override file, or to nullptr
    // when no override is registered for pUri.
    _Check_return_ HRESULT TryGetOverrideResource(_In_ IPALUri* pUri, _Outptr_result_maybenull_ IPALResource** ppResource);

    // True when uri (case-insensitive canonical form) is overridden with TEXT markup (a .xaml file). The
    // compiled .xbf lookup for that URI must then be skipped so the text is parsed instead.
    bool HasTextOverride(_In_reads_(count) const WCHAR* uri, size_t count);

    // Link-free hook for code (the parser) that also ships in binaries without this registry (GenXbf).
    // Set the first time an override is registered.
    using TextOverrideProbe = bool (*)(const WCHAR* uri, size_t count);
    inline std::atomic<TextOverrideProbe> g_textOverrideProbe{ nullptr };

    // Called after every successful Application.LoadComponent while registered, so a hot reload agent can
    // finish wiring a component built from override markup (fields, event handlers, bindings).
    typedef void (WINAPI *LoadCallback)(_In_ IInspectable* component, _In_z_ const WCHAR* uri, _In_opt_ void* context);
    void SetLoadCallback(_In_opt_ LoadCallback callback, _In_opt_ void* context);
    void InvokeLoadCallback(_In_ IInspectable* component, _In_z_ const WCHAR* uri);
}
