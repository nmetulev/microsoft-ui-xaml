// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Xaml;

namespace Microsoft.UI.Xaml.Markup.Compiler.CodeGen
{
    internal class CppWinRT_CodeGenerator<T> : NativeCodeGenerator<T>
    {
        // The name to look up in the projected primitive table. XamlType.ToString() includes the XAML namespace once something
        // has initialized it, which depends on what else the same compile looked at; hot reload builds use the CLR name so a
        // one-page compile and a full build agree.
        protected string PrimitiveLookupName(global::System.Xaml.XamlType type)
        {
            return ProjectInfo.IsHotReloadBuild && type.UnderlyingType != null ? type.UnderlyingType.FullName : type.ToString();
        }

        // C++/WinRT hot reload builds (EnableXamlHotReload): a host applies a page's new markup live only when the generated C++
        // is byte-identical to the running build's. Connection IDs are stable there, so declarations whose order means nothing
        // (switch cases, members, method definitions) follow connection ID or step name instead of document order, and comments
        // don't carry line numbers: moving, wrapping or reordering elements then leaves the C++ unchanged. Statement order
        // inside a method (which binding updates first) stays in document order, as in every other build.
        protected bool IsCppHotReloadCodeGen
        {
            get { return ProjectInfo != null && ProjectInfo.IsCppHotReloadCodeGen; }
        }

        protected IEnumerable<ConnectionIdElement> InIdOrder(IEnumerable<ConnectionIdElement> elements)
        {
            return IsCppHotReloadCodeGen ? elements.OrderBy(e => e.ConnectionId).ToList() : elements;
        }

        protected IEnumerable<BindUniverse> InIdOrder(IEnumerable<BindUniverse> bindUniverses)
        {
            return IsCppHotReloadCodeGen ? bindUniverses.OrderBy(bu => bu.RootElement.ConnectionId).ToList() : bindUniverses;
        }

        protected IEnumerable<BindPathStep> InDeclarationOrder(IEnumerable<BindPathStep> steps)
        {
            return IsCppHotReloadCodeGen ? steps.OrderBy(s => s.CodeName, StringComparer.Ordinal).ToList() : steps;
        }

        protected IEnumerable<IGrouping<string, TItem>> InKeyOrder<TItem>(IEnumerable<IGrouping<string, TItem>> groups)
        {
            return IsCppHotReloadCodeGen ? groups.OrderBy(g => g.Key, StringComparer.Ordinal).ToList() : groups;
        }

        // Pass 1 (the .g.h) has no stable connection IDs yet: its declarations follow their names.
        protected IEnumerable<FieldDefinition> InNameOrder(IEnumerable<FieldDefinition> fields)
        {
            return IsCppHotReloadCodeGen ? fields.OrderBy(f => f.FieldName, StringComparer.Ordinal).ToList() : fields;
        }

        protected IEnumerable<BindUniverse> InNameOrder(IEnumerable<BindUniverse> bindUniverses)
        {
            return IsCppHotReloadCodeGen ? bindUniverses.OrderBy(bu => bu.BindingsClassName, StringComparer.Ordinal).ToList() : bindUniverses;
        }

        // The comment next to generated code for an element: its file and line, or in C++ hot reload builds its file and
        // name (or type), which don't change when lines are inserted above it.
        protected string SourceComment(ConnectionIdElement element)
        {
            if (!IsCppHotReloadCodeGen)
            {
                return element.LineNumberAndXamlFile;
            }
            return element.ParentFileCodeInfo.ApparentRelativePath + " " +
                (String.IsNullOrEmpty(element.ElementName) ? element.Type.Name : element.ElementName);
        }

        public override string ToStringWithCulture(ICodeGenOutput codegenOutput)
        {
            return codegenOutput.CppWinRTName();
        }

        public override string ToStringWithCulture(XamlType type)
        {
            return type.CppWinRTName();
        }

