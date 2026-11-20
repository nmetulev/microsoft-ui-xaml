// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

#include "precomp.h"
#include "XamlHotReloadRegistryUnitTests.h"
#include <XamlHotReloadRegistry.h>
#include <functional>

using namespace XamlHotReload;

namespace Windows { namespace UI { namespace Xaml { namespace Tests { namespace XamlDiagnostics {

    namespace
    {
        bool Accepted(const wchar_t* uri, const wchar_t* expectedKey)
        {
            std::wstring key;
            return NormalizeComponentUri(uri, key) == nullptr && key == expectedKey;
        }

        bool Refused(const wchar_t* uri)
        {
            std::wstring key;
            return NormalizeComponentUri(uri, key) != nullptr;
        }

        const std::uint8_t c_xbf[] = { 'X', 'B', 'F', 0 };

        // A weak reference for RootPropertyTable: alive while the object it names is in g_alive.
        struct FakeWeak
        {
            int Object = 0;
        };
        std::vector<int> g_alive;
        bool IsAlive(const FakeWeak& weak)
        {
            return std::find(g_alive.begin(), g_alive.end(), weak.Object) != g_alive.end();
        }
    }

    void XamlHotReloadRegistryUnitTests::ComponentUrisAreNormalized()
    {
        VERIFY_IS_TRUE(Accepted(L"ms-appx:///MainPage.xaml", L"ms-appx:///mainpage.xbf"));
        VERIFY_IS_TRUE(Accepted(L"ms-appx:///Views/Home.xbf", L"ms-appx:///views/home.xbf"));
        VERIFY_IS_TRUE(Accepted(L"MS-APPX:///Views/HOME.XAML", L"ms-appx:///views/home.xbf"));
        VERIFY_IS_TRUE(Accepted(L"ms-resource:///Files/BrandKit/Themes/Generic.xaml", L"ms-appx:///brandkit/themes/generic.xbf"));
        VERIFY_IS_TRUE(Accepted(L"ms-appx:///CommunityToolkit.WinUI.Controls/Themes/Generic.xbf", L"ms-appx:///communitytoolkit.winui.controls/themes/generic.xbf"));
        VERIFY_IS_TRUE(Accepted(L"ms-appx:///Microsoft.UI.XamlCustom/Page.xaml", L"ms-appx:///microsoft.ui.xamlcustom/page.xbf"));
    }

    void XamlHotReloadRegistryUnitTests::OnlyTheAppsOwnMarkupCanBeReplaced()
    {
        VERIFY_IS_TRUE(Refused(L"ms-appx:///Microsoft.UI.Xaml/Themes/themeresources.xaml"));
        VERIFY_IS_TRUE(Refused(L"MS-APPX:///microsoft.ui.xaml.controls/Generic.xbf"));
        VERIFY_IS_TRUE(Refused(L"ms-resource:///Files/Microsoft.UI.Xaml/Themes/generic.xaml"));
        VERIFY_IS_TRUE(Refused(L"ms-appdata:///local/Page.xaml"));
        VERIFY_IS_TRUE(Refused(L"file:///C:/temp/Page.xaml"));
        VERIFY_IS_TRUE(Refused(L"ms-appx://SomePackage/Page.xaml"));
        VERIFY_IS_TRUE(Refused(L"ms-appx:///Pages/../Secret.xaml"));
        VERIFY_IS_TRUE(Refused(L"ms-appx:///./Page.xaml"));
        VERIFY_IS_TRUE(Refused(L"ms-appx:///Pages//Page.xaml"));
        VERIFY_IS_TRUE(Refused(L"ms-appx:///Pages\\Page.xaml"));
        VERIFY_IS_TRUE(Refused(L"ms-appx:///Page.xaml?x=1"));
        VERIFY_IS_TRUE(Refused(L"ms-appx:///Page.xaml#x"));
        VERIFY_IS_TRUE(Refused(L"ms-appx:///Page%2Examl"));
        VERIFY_IS_TRUE(Refused(L"ms-appx:///Assets/Logo.png"));
        VERIFY_IS_TRUE(Refused(L"ms-appx:///.xaml"));
        VERIFY_IS_TRUE(Refused(L"ms-appx:///"));
        VERIFY_IS_TRUE(Refused(L""));
    }

    void XamlHotReloadRegistryUnitTests::ReplacedXbfIsServedOnlyForXbfRequests()
    {
        XbfTable table;
        VERIFY_IS_FALSE(table.Contains(L"ms-appx:///MainPage.xaml"));

        VERIFY_IS_NULL(table.Set(L"ms-appx:///MainPage.xaml", c_xbf, sizeof(c_xbf)));
        VERIFY_IS_NOT_NULL(table.FindXbf(L"ms-appx:///MainPage.xbf").get());
        VERIFY_IS_NOT_NULL(table.FindXbf(L"ms-resource:///Files/mainpage.XBF").get());
        VERIFY_IS_NULL(table.FindXbf(L"ms-appx:///MainPage.xaml").get());
        VERIFY_IS_NULL(table.FindXbf(L"ms-appx:///OtherPage.xbf").get());

        VERIFY_IS_TRUE(table.Contains(L"ms-appx:///MainPage.xaml"));
        VERIFY_IS_TRUE(table.Contains(L"ms-appx:///MainPage.xbf"));
        VERIFY_IS_FALSE(table.Contains(L"file:///C:/app/MainPage.xbf"));

        VERIFY_IS_NOT_NULL(table.Set(L"ms-appx:///Microsoft.UI.Xaml/x.xaml", c_xbf, sizeof(c_xbf)));
        VERIFY_IS_NOT_NULL(table.Set(L"ms-appx:///Page.xaml", nullptr, 0));
        VERIFY_IS_FALSE(table.Contains(L"ms-appx:///Page.xaml"));
    }

