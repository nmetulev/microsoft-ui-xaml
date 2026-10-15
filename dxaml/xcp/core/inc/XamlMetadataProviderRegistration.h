// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

//  Abstract:
//      Contract types for the registered (side) IXamlMetadataProvider registry behind
//      Microsoft.UI.Xaml.Markup.XamlMetadataProviderRegistry, and the hot reload service's RegisterMetadataProvider.
//
//      A registered provider is an IXamlMetadataProvider added while the app runs, consulted only after the
//      application's own provider declines a name, so registering one can never change the identity of a type that
//      already resolves. The rules (ambiguity, conflicts, shared types, what unregistering forgets) live in the
//      standard C++ XamlMetadataProviderRegistry.h so they can be tested without the XAML build.
//
//      Spec: docs\api-specs\XamlMetadataProviderRegistry\XamlMetadataProviderRegistry.md.

#pragma once

#include <cstdint>
#include <string>
#include <xstring_ptr.h>
#include <XamlMetadataProviderRegistry.h>

namespace DirectUI
{
    // Monotonically increasing counter describing the state of the registry. Bumped by every successful
    // registration, unregistration and cache invalidation.
    using XamlMetadataProviderGeneration = XamlMetadataProviderRegistry::RegistryGeneration;

    // Stable, non-zero identifier handed out for each successfully registered provider. Never reused.
    using XamlMetadataProviderId = XamlMetadataProviderRegistry::ProviderId;

    inline constexpr XamlMetadataProviderId c_invalidXamlMetadataProviderId = XamlMetadataProviderRegistry::c_invalidProviderId;

    using XamlMetadataProviderRegistrationStatus = XamlMetadataProviderRegistry::RegistryStatus;
    using XamlMetadataProviderRefusalReason = XamlMetadataProviderRegistry::RefusalReason;
    using XamlMetadataProviderRegistrationResult = XamlMetadataProviderRegistry::RegisterResult<xstring_ptr>;
    using XamlMetadataProviderUnregistrationResult = XamlMetadataProviderRegistry::UnregisterResult;

    struct XamlMetadataCacheInvalidationResult
    {
        // The registry generation observed after this call completed.
        XamlMetadataProviderGeneration Generation = 0;

        // Number of cached *unresolved* type name entries that were evicted. Entries that resolved to a real type are
        // never evicted by invalidation, so this never counts a live type.
        std::size_t UnresolvedEntriesEvicted = 0;
    };

    // The HRESULT and message a public caller sees for a registration that didn't succeed. S_OK for Succeeded and
    // AlreadyRegistered.
    inline HRESULT DescribeRegistrationResult(const XamlMetadataProviderRegistrationResult& result, std::wstring& message)
    {
        message.clear();
        switch (result.Status)
        {
            case XamlMetadataProviderRegistrationStatus::Succeeded:
            case XamlMetadataProviderRegistrationStatus::AlreadyRegistered:
                return S_OK;

            case XamlMetadataProviderRegistrationStatus::Conflict:
                message = L"The metadata provider describes '";
                message.append(result.ConflictingName.GetBuffer(), result.ConflictingName.GetCount());
                message += L"' as a different type than the registered provider that already supplied it. Unregister that "
                           L"provider first.";
                return HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS);

            default:
                break;
        }

        switch (result.Reason)
        {
            case XamlMetadataProviderRefusalReason::NullProvider:
                message = L"The metadata provider is null.";
                return E_INVALIDARG;
            case XamlMetadataProviderRefusalReason::RegistryFull:
                message = L"Too many metadata providers are registered. Unregister one first.";
                return E_BOUNDS;
            case XamlMetadataProviderRefusalReason::ProviderFailed:
                message = L"The metadata provider failed when asked about a type that is already in use.";
                return E_FAIL;
            case XamlMetadataProviderRefusalReason::ConcurrentModification:
                message = L"Metadata providers kept changing on other threads during the registration. Try again.";
                return E_CHANGED_STATE;
            default:
                message = L"The metadata provider was refused.";
                return E_FAIL;
        }
    }
}
