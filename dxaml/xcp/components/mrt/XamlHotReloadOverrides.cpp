// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

#include "precomp.h"
#include "PalResourceManager.h"
#include "BasePALResource.h"
#include <XamlHotReloadOverrides.h>
#include <XamlHotReloadRegistry.h>
#include <appmodel.h>

namespace
{
    // Intentionally leaked so lookups during process shutdown never touch a destroyed table.
    XamlHotReload::MarkupTable& Table()
    {
        static auto* table = new XamlHotReload::MarkupTable();
        return *table;
    }

    XamlHotReloadOverrides::OverrideKind ToOverrideKind(XamlHotReload::MarkupKind kind)
    {
        switch (kind)
        {
            case XamlHotReload::MarkupKind::Text: return XamlHotReloadOverrides::OverrideKind::Text;
            case XamlHotReload::MarkupKind::Binary: return XamlHotReloadOverrides::OverrideKind::Binary;
            default: return XamlHotReloadOverrides::OverrideKind::None;
        }
    }

    XamlHotReloadOverrides::OverrideKind ProbeTable(const WCHAR* uri, size_t count)
    {
        return uri ? ToOverrideKind(Table().Find(std::wstring_view(uri, count)).Kind) : XamlHotReloadOverrides::OverrideKind::None;
    }

    // Memory holding replaced markup. Shares the table's copy, so it stays valid after the URI is replaced again.
    class SharedMarkupMemory final : public IPALMemory
    {
    public:
        explicit SharedMarkupMemory(std::shared_ptr<const std::vector<std::uint8_t>> content)
            : m_content(std::move(content))
        {
        }

        XUINT32 AddRef() const override { return static_cast<XUINT32>(InterlockedIncrement(&m_refs)); }
        XUINT32 Release() const override
        {
            const auto refs = static_cast<XUINT32>(InterlockedDecrement(&m_refs));
            if (refs == 0)
            {
                delete this;
            }
            return refs;
        }
        void* GetAddress() const override { return const_cast<std::uint8_t*>(m_content->data()); }
        XUINT32 GetSize() const override { return static_cast<XUINT32>(m_content->size()); }

    private:
        ~SharedMarkupMemory() = default;

        mutable LONG m_refs = 1;
        std::shared_ptr<const std::vector<std::uint8_t>> m_content;
    };

    class MarkupResource final : public CBasePALResource
    {
    public:
        MarkupResource(_In_ IPALUri* uri, std::shared_ptr<const std::vector<std::uint8_t>> content)
            : CBasePALResource(uri)
            , m_content(std::move(content))
        {
        }

        _Check_return_ HRESULT Load(_Outptr_ IPALMemory** ppMemory) override
        {
            *ppMemory = new SharedMarkupMemory(m_content);
            return S_OK;
        }

        _Check_return_ HRESULT Exists(_Out_ bool* pfExists) override
        {
            *pfExists = true;
            return S_OK;
        }

    private:
        std::shared_ptr<const std::vector<std::uint8_t>> m_content;
    };

    bool ComputeEnabledForApp()
    {
        // ms-appx:/// resolves against the package root for a packaged app and the executable's folder otherwise.
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

bool XamlHotReloadOverrides::IsEnabledForApp()
{
    static const bool enabled = ComputeEnabledForApp();
    return enabled;
}

_Check_return_ HRESULT XamlHotReloadOverrides::SetMarkup(
    _In_z_ const WCHAR* uri,
    OverrideKind kind,
    _In_reads_(size) const BYTE* content,
    UINT32 size,
    _Outptr_result_maybenull_z_ const WCHAR** reason)
{
    *reason = nullptr;
    const auto tableKind = kind == OverrideKind::Text ? XamlHotReload::MarkupKind::Text
        : kind == OverrideKind::Binary ? XamlHotReload::MarkupKind::Binary
        : XamlHotReload::MarkupKind::None;
    if (const wchar_t* refused = Table().Set(uri ? uri : L"", tableKind, content, size))
    {
        *reason = refused;
        return E_INVALIDARG;
    }
    g_kindProbe = &ProbeTable;
    g_markupGeneration.fetch_add(1);
    return S_OK;
}

_Check_return_ HRESULT XamlHotReloadOverrides::RemoveMarkup(_In_opt_z_ const WCHAR* uri)
{
    if (!Table().Remove(uri ? uri : L""))
    {
        return E_INVALIDARG;
    }
    g_markupGeneration.fetch_add(1);
    return S_OK;
}

_Check_return_ HRESULT XamlHotReloadOverrides::TryGetOverrideResource(_In_ IPALUri* pUri, _Outptr_result_maybenull_ IPALResource** ppResource)
{
    *ppResource = nullptr;

    if (g_kindProbe.load() == nullptr || pUri == nullptr)
    {
        return S_OK;
    }

    xstring_ptr strCanonical;
    IFC_RETURN(pUri->GetCanonical(&strCanonical));
    const auto entry = Table().Find(std::wstring_view(strCanonical.GetBuffer(), strCanonical.GetCount()));
    if (!entry.Content)
    {
        return S_OK;
    }

    *ppResource = new MarkupResource(pUri, entry.Content);
    return S_OK;
}
