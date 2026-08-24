// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

//  Abstract:
//      Contract types for the side IXamlMetadataProvider registry.
//
//      A "side" metadata provider is an additional IXamlMetadataProvider that is registered
//      with the running process *after* startup, without regenerating the fixed type tables
//      that the XAML compiler emits into an application's generated XamlTypeInfo. It exists so
//      that tooling (for example a hot reload host) can teach a running application about types
//      that did not exist when the application's generated provider was compiled.
//
//      The registry is intentionally additive: the application's own metadata provider is always
//      consulted first, and side providers only ever act as a fallback for names that would
//      otherwise fail to resolve. That guarantees that registering a side provider can never
//      change the identity of a type that already resolves.
//
//      Every mutation of the registry bumps a process-wide monotonic generation. Callers can
//      observe the generation to detect that their registration actually took effect, so a stale
//      cache cannot silently hide a registration.

#pragma once

#include <cstdint>

namespace DirectUI
{
    // Monotonically increasing counter describing the state of the side provider registry.
    // Bumped by every successful registration, unregistration and cache invalidation.
    using XamlMetadataProviderGeneration = std::uint64_t;

    // Stable, non-zero identifier handed out for each successfully registered side provider.
    using XamlMetadataProviderId = std::uint64_t;

    inline constexpr XamlMetadataProviderId c_invalidXamlMetadataProviderId = 0;

    // Upper bound on simultaneously registered side providers. This registry is a targeted hot
    // reload facility, not a general plugin system, so the bound is deliberately small.
    inline constexpr std::size_t c_maxSideXamlMetadataProviders = 64;

    enum class XamlMetadataProviderRegistrationStatus : std::uint32_t
    {
        // The provider was added to the registry by this call. ProviderId is valid.
        Registered = 0,

        // This exact provider instance was already registered. ProviderId is the id handed out by
        // the original registration and the generation is unchanged. Registration is idempotent.
        AlreadyRegistered = 1,

        // The provider answers for at least one type name that an already registered side provider
        // also answers for. The provider was NOT registered and the generation is unchanged.
        // Duplicate type names are rejected rather than resolved by registration order.
        Conflict = 2,

        // The registry declined the request. See XamlMetadataProviderRegistrationResult::Reason.
        // The provider was NOT registered and the generation is unchanged.
        Refused = 3,
    };

    enum class XamlMetadataProviderRefusalReason : std::uint32_t
    {
        None = 0,

        // A null provider was supplied.
        NullProvider = 1,

        // Another thread mutated the registry while this registration was being validated.
        // The caller may retry; retries are safe because registration is idempotent.
        ConcurrentModification = 2,

        // c_maxSideXamlMetadataProviders providers are already registered.
        RegistryFull = 3,

        // The provider under test failed while being probed for conflicts.
        ProviderFailed = 4,

        // No provider with the supplied id is registered (unregistration only).
        UnknownProviderId = 5,
    };

    struct XamlMetadataProviderRegistrationResult
    {
        XamlMetadataProviderRegistrationStatus Status = XamlMetadataProviderRegistrationStatus::Refused;
        XamlMetadataProviderRefusalReason Reason = XamlMetadataProviderRefusalReason::None;

        // Non-zero only when Status is Registered or AlreadyRegistered.
        XamlMetadataProviderId ProviderId = c_invalidXamlMetadataProviderId;

        // The registry generation observed after this call completed. Always populated, so a caller
        // can tell whether anything changed even when the call did not register anything.
        XamlMetadataProviderGeneration Generation = 0;

        // True when, as a result of this call, the supplied provider participates in type lookup.
        bool IsRegistered() const
        {
            return Status == XamlMetadataProviderRegistrationStatus::Registered ||
                   Status == XamlMetadataProviderRegistrationStatus::AlreadyRegistered;
        }
    };

    struct XamlMetadataCacheInvalidationResult
    {
        // The registry generation observed after this call completed.
        XamlMetadataProviderGeneration Generation = 0;

        // Number of cached *unresolved* type name entries that were evicted. Entries that resolved
        // to a real type are never evicted, so this never counts a live type.
        std::size_t UnresolvedEntriesEvicted = 0;
    };
}
