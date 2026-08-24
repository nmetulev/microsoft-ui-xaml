// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

#include "precomp.h"
#include <XamlLogging.h>

#include <StringUtilities.h>
#include <XStringUtilities.h>

#include <TypeTableStructs.h>
#include <CustomClassInfo.h>
#include <MetadataAPI.h>
#include <MockDependencyProperty.h>
#include <MockDynamicMetadataStorage.h>
#include <MockXamlMetadataProvider.h>
#include <MockSideXamlMetadataProvider.h>
#include <MockClassInfo.h>
#include <ThreadLocalStorage.h>
#include <CStaticLock.h>

#include "MetadataUnitTests.h"
#include "CustomXamlProviders.h"

using namespace DirectUI;
using namespace xaml_interop;
using namespace xaml_markup;

namespace Microsoft { namespace UI { namespace Xaml { namespace Tests { namespace Metadata {

    DECLARE_CONST_STRING_IN_TEST_CODE(c_NameDependencyObject, L"DependencyObject");
    DECLARE_CONST_STRING_IN_TEST_CODE(c_NameControl, L"Control");
    DECLARE_CONST_STRING_IN_TEST_CODE(c_FullNameDependencyObject, L"Microsoft.UI.Xaml.DependencyObject");
    DECLARE_CONST_STRING_IN_TEST_CODE(c_FullNameControl, L"Microsoft.UI.Xaml.Controls.Control");

    DECLARE_CONST_STRING_IN_TEST_CODE(c_NamePoint, L"Point");
    DECLARE_CONST_STRING_IN_TEST_CODE(c_NameRect, L"Rect");
    DECLARE_CONST_STRING_IN_TEST_CODE(c_NameSize, L"Size");

    DECLARE_CONST_STRING_IN_TEST_CODE(c_Name, L"Name");
    DECLARE_CONST_STRING_IN_TEST_CODE(c_Width, L"Width");

    bool MetadataUnitTests::ClassSetup()
    {
        THROW_IF_FAILED(StaticLockGlobalInit());
        return true;
    }

    bool MetadataUnitTests::ClassCleanup()
    {
        StaticLockGlobalDeinit();
        return true;
    }

    // Verify the common storage access (DynamicMetadataStorageInstanceWithLock) locks and unlocks automatically.
    void MetadataUnitTests::StorageAccessIsThreadSafe()
    {
        auto mock = TlsProvider<DynamicMetadataStorageMock>::CreateWrappedObject();

        // Verify member access via DynamicMetadataStorageInstanceWithLock
        {
            DynamicMetadataStorageInstanceWithLock storage;
            VERIFY_IS_TRUE(CStaticLock::IsLocked());
            auto customPropertiesCache = storage->m_customPropertiesCache;
            VERIFY_IS_TRUE(mock->LastAccessWasLocked, L"DynamicMetadataStorage accessed with a lock");
        }

        // Verify state after DynamicMetadataStorageInstanceWithLock destruction
        VERIFY_IS_FALSE(CStaticLock::IsLocked());

        // Verify access on Reset
        mock->LastAccessWasLocked = false;
        DynamicMetadataStorageInstanceWithLock::Reset();
        VERIFY_IS_TRUE(mock->LastAccessWasLocked, L"DynamicMetadataStorage accessed with a lock");
    }

    void MetadataUnitTests::CanResolveCustomTypeByTypeName()
    {
        wrl_wrappers::HStringReference customTypeStringRef(L"CustomNamespace.CustomType");

        auto customType = MockXamlType::CreateMetadata(customTypeStringRef.Get())
            ->WithBaseType(KnownTypeIndex::Object);

        MockXamlMetadataProvider provider;
        provider.GetXamlTypeByFullNameCallback = [&customTypeStringRef, customType](HSTRING fullName, IXamlType** ppXamlType) -> HRESULT
        {
            if (fullName == customTypeStringRef)
            {
                return customType.CopyTo(ppXamlType);
            }

            *ppXamlType = nullptr;
            return S_OK;
        };

        wxaml_interop::TypeName customTypeName = { customTypeStringRef.Get(), wxaml_interop::TypeKind_Metadata };
        const CClassInfo* resolvedType = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::GetClassInfoByTypeName(customTypeName, &resolvedType));
        VERIFY_IS_NOT_NULL(resolvedType);
        VERIFY_ARE_EQUAL(customType.Get(), static_cast<const CCustomClassInfo*>(resolvedType)->GetXamlTypeNoRef());
    }

    void MetadataUnitTests::CanResolveDirectiveOnCustomType()
    {
        auto mock = TlsProvider<DynamicMetadataStorageMock>::CreateWrappedObject();

        CClassInfo customType;
        customType.m_nIndex = static_cast<KnownTypeIndex>(KnownTypeCount);
        customType.m_nBaseTypeIndex = KnownTypeIndex::Control;

        const CDependencyProperty* result;
        VERIFY_SUCCEEDED(MetadataAPI::TryGetDependencyPropertyByName(&customType, c_Name, &result, /* allowDirectives */ true));
        VERIFY_ARE_EQUAL(KnownPropertyIndex::DependencyObject_Name, result->GetIndex());
    }

    void MetadataUnitTests::IsAssignableFrom()
    {
        // Positive tests.
        VERIFY_IS_TRUE(!!MetadataAPI::IsAssignableFrom(KnownTypeIndex::DependencyObject, KnownTypeIndex::FrameworkElement));
        VERIFY_IS_TRUE(!!MetadataAPI::IsAssignableFrom(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::DependencyObject), MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::FrameworkElement)));

        VERIFY_IS_TRUE(!!MetadataAPI::IsAssignableFrom(KnownTypeIndex::DependencyObject, KnownTypeIndex::Control));
        VERIFY_IS_TRUE(!!MetadataAPI::IsAssignableFrom(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::DependencyObject), MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Control)));

