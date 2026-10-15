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

            #pragma region Registered IXamlMetadataProviders (XamlMetadataProviderRegistry)

            BEGIN_TEST_METHOD(SideProvider_UnknownTypeIsCachedAsUnresolvedMiss)
                TEST_METHOD_PROPERTY(L"Description", L"A name no provider describes is cached as an unresolved placeholder, which is the miss a later registration has to defeat.")
            END_TEST_METHOD()

            BEGIN_TEST_METHOD(SideProvider_RegistrationForgetsCachedMisses)
                TEST_METHOD_PROPERTY(L"Description", L"Registering a provider makes a previously unresolvable name resolve, with no separate invalidation, and tells the parser caches to refresh.")
            END_TEST_METHOD()

            BEGIN_TEST_METHOD(SideProvider_ResolvesByTypeName)
                TEST_METHOD_PROPERTY(L"Description", L"GetClassInfoByTypeName resolves through a registered provider, for WinRT types and for assembly-qualified .NET types.")
            END_TEST_METHOD()

            BEGIN_TEST_METHOD(SideProvider_DoesNotDisturbTypesTheAppProviderOwns)
                TEST_METHOD_PROPERTY(L"Description", L"Types the application provider resolves keep their CClassInfo and IXamlType identity across registration and unregistration.")
            END_TEST_METHOD()

            BEGIN_TEST_METHOD(SideProvider_RegistrationIsIdempotent)
                TEST_METHOD_PROPERTY(L"Description", L"Registering the same provider twice reports AlreadyRegistered, reuses the id, and does not bump the generation.")
            END_TEST_METHOD()

            BEGIN_TEST_METHOD(SideProvider_ConflictingProviderIsRejected)
                TEST_METHOD_PROPERTY(L"Description", L"A provider that describes a name already in use as a different type is rejected with Conflict, naming the type.")
            END_TEST_METHOD()

            BEGIN_TEST_METHOD(SideProvider_SharedTypeSurvivesUntilLastProviderLeaves)
                TEST_METHOD_PROPERTY(L"Description", L"Two providers describing the same type share it, and it stays resolvable until the last of them is unregistered.")
            END_TEST_METHOD()

            BEGIN_TEST_METHOD(SideProvider_AmbiguousNameResolvesToNothing)
                TEST_METHOD_PROPERTY(L"Description", L"Registered providers that disagree about an unused name make it resolve to nothing; unregistering one settles it.")
            END_TEST_METHOD()

            BEGIN_TEST_METHOD(SideProvider_UnregisterForgetsOnlyItsTypes)
                TEST_METHOD_PROPERTY(L"Description", L"Unregistering forgets only the names that provider alone supplied; old type entries stay valid for live objects.")
            END_TEST_METHOD()

            BEGIN_TEST_METHOD(SideProvider_RegistryOwnsProviderLifetime)
                TEST_METHOD_PROPERTY(L"Description", L"The registry keeps the provider alive while registered and releases it on unregistration.")
            END_TEST_METHOD()

            BEGIN_TEST_METHOD(SideProvider_GenerationIsObservable)
                TEST_METHOD_PROPERTY(L"Description", L"Every registration, invalidation and unregistration bumps the monotonic generation.")
            END_TEST_METHOD()

            BEGIN_TEST_METHOD(SideProvider_InvalidationKeepsResolvedTypes)
                TEST_METHOD_PROPERTY(L"Description", L"InvalidateUnresolvedTypeCache evicts only cached misses; already-resolved names keep their identity.")
            END_TEST_METHOD()

            BEGIN_TEST_METHOD(SideProvider_LockContractIsExplicit)
                TEST_METHOD_PROPERTY(L"Description", L"Registry state is mutated under CStaticLock and the conflict probe runs without it; the lookup path invokes providers with the lock held, as before.")
            END_TEST_METHOD()

            BEGIN_TEST_METHOD(SideProvider_RefusesNullAndUnknownProvider)
                TEST_METHOD_PROPERTY(L"Description", L"A null provider and a provider that is not registered are Refused with a reason.")
            END_TEST_METHOD()

            #pragma endregion
        };
    }
} } } }
