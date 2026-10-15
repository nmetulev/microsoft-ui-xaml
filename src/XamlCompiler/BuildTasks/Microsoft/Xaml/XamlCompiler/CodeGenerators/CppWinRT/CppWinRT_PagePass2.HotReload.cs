// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Microsoft.UI.Xaml.Markup.Compiler.CodeGen
{
    // C++/WinRT hot reload builds only (EnableXamlHotReload). Nothing is emitted for other builds, so their output is unchanged.
    internal partial class CppWinRT_PagePass2
    {
        // The root case of GetBindingConnector. A hot reload host loads new markup into a live instance with
        // Application::LoadComponent, which asks for the root's binding connector again:
        // - the previous Bindings stop tracking and stop listening for Loading (or Activated): that subscription holds a raw
        //   pointer to them, and they are released when replaced;
        // - a live element doesn't raise Loading again, so when the instance is already loaded the new Bindings start once the
        //   markup has loaded (the elements they bind are connected after this call).
        public string HotReloadRootBindingsStart(ConnectionIdElement element)
        {
            string xamlBindings = Projection(ProjectInfo.RootNamespace) + "::implementation::XamlBindings";
            string frameworkElement = Projection(KnownNamespaces.Xaml) + "::FrameworkElement";
            string name = element.ElementCodeName;
            bool isWindow = element.Type.IsDerivedFromWindow();
            string startEvent = isWindow ? "Activated" : "Loading";
            string isLiveCondition = isWindow
                ? "auto hotReloadContent = " + name + ".Content().try_as<" + frameworkElement + ">(); hotReloadContent && hotReloadContent.IsLoaded()"
                : name + ".IsLoaded()";
            return
                "                    // Hot reload: this markup can load again into a live instance. The previous bindings stop first.\r\n" +
                "                    if (Bindings)\r\n" +
                "                    {\r\n" +
                "                        Bindings->StopTracking();\r\n" +
                "                        " + name + "." + startEvent + "(_hotReloadBindingsStartToken);\r\n" +
                "                    }\r\n" +
                "                    Bindings = bindings;\r\n" +
                "                    _hotReloadBindingsStartToken = " + name + "." + startEvent + "({&*bindings, &" + xamlBindings + "::" + startEvent + "});\r\n" +
                "                    // A live element doesn't raise " + startEvent + " again: the new bindings start once the markup has loaded.\r\n" +
                "                    if (" + isLiveCondition + ")\r\n" +
                "                    {\r\n" +
                "                        " + name + ".DispatcherQueue().TryEnqueue([hotReloadBindings = bindings->get_weak()]()\r\n" +
                "                        {\r\n" +
                "                            if (auto liveBindings = hotReloadBindings.get())\r\n" +
                "                            {\r\n" +
                "                                liveBindings->Initialize();\r\n" +
                "                            }\r\n" +
                "                        });\r\n" +
                "                    }";
        }
    }
}
