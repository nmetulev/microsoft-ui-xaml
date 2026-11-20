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

    // XAML hot reload: an entry MetadataAPI::ImportUnknownClassInfo made because no provider knew the name, i.e. a
    // cached "not found". These are the names forgotten when a tool says types changed.
    bool IsUnresolved() const
    {
        return m_isUnresolved;
    }

    void MarkUnresolved()
    {
        m_isUnresolved = true;
    }

    // XAML hot reload: the type's code may have changed (new members). The next member lookup that finds nothing asks
    // the metadata provider for the type again (MetadataAPI::RefreshChangedCustomType).
    void MarkChanged()
    {
        m_changed = true;
    }

    bool IsChanged() const
    {
        return m_changed;
    }

    // Swaps in the provider's current description of this type, and returns the previous one, which the caller keeps
    // alive because GetXamlTypeNoRef callers don't hold a reference. A null xamlType keeps the current one.
    ctl::ComPtr<xaml_markup::IXamlType> ReplaceXamlType(_In_opt_ xaml_markup::IXamlType* xamlType)
    {
        m_changed = false;
        ctl::ComPtr<xaml_markup::IXamlType> previous = m_spXamlType;
        if (xamlType != nullptr)
        {
            m_spXamlType = xamlType;
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
    bool                                                    m_isUnresolved{};
    bool                                                    m_changed{};
};
