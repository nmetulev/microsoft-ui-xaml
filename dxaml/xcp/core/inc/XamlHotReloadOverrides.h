// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

#pragma once

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

namespace XamlHotReloadOverrides
{
    // Registers, replaces, or (when filePath is null or empty) removes an override.
    _Check_return_ HRESULT SetOverride(_In_z_ const WCHAR* uri, _In_opt_z_ const WCHAR* filePath);

    void ClearAll();

    // Sets *ppResource to a resource backed by the override file, or to nullptr
    // when no override is registered for pUri.
    _Check_return_ HRESULT TryGetOverrideResource(_In_ IPALUri* pUri, _Outptr_result_maybenull_ IPALResource** ppResource);
}
