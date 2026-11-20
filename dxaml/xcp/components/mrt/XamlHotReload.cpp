// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

// XAML hot reload: the replaced .xbf of each component, served to the parser in place of the built file, and the
// stamp check. See XamlHotReload.h.

#include "precomp.h"
#include <BasePALResource.h>
#include <PalResourceManager.h>
#include <appmodel.h>
#include <XamlHotReload.h>
#include <XamlHotReloadRegistry.h>
#include "ResourceMemory.h"

namespace
{
    // Intentionally leaked so lookups during process shutdown never touch a destroyed table.
    XamlHotReload::XbfTable& Table()
    {
        static auto* table = new XamlHotReload::XbfTable();
        return *table;
    }

    bool TableHasReplacedXbf(const wchar_t* uri, std::size_t count)
    {
        return uri != nullptr && Table().Contains(std::wstring_view(uri, count));
    }

    // A replaced .xbf. It holds its own reference to the content, so it stays valid after the URI is replaced again.
    class ReplacedXbfResource final : public CBasePALResource
    {
    public:
        ReplacedXbfResource(_In_ IPALUri* uri, XamlHotReload::XbfContent content)
            : CBasePALResource(uri)
            , m_content(std::move(content))
        {
        }

        _Check_return_ HRESULT Load(_Outptr_ IPALMemory** ppMemory) override
        {
            *ppMemory = make_xref<CResourceMemory>(
                const_cast<XUINT8*>(m_content->data()), static_cast<XUINT32>(m_content->size()), this).detach();
            return S_OK;
        }

        _Check_return_ HRESULT Exists(_Out_ bool* pfExists) override
        {
            *pfExists = true;
            return S_OK;
        }

    private:
        XamlHotReload::XbfContent m_content;
    };

    // ms-appx:/// is the package root for a packaged app, and the executable's folder otherwise.
    bool HasStamp()
    {
        std::wstring folder;
        UINT32 length = 0;
        if (GetCurrentPackagePath(&length, nullptr) == ERROR_INSUFFICIENT_BUFFER)
        {
            folder.resize(length);
            if (GetCurrentPackagePath(&length, folder.data()) != ERROR_SUCCESS)
            {
                return false;
            }
            folder.resize(wcslen(folder.c_str()));
        }
        else
        {
            std::wstring module(MAX_PATH, L'\0');
            for (;;)
            {
                const DWORD written = GetModuleFileNameW(nullptr, module.data(), static_cast<DWORD>(module.size()));
                if (written == 0)
                {
                    return false;
                }
                if (written < module.size())
                {
                    module.resize(written);
                    break;
                }
                module.resize(module.size() * 2);
            }
            const auto slash = module.find_last_of(L"\\/");
            if (slash == std::wstring::npos)
            {
                return false;
            }
            folder = module.substr(0, slash);
        }

        const DWORD attributes = GetFileAttributesW(XamlHotReload::StampPath(folder).c_str());
        return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) == 0;
    }
}

bool XamlHotReload::IsEnabledForApp()
{
    static const bool enabled = HasStamp();
    return enabled;
}

_Check_return_ HRESULT XamlHotReload::SetComponentXbf(
    _In_z_ const wchar_t* uri,
    _In_reads_(size) const std::uint8_t* content,
    std::uint32_t size,
    _Outptr_result_maybenull_z_ const wchar_t** reason)
{
    *reason = Table().Set(uri ? uri : L"", content, size);
    if (*reason)
    {
        return E_INVALIDARG;
    }
    g_replacedXbfProbe = &TableHasReplacedXbf;
    g_markupGeneration.fetch_add(1);
    return S_OK;
}

_Check_return_ HRESULT XamlHotReload::TryGetReplacedXbf(_In_ IPALUri* pUri, _Outptr_result_maybenull_ IPALResource** ppResource)
{
    *ppResource = nullptr;
    if (!IsActive() || pUri == nullptr)
    {
        return S_OK;
    }

    xstring_ptr strCanonical;
    IFC_RETURN(pUri->GetCanonical(&strCanonical));
    if (auto content = Table().FindXbf(std::wstring_view(strCanonical.GetBuffer(), strCanonical.GetCount())))
    {
        *ppResource = make_xref<ReplacedXbfResource>(pUri, std::move(content)).detach();
    }
    return S_OK;
}