        public static String Projection(string typeName)
        {
            string newName = Globalize(typeName);

            // The check of "::winrt::" instead of "::winrt" is deliberate here - otherwise user-defined types from a namespace starting
            // with "winrt" could be confused as already having the "::winrt" prefix (e.g. a "winrt_UserNamespace::UserType" that we want to convert to "::winrt::winrt_UserNamespace::UserType").
            if (!newName.StartsWith("::winrt::"))
            {
                newName = "::winrt" + newName;
            }
            else
            {
                throw new ArgumentException("Name should not already contain ::winrt prefix");
            }
            newName = newName.Replace("<::", "<::winrt::");
            newName = newName.Replace("::winrt::winrt::", "::winrt::");
            return newName;
        }

        protected string GetBindingTrackingClassName(BindUniverse bindUniverse, XamlClassCodeInfo codeInfo)
        {
            return Colonize(bindUniverse.NeedsCppBindingTrackingClass ?
                bindUniverse.BindingsTrackingClassName :
                Projection($"{ProjectInfo.RootNamespace}::implementation::XamlBindingTrackingBase"));
        }

        public IEnumerable<string> GetCacheDeclarations(BindUniverse bindUniverse)
        {
            foreach (var step in bindUniverse.BindPathSteps.Values.Where(step => step.IsIncludedInUpdate == true && step.NeedsUpdateChildListeners))
            {
                if (step.ImplementsINPC && step.RequiresChildNotification)
                {
                    if (step is RootStep)
                    {
                        yield return $"::winrt::weak_ref<{Projection(KnownNamespaces.XamlData)}::INotifyPropertyChanged> cachePC_{step.CodeName};";
                    }
                    else
                    {
                        yield return $"{Projection(KnownNamespaces.XamlData)}::INotifyPropertyChanged cachePC_{step.CodeName}{{nullptr}};";
                    }
                }
                if (step.ImplementsINDEI)
                {
                    yield return $"{Projection(KnownNamespaces.XamlData)}::INotifyDataErrorInfo cacheEC_{step.CodeName}{{nullptr}};";
                }
                if (step.ImplementsIObservableVector && step.RequiresChildNotification)
                {
                    yield return step.ValueType.CppWinRTName() + " cacheVC_" + step.CodeName + "{nullptr};";
                }
                if (step.ImplementsIObservableMap && step.RequiresChildNotification)
                {
                    yield return step.ValueType.CppWinRTName() + " cacheMC_" + step.CodeName + "{nullptr};";
                }
                else if (step.ImplementsINCC)
                {
                    yield return $"{Projection(KnownNamespaces.XamlInterop)}::INotifyCollectionChanged cacheCC_{step.CodeName}{{nullptr}};";
                }
                foreach (var child in step.TrackingSteps.OfType<DependencyPropertyStep>())
                {
                    if (step is RootStep)
                    {
                        yield return $"::winrt::weak_ref<{Projection(KnownNamespaces.Xaml)}::DependencyObject> cacheDPC_{child.CodeName};";
                    }
                    else
                    {
                        yield return $"{Projection(KnownNamespaces.Xaml)}::DependencyObject cacheDPC_{child.CodeName}{{nullptr}};";
                    }
                }
            }
        }

        public IEnumerable<string> GetTokenDeclarations(BindUniverse bindUniverse)
        {
            foreach (var step in bindUniverse.BindPathSteps.Values.Where(step => step.IsIncludedInUpdate == true && step.NeedsUpdateChildListeners))
            {
                if (step.ImplementsINPC)
                {
                    yield return $"::winrt::event_token tokenPC_{step.CodeName} {{}};";
                }
                if (step.ImplementsINDEI)
                {
                    yield return $"::winrt::event_token tokenEC_{step.CodeName} {{}};";
                }
                if (step.ImplementsIObservableVector)
                {
                    yield return $"::winrt::event_token tokenVC_{step.CodeName} {{}};";
                }
                if (step.ImplementsIObservableMap)
                {
                    yield return $"::winrt::event_token tokenMC_{step.CodeName} {{}};";
                }
                else if (step.ImplementsINCC)
                {
                    yield return $"::winrt::event_token tokenCC_{step.CodeName} {{}};";
                }
            }

            foreach (var step in bindUniverse.BindPathSteps.Values.Where(step => step.IsIncludedInUpdate == true && step.NeedsUpdateChildListeners))
            {
                foreach (var child in step.TrackingSteps.OfType<DependencyPropertyStep>())
                {
                    yield return $"__int64 tokenDPC_{child.CodeName}{{0}};";
                }
            }
        }
    }
}
