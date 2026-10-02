// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

#include "precomp.h"
#include "PalResourceManager.h"
#include "BasePALResource.h"
#include "FilePathResource.h"
#include <XamlHotReloadOverrides.h>
#include <atomic>
#include <map>
#include <mutex>
#include <string>
#include <cwctype>

namespace
{
    std::mutex& OverrideLock()
    {
        static std::mutex lock;
        return lock;
    }

    // Intentionally leaked so lookups during process shutdown never touch a destroyed map.
    std::map<std::wstring, std::wstring>& OverrideMap()
    {
        static auto* map = new std::map<std::wstring, std::wstring>();
        return *map;
    }

    std::atomic<bool> s_hasOverrides{ false };

    std::wstring NormalizeUri(_In_reads_(count) const WCHAR* uri, size_t count)
    {
        std::wstring key(uri, count);
        for (auto& ch : key)
        {
            ch = static_cast<WCHAR>(std::towlower(ch));
        }
        return key;
    }
}

_Check_return_ HRESULT XamlHotReloadOverrides::SetOverride(_In_z_ const WCHAR* uri, _In_opt_z_ const WCHAR* filePath)
{
    if (uri == nullptr || *uri == L'\0')
    {
        return E_INVALIDARG;
    }

    const std::wstring key = NormalizeUri(uri, wcslen(uri));

    std::lock_guard<std::mutex> guard(OverrideLock());
    auto& map = OverrideMap();
    if (filePath == nullptr || *filePath == L'\0')
    {
        map.erase(key);
    }
    else
    {
        map[key] = filePath;
    }
    s_hasOverrides = !map.empty();
    return S_OK;
}

void XamlHotReloadOverrides::ClearAll()
{
    std::lock_guard<std::mutex> guard(OverrideLock());
    OverrideMap().clear();
    s_hasOverrides = false;
}

_Check_return_ HRESULT XamlHotReloadOverrides::TryGetOverrideResource(_In_ IPALUri* pUri, _Outptr_result_maybenull_ IPALResource** ppResource)
{
    *ppResource = nullptr;

    if (!s_hasOverrides || pUri == nullptr)
    {
        return S_OK;
    }

    xstring_ptr strCanonical;
    IFC_RETURN(pUri->GetCanonical(&strCanonical));
    const std::wstring key = NormalizeUri(strCanonical.GetBuffer(), strCanonical.GetCount());

    std::wstring filePath;
    {
        std::lock_guard<std::mutex> guard(OverrideLock());
        auto& map = OverrideMap();
        auto it = map.find(key);
        if (it == map.end())
        {
            return S_OK;
        }
        filePath = it->second;
    }

    xstring_ptr strFilePath;
    IFC_RETURN(xstring_ptr::CloneBuffer(filePath.c_str(), static_cast<XUINT32>(filePath.size()), &strFilePath));
    IFC_RETURN(CFilePathResource::Create(pUri, strFilePath, ppResource));

#if DBG
    WCHAR message[1024];
    swprintf_s(message, L"XamlHotReload: %s -> %s\n", key.c_str(), filePath.c_str());
    OutputDebugStringW(message);
#endif
    return S_OK;
}
