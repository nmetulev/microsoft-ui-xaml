// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

// Tests for the XAML hot reload markup table (core\inc\XamlHotReloadRegistry.h): which URIs may be replaced, how they are
// normalized, generations, subscriber cookies and the stamp location. The header is standard C++ only, so this builds
// with cl.exe alone (run-portable.ps1).

#include "XamlHotReloadRegistry.h"

#include <cstdio>
#include <functional>
#include <string>
#include <vector>

using namespace XamlHotReload;

namespace
{
    int g_failures = 0;
    int g_checks = 0;

    void Check(bool condition, const wchar_t* what)
    {
        ++g_checks;
        if (!condition)
        {
            ++g_failures;
            std::wprintf(L"  FAILED: %s\n", what);
        }
    }

    bool Accepted(const wchar_t* uri, const wchar_t* expectedKey)
    {
        std::wstring key;
        return NormalizeMarkupUri(uri, key) == nullptr && key == expectedKey;
    }

    bool Refused(const wchar_t* uri)
    {
        std::wstring key;
        return NormalizeMarkupUri(uri, key) != nullptr;
    }

    const std::uint8_t c_text[] = { '<', 'P', '/', '>' };

    void Normalization()
    {
        Check(Accepted(L"ms-appx:///MainPage.xaml", L"ms-appx:///mainpage.xaml"), L"app page is accepted and lowercased");
        Check(Accepted(L"ms-appx:///Pages/Home.xbf", L"ms-appx:///pages/home.xbf"), L"compiled page in a folder is accepted");
        Check(Accepted(L"ms-resource:///Files/BrandKit/Themes/Generic.xaml", L"ms-appx:///brandkit/themes/generic.xaml"),
            L"ms-resource:///Files/ form maps to ms-appx:///");
        Check(Accepted(L"ms-appx:///CommunityToolkit.WinUI.Controls/Themes/Generic.xbf",
            L"ms-appx:///communitytoolkit.winui.controls/themes/generic.xbf"), L"third-party library markup is accepted");
        Check(Accepted(L"ms-appx:///Microsoft.UI.XamlCustom/Page.xaml", L"ms-appx:///microsoft.ui.xamlcustom/page.xaml"),
            L"only the exact framework folders are protected");
    }

    void Scope()
    {
        Check(Refused(L"ms-appx:///Microsoft.UI.Xaml/Themes/themeresources.xaml"), L"WinUI resources are refused");
        Check(Refused(L"MS-APPX:///microsoft.ui.xaml.controls/Generic.xbf"), L"WinUI controls resources are refused");
        Check(Refused(L"ms-resource:///Files/Microsoft.UI.Xaml/Themes/generic.xaml"), L"WinUI via ms-resource is refused");
        Check(Refused(L"ms-appdata:///local/Page.xaml"), L"ms-appdata is refused");
        Check(Refused(L"file:///C:/temp/Page.xaml"), L"file URIs are refused");
        Check(Refused(L"ms-appx://SomePackage/Page.xaml"), L"URIs naming another package are refused");
        Check(Refused(L"ms-appx:///Pages/../Secret.xaml"), L"dot-dot segments are refused");
        Check(Refused(L"ms-appx:///./Page.xaml"), L"dot segments are refused");
        Check(Refused(L"ms-appx:///Pages//Page.xaml"), L"empty segments are refused");
        Check(Refused(L"ms-appx:///Pages\\Page.xaml"), L"backslashes are refused");
        Check(Refused(L"ms-appx:///Page.xaml?x=1"), L"queries are refused");
        Check(Refused(L"ms-appx:///Page.xaml#x"), L"fragments are refused");
        Check(Refused(L"ms-appx:///Page%2Examl"), L"escapes are refused");
        Check(Refused(L"ms-appx:///Assets/Logo.png"), L"non-markup files are refused");
        Check(Refused(L"ms-appx:///.xaml"), L"a bare extension is refused");
        Check(Refused(L"ms-appx:///"), L"an empty path is refused");
        Check(Refused(L""), L"an empty URI is refused");
    }