    void XamlHotReloadRegistryUnitTests::ReplacedXbfIsCopied()
    {
        XbfTable table;
        std::uint8_t content[] = { 'a', 'b' };
        VERIFY_IS_NULL(table.Set(L"ms-appx:///Page.xaml", content, sizeof(content)));
        content[0] = 'z';
        const XbfContent first = table.FindXbf(L"ms-appx:///Page.xbf");
        VERIFY_ARE_EQUAL('a', (*first)[0]);

        VERIFY_IS_NULL(table.Set(L"ms-appx:///Page.xaml", c_xbf, sizeof(c_xbf)));
        VERIFY_ARE_EQUAL(sizeof(content), first->size());
        VERIFY_ARE_EQUAL(sizeof(c_xbf), table.FindXbf(L"ms-appx:///Page.xbf")->size());
    }

    void XamlHotReloadRegistryUnitTests::LoadSubscribersByCookie()
    {
        CookieList<std::function<void(int&)>> list;
        int calls = 0;
        const auto first = list.Add([](int& n) { n += 1; });
        const auto second = list.Add([](int& n) { n += 10; });
        VERIFY_IS_TRUE(first != 0 && second != 0 && first != second);

        for (const auto& callback : list.Snapshot()) { callback(calls); }
        VERIFY_ARE_EQUAL(11, calls);

        VERIFY_IS_TRUE(list.Remove(first));
        VERIFY_IS_FALSE(list.Remove(first));
        VERIFY_IS_FALSE(list.Remove(12345));

        calls = 0;
        for (const auto& callback : list.Snapshot()) { callback(calls); }
        VERIFY_ARE_EQUAL(10, calls);

        bool removedSelf = false;
        std::uint32_t selfCookie = 0;
        selfCookie = list.Add([&](int&) { removedSelf = list.Remove(selfCookie); });
        for (const auto& callback : list.Snapshot()) { callback(calls); }
        VERIFY_IS_TRUE(removedSelf);
        VERIFY_ARE_EQUAL(1u, list.Snapshot().size());

        const auto third = list.Add([](int&) {});
        VERIFY_IS_TRUE(third > selfCookie);
        list.Remove(second);
        list.Remove(third);
        VERIFY_IS_TRUE(list.Empty());
    }

    void XamlHotReloadRegistryUnitTests::StampPath()
    {
        VERIFY_ARE_EQUAL(std::wstring(L"C:\\App\\Microsoft.UI.Xaml.HotReload.enabled"), XamlHotReload::StampPath(L"C:\\App"));
        VERIFY_ARE_EQUAL(std::wstring(L"C:\\App\\Microsoft.UI.Xaml.HotReload.enabled"), XamlHotReload::StampPath(L"C:\\App\\"));
    }

    void XamlHotReloadRegistryUnitTests::RemovedRootPropertiesAreCleared()
    {
        using P = std::vector<std::uint16_t>;
        VERIFY_IS_TRUE(RootPropertiesToClear(P{}, P{ 1, 2 }).empty());
        VERIFY_IS_TRUE(RootPropertiesToClear(P{ 1, 2, 3 }, P{ 3, 1 }) == P{ 2 });
        VERIFY_IS_TRUE(RootPropertiesToClear(P{ 4, 4, 5 }, P{}) == (P{ 4, 5 }));
        VERIFY_IS_TRUE(RootPropertiesToClear(P{ 1, 2 }, P{ 2, 1 }).empty());
    }

    void XamlHotReloadRegistryUnitTests::RootPropertiesFollowTheLiveComponent()
    {
        using P = std::vector<std::uint16_t>;
        RootPropertyTable<FakeWeak> table;
        int pageA = 0, pageB = 0;
        const std::uint32_t uiThread = 10, otherThread = 20;
        g_alive = { 1, 2 };

        VERIFY_IS_TRUE(table.Record(&pageA, uiThread, FakeWeak{ 1 }, P{ 7, 8 }, IsAlive).empty());
        VERIFY_IS_TRUE(table.Record(&pageB, otherThread, FakeWeak{ 2 }, P{ 9 }, IsAlive).empty());
        VERIFY_IS_TRUE(table.Record(&pageA, uiThread, FakeWeak{ 1 }, P{ 8 }, IsAlive) == (P{ 7, 8 }));
        VERIFY_IS_TRUE(table.Record(&pageA, uiThread, FakeWeak{ 1 }, P{}, IsAlive) == P{ 8 });
        VERIFY_ARE_EQUAL(2u, table.Size());

        // pageA dies and a new object takes its address: it starts fresh.
        g_alive = { 2, 3 };
        VERIFY_IS_TRUE(table.Record(&pageA, uiThread, FakeWeak{ 3 }, P{ 5 }, IsAlive).empty());
        VERIFY_IS_TRUE(table.Record(&pageA, uiThread, FakeWeak{ 3 }, P{ 6 }, IsAlive) == P{ 5 });

        // Many short-lived components on one thread: dead ones are swept, so the table tracks what's alive.
        std::vector<int> objects(1000);
        for (int i = 0; i < 1000; ++i)
        {
            g_alive = { 2, 3, 100 + i };
            table.Record(&objects[i], uiThread, FakeWeak{ 100 + i }, P{ 1 }, IsAlive);
        }
        VERIFY_IS_TRUE(table.Size() < 200);
        g_alive = { 2, 3 };
        VERIFY_IS_TRUE(table.Record(&pageA, uiThread, FakeWeak{ 3 }, P{}, IsAlive) == P{ 6 });
        VERIFY_IS_TRUE(table.Record(&pageB, otherThread, FakeWeak{ 2 }, P{}, IsAlive) == P{ 9 });
    }
}}}}}
