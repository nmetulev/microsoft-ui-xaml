// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

#pragma once

#include <CStaticLock.h>
#include <MockXamlType.h>

namespace Microsoft { namespace UI { namespace Xaml { namespace Tests {
    namespace Metadata {

        // A stand-in for the IXamlMetadataProvider a hot reload host would hand to
        // MetadataAPI::RegisterSideMetadataProvider.
        //
        // Unlike MockXamlMetadataProvider this deliberately does NOT install itself as
        // DynamicMetadataStorage::m_metadataProvider: a side provider is an *additional* provider and
        // must be reachable only through the registry.
        class MockSideXamlMetadataProvider : public Microsoft::WRL::RuntimeClass<xaml_markup::IXamlMetadataProvider>
        {
        public:
            static Microsoft::WRL::ComPtr<MockSideXamlMetadataProvider> Create()
            {
                return Microsoft::WRL::Make<MockSideXamlMetadataProvider>();
            }
            // Teaches this provider about a type, the way a freshly compiled Page would be added.
            Microsoft::WRL::ComPtr<MockSideXamlMetadataProvider> WithType(_In_ PCWSTR fullName)
            {
                Entry entry;
                entry.Name.Set(fullName);
                entry.Type = MockXamlType::CreateMetadata(entry.Name.Get())
                    ->WithBaseType(KnownTypeIndex::Object);

                m_entries.push_back(std::move(entry));
                return this;
            }

            Microsoft::WRL::ComPtr<xaml_markup::IXamlType> GetTypeNoRef(_In_ PCWSTR fullName) const
            {
                wrl_wrappers::HStringReference nameRef(fullName);
                for (const auto& entry : m_entries)
                {
                    if (AreSameString(entry.Name.Get(), nameRef.Get()))
                    {
                        return entry.Type;
                    }
                }
                return nullptr;
            }

            IFACEMETHODIMP GetXamlType(_In_ wxaml_interop::TypeName type, _Out_ xaml_markup::IXamlType** ppXamlType)
            {
                return GetXamlTypeByFullName(type.Name, ppXamlType);
            }

            IFACEMETHODIMP GetXamlTypeByFullName(_In_ HSTRING hFullName, _Out_ xaml_markup::IXamlType** ppXamlType)
            {
                // The registry must never call a provider while holding the process-wide metadata
                // lock, because a real provider is user code that can call straight back into the
                // metadata API. Record a violation rather than deadlocking so the test can report it.
                if (DirectUI::CStaticLock::IsOwnedByCurrentThread())
                {
                    WasCalledUnderStaticLock = true;
                }

                ++GetXamlTypeByFullNameCallCount;

                *ppXamlType = nullptr;

                for (const auto& entry : m_entries)
                {
                    if (AreSameString(entry.Name.Get(), hFullName))
                    {
                        return entry.Type.CopyTo(ppXamlType);
                    }
                }

                return S_OK;
            }

            IFACEMETHODIMP GetXmlnsDefinitions(_Out_ XUINT32* pnLength, _Outptr_result_buffer_all_maybenull_(*pnLength) xaml_markup::XmlnsDefinition** ppDefinitions)
            {
                *pnLength = 0;
                *ppDefinitions = nullptr;
                return S_OK;
            }

            unsigned int GetXamlTypeByFullNameCallCount = 0;
            bool WasCalledUnderStaticLock = false;

        private:
            // HSTRING is a handle, so it must be compared by value rather than by pointer.
            static bool AreSameString(HSTRING left, HSTRING right)
            {
                INT32 comparison = 0;
                if (FAILED(::WindowsCompareStringOrdinal(left, right, &comparison)))
                {
                    return false;
                }
                return comparison == 0;
            }

            struct Entry
            {
                wrl_wrappers::HString Name;
                Microsoft::WRL::ComPtr<xaml_markup::IXamlType> Type;
            };

            std::vector<Entry> m_entries;
        };

    }

} } } }