    void Table()
    {
        MarkupTable table;
        Check(table.Generation() == 0 && table.Empty(), L"a new table is empty at generation 0");

        Check(table.Set(L"ms-appx:///MainPage.xaml", MarkupKind::Text, c_text, sizeof(c_text)) == nullptr, L"text markup is stored");
        Check(table.Generation() == 1, L"storing bumps the generation");
        const auto entry = table.Find(L"MS-APPX:///mainpage.XAML");
        Check(entry.Kind == MarkupKind::Text && entry.Content && entry.Content->size() == sizeof(c_text),
            L"lookups are case-insensitive and return the content");
        Check(table.Find(L"ms-resource:///Files/MainPage.xaml").Kind == MarkupKind::Text, L"the ms-resource form finds it");

        std::uint8_t mutableContent[] = { 'a', 'b' };
        Check(table.Set(L"ms-appx:///Copy.xaml", MarkupKind::Text, mutableContent, sizeof(mutableContent)) == nullptr, L"stored");
        mutableContent[0] = 'z';
        Check((*table.Find(L"ms-appx:///Copy.xaml").Content)[0] == 'a', L"content is copied, not referenced");

        const auto before = table.Find(L"ms-appx:///MainPage.xaml").Content;
        Check(table.Set(L"ms-appx:///MainPage.xaml", MarkupKind::Text, mutableContent, sizeof(mutableContent)) == nullptr, L"replaced");
        Check(before->size() == sizeof(c_text), L"a reader of the previous content keeps it after a replacement");

        const auto generation = table.Generation();
        Check(table.Set(L"ms-appx:///Microsoft.UI.Xaml/x.xaml", MarkupKind::Text, c_text, sizeof(c_text)) != nullptr,
            L"refused URIs are not stored");
        Check(table.Set(L"ms-appx:///Page.xbf", MarkupKind::Text, c_text, sizeof(c_text)) != nullptr, L".xbf needs binary markup");
        Check(table.Set(L"ms-appx:///Page.xaml", MarkupKind::None, c_text, sizeof(c_text)) != nullptr, L"an unknown kind is refused");
        Check(table.Set(L"ms-appx:///Page.xaml", MarkupKind::Text, nullptr, 0) != nullptr, L"empty content is refused");
        Check(table.Generation() == generation, L"refusals don't bump the generation");

        Check(table.Set(L"ms-appx:///Lib/Generic.xaml", MarkupKind::Binary, c_text, sizeof(c_text)) == nullptr,
            L"binary markup can be stored under a .xaml URI");
        Check(table.Find(L"ms-appx:///Lib/Generic.xaml").Kind == MarkupKind::Binary, L"and keeps its kind");

        Check(table.Remove(L"ms-appx:///MainPage.xaml"), L"removal succeeds");
        Check(table.Find(L"ms-appx:///MainPage.xaml").Kind == MarkupKind::None, L"removed URIs are gone");
        Check(table.Generation() == generation + 2, L"removal bumps the generation");
        Check(!table.Remove(L"file:///x.xaml"), L"removing an invalid URI fails");
        Check(table.Remove(L""), L"an empty URI removes everything");
        Check(table.Empty(), L"the table is empty after removing everything");
    }

    void Cookies()
    {
        CookieList<std::function<void(int&)>> list;
        int calls = 0;
        const auto first = list.Add([](int& n) { n += 1; });
        const auto second = list.Add([](int& n) { n += 10; });
        Check(first != 0 && second != 0 && first != second, L"cookies are distinct and non-zero");

        for (const auto& callback : list.Snapshot())
        {
            callback(calls);
        }
        Check(calls == 11, L"every subscriber is called");

        Check(list.Remove(first), L"a subscriber is removed by its cookie");
        Check(!list.Remove(first), L"a cookie can't be removed twice");
        Check(!list.Remove(12345), L"an unknown cookie is refused");

        calls = 0;
        for (const auto& callback : list.Snapshot())
        {
            callback(calls);
        }
        Check(calls == 10, L"removed subscribers are not called");

        // A subscriber removing itself while the list is being walked (the snapshot keeps the walk valid).
        bool removedSelf = false;
        std::uint32_t selfCookie = 0;
        selfCookie = list.Add([&](int&) { removedSelf = list.Remove(selfCookie); });
        for (const auto& callback : list.Snapshot())
        {
            callback(calls);
        }
        Check(removedSelf && list.Snapshot().size() == 1, L"a subscriber can unsubscribe from its own callback");

        const auto third = list.Add([](int&) {});
        Check(third > selfCookie, L"cookies are never reused");
        list.Remove(second);
        list.Remove(third);
        Check(list.Empty(), L"the list is empty after removing everything");
    }

    void Stamp()
    {
        Check(StampPath(L"C:\\App") == L"C:\\App\\Microsoft.UI.Xaml.HotReload.enabled", L"stamp path in a folder");
        Check(StampPath(L"C:\\App\\") == L"C:\\App\\Microsoft.UI.Xaml.HotReload.enabled", L"stamp path with a trailing slash");
    }
}

int wmain()
{
    std::wprintf(L"=== XAML hot reload registry tests (standard C++ only) ===\n");
    Normalization();
    Scope();
    Table();
    Cookies();
    Stamp();
    std::wprintf(L"%d check(s), %d failed\n", g_checks, g_failures);
    return g_failures == 0 ? 0 : 1;
}
