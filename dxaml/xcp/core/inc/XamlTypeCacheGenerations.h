// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

#pragma once

#include <atomic>
#include <cstdint>

// Process-wide counters that tell each UI thread's parser caches the set of resolvable XAML type names changed.
// Each cache remembers the values it last saw and refreshes itself on its next use, so no thread touches another
// thread's caches. Header-only so the parser, which also ships in binaries without the metadata registry (GenXbf),
// can read them without linking anything.
namespace XamlTypeCacheGenerations
{
    // Names that failed to resolve may now resolve: an IXamlMetadataProvider was registered
    // (XamlMetadataProviderRegistry.Register), or a tool made new types available (IXamlHotReloadService).
    // Parser "type not found" caches compare against it.
    inline std::atomic<std::uint32_t> g_typesAdded{ 0 };

    // Names that resolved may no longer resolve: a registered provider was unregistered. Parser name -> type caches,
    // fully qualified property caches and cached node streams compare against it.
    inline std::atomic<std::uint32_t> g_typesRemoved{ 0 };

    // Types that resolved may have new members: a tool updated their code while the app runs
    // (IXamlHotReloadService::InvalidateTypeCaches / IXamlHotReloadService3::InvalidateTypes). The resolved types keep
    // their identity; the parser's per-type property caches (which also remember "no such property") compare against it.
    inline std::atomic<std::uint32_t> g_typesChanged{ 0 };
}
