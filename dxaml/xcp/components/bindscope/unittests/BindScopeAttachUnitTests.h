// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

#pragma once
#include <WexTestClass.h>

namespace Microsoft { namespace UI { namespace Xaml { namespace Tests { namespace BindScope {

class BindScopeAttachUnitTests : public WEX::TestClass<BindScopeAttachUnitTests>
{
public:
    BEGIN_TEST_CLASS(BindScopeAttachUnitTests)
        TEST_CLASS_PROPERTY(L"Classification", L"Unit")
        TEST_CLASS_PROPERTY(L"TestPass:IncludeOnlyOn", L"Desktop")
    END_TEST_CLASS()

    BEGIN_TEST_METHOD(AttachConnectsEveryRowAndInitializes)
        TEST_METHOD_PROPERTY(L"Description", L"Attach reports Attached, connects every manifest row in ascending order, and runs the initial update in place of the Loading callback a cold parse relies on.")
    END_TEST_METHOD()

    BEGIN_TEST_METHOD(RepeatAttachIsRefusedAndNeverReconnects)
        TEST_METHOD_PROPERTY(L"Description", L"A second attach of the same scope revision reports AlreadyAttached, keeps the same ownership id and scope object, and issues no Connect calls.")
    END_TEST_METHOD()

    BEGIN_TEST_METHOD(BaseTreeFaultDominatesAndFailsClosed)
        TEST_METHOD_PROPERTY(L"Description", L"A stale base tree refuses before any mutation, dominates a simultaneous scope fault, and an unknown base tree fails closed.")
    END_TEST_METHOD()

    BEGIN_TEST_METHOD(SameTypedTargetSwapIsRefusedOnIdentity)
        TEST_METHOD_PROPERTY(L"Description", L"Two adjacent same-typed elements swapped in the target map are refused on object identity, before any Connect, even though the type guard agrees for both.")
    END_TEST_METHOD()

    BEGIN_TEST_METHOD(IncompleteTargetMapIsRefused)
        TEST_METHOD_PROPERTY(L"Description", L"A target map that does not cover every connector-declared required id is refused, because a scope with an unconnected target is silently inert.")
    END_TEST_METHOD()

    BEGIN_TEST_METHOD(NoEffectScopeIsRefusedNotAttached)
        TEST_METHOD_PROPERTY(L"Description", L"A scope that cannot be initialized or stopped is refused rather than reported as attached.")
    END_TEST_METHOD()

    BEGIN_TEST_METHOD(ReplaceStopsTheOutgoingScope)
        TEST_METHOD_PROPERTY(L"Description", L"Replace detaches the previous scope before publishing the new one, takes a new ownership id, and leaves exactly one writer.")
    END_TEST_METHOD()

    BEGIN_TEST_METHOD(DetachClearsOwnershipAndIsNotIdempotentAsSuccess)
        TEST_METHOD_PROPERTY(L"Description", L"Detach releases the scope and clears the ownership record; a second detach is a refusal, not a success.")
    END_TEST_METHOD()

    BEGIN_TEST_METHOD(CachedUnrealizedRootAttachesThroughNamescope)
        TEST_METHOD_PROPERTY(L"Description", L"A root constructed but never realized, whose elements have no walkable owner chain, still attaches through namescope name resolution.")
    END_TEST_METHOD()

    BEGIN_TEST_METHOD(OwnershipIsPublishedOnlyAfterConnect)
        TEST_METHOD_PROPERTY(L"Description", L"Runtime ownership is not observable while the scope is still being populated, and a partial connect reports Desynchronized naming the failing id.")
    END_TEST_METHOD()

    BEGIN_TEST_METHOD(UnnamedRowFromAnotherRootIsRefused)
        TEST_METHOD_PROPERTY(L"Description", L"A row with no stable name whose object belongs to a different root is refused on namescope ownership.")
    END_TEST_METHOD()

    BEGIN_TEST_METHOD(CleanSuitePasses)
        TEST_METHOD_PROPERTY(L"Description", L"The full scenario suite passes with no seeded defect.")
    END_TEST_METHOD()

    BEGIN_TEST_METHOD(EverySeededMutantIsKilled)
        TEST_METHOD_PROPERTY(L"Description", L"Each seeded defect disables exactly one guard, and the suite must detect every one of them. A surviving mutant means the suite does not actually enforce that guard.")
    END_TEST_METHOD()
};

} } } } }