        VERIFY_IS_TRUE(!!MetadataAPI::IsAssignableFrom(KnownTypeIndex::DependencyObject, KnownTypeIndex::String));
        VERIFY_IS_TRUE(!!MetadataAPI::IsAssignableFrom(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::DependencyObject), MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::String)));

        // Negative tests.
        VERIFY_IS_FALSE(!!MetadataAPI::IsAssignableFrom(KnownTypeIndex::Control, KnownTypeIndex::DependencyObject));
        VERIFY_IS_FALSE(!!MetadataAPI::IsAssignableFrom(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Control), MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::DependencyObject)));

        VERIFY_IS_FALSE(!!MetadataAPI::IsAssignableFrom(KnownTypeIndex::FrameworkElement, KnownTypeIndex::Application));
        VERIFY_IS_FALSE(!!MetadataAPI::IsAssignableFrom(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::FrameworkElement), MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Application)));

        // If target type index is KnownTypeIndex::Object, anything can be assigned to it.
        VERIFY_IS_TRUE(!!MetadataAPI::IsAssignableFrom(KnownTypeIndex::Object, KnownTypeIndex::ICommand));
        VERIFY_IS_TRUE(!!MetadataAPI::IsAssignableFrom(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Object), MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::ICommand)));

        // enum has no type handle
        VERIFY_IS_TRUE(!!MetadataAPI::IsAssignableFrom(KnownTypeIndex::Stretch, KnownTypeIndex::Stretch));
        VERIFY_IS_TRUE(!!MetadataAPI::IsAssignableFrom(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Stretch), MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Stretch)));

        VERIFY_IS_FALSE(!!MetadataAPI::IsAssignableFrom(KnownTypeIndex::DependencyObject, KnownTypeIndex::Stretch));
        VERIFY_IS_FALSE(!!MetadataAPI::IsAssignableFrom(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::DependencyObject), MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Stretch)));

        VERIFY_IS_FALSE(!!MetadataAPI::IsAssignableFrom(KnownTypeIndex::Stretch, KnownTypeIndex::SolidColorBrush));
        VERIFY_IS_FALSE(!!MetadataAPI::IsAssignableFrom(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Stretch), MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::SolidColorBrush)));

        VERIFY_IS_FALSE(!!MetadataAPI::IsAssignableFrom(KnownTypeIndex::SolidColorBrush, KnownTypeIndex::Stretch));
        VERIFY_IS_FALSE(!!MetadataAPI::IsAssignableFrom(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::SolidColorBrush), MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Stretch)));

        VERIFY_IS_FALSE(!!MetadataAPI::IsAssignableFrom(KnownTypeIndex::Enumerated, KnownTypeIndex::Stretch));
        VERIFY_IS_FALSE(!!MetadataAPI::IsAssignableFrom(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Enumerated), MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Stretch)));

        VERIFY_IS_FALSE(!!MetadataAPI::IsAssignableFrom(KnownTypeIndex::Stretch, KnownTypeIndex::Enumerated));
        VERIFY_IS_FALSE(!!MetadataAPI::IsAssignableFrom(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Stretch), MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Enumerated)));

        // 32-bit type handle cusp
        VERIFY_IS_TRUE(!!MetadataAPI::IsAssignableFrom(KnownTypeIndex::ListViewBase, KnownTypeIndex::ListView));
        VERIFY_IS_TRUE(!!MetadataAPI::IsAssignableFrom(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::ListViewBase), MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::ListView)));

        // Internal hierarchy leaking to the outside world.  Yes, Duration, RepeatBehavior and KeyTime can be assigned to TimeSpan...
        VERIFY_IS_TRUE(!!MetadataAPI::IsAssignableFrom(KnownTypeIndex::TimeSpan, KnownTypeIndex::Duration));
        VERIFY_IS_TRUE(!!MetadataAPI::IsAssignableFrom(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::TimeSpan), MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Duration)));

        VERIFY_IS_TRUE(!!MetadataAPI::IsAssignableFrom(KnownTypeIndex::TimeSpan, KnownTypeIndex::RepeatBehavior));
        VERIFY_IS_TRUE(!!MetadataAPI::IsAssignableFrom(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::TimeSpan), MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::RepeatBehavior)));

        VERIFY_IS_TRUE(!!MetadataAPI::IsAssignableFrom(KnownTypeIndex::Duration, KnownTypeIndex::RepeatBehavior));
        VERIFY_IS_TRUE(!!MetadataAPI::IsAssignableFrom(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Duration), MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::RepeatBehavior)));

        VERIFY_IS_TRUE(!!MetadataAPI::IsAssignableFrom(KnownTypeIndex::TimeSpan, KnownTypeIndex::KeyTime));
        VERIFY_IS_TRUE(!!MetadataAPI::IsAssignableFrom(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::TimeSpan), MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::KeyTime)));
    }

    void MetadataUnitTests::GetClassInfoByName()
    {
        const CClassInfo* pType = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::GetClassInfoByFullName(c_NameDependencyObject, &pType));
        VERIFY_ARE_EQUAL(KnownTypeIndex::DependencyObject, pType->GetIndex());

        VERIFY_SUCCEEDED(MetadataAPI::GetClassInfoByFullName(c_NameControl, &pType));
        VERIFY_ARE_EQUAL(KnownTypeIndex::Control, pType->GetIndex());
    }

    void MetadataUnitTests::GetClassInfoByFullName()
    {
        const CClassInfo* pType = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::GetClassInfoByFullName(c_FullNameDependencyObject, &pType));
        VERIFY_ARE_EQUAL(KnownTypeIndex::DependencyObject, pType->GetIndex());

        VERIFY_SUCCEEDED(MetadataAPI::GetClassInfoByFullName(c_FullNameControl, &pType));
        VERIFY_ARE_EQUAL(KnownTypeIndex::Control, pType->GetIndex());
    }

    void MetadataUnitTests::GetTypeNameByClassInfo()
    {
        wxaml_interop::TypeName typeNameDO = {};
        VERIFY_SUCCEEDED(MetadataAPI::GetTypeNameByClassInfo(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::DependencyObject), &typeNameDO));
        VERIFY_ARE_EQUAL(wxaml_interop::TypeKind::TypeKind_Metadata, typeNameDO.Kind);
        VERIFY_ARE_STRINGS_EQUAL(L"Microsoft.UI.Xaml.DependencyObject", typeNameDO.Name);

        wxaml_interop::TypeName typeNameInt32 = {};
        VERIFY_SUCCEEDED(MetadataAPI::GetTypeNameByClassInfo(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Int32), &typeNameInt32));
        VERIFY_ARE_EQUAL(wxaml_interop::TypeKind::TypeKind_Primitive, typeNameInt32.Kind);
        VERIFY_ARE_STRINGS_EQUAL(L"Int32", typeNameInt32.Name);

        WindowsDeleteString(typeNameInt32.Name);
        WindowsDeleteString(typeNameDO.Name);
    }

    void MetadataUnitTests::IsConstructible()
    {
        VERIFY_IS_TRUE(!!MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::DependencyObject)->IsConstructible());
        VERIFY_IS_FALSE(!!MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::EventHandlerStub)->IsConstructible());
    }

    void MetadataUnitTests::BaseTypes()
    {
        VERIFY_ARE_EQUAL(KnownTypeIndex::UnknownType, MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Object)->GetBaseType()->GetIndex());
        VERIFY_ARE_EQUAL(KnownTypeIndex::Object, MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::DependencyObject)->GetBaseType()->GetIndex());
        VERIFY_ARE_EQUAL(KnownTypeIndex::UIElement, MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::FrameworkElement)->GetBaseType()->GetIndex());
        VERIFY_ARE_EQUAL(KnownTypeIndex::UnknownType, MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::IVectorOfPageStackEntry)->GetBaseType()->GetIndex());
    }

    void MetadataUnitTests::ValidateISupportInitializeFlagOnTypes()
    {
        VERIFY_IS_TRUE(!!MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Selector)->IsISupportInitialize());
        VERIFY_IS_TRUE(!!MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::GridView)->IsISupportInitialize());
    }

    void MetadataUnitTests::DependencyObjectIsInGoodState()
    {
        const CClassInfo* pType = MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::DependencyObject);
        VERIFY_ARE_EQUAL(KnownPropertyIndex::UnknownType_UnknownProperty, pType->GetContentProperty()->GetIndex());
        VERIFY_ARE_EQUAL(KnownNamespaceIndex::Microsoft_UI_Xaml, pType->GetNamespace()->GetIndex());
        VERIFY_ARE_STRINGS_EQUAL(L"DependencyObject", pType->GetName());
        VERIFY_ARE_STRINGS_EQUAL(L"Microsoft.UI.Xaml.DependencyObject", pType->GetFullName());
        VERIFY_IS_FALSE(!!pType->HasTypeConverter());
        VERIFY_IS_FALSE(!!pType->IsBindable());
        VERIFY_IS_TRUE(!!pType->IsBuiltinType());
        VERIFY_IS_FALSE(!!pType->IsCollection());
        VERIFY_IS_TRUE(!!pType->IsConstructible());
        VERIFY_IS_FALSE(!!pType->IsDictionary());
        VERIFY_IS_FALSE(!!pType->IsInterface());
        VERIFY_IS_FALSE(!!pType->IsEnum());
        VERIFY_IS_FALSE(!!pType->IsISupportInitialize());
        VERIFY_IS_FALSE(!!pType->IsMarkupExtension());
        VERIFY_IS_TRUE(!!pType->IsNullable());
        VERIFY_IS_FALSE(!!pType->IsNumericType());
        VERIFY_IS_FALSE(!!pType->IsPrimitive());
        VERIFY_IS_FALSE(!!pType->IsValueType());
        VERIFY_IS_FALSE(!!pType->IsWhitespaceSignificant());
        VERIFY_IS_FALSE(!!pType->TrimSurroundingWhitespace());
    }

    void MetadataUnitTests::GetPrimitiveClassInfo()
    {
        const CClassInfo* pNormalizedType = nullptr;

        // DO should normalize to Object.
        const CClassInfo* pTypeDO = MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::DependencyObject);
        VERIFY_SUCCEEDED(MetadataAPI::GetPrimitiveClassInfo(pTypeDO, &pNormalizedType));
        VERIFY_ARE_EQUAL(KnownTypeIndex::Object, pNormalizedType->GetIndex());

        // Int32 should normalize to Int32.
        const CClassInfo* pTypeInt32 = MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Int32);
        VERIFY_SUCCEEDED(MetadataAPI::GetPrimitiveClassInfo(pTypeInt32, &pNormalizedType));
        VERIFY_ARE_EQUAL(KnownTypeIndex::Int32, pNormalizedType->GetIndex());
    }

    void MetadataUnitTests::CanExtractNamespaceNameAndShortName()
    {
        xephemeral_string_ptr namespaceName, typeName;

        DECLARE_CONST_STRING_IN_TEST_CODE(c_fullTypeName1, L"FooNamespace.FooType");
        VERIFY_SUCCEEDED(MetadataAPI::ExtractNamespaceNameAndShortName(c_fullTypeName1, &namespaceName, &typeName));
        VERIFY_IS_TRUE(!!namespaceName.Equals(L"FooNamespace"));
        VERIFY_IS_TRUE(!!typeName.Equals(L"FooType"));

        DECLARE_CONST_STRING_IN_TEST_CODE(c_fullTypeName2, L"FooNamespace.FooSubNamespace.FooType");
        VERIFY_SUCCEEDED(MetadataAPI::ExtractNamespaceNameAndShortName(c_fullTypeName2, &namespaceName, &typeName));
        VERIFY_IS_TRUE(!!namespaceName.Equals(L"FooNamespace.FooSubNamespace"));
        VERIFY_IS_TRUE(!!typeName.Equals(L"FooType"));

        DECLARE_CONST_STRING_IN_TEST_CODE(c_fullTypeName3, L"FooNamespace.FooType<BarType>");
        VERIFY_SUCCEEDED(MetadataAPI::ExtractNamespaceNameAndShortName(c_fullTypeName3, &namespaceName, &typeName));
        VERIFY_IS_TRUE(!!namespaceName.Equals(L"FooNamespace"));
        VERIFY_IS_TRUE(!!typeName.Equals(L"FooType<BarType>"));

        DECLARE_CONST_STRING_IN_TEST_CODE(c_fullTypeName4, L"FooNamespace.FooSubNamespace.FooType<BarType>");
        VERIFY_SUCCEEDED(MetadataAPI::ExtractNamespaceNameAndShortName(c_fullTypeName4, &namespaceName, &typeName));
        VERIFY_IS_TRUE(!!namespaceName.Equals(L"FooNamespace.FooSubNamespace"));
        VERIFY_IS_TRUE(!!typeName.Equals(L"FooType<BarType>"));

        DECLARE_CONST_STRING_IN_TEST_CODE(c_fullTypeName5, L"FooNamespace.FooType<BarNamespace.BarType>");
        VERIFY_SUCCEEDED(MetadataAPI::ExtractNamespaceNameAndShortName(c_fullTypeName5, &namespaceName, &typeName));
        VERIFY_IS_TRUE(!!namespaceName.Equals(L"FooNamespace"));
        VERIFY_IS_TRUE(!!typeName.Equals(L"FooType<BarNamespace.BarType>"));

        DECLARE_CONST_STRING_IN_TEST_CODE(c_fullTypeName6, L"FooNamespace.FooSubNamespace.FooType<BarNamespace.BarType>");
        VERIFY_SUCCEEDED(MetadataAPI::ExtractNamespaceNameAndShortName(c_fullTypeName6, &namespaceName, &typeName));
        VERIFY_IS_TRUE(!!namespaceName.Equals(L"FooNamespace.FooSubNamespace"));
        VERIFY_IS_TRUE(!!typeName.Equals(L"FooType<BarNamespace.BarType>"));

        DECLARE_CONST_STRING_IN_TEST_CODE(c_fullTypeName7, L"FooNamespace.FooType<BarNamespace.BarSubNamespace.BarType>");
        VERIFY_SUCCEEDED(MetadataAPI::ExtractNamespaceNameAndShortName(c_fullTypeName7, &namespaceName, &typeName));
        VERIFY_IS_TRUE(!!namespaceName.Equals(L"FooNamespace"));
        VERIFY_IS_TRUE(!!typeName.Equals(L"FooType<BarNamespace.BarSubNamespace.BarType>"));

        DECLARE_CONST_STRING_IN_TEST_CODE(c_fullTypeName8, L"FooNamespace.FooSubNamespace.FooType<BarNamespace.BarSubNamespace.BarType>");
        VERIFY_SUCCEEDED(MetadataAPI::ExtractNamespaceNameAndShortName(c_fullTypeName8, &namespaceName, &typeName));
        VERIFY_IS_TRUE(!!namespaceName.Equals(L"FooNamespace.FooSubNamespace"));
        VERIFY_IS_TRUE(!!typeName.Equals(L"FooType<BarNamespace.BarSubNamespace.BarType>"));
    }

    void MetadataUnitTests::IXamlMemberTypeMayReturnNull()
    {
        auto mock = TlsProvider<DynamicMetadataStorageMock>::CreateWrappedObject();

        IXamlMemberTypeMayReturnNull_XamlMember member;
        const CDependencyProperty* pResult = nullptr;

        // Import a custom property.
        const CClassInfo* pType = MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::DependencyObject);
        VERIFY_ARE_EQUAL(S_OK, MetadataAPI::ImportPropertyInfo(pType, &member, &pResult));
    }

    void MetadataUnitTests::FoundationTypesInCorrectNamespace()
    {
        const CClassInfo* pPointType = MetadataAPI::GetBuiltinClassInfoByName(c_NamePoint);
        VERIFY_ARE_EQUAL(KnownNamespaceIndex::Windows_Foundation, pPointType->GetNamespace()->m_nIndex);

        const CClassInfo* pRectType = MetadataAPI::GetBuiltinClassInfoByName(c_NameRect);
        VERIFY_ARE_EQUAL(KnownNamespaceIndex::Windows_Foundation, pRectType->GetNamespace()->m_nIndex);

        const CClassInfo* pSizeType = MetadataAPI::GetBuiltinClassInfoByName(c_NameSize);
        VERIFY_ARE_EQUAL(KnownNamespaceIndex::Windows_Foundation, pSizeType->GetNamespace()->m_nIndex);
    }

    void MetadataUnitTests::RequiresPeerActivation()
    {
        // Types that currently have custom logic/state in the framework peer.
        VERIFY_IS_TRUE(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::EntranceThemeTransition)->RequiresPeerActivation());
        VERIFY_IS_TRUE(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::AddDeleteThemeTransition)->RequiresPeerActivation());
        VERIFY_IS_TRUE(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Binding)->RequiresPeerActivation());
        VERIFY_IS_TRUE(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Button)->RequiresPeerActivation());
        VERIFY_IS_TRUE(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::TextBox)->RequiresPeerActivation());
        VERIFY_IS_TRUE(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::UserControl)->RequiresPeerActivation());
        VERIFY_IS_TRUE(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::ContentControl)->RequiresPeerActivation());
        VERIFY_IS_TRUE(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::ItemsControl)->RequiresPeerActivation());
        VERIFY_IS_TRUE(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::ListView)->RequiresPeerActivation());

        // Types that don't have custom logic/state in the framework peer.
        VERIFY_IS_FALSE(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::DependencyObject)->RequiresPeerActivation());
        VERIFY_IS_FALSE(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::TextBlock)->RequiresPeerActivation());
        VERIFY_IS_FALSE(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Control)->RequiresPeerActivation());
        VERIFY_IS_FALSE(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::ContentPresenter)->RequiresPeerActivation());
        VERIFY_IS_FALSE(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::StackPanel)->RequiresPeerActivation());
        VERIFY_IS_FALSE(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Grid)->RequiresPeerActivation());
        VERIFY_IS_FALSE(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Canvas)->RequiresPeerActivation());
        VERIFY_IS_FALSE(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Border)->RequiresPeerActivation());
        VERIFY_IS_FALSE(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Underline)->RequiresPeerActivation());
    }

    void MetadataUnitTests::GetStorageType()
    {
        CDependencyProperty dp;

        dp.SetPropertyTypeIndex(KnownTypeIndex::String);
        VERIFY_ARE_EQUAL(valueString, dp.GetStorageType());

        dp.SetPropertyTypeIndex(KnownTypeIndex::Uri);
        VERIFY_ARE_EQUAL(valueString, dp.GetStorageType());

        dp.SetPropertyTypeIndex(KnownTypeIndex::Int32);
        VERIFY_ARE_EQUAL(valueSigned, dp.GetStorageType());

        dp.SetPropertyTypeIndex(KnownTypeIndex::Double);
        dp.SetIndex(KnownPropertyIndex::TimeSpan_Seconds);
        VERIFY_ARE_EQUAL(valueDouble, dp.GetStorageType());
    }

    void MetadataUnitTests::GetOffset()
    {
        MockDependencyProperty dp;

        dp.offset = 42;
        VERIFY_ARE_EQUAL(42, dp.GetOffset());
    }

    void MetadataUnitTests::GetGroupOffset()
    {
        MockDependencyProperty dp;

        dp.groupOffset = 42;
        VERIFY_ARE_EQUAL(42, dp.GetGroupOffset());
    }

    void MetadataUnitTests::ReRegisteringBuiltinDPDoesNotAV()
    {
        auto mock = TlsProvider<DynamicMetadataStorageMock>::CreateWrappedObject();

        MockDependencyProperty dp;

        VERIFY_SUCCEEDED(MetadataAPI::AssociateDependencyProperty(
            MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::FrameworkElement),
            &dp));

        // Register one more time.
        VERIFY_SUCCEEDED(MetadataAPI::AssociateDependencyProperty(
            MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::FrameworkElement),
            &dp));

        VERIFY_ARE_EQUAL(1, mock->GetStorage()->m_customDPsByTypeAndNameCache->size());
    }

    void MetadataUnitTests::ReRegisteringCustomDPSetsUnderlyingDP()
    {
        DECLARE_CONST_STRING_IN_TEST_CODE(c_fooPropertyName, L"Foo");

        auto mock = TlsProvider<DynamicMetadataStorageMock>::CreateWrappedObject();

        CCustomProperty* customProp = nullptr;
        VERIFY_SUCCEEDED(CCustomProperty::Create(
            mock->GetStorage()->GetNextAvailablePropertyIndex(),
            KnownTypeIndex::DependencyObject,
            KnownTypeIndex::FrameworkElement,
            MetaDataPropertyInfoFlags::None,
            nullptr,
            c_fooPropertyName,
            &customProp));

        VERIFY_ARE_EQUAL(nullptr, mock->GetStorage()->m_customDPsByTypeAndNameCache);

        // Register "Foo"
        VERIFY_SUCCEEDED(MetadataAPI::AssociateDependencyProperty(
            MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::FrameworkElement),
            customProp));
        VERIFY_ARE_EQUAL(1, mock->GetStorage()->m_customDPsByTypeAndNameCache->size());

        // Verify stored property
        DynamicMetadataStorage::PropertiesTable* propertiesMap
            = mock->GetStorage()->m_customDPsByTypeAndNameCache->find(KnownTypeIndex::FrameworkElement)->second.get();
        const CDependencyProperty* storedProperty1 = propertiesMap->find(c_fooPropertyName)->second;
        VERIFY_ARE_EQUAL(storedProperty1, customProp);

        // Register "Foo" again with different prop
        CCustomProperty* customProp2 = nullptr;
        VERIFY_SUCCEEDED(CCustomProperty::Create(
            mock->GetStorage()->GetNextAvailablePropertyIndex(),
            KnownTypeIndex::DependencyObject,
            KnownTypeIndex::FrameworkElement,
            MetaDataPropertyInfoFlags::None,
            nullptr,
            c_fooPropertyName,
            &customProp2));

        VERIFY_SUCCEEDED(MetadataAPI::AssociateDependencyProperty(
            MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::FrameworkElement),
            customProp2));

        VERIFY_ARE_EQUAL(1, mock->GetStorage()->m_customDPsByTypeAndNameCache->size());

        // Verify stored property is still the original property
        const CDependencyProperty* storedProperty2 = propertiesMap->find(c_fooPropertyName)->second;
        VERIFY_ARE_EQUAL(storedProperty2, customProp2);

        // Verify underlying DP was set
        const CDependencyProperty* underlyingProp = nullptr;
        VERIFY_SUCCEEDED(customProp->TryGetUnderlyingDP(&underlyingProp));
        VERIFY_ARE_EQUAL(underlyingProp, customProp2);
    }

    void MetadataUnitTests::RunClassConstructorIsDelayed()
    {
        auto mock1 = TlsProvider<ClassInfoCallbacks>::CreateWrappedObject();
        bool constructorCalled = false;
        mock1->RunClassConstructorIfNecessary = [&constructorCalled]() -> HRESULT
        {
            constructorCalled = true;
            return S_OK;
        };

        IXamlMemberTypeMayReturnNull_XamlMember member;
        const CDependencyProperty* pResult = nullptr;

        // Import a custom property.
        wrl_wrappers::HStringReference customTypeStringRef(L"CustomNamespace.CustomType");

        auto customType = MockXamlType::CreateMetadata(customTypeStringRef.Get())
            ->WithBaseType(KnownTypeIndex::Object);

        MockXamlMetadataProvider provider;
        provider.GetXamlTypeByFullNameCallback = [&customTypeStringRef, customType](HSTRING fullName, IXamlType** ppXamlType) -> HRESULT
        {
            if (fullName == customTypeStringRef)
            {
                return customType.CopyTo(ppXamlType);
            }

            *ppXamlType = nullptr;
            return S_OK;
        };

        wxaml_interop::TypeName customTypeName = { customTypeStringRef.Get(), wxaml_interop::TypeKind_Metadata };
        const CClassInfo* resolvedType = nullptr;
    }

    void MetadataUnitTests::RunClassConstructorIsNotDelayed()
    {
        auto mock1 = TlsProvider<ClassInfoCallbacks>::CreateWrappedObject();
        bool constructorCalled = false;
        mock1->RunClassConstructorIfNecessary = [&constructorCalled]() -> HRESULT
        {
            constructorCalled = true;
            return S_OK;
        };

        IXamlMemberTypeMayReturnNull_XamlMember member;
        const CDependencyProperty* pResult = nullptr;

        // Import a custom property.
        wrl_wrappers::HStringReference customTypeStringRef(L"CustomNamespace.CustomType");

        auto customType = MockXamlType::CreateMetadata(customTypeStringRef.Get())
            ->WithBaseType(KnownTypeIndex::Object);

        MockXamlMetadataProvider provider;
        provider.GetXamlTypeByFullNameCallback = [&customTypeStringRef, customType](HSTRING fullName, IXamlType** ppXamlType) -> HRESULT
        {
            if (fullName == customTypeStringRef)
            {
                return customType.CopyTo(ppXamlType);
            }

            *ppXamlType = nullptr;
            return S_OK;
        };

        wxaml_interop::TypeName customTypeName = { customTypeStringRef.Get(), wxaml_interop::TypeKind_Metadata };
        const CClassInfo* resolvedType = nullptr;
    }

    void MetadataUnitTests::TestPropertyGetters()
    {
        DECLARE_CONST_STRING_IN_TEST_CODE(c_customPropertyName, L"CustomProperty");
        DECLARE_CONST_STRING_IN_TEST_CODE(c_customDependencyPropertyName, L"CustomDependencyProperty");

        auto mock = TlsProvider<DynamicMetadataStorageMock>::CreateWrappedObject();

        CCustomProperty* customProp = nullptr;

        VERIFY_SUCCEEDED(CCustomProperty::Create(
            mock->GetStorage()->GetNextAvailablePropertyIndex(),
            KnownTypeIndex::DependencyObject,
            KnownTypeIndex::FrameworkElement,
            MetaDataPropertyInfoFlags::None,
            nullptr,
            c_customPropertyName,
            &customProp));

        VERIFY_IS_TRUE(customProp->Is<CCustomProperty>());
        VERIFY_IS_TRUE(customProp->Is<CDependencyProperty>());
        VERIFY_IS_FALSE(customProp->Is<CCustomDependencyProperty>());
        VERIFY_IS_FALSE(customProp->Is<CSimpleProperty>());
        VERIFY_ARE_EQUAL(customProp, customProp->AsOrNull<CCustomProperty>());
        VERIFY_ARE_EQUAL(customProp, customProp->AsOrNull<CDependencyProperty>());
        VERIFY_IS_NULL(customProp->AsOrNull<CCustomDependencyProperty>());
        VERIFY_IS_NULL(customProp->AsOrNull<CSimpleProperty>());

        CCustomDependencyProperty* customDependencyProp = nullptr;

        VERIFY_SUCCEEDED(CCustomDependencyProperty::Create(
            mock->GetStorage()->GetNextAvailablePropertyIndex(),
            MetaDataPropertyInfoFlags::None,
            c_customDependencyPropertyName,
            &customDependencyProp));

        VERIFY_IS_FALSE(customDependencyProp->Is<CCustomProperty>());
        VERIFY_IS_TRUE(customDependencyProp->Is<CDependencyProperty>());
        VERIFY_IS_TRUE(customDependencyProp->Is<CCustomDependencyProperty>());
        VERIFY_IS_FALSE(customDependencyProp->Is<CSimpleProperty>());
        VERIFY_IS_NULL(customDependencyProp->AsOrNull<CCustomProperty>());
        VERIFY_ARE_EQUAL(customDependencyProp, customDependencyProp->AsOrNull<CDependencyProperty>());
        VERIFY_ARE_EQUAL(customDependencyProp, customDependencyProp->AsOrNull<CCustomDependencyProperty>());
        VERIFY_IS_NULL(customDependencyProp->AsOrNull<CSimpleProperty>());

        const CPropertyBase* builtInProperty = MetadataAPI::GetPropertyBaseByIndex(KnownPropertyIndex::DependencyObject_Name);

        VERIFY_IS_FALSE(builtInProperty->Is<CCustomProperty>());
        VERIFY_IS_TRUE(builtInProperty->Is<CDependencyProperty>());
        VERIFY_IS_FALSE(builtInProperty->Is<CCustomDependencyProperty>());
        VERIFY_IS_FALSE(builtInProperty->Is<CSimpleProperty>());
        VERIFY_IS_NULL(builtInProperty->AsOrNull<CCustomProperty>());
        VERIFY_ARE_EQUAL(builtInProperty, builtInProperty->AsOrNull<CDependencyProperty>());
        VERIFY_IS_NULL(builtInProperty->AsOrNull<CCustomDependencyProperty>());
        VERIFY_IS_NULL(builtInProperty->AsOrNull<CSimpleProperty>());

        {
            auto prop = MetadataAPI::GetPropertyBaseByIndex(KnownPropertyIndex::DependencyObject_Name);
            VERIFY_ARE_EQUAL(builtInProperty, prop);
        }

        {
            auto prop = MetadataAPI::GetDependencyPropertyByIndex(KnownPropertyIndex::DependencyObject_Name);
            VERIFY_ARE_EQUAL(builtInProperty, prop);
        }

        {
            const CPropertyBase* prop = MetadataAPI::TryGetBuiltInPropertyBaseByName(
                MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::DependencyObject),
                c_Name,
                true);
            VERIFY_ARE_EQUAL(builtInProperty, prop);
        }

        {
            const CPropertyBase* prop = MetadataAPI::TryGetBuiltInPropertyBaseByName(
                MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::FrameworkElement),
                c_customPropertyName);
            VERIFY_IS_NULL(prop);
        }

        {
            const CDependencyProperty* prop = nullptr;

            VERIFY_SUCCEEDED(MetadataAPI::TryGetDependencyPropertyByName(
                MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::DependencyObject),
                c_Name,
                &prop,
                true));

            VERIFY_ARE_EQUAL(builtInProperty, prop);
        }
    }
    #pragma region Side IXamlMetadataProvider registry (experimental)

    DECLARE_CONST_STRING_IN_TEST_CODE(c_FullNameNewPage, L"HotReload.NewPage");
    DECLARE_CONST_STRING_IN_TEST_CODE(c_FullNameOtherPage, L"HotReload.OtherPage");
    DECLARE_CONST_STRING_IN_TEST_CODE(c_FullNameAppOwnedType, L"HotReload.AppOwnedType");
    DECLARE_CONST_STRING_IN_TEST_CODE(c_FullNameContestedType, L"HotReload.ContestedType");

    namespace
    {
        // The application's own generated provider. It knows about c_FullNameAppOwnedType, optionally
        // about c_FullNameContestedType, and nothing else - which is the situation after a new Page is
        // added to a running app.
        void SetUpAppProvider(
            MockXamlMetadataProvider& provider,
            const Microsoft::WRL::ComPtr<MockXamlType>& appOwnedType,
            const Microsoft::WRL::ComPtr<MockXamlType>& contestedType = nullptr)
        {
            auto resolve = [appOwnedType, contestedType](HSTRING fullName, IXamlType** ppXamlType) -> HRESULT
            {
                if (appOwnedType != nullptr && wrl_wrappers::HStringReference(L"HotReload.AppOwnedType") == fullName)
                {
                    return appOwnedType.CopyTo(ppXamlType);
                }

                if (contestedType != nullptr && wrl_wrappers::HStringReference(L"HotReload.ContestedType") == fullName)
                {
                    return contestedType.CopyTo(ppXamlType);
                }

                *ppXamlType = nullptr;
                return S_OK;
            };

            provider.GetXamlTypeByFullNameCallback = resolve;

            provider.GetXamlTypeCallback = [resolve](wxaml_interop::TypeName typeName, IXamlType** ppXamlType) -> HRESULT
            {
                return resolve(typeName.Name, ppXamlType);
            };
        }

        bool IsUnresolved(const CClassInfo* type)
        {
            return type != nullptr &&
                   !MetadataAPI::IsKnownIndex(type->GetIndex()) &&
                   static_cast<const CCustomClassInfo*>(type)->IsUnresolvedPlaceholder();
        }

        ULONG GetRefCount(_In_ xaml_markup::IXamlMetadataProvider* object)
        {
            object->AddRef();
            return object->Release();
        }
    }

    // 1. Before anything is registered, a name nobody describes resolves to an unresolved placeholder.
    //    That placeholder IS the cached miss the rest of these tests have to defeat.
    void MetadataUnitTests::SideProvider_UnknownTypeIsCachedAsUnresolvedMiss()
    {
        MockXamlMetadataProvider appProvider;
        SetUpAppProvider(appProvider, nullptr);

        const CClassInfo* resolved = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::TryGetClassInfoByFullName(c_FullNameNewPage, false, &resolved));
        VERIFY_IS_NOT_NULL(resolved);
        VERIFY_IS_TRUE(IsUnresolved(resolved), L"An undescribed name is cached as an unresolved placeholder");

        // The miss really is cached: a second lookup returns the very same placeholder rather than
        // asking the provider again.
        const CClassInfo* resolvedAgain = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::TryGetClassInfoByFullName(c_FullNameNewPage, false, &resolvedAgain));
        VERIFY_ARE_EQUAL(resolved, resolvedAgain, L"The miss is cached, not recomputed");
    }

    // 2. The headline scenario, including its own mutation proof.
    //
    //    Note step 3: after registering the provider but BEFORE invalidating, the lookup still misses.
    //    That is deliberate. It proves the cached miss is real and that InvalidateUnresolvedTypeCache
    //    is load-bearing - if the invalidation logic were removed, step 5 would fail.
    void MetadataUnitTests::SideProvider_ResolvesNameThatPreviouslyMissed()
    {
        MockXamlMetadataProvider appProvider;
        SetUpAppProvider(appProvider, nullptr);

        // Step 1: the name does not resolve, and the miss is cached.
        const CClassInfo* beforeRegistration = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::TryGetClassInfoByFullName(c_FullNameNewPage, false, &beforeRegistration));
        VERIFY_IS_TRUE(IsUnresolved(beforeRegistration), L"Lookup misses before registration");

        // Step 2: register the side provider that knows the new type.
        auto sideProvider = MockSideXamlMetadataProvider::Create()->WithType(L"HotReload.NewPage");

        auto registration = MetadataAPI::RegisterSideMetadataProvider(sideProvider.Get());
        VERIFY_ARE_EQUAL(XamlMetadataProviderRegistrationStatus::Registered, registration.Status);
        VERIFY_IS_TRUE(registration.IsRegistered());
        VERIFY_IS_TRUE(registration.ProviderId != c_invalidXamlMetadataProviderId, L"A registered provider gets a real id");

        // Step 3 (mutation proof): registration alone must NOT be enough, because the miss is cached.
        const CClassInfo* afterRegistrationOnly = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::TryGetClassInfoByFullName(c_FullNameNewPage, false, &afterRegistrationOnly));
        VERIFY_ARE_EQUAL(beforeRegistration, afterRegistrationOnly, L"The cached miss still shadows the new provider");
        VERIFY_IS_TRUE(IsUnresolved(afterRegistrationOnly));

        // Step 4: invalidate the cached miss for just this name.
        auto invalidation = MetadataAPI::InvalidateUnresolvedTypeCache(c_FullNameNewPage);
        VERIFY_ARE_EQUAL(static_cast<size_t>(1), invalidation.UnresolvedEntriesEvicted, L"Exactly the one cached miss was evicted");
        VERIFY_IS_TRUE(invalidation.Generation > registration.Generation, L"Invalidation is observable via the generation");

        // Step 5: the name now resolves through the side provider.
        const CClassInfo* afterInvalidation = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::TryGetClassInfoByFullName(c_FullNameNewPage, false, &afterInvalidation));
        VERIFY_IS_NOT_NULL(afterInvalidation);
        VERIFY_IS_FALSE(IsUnresolved(afterInvalidation), L"The name resolves through the side provider");
        VERIFY_ARE_EQUAL(
            sideProvider->GetTypeNoRef(L"HotReload.NewPage").Get(),
            static_cast<const CCustomClassInfo*>(afterInvalidation)->GetXamlTypeNoRef(),
            L"The resolved type is the one the side provider supplied");
    }

    // 3. The by-TypeName entry point (used by Frame.Navigate(typeof(NewPage))) resolves too.
    void MetadataUnitTests::SideProvider_ResolvesByTypeName()
    {
        MockXamlMetadataProvider appProvider;
        SetUpAppProvider(appProvider, nullptr);

        auto sideProvider = MockSideXamlMetadataProvider::Create()->WithType(L"HotReload.NewPage");
        VERIFY_IS_TRUE(MetadataAPI::RegisterSideMetadataProvider(sideProvider.Get()).IsRegistered());

        wrl_wrappers::HStringReference newPageRef(L"HotReload.NewPage");
        wxaml_interop::TypeName newPageTypeName = { newPageRef.Get(), wxaml_interop::TypeKind_Metadata };

        const CClassInfo* resolved = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::GetClassInfoByTypeName(newPageTypeName, &resolved));
        VERIFY_IS_NOT_NULL(resolved);
        VERIFY_IS_FALSE(IsUnresolved(resolved), L"GetClassInfoByTypeName resolves through the side provider");
        VERIFY_ARE_EQUAL(
            sideProvider->GetTypeNoRef(L"HotReload.NewPage").Get(),
            static_cast<const CCustomClassInfo*>(resolved)->GetXamlTypeNoRef());
    }

    // 4. Registration is additive. Two independent halves:
    //      (a) a name already resolved keeps its identity, and
    //      (b) a name resolved for the FIRST time after registration still goes to the application
    //          provider when both providers can answer.
    //    (b) is the one that actually pins the ordering down. (a) alone would pass even if side
    //    providers ran first, because the positive cache would hide the ordering.
    void MetadataUnitTests::SideProvider_DoesNotDisturbTypesTheAppProviderOwns()
    {
        wrl_wrappers::HStringReference appOwnedRef(L"HotReload.AppOwnedType");
        auto appOwnedType = MockXamlType::CreateMetadata(appOwnedRef.Get())
            ->WithBaseType(KnownTypeIndex::Object);

        wrl_wrappers::HStringReference contestedRef(L"HotReload.ContestedType");
        auto appContestedType = MockXamlType::CreateMetadata(contestedRef.Get())
            ->WithBaseType(KnownTypeIndex::Object);

        MockXamlMetadataProvider appProvider;
        SetUpAppProvider(appProvider, appOwnedType, appContestedType);

        const CClassInfo* before = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::TryGetClassInfoByFullName(c_FullNameAppOwnedType, false, &before));
        VERIFY_IS_FALSE(IsUnresolved(before), L"The app provider resolves its own type");

        // A side provider that also claims to own both app types must not be able to take either
        // over, because side providers are only consulted after the app provider declines.
        // Note it supplies its OWN IXamlType for the contested name, so identity distinguishes them.
        auto sideProvider = MockSideXamlMetadataProvider::Create()
            ->WithType(L"HotReload.AppOwnedType")
            ->WithType(L"HotReload.ContestedType")
            ->WithType(L"HotReload.NewPage");
        VERIFY_IS_TRUE(MetadataAPI::RegisterSideMetadataProvider(sideProvider.Get()).IsRegistered());
        MetadataAPI::InvalidateUnresolvedTypeCache();

        // (a) The already-cached type is untouched.
        const CClassInfo* after = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::TryGetClassInfoByFullName(c_FullNameAppOwnedType, false, &after));
        VERIFY_ARE_EQUAL(before, after, L"The app-owned type keeps the same CClassInfo identity");
        VERIFY_ARE_EQUAL(
            appOwnedType.Get(),
            static_cast<const CCustomClassInfo*>(after)->GetXamlTypeNoRef(),
            L"The app-owned type keeps the same IXamlType identity");

        // (b) c_FullNameContestedType has never been looked up, so this resolution really does run
        //     the provider chain. Both providers can answer; the application provider must win.
        VERIFY_ARE_NOT_EQUAL(
            appContestedType.Get(),
            sideProvider->GetTypeNoRef(L"HotReload.ContestedType").Get(),
            L"The two providers really do offer different IXamlType instances");

        const CClassInfo* contested = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::TryGetClassInfoByFullName(c_FullNameContestedType, false, &contested));
        VERIFY_IS_FALSE(IsUnresolved(contested));
        VERIFY_ARE_EQUAL(
            appContestedType.Get(),
            static_cast<const CCustomClassInfo*>(contested)->GetXamlTypeNoRef(),
            L"A contested name resolves through the application provider, not the side provider");
    }

    // 5. Registering the same provider twice is a no-op that says so.
    void MetadataUnitTests::SideProvider_RegistrationIsIdempotent()
    {
        MockXamlMetadataProvider appProvider;
        SetUpAppProvider(appProvider, nullptr);

        auto sideProvider = MockSideXamlMetadataProvider::Create()->WithType(L"HotReload.NewPage");

        auto first = MetadataAPI::RegisterSideMetadataProvider(sideProvider.Get());
        VERIFY_ARE_EQUAL(XamlMetadataProviderRegistrationStatus::Registered, first.Status);

        auto second = MetadataAPI::RegisterSideMetadataProvider(sideProvider.Get());
        VERIFY_ARE_EQUAL(XamlMetadataProviderRegistrationStatus::AlreadyRegistered, second.Status);
        VERIFY_ARE_EQUAL(first.ProviderId, second.ProviderId, L"The original id is reported back");
        VERIFY_ARE_EQUAL(first.Generation, second.Generation, L"A no-op registration does not bump the generation");
        VERIFY_ARE_EQUAL(static_cast<size_t>(1), MetadataAPI::GetSideMetadataProviderCount(), L"The provider is not registered twice");
    }

    // 6. Two providers answering for the same name is rejected, not resolved by ordering.
    void MetadataUnitTests::SideProvider_ConflictingProviderIsRejected()
    {
        MockXamlMetadataProvider appProvider;
        SetUpAppProvider(appProvider, nullptr);

        auto firstProvider = MockSideXamlMetadataProvider::Create()->WithType(L"HotReload.NewPage");
        auto firstRegistration = MetadataAPI::RegisterSideMetadataProvider(firstProvider.Get());
        VERIFY_ARE_EQUAL(XamlMetadataProviderRegistrationStatus::Registered, firstRegistration.Status);

        // Resolve the name so the registry knows firstProvider owns it.
        const CClassInfo* resolved = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::TryGetClassInfoByFullName(c_FullNameNewPage, false, &resolved));
        VERIFY_IS_FALSE(IsUnresolved(resolved));

        auto generationBeforeConflict = MetadataAPI::GetMetadataProviderGeneration();

        // A second provider that answers for the same name is refused outright.
        auto conflictingProvider = MockSideXamlMetadataProvider::Create()->WithType(L"HotReload.NewPage");
        auto conflict = MetadataAPI::RegisterSideMetadataProvider(conflictingProvider.Get());

        VERIFY_ARE_EQUAL(XamlMetadataProviderRegistrationStatus::Conflict, conflict.Status);
        VERIFY_IS_FALSE(conflict.IsRegistered());
        VERIFY_ARE_EQUAL(c_invalidXamlMetadataProviderId, conflict.ProviderId, L"A rejected provider gets no id");
        VERIFY_ARE_EQUAL(generationBeforeConflict, conflict.Generation, L"A rejected registration does not bump the generation");
        VERIFY_ARE_EQUAL(static_cast<size_t>(1), MetadataAPI::GetSideMetadataProviderCount(), L"The conflicting provider was not added");

        // The winner is deterministic: the name still resolves through the provider that owned it,
        // not through whichever provider registered most recently.
        MetadataAPI::InvalidateUnresolvedTypeCache();
        const CClassInfo* stillResolved = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::TryGetClassInfoByFullName(c_FullNameNewPage, false, &stillResolved));
        VERIFY_ARE_EQUAL(
            firstProvider->GetTypeNoRef(L"HotReload.NewPage").Get(),
            static_cast<const CCustomClassInfo*>(stillResolved)->GetXamlTypeNoRef(),
            L"The original owner still wins");

        // A provider that answers for a *different* name is fine.
        auto disjointProvider = MockSideXamlMetadataProvider::Create()->WithType(L"HotReload.OtherPage");
        VERIFY_ARE_EQUAL(
            XamlMetadataProviderRegistrationStatus::Registered,
            MetadataAPI::RegisterSideMetadataProvider(disjointProvider.Get()).Status);
    }

    // 7. The registry owns the provider's lifetime for as long as it is registered.
    void MetadataUnitTests::SideProvider_RegistryOwnsProviderLifetime()
    {
        MockXamlMetadataProvider appProvider;
        SetUpAppProvider(appProvider, nullptr);

        auto sideProvider = MockSideXamlMetadataProvider::Create()->WithType(L"HotReload.NewPage");

        const ULONG refCountBefore = GetRefCount(sideProvider.Get());

        auto registration = MetadataAPI::RegisterSideMetadataProvider(sideProvider.Get());
        VERIFY_ARE_EQUAL(XamlMetadataProviderRegistrationStatus::Registered, registration.Status);

        const ULONG refCountAfterRegister = GetRefCount(sideProvider.Get());
        VERIFY_ARE_EQUAL(refCountBefore + 1, refCountAfterRegister, L"The registry took a strong reference");

        auto unregistration = MetadataAPI::UnregisterSideMetadataProvider(registration.ProviderId);
        VERIFY_ARE_EQUAL(XamlMetadataProviderRegistrationStatus::Registered, unregistration.Status);
        VERIFY_IS_TRUE(unregistration.Generation > registration.Generation, L"Unregistration is observable");

        const ULONG refCountAfterUnregister = GetRefCount(sideProvider.Get());
        VERIFY_ARE_EQUAL(refCountBefore, refCountAfterUnregister, L"The registry released its reference");
        VERIFY_ARE_EQUAL(static_cast<size_t>(0), MetadataAPI::GetSideMetadataProviderCount());
    }

    // 8. The generation is monotonic and observable, so a stale cache cannot hide a registration.
    void MetadataUnitTests::SideProvider_GenerationIsObservable()
    {
        MockXamlMetadataProvider appProvider;
        SetUpAppProvider(appProvider, nullptr);

        const auto initialGeneration = MetadataAPI::GetMetadataProviderGeneration();

        auto sideProvider = MockSideXamlMetadataProvider::Create()->WithType(L"HotReload.NewPage");
        auto registration = MetadataAPI::RegisterSideMetadataProvider(sideProvider.Get());
        VERIFY_IS_TRUE(registration.Generation > initialGeneration, L"Registration bumps the generation");
        VERIFY_ARE_EQUAL(registration.Generation, MetadataAPI::GetMetadataProviderGeneration());

        auto invalidation = MetadataAPI::InvalidateUnresolvedTypeCache();
        VERIFY_IS_TRUE(invalidation.Generation > registration.Generation, L"Invalidation bumps the generation");

        auto secondInvalidation = MetadataAPI::InvalidateUnresolvedTypeCache(c_FullNameNewPage);
        VERIFY_IS_TRUE(secondInvalidation.Generation > invalidation.Generation, L"The generation is strictly monotonic");

        auto unregistration = MetadataAPI::UnregisterSideMetadataProvider(registration.ProviderId);
        VERIFY_IS_TRUE(unregistration.Generation > secondInvalidation.Generation, L"Unregistration bumps the generation");
    }

    // 9. Invalidation is surgical: only cached misses go.
    void MetadataUnitTests::SideProvider_InvalidationKeepsResolvedTypes()
    {
        wrl_wrappers::HStringReference appOwnedRef(L"HotReload.AppOwnedType");
        auto appOwnedType = MockXamlType::CreateMetadata(appOwnedRef.Get())
            ->WithBaseType(KnownTypeIndex::Object);

        MockXamlMetadataProvider appProvider;
        SetUpAppProvider(appProvider, appOwnedType);

        // One name resolves, two do not.
        const CClassInfo* resolvedType = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::TryGetClassInfoByFullName(c_FullNameAppOwnedType, false, &resolvedType));
        VERIFY_IS_FALSE(IsUnresolved(resolvedType));

        const CClassInfo* missOne = nullptr;
        const CClassInfo* missTwo = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::TryGetClassInfoByFullName(c_FullNameNewPage, false, &missOne));
        VERIFY_SUCCEEDED(MetadataAPI::TryGetClassInfoByFullName(c_FullNameOtherPage, false, &missTwo));
        VERIFY_IS_TRUE(IsUnresolved(missOne));
        VERIFY_IS_TRUE(IsUnresolved(missTwo));

        // Targeted invalidation touches exactly one entry.
        auto targeted = MetadataAPI::InvalidateUnresolvedTypeCache(c_FullNameNewPage);
        VERIFY_ARE_EQUAL(static_cast<size_t>(1), targeted.UnresolvedEntriesEvicted);

        // Invalidating the resolved name is a no-op: resolved types are never evicted.
        auto onResolved = MetadataAPI::InvalidateUnresolvedTypeCache(c_FullNameAppOwnedType);
        VERIFY_ARE_EQUAL(static_cast<size_t>(0), onResolved.UnresolvedEntriesEvicted, L"A resolved name is never evicted");

        const CClassInfo* resolvedTypeAgain = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::TryGetClassInfoByFullName(c_FullNameAppOwnedType, false, &resolvedTypeAgain));
        VERIFY_ARE_EQUAL(resolvedType, resolvedTypeAgain, L"The resolved type kept its identity");

        // The bulk overload takes the remaining miss and nothing else.
        auto bulk = MetadataAPI::InvalidateUnresolvedTypeCache();
        VERIFY_ARE_EQUAL(static_cast<size_t>(1), bulk.UnresolvedEntriesEvicted, L"Only the remaining miss was evicted");

        const CClassInfo* resolvedTypeFinal = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::TryGetClassInfoByFullName(c_FullNameAppOwnedType, false, &resolvedTypeFinal));
        VERIFY_ARE_EQUAL(resolvedType, resolvedTypeFinal, L"Bulk invalidation still leaves resolved types alone");
    }

    // 10. The threading contract, stated as it actually is rather than as we would like it to be.
    //
    //     CStaticLock is a CRITICAL_SECTION, so it is recursive for the owning thread. The registry
    //     mutates its own state under that lock and probes a candidate provider *without* it. The
    //     lookup path is a different story: MetadataAPI::TryGetClassInfoByFullName already holds the
    //     lock across the call into the application's own provider, and side providers are consulted
    //     from that same place, so they inherit that pre-existing behaviour. This test pins both
    //     halves down so the contract is explicit instead of assumed.
    void MetadataUnitTests::SideProvider_LockContractIsExplicit()
    {
        auto storageMock = TlsProvider<DynamicMetadataStorageMock>::CreateWrappedObject();
        MockXamlMetadataProvider appProvider(storageMock);
        SetUpAppProvider(appProvider, nullptr);

        VERIFY_IS_FALSE(CStaticLock::IsLocked(), L"The test starts with no lock held");

        auto sideProvider = MockSideXamlMetadataProvider::Create()->WithType(L"HotReload.NewPage");

        // (a) Registry state is mutated under CStaticLock, and the lock is not leaked.
        storageMock->LastAccessWasLocked = false;
        auto registration = MetadataAPI::RegisterSideMetadataProvider(sideProvider.Get());
        VERIFY_ARE_EQUAL(XamlMetadataProviderRegistrationStatus::Registered, registration.Status);
        VERIFY_IS_TRUE(storageMock->LastAccessWasLocked, L"Registry state is mutated under CStaticLock");
        VERIFY_IS_FALSE(CStaticLock::IsLocked(), L"Registration does not leak the lock");

        // (b) Drive a lookup so the registry records that this provider owns the name.
        const CClassInfo* resolved = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::TryGetClassInfoByFullName(c_FullNameNewPage, false, &resolved));
        VERIFY_IS_FALSE(IsUnresolved(resolved));
        VERIFY_IS_TRUE(sideProvider->GetXamlTypeByFullNameCallCount > 0, L"The side provider really was called");
        VERIFY_IS_FALSE(CStaticLock::IsLocked(), L"Lookup does not leak the lock");

        // (c) The guarantee the registry itself makes: the conflict probe runs with no lock held, so
        //     a provider is free to call back into the metadata API while being validated.
        auto conflictingProvider = MockSideXamlMetadataProvider::Create()->WithType(L"HotReload.NewPage");
        VERIFY_ARE_EQUAL(
            XamlMetadataProviderRegistrationStatus::Conflict,
            MetadataAPI::RegisterSideMetadataProvider(conflictingProvider.Get()).Status);
        VERIFY_IS_TRUE(conflictingProvider->GetXamlTypeByFullNameCallCount > 0, L"The candidate really was probed");
        VERIFY_IS_FALSE(conflictingProvider->WasCalledUnderStaticLock, L"The conflict probe runs without CStaticLock held");

        // (d) The pre-existing behaviour the lookup path inherits, recorded so it is not a surprise.
        //     TryGetClassInfoByFullName takes DynamicMetadataStorageInstanceWithLock and keeps it
        //     across ImportClassInfoFromMetadataProvider, which is how the application's own provider
        //     has always been invoked. A side provider is called from the same place, so a side
        //     provider must be as re-entrancy tolerant as an application provider already has to be.
        //     If the lookup path is ever changed to drop the lock first, flip this assertion.
        VERIFY_IS_TRUE(
            sideProvider->WasCalledUnderStaticLock,
            L"Documented today's behaviour: the lookup path invokes providers with CStaticLock held");
    }

    // 11. The result model never lies about a failure.
    void MetadataUnitTests::SideProvider_RefusesNullAndUnknownId()
    {
        MockXamlMetadataProvider appProvider;
        SetUpAppProvider(appProvider, nullptr);

        auto nullResult = MetadataAPI::RegisterSideMetadataProvider(nullptr);
        VERIFY_ARE_EQUAL(XamlMetadataProviderRegistrationStatus::Refused, nullResult.Status);
        VERIFY_ARE_EQUAL(XamlMetadataProviderRefusalReason::NullProvider, nullResult.Reason);
        VERIFY_IS_FALSE(nullResult.IsRegistered());
        VERIFY_ARE_EQUAL(c_invalidXamlMetadataProviderId, nullResult.ProviderId);

        auto unknownId = MetadataAPI::UnregisterSideMetadataProvider(12345);
        VERIFY_ARE_EQUAL(XamlMetadataProviderRegistrationStatus::Refused, unknownId.Status);
        VERIFY_ARE_EQUAL(XamlMetadataProviderRefusalReason::UnknownProviderId, unknownId.Reason);

        // Neither failure disturbed the registry.
        VERIFY_ARE_EQUAL(static_cast<size_t>(0), MetadataAPI::GetSideMetadataProviderCount());
    }

    #pragma endregion
} } } } }