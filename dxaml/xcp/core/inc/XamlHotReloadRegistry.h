// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

#pragma once

// XAML hot reload bookkeeping that doesn't depend on the framework: which component URIs have replaced .xbf, and who
// wants to hear about loads. Standard library only, so its rules can be unit tested without the rest of XAML.

#include <algorithm>
#include <cstdint>
#include <cwctype>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace XamlHotReload
{
    // The file the XAML build writes to an app's output folder (and so its package root) to allow hot reload.
    inline constexpr wchar_t c_stampFileName[] = L"Microsoft.UI.Xaml.HotReload.enabled";

    inline std::wstring StampPath(std::wstring_view appFolder)
    {
        std::wstring path(appFolder);
        if (!path.empty() && path.back() != L'\\' && path.back() != L'/')
        {
            path += L'\\';
        }
        return path + c_stampFileName;
    }

    inline bool EndsWith(std::wstring_view text, std::wstring_view suffix)
    {
        return text.size() > suffix.size() && text.compare(text.size() - suffix.size(), suffix.size(), suffix) == 0;
    }

    // Why the .xbf of the component at uri can't be replaced, or nullptr when it can. uri is the component's markup
    // URI, as passed to Application.LoadComponent (ms-appx:///Views/MainPage.xaml), or its compiled form (.xbf).
    // ms-resource:///Files/<path> is the same file as ms-appx:///<path>.
    // On success, key is what every lookup compares: lowercase ms-appx:///<path>.xbf.
    inline const wchar_t* NormalizeComponentUri(std::wstring_view uri, std::wstring& key)
    {
        key.assign(uri.begin(), uri.end());
        for (auto& ch : key)
        {
            ch = static_cast<wchar_t>(std::towlower(ch));
        }

        static constexpr std::wstring_view msResourceFiles = L"ms-resource:///files/";
        static constexpr std::wstring_view msAppx = L"ms-appx:///";
        if (key.compare(0, msResourceFiles.size(), msResourceFiles) == 0)
        {
            key = std::wstring(msAppx) + key.substr(msResourceFiles.size());
        }
        if (key.compare(0, msAppx.size(), msAppx) != 0)
        {
            return L"only the app's own markup (ms-appx:///) can be replaced";
        }

        const std::wstring_view path = std::wstring_view(key).substr(msAppx.size());
        if (path.empty() || path.find_first_of(L"\\?#%") != std::wstring_view::npos)
        {
            return L"the URI must be a plain path";
        }
        for (size_t start = 0; start <= path.size();)
        {
            const size_t end = std::min(path.find(L'/', start), path.size());
            const std::wstring_view segment = path.substr(start, end - start);
            if (segment.empty() || segment == L"." || segment == L"..")
            {
                return L"the URI must be a plain path";
            }
            start = end + 1;
        }

        // WinUI's own markup (ms-appx:///Microsoft.UI.Xaml/..., ms-appx:///Microsoft.UI.Xaml.Controls/...).
        static constexpr std::wstring_view framework = L"microsoft.ui.xaml";
        if (path.compare(0, framework.size(), framework) == 0 &&
            (path.size() == framework.size() || path[framework.size()] == L'/' || path[framework.size()] == L'.'))
        {
            return L"WinUI's own markup can't be replaced";
        }

        // The file name needs a stem: EndsWith also requires the text to be longer than the extension.
        const size_t slash = path.find_last_of(L'/');
        const std::wstring_view fileName = slash == std::wstring_view::npos ? path : path.substr(slash + 1);
        if (EndsWith(fileName, L".xaml"))
        {
            key.replace(key.size() - 5, 5, L".xbf");
        }
        else if (!EndsWith(fileName, L".xbf"))
        {
            return L"the URI must name a .xaml or .xbf file";
        }
        return nullptr;
    }

    using XbfContent = std::shared_ptr<const std::vector<std::uint8_t>>;

    // Thread-safe component URI -> replaced .xbf table.
    class XbfTable
    {
    public:
        // Returns nullptr on success, otherwise why the URI or content was refused. The content is copied.
        const wchar_t* Set(std::wstring_view uri, const std::uint8_t* content, std::size_t size)
        {
            std::wstring key;
            if (const wchar_t* reason = NormalizeComponentUri(uri, key))
            {
                return reason;
            }
            if (content == nullptr || size == 0)
            {
                return L"the .xbf is empty";
            }
            auto copy = std::make_shared<const std::vector<std::uint8_t>>(content, content + size);

            std::lock_guard<std::mutex> guard(m_lock);
            m_entries[std::move(key)] = std::move(copy);
            return nullptr;
        }

        // The replaced .xbf for a request of uri as compiled markup (a .xbf URI), or null. A request for the text
        // form (.xaml) never matches: the parser asks for the .xbf first, so it never falls back to the text.
        XbfContent FindXbf(std::wstring_view uri) const
        {
            static constexpr std::wstring_view xbf = L".xbf";
            if (uri.size() <= xbf.size() ||
                !std::equal(xbf.begin(), xbf.end(), uri.end() - xbf.size(), [](wchar_t a, wchar_t b) { return a == std::towlower(b); }))
            {
                return nullptr;
            }
            return Find(uri);
        }

        // Whether uri (.xaml or .xbf) names a component whose .xbf was replaced.
        bool Contains(std::wstring_view uri) const
        {
            return Find(uri) != nullptr;
        }

    private:
        XbfContent Find(std::wstring_view uri) const
        {
            std::wstring key;
            if (NormalizeComponentUri(uri, key) != nullptr)
            {
                return nullptr;
            }
            std::lock_guard<std::mutex> guard(m_lock);
            const auto it = m_entries.find(key);
            return it == m_entries.end() ? nullptr : it->second;
        }

        mutable std::mutex m_lock;
        std::map<std::wstring, XbfContent> m_entries;
    };

    // Subscribers identified by a cookie. Snapshot() lets callers invoke them without holding the lock, so a
    // subscriber may unsubscribe from inside its own callback.
    template <typename T>
    class CookieList
    {
    public:
        std::uint32_t Add(T value)
        {
            std::lock_guard<std::mutex> guard(m_lock);
            const std::uint32_t cookie = ++m_lastCookie;
            m_items.emplace_back(cookie, std::move(value));
            return cookie;
        }

        bool Remove(std::uint32_t cookie)
        {
            std::lock_guard<std::mutex> guard(m_lock);
            const auto it = std::find_if(m_items.begin(), m_items.end(), [cookie](const auto& item) { return item.first == cookie; });
            if (it == m_items.end())
            {
                return false;
            }
            m_items.erase(it);
            return true;
        }

        std::vector<T> Snapshot() const
        {
            std::lock_guard<std::mutex> guard(m_lock);
            std::vector<T> items;
            items.reserve(m_items.size());
            for (const auto& item : m_items)
            {
                items.push_back(item.second);
            }
            return items;
        }

        bool Empty() const
        {
            std::lock_guard<std::mutex> guard(m_lock);
            return m_items.empty();
        }

    private:
        mutable std::mutex m_lock;
        std::vector<std::pair<std::uint32_t, T>> m_items;
        std::uint32_t m_lastCookie = 0;
    };
}
