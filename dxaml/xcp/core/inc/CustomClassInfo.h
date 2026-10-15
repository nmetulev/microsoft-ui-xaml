// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

#pragma once
#include "TypeTableStructs.h"
#include "TypeNamePtr.h"

class CCustomClassInfo : public CClassInfo
{
    friend class CClassInfo;

public:
    static _Check_return_ HRESULT Create(
        _In_ KnownNamespaceIndex eNamespaceIndex,
        _In_ const CClassInfo* pBaseType,
        _In_ KnownTypeIndex eTypeIndex,
        _In_ MetaDataTypeInfoFlags eTypeFlags,
        _In_ xaml_markup::IXamlType* pXamlType,
        _In_ const xstring_ptr_view& strName,
        _In_ const xstring_ptr_view& strFullName,
        _In_ bool isBindable,
        _In_ KnownTypeIndex boxedTypeIndex,
        _Outptr_ CCustomClassInfo** ppType);

    xaml_markup::IXamlType* GetXamlTypeNoRef() const
    {
        return m_spXamlType.Get();
    }

    bool IsBindable() const
    {
        return m_isBindable;
    }

    // True when this entry was manufactured by MetadataAPI::ImportUnknownClassInfo because no
    // metadata provider could describe the name, i.e. it is a cached *miss* rather than a type.
    // Only these entries are eligible for eviction by MetadataAPI::InvalidateUnresolvedTypeCache,
    // which is what keeps already-resolved types stable across a side provider registration.
    bool IsUnresolvedPlaceholder() const
    {
        return m_isUnresolvedPlaceholder;
    }

    void MarkUnresolvedPlaceholder()
    {
        m_isUnresolvedPlaceholder = true;
    }

    // Non-zero when this type was supplied by a provider registered after startup
    // (XamlMetadataProviderRegistry.Register): the registry claim that records which providers supply it. When its
    // last provider is unregistered, MetadataAPI forgets the names that map to this entry. The entry itself stays,
    // because live objects and KnownTypeIndex values refer to it.
    std::uint64_t GetSideProviderClaim() const
    {
        return m_sideProviderClaim;
    }

    void SetSideProviderClaim(std::uint64_t claim)
    {
        m_sideProviderClaim = claim;
    }

    void UpdateContentPropertyIndex(KnownPropertyIndex index)
    {
        m_nContentPropertyIndex = index;
    }

    bool RepresentsBoxedType() const;

    const CClassInfo* GetBoxedType() const;

    void Invalidate()
    {
        m_spXamlType = nullptr;
    }

    // Hot reload: the type's code changed (new members). The next member lookup that misses asks the metadata provider
    // for this type again (MetadataAPI::RefreshChangedCustomType); the entry, its index and its properties stay.
    void MarkChanged()
    {
        m_changed = true;
    }

    bool IsChanged() const
    {
        return m_changed;
    }

    // Swaps in the provider's current description of this type. Returns the previous one, which the caller keeps alive:
    // another thread may still be using it.
    ctl::ComPtr<xaml_markup::IXamlType> ReplaceXamlType(_In_opt_ xaml_markup::IXamlType* xamlType)
    {
        m_changed = false;
        ctl::ComPtr<xaml_markup::IXamlType> previous = std::move(m_spXamlType);
        if (xamlType)
        {
            m_spXamlType = xamlType;
        }
        else
        {
            m_spXamlType = previous;
        }
        return previous;
    }

    const xstring_ptr& GetFullNameStorage() const
    {
        return m_strFullName;
    }

private:
    CCustomClassInfo() = default;

    ctl::ComPtr<xaml_markup::IXamlType>       m_spXamlType;
    xstring_ptr                                             m_strName;
    xstring_ptr                                             m_strFullName;
    KnownPropertyIndex                                      m_nContentPropertyIndex;
    KnownTypeIndex                                          m_boxedTypeIndex;
    KnownNamespaceIndex                                     m_nNamespaceIndex;
    bool                                                    m_isBindable{};
    bool                                                    m_isUnresolvedPlaceholder{};
    bool                                                    m_changed{};
    std::uint64_t                                           m_sideProviderClaim{};
};
