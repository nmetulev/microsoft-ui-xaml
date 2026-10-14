// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Microsoft.UI.Xaml.Markup.Compiler.CodeGen
{
    // Hot reload builds only (EnableXamlHotReload): InitializeComponent can run again on a live instance after its
    // markup changed. Nothing is emitted for other builds, so their output is unchanged.
    internal partial class CSharpPagePass1
    {
        private bool HasRootBindings
        {
            get { return Model.CodeInfo.BindStatus != BindStatus.None; }
        }

        // Before LoadComponent: the compiled bindings of the previous run stop listening, so they don't keep running
        // alongside the new ones (and against members they never set).
        public string HotReloadBeforeLoadComponent()
        {
            if (!IsHotReloadCodeGen || !HasRootBindings)
            {
                return string.Empty;
            }
            string bindingsInterface = "I" + Model.CodeInfo.ClassName.ShortName + "_Bindings";
            return
                "            // Hot reload: when this runs again on a live instance, the previous compiled bindings stop first.\r\n" +
                "            " + bindingsInterface + " previousBindings = this.Bindings;\r\n" +
                "            if (previousBindings != null)\r\n" +
                "            {\r\n" +
                "                previousBindings.StopTracking();\r\n" +
                "            }\r\n" +
                "\r\n";
        }

        // After LoadComponent: on a live element the compiled bindings' start event (Loading, or Activated for a Window)
        // has already happened, so they are updated here.
        public string HotReloadAfterLoadComponent()
        {
            if (!IsHotReloadCodeGen || !HasRootBindings)
            {
                return string.Empty;
            }
            string frameworkElement = Globalize(KnownTypes.FrameworkElement);
            return
                "\r\n" +
                "            // Hot reload: a live element doesn't raise Loading (or Activated) again, so the new bindings start here.\r\n" +
                "            if (((object)this is " + frameworkElement + " hotReloadElement && hotReloadElement.IsLoaded) ||\r\n" +
                "                ((object)this is global::Microsoft.UI.Xaml.Window hotReloadWindow && hotReloadWindow.Content is " + frameworkElement + " hotReloadContent && hotReloadContent.IsLoaded))\r\n" +
                "            {\r\n" +
                "                this.Bindings?.Update();\r\n" +
                "            }\r\n";
        }
    }
}
