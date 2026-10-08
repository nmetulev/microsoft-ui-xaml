// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

#pragma once

// The XAML hot reload markup table: which URIs have replaced markup, and who wants to hear about loads.
// Standard library only, so the rules can be tested without the XAML build (unittests\run-portable.ps1).

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
    enum class MarkupKind : std::uint8_t { None = 0, Text = 1, Binary = 2 };

    // Why a URI can't have its markup replaced, or nullptr when it can.
    // On success, key is the canonical form every lookup uses: lowercase, ms-appx:///<path>.
    inline const wchar_t* NormalizeMarkupUri(std::wstring_view uri, std::wstring& key)
    {
        key.assign(uri.begin(), uri.end());
        for (auto& ch : key)
        {
            ch = static_cast<wchar_t>(std::towlower(ch));
        }

        // Default-style and resource lookups use the ms-resource:///Files/ form of an app or library file.
        static constexpr std::wstring_view msResourceFiles = L"ms-resource:///files/";
        static constexpr std::wstring_view msAppx = L"ms-appx:///";
        if (key.compare(0, msResourceFiles.size(), msResourceFiles) == 0)
        {
            key = std::wstring(msAppx) + key.substr(msResourceFiles.size());
        }
        if (key.compare(0, msAppx.size(), msAppx) != 0)
        {
            return L"only ms-appx:/// (or ms-resource:///Files/) markup of the app can be replaced";
        }

        const std::wstring_view path = std::wstring_view(key).substr(msAppx.size());
        if (path.empty() || path.find_first_of(L"\\?#%") != std::wstring_view::npos)
        {
            return L"the URI must be a plain path";
        }
        size_t start = 0;
        while (start <= path.size())
        {
            const size_t end = std::min(path.find(L'/', start), path.size());
            const std::wstring_view segment = path.substr(start, end - start);
            if (segment.empty() || segment == L"." || segment == L"..")
            {
                return L"the URI must be a plain path";
            }
            start = end + 1;
        }

        // WinUI's own resources (ms-appx:///Microsoft.UI.Xaml/..., ms-appx:///Microsoft.UI.Xaml.Controls/...).
        static constexpr std::wstring_view framework = L"microsoft.ui.xaml";
        if (path.compare(0, framework.size(), framework) == 0 &&
            (path.size() == framework.size() || path[framework.size()] == L'/' || path[framework.size()] == L'.'))
        {
            return L"WinUI's own markup can't be replaced";
        }

        const auto endsWith = [&](std::wstring_view suffix)
        {
            return path.size() > suffix.size() && path.compare(path.size() - suffix.size(), suffix.size(), suffix) == 0;
        };
        if (!endsWith(L".xaml") && !endsWith(L".xbf"))
        {
            return L"only .xaml and .xbf markup can be replaced";
        }
        return nullptr;
    }

    struct MarkupEntry
    {
        MarkupKind Kind = MarkupKind::None;
        std::shared_ptr<const std::vector<std::uint8_t>> Content;
    };

    // Thread-safe URI -> replaced markup table. Every change bumps Generation(), which each UI thread compares with the
    // value its caches last saw.
    class MarkupTable
    {
    public:
        // Returns nullptr on success, otherwise why the URI was refused.
        const wchar_t* Set(std::wstring_view uri, MarkupKind kind, const std::uint8_t* content, std::size_t size)
        {
            std::wstring key;
            if (const wchar_t* reason = NormalizeMarkupUri(uri, key))
            {
                return reason;
            }
            if (kind != MarkupKind::Text && kind != MarkupKind::Binary)
            {
                return L"unknown markup kind";
            }
            if (kind == MarkupKind::Text && key.size() > 4 && key.compare(key.size() - 4, 4, L".xbf") == 0)
            {
                return L"an .xbf URI needs binary markup";
            }
            if (content == nullptr || size == 0)
            {
                return L"markup content is empty";
            }
            auto copy = std::make_shared<const std::vector<std::uint8_t>>(content, content + size);

            std::lock_guard<std::mutex> guard(m_lock);
            m_entries[std::move(key)] = MarkupEntry{ kind, std::move(copy) };
            ++m_generation;
            return nullptr;
        }

        // Removes uri, or everything when uri is empty. Returns false only for a URI that can't be normalized.
        bool Remove(std::wstring_view uri)
        {
            std::lock_guard<std::mutex> guard(m_lock);
            if (uri.empty())
            {
                m_entries.clear();
            }
            else
            {
                std::wstring key;
                if (NormalizeMarkupUri(uri, key) != nullptr)
                {
                    return false;
                }
                m_entries.erase(key);
            }
            ++m_generation;
            return true;
        }

        MarkupEntry Find(std::wstring_view uri) const
        {
            std::wstring key;
            if (NormalizeMarkupUri(uri, key) != nullptr)
            {
                return {};
            }
            std::lock_guard<std::mutex> guard(m_lock);
            const auto it = m_entries.find(key);
            return it == m_entries.end() ? MarkupEntry{} : it->second;
        }

        bool Empty() const
        {
            std::lock_guard<std::mutex> guard(m_lock);
            return m_entries.empty();
        }

        std::uint32_t Generation() const
        {
            std::lock_guard<std::mutex> guard(m_lock);
            return m_generation;
        }

    private:
        mutable std::mutex m_lock;
        std::map<std::wstring, MarkupEntry> m_entries;
        std::uint32_t m_generation = 0;
    };

    // Subscribers identified by a cookie. Snapshot() lets callers invoke them without holding the lock, so a
    // subscriber may unsubscribe (or subscribe another) from inside its own callback.
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
            for (auto it = m_items.begin(); it != m_items.end(); ++it)
            {
                if (it->first == cookie)
                {
                    m_items.erase(it);
                    return true;
                }
            }
            return false;
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

    // The file the XAML compiler writes to the app's output (and so its package root) to allow hot reload.
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
}
