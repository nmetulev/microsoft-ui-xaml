// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

#pragma once

#include <WexTestClass.h>

namespace Microsoft { namespace UI { namespace Xaml { namespace Tests { 
    namespace Metadata {

        class MetadataUnitTests : public WEX::TestClass < MetadataUnitTests >
        {
        public:
            BEGIN_TEST_CLASS(MetadataUnitTests)
                TEST_CLASS_PROPERTY(L"Classification", L"Integration")
                TEST_CLASS_PROPERTY(L"TestPass:IncludeOnlyOn", L"Desktop")
            END_TEST_CLASS()

            TEST_CLASS_SETUP(ClassSetup)
            TEST_CLASS_CLEANUP(ClassCleanup)

            TEST_METHOD(StorageAccessIsThreadSafe)
            TEST_METHOD(CanResolveCustomTypeByTypeName)
            TEST_METHOD(CanResolveDirectiveOnCustomType)
            TEST_METHOD(IsAssignableFrom)
            TEST_METHOD(GetClassInfoByName)
            TEST_METHOD(GetClassInfoByFullName)
            TEST_METHOD(GetTypeNameByClassInfo)
            TEST_METHOD(IsConstructible)
            TEST_METHOD(BaseTypes)
            TEST_METHOD(ValidateISupportInitializeFlagOnTypes)
            TEST_METHOD(DependencyObjectIsInGoodState)
            TEST_METHOD(GetPrimitiveClassInfo)
            TEST_METHOD(CanExtractNamespaceNameAndShortName)
            
            BEGIN_TEST_METHOD(IXamlMemberTypeMayReturnNull)
                TEST_METHOD_PROPERTY(L"Description", L"Validates MetadataAPI::ImportPropertyInfo can deal with IXamlMember.Type returning nullptr.")
            END_TEST_METHOD()

            BEGIN_TEST_METHOD(FoundationTypesInCorrectNamespace)
                TEST_METHOD_PROPERTY(L"Description", L"Validates wf::{Size, Rect, Point} are in the correct namespace.")
            END_TEST_METHOD()

            TEST_METHOD(RequiresPeerActivation)
            TEST_METHOD(GetStorageType)
            TEST_METHOD(GetOffset)
            TEST_METHOD(GetGroupOffset)

            BEGIN_TEST_METHOD(ReRegisteringBuiltinDPDoesNotAV)
            END_TEST_METHOD()

            BEGIN_TEST_METHOD(ReRegisteringCustomDPSetsUnderlyingDP)
            END_TEST_METHOD()

            BEGIN_TEST_METHOD(RunClassConstructorIsDelayed)
                TEST_METHOD_PROPERTY(L"Description", L"Validates CClassInfo::RunClassConstructorIfNecessary is not called when we import ClassInfo. But called when we import custom property info.")
            END_TEST_METHOD()

            BEGIN_TEST_METHOD(RunClassConstructorIsNotDelayed)
                TEST_METHOD_PROPERTY(L"Description", L"For older builds, Validates CClassInfo::RunClassConstructorIfNecessary is called when we import ClassInfo.")
            END_TEST_METHOD()

            TEST_METHOD(TestPropertyGetters)

            #pragma region Side IXamlMetadataProvider registry (experimental)

            BEGIN_TEST_METHOD(SideProvider_UnknownTypeIsCachedAsUnresolvedMiss)
                TEST_METHOD_PROPERTY(L"Description", L"A name no provider describes is cached as an unresolved placeholder, which is the miss a later registration has to defeat.")
            END_TEST_METHOD()

            BEGIN_TEST_METHOD(SideProvider_ResolvesNameThatPreviouslyMissed)
                TEST_METHOD_PROPERTY(L"Description", L"Registering a side provider plus invalidating the cached miss makes a previously unresolvable name resolve. Also proves registration alone is NOT enough.")
            END_TEST_METHOD()

            BEGIN_TEST_METHOD(SideProvider_ResolvesByTypeName)
                TEST_METHOD_PROPERTY(L"Description", L"GetClassInfoByTypeName resolves through a side provider, not just the by-full-name path.")
            END_TEST_METHOD()

            BEGIN_TEST_METHOD(SideProvider_DoesNotDisturbTypesTheAppProviderOwns)
                TEST_METHOD_PROPERTY(L"Description", L"A type the application provider already resolves keeps the exact same CClassInfo and IXamlType identity across registration and invalidation.")
            END_TEST_METHOD()

            BEGIN_TEST_METHOD(SideProvider_RegistrationIsIdempotent)
                TEST_METHOD_PROPERTY(L"Description", L"Registering the same provider instance twice reports AlreadyRegistered, reuses the id, and does not bump the generation.")
            END_TEST_METHOD()

            BEGIN_TEST_METHOD(SideProvider_ConflictingProviderIsRejected)
                TEST_METHOD_PROPERTY(L"Description", L"A second provider that answers for a name an existing side provider already owns is rejected with Conflict and is not registered.")
            END_TEST_METHOD()

            BEGIN_TEST_METHOD(SideProvider_RegistryOwnsProviderLifetime)
                TEST_METHOD_PROPERTY(L"Description", L"The registry keeps the provider alive after the caller drops its reference, and releases it on unregistration.")
            END_TEST_METHOD()

            BEGIN_TEST_METHOD(SideProvider_GenerationIsObservable)
                TEST_METHOD_PROPERTY(L"Description", L"Every registration, invalidation and unregistration bumps the monotonic generation, so a stale cache cannot hide a registration.")
            END_TEST_METHOD()

            BEGIN_TEST_METHOD(SideProvider_InvalidationKeepsResolvedTypes)
                TEST_METHOD_PROPERTY(L"Description", L"InvalidateUnresolvedTypeCache evicts only cached misses; already-resolved names keep their identity.")
            END_TEST_METHOD()

            BEGIN_TEST_METHOD(SideProvider_LockContractIsExplicit)
                TEST_METHOD_PROPERTY(L"Description", L"Registry state is mutated under CStaticLock and the conflict probe runs without it; the lookup path inherits the pre-existing behaviour of invoking providers with the lock held.")
            END_TEST_METHOD()

            BEGIN_TEST_METHOD(SideProvider_RefusesNullAndUnknownId)
                TEST_METHOD_PROPERTY(L"Description", L"The result model is truthful for the failure cases: a null provider and an unknown provider id are Refused with a reason.")
            END_TEST_METHOD()

            #pragma endregion
        };
    }
} } } }
