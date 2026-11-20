// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

#pragma once
#include "precomp.h"
#include <XamlLogging.h>
#include <WexTestClass.h>

namespace Windows { namespace UI { namespace Xaml { namespace Tests { namespace XamlDiagnostics {
    class XamlHotReloadRegistryUnitTests : public WEX::TestClass<XamlHotReloadRegistryUnitTests>
    {
    public:
        BEGIN_TEST_CLASS(XamlHotReloadRegistryUnitTests)
            TEST_CLASS_PROPERTY(L"Classification", L"Integration")
            TEST_CLASS_PROPERTY(L"TestPass:IncludeOnlyOn", L"Desktop")
        END_TEST_CLASS()

        BEGIN_TEST_METHOD(ComponentUrisAreNormalized)
            TEST_METHOD_PROPERTY(L"Description", L"A page's .xaml and .xbf URIs, in either ms-appx or ms-resource form and any case, name the same replaced .xbf.")
        END_TEST_METHOD()

        BEGIN_TEST_METHOD(OnlyTheAppsOwnMarkupCanBeReplaced)
            TEST_METHOD_PROPERTY(L"Description", L"WinUI's own markup, other schemes and non-plain paths are refused.")
        END_TEST_METHOD()

        BEGIN_TEST_METHOD(ReplacedXbfIsServedOnlyForXbfRequests)
            TEST_METHOD_PROPERTY(L"Description", L"The parser gets the replaced .xbf when it asks for the .xbf, never in place of text .xaml.")
        END_TEST_METHOD()

        BEGIN_TEST_METHOD(ReplacedXbfIsCopied)
            TEST_METHOD_PROPERTY(L"Description", L"Content is copied, and a reader of earlier content keeps it after a replacement.")
        END_TEST_METHOD()

        BEGIN_TEST_METHOD(LoadSubscribersByCookie)
            TEST_METHOD_PROPERTY(L"Description", L"Subscribers are called until unsubscribed, and may unsubscribe from their own callback.")
        END_TEST_METHOD()

        BEGIN_TEST_METHOD(StampPath)
        END_TEST_METHOD()

        BEGIN_TEST_METHOD(RemovedRootPropertiesAreCleared)
            TEST_METHOD_PROPERTY(L"Description", L"Only what the previous load set and the latest doesn't is cleared, once each.")
        END_TEST_METHOD()

        BEGIN_TEST_METHOD(RootPropertiesFollowTheLiveComponent)
            TEST_METHOD_PROPERTY(L"Description", L"A component loaded again gets its previous properties; a new object at a dead one's address doesn't, and dead entries are swept.")
        END_TEST_METHOD()
    };
}}}}}
