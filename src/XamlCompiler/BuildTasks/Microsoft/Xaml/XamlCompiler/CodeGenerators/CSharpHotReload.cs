// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Microsoft.UI.Xaml.Markup.Compiler.CodeGen
{
    // EnableXamlHotReloadCodegen: the code the C# templates add so that .NET hot reload can update the app after a page is
    // compiled again. Each piece is empty without it, so other builds generate the same code as before.

    internal partial class CSharpPagePass1
    {
        private bool HasHotReloadBindings => IsHotReloadCodegen && Model.CodeInfo.BindStatus != BindStatus.None;

        // A hot reload tool can run InitializeComponent again on a live object (it resets _contentLoaded first). The compiled
        // bindings of the previous run stop, and the new ones start: a live element doesn't raise Loading (a live window,
        // Activated) again.
        public string HotReloadBeforeLoadComponent => !HasHotReloadBindings ? string.Empty :
            "            this.Bindings?.StopTracking();\r\n";

        public string HotReloadAfterLoadComponent => !HasHotReloadBindings ? string.Empty :
            "            if (((object)this as " + Globalize(KnownTypes.FrameworkElement) + ")?.IsLoaded == true ||\r\n" +
            "                (((object)this as " + Globalize(KnownTypes.Window) + ")?.Content as " + Globalize(KnownTypes.FrameworkElement) + ")?.IsLoaded == true)\r\n" +
            "            {\r\n" +
            "                this.Bindings?.Update();\r\n" +
            "            }\r\n";
    }

    internal partial class CSharpPagePass2
    {
        // A static field's " = false" gives the class a static constructor, and Roslyn fails to update the static
        // constructor of a class that an earlier update added (an x:Bind's Bindings class).
        public string StaticFalseInitializer => IsHotReloadCodegen ? string.Empty : " = false";

        // Bindings created before an update that added their tracking have none.
        public string HotReloadNullConditional => IsHotReloadCodegen ? "?" : string.Empty;

        // Bindings that InitializeComponent replaced stay subscribed to Loading (Activated); they don't start again.
        public string HotReloadReplacedBindingsGuard => !IsHotReloadCodegen ? string.Empty :
            "                if (!object.ReferenceEquals(this.dataRoot?.Bindings, this))\r\n" +
            "                {\r\n" +
            "                    return;\r\n" +
            "                }\r\n";
    }

    internal partial class CSharpTypeInfoPass2
    {
        private bool HasHotReloadProvider =>
            IsHotReloadCodegen && !Model.IsPass1 && Model.GenerateTypeInfo && !ProjectInfo.EnableTypeInfoReflection;

        // After an update, .NET hot reload calls the handlers' ClearCache; the provider then forgets what it looked up, so a
        // type or member the update added is found.
        public string HotReloadMetadataUpdateHandler => !HasHotReloadProvider ? string.Empty :
            "[assembly: global::System.Reflection.Metadata.MetadataUpdateHandler(typeof(" +
            Globalize(ProjectInfo.XamlTypeInfoNamespace) + ".XamlTypeInfoProvider))]\r\n";

        public string HotReloadResetCall => !HasHotReloadProvider ? string.Empty :
            "            ResetIfUpdated();\r\n";

        public string HotReloadProviderMembers => !HasHotReloadProvider ? string.Empty :
            "        private static int s_updateCount;\r\n" +
            "        private int _updateCountSeen;\r\n" +
            "\r\n" +
            "        public static void ClearCache(global::System.Type[] updatedTypes)\r\n" +
            "        {\r\n" +
            "            global::System.Threading.Interlocked.Increment(ref s_updateCount);\r\n" +
            "        }\r\n" +
            "\r\n" +
            "        private void ResetIfUpdated()\r\n" +
            "        {\r\n" +
            "            if (_updateCountSeen != s_updateCount)\r\n" +
            "            {\r\n" +
            "                lock (_xamlTypeCacheByType)\r\n" +
            "                {\r\n" +
            "                    lock (_xamlMembers)\r\n" +
            "                    {\r\n" +
            "                        _updateCountSeen = s_updateCount;\r\n" +
            "                        _xamlTypeCacheByName.Clear();\r\n" +
            "                        _xamlTypeCacheByType.Clear();\r\n" +
            "                        _xamlMembers.Clear();\r\n" +
            "                        _typeNameTable = null;\r\n" +
            "                        _typeTable = null;\r\n" +
            "                    }\r\n" +
            "                }\r\n" +
            "            }\r\n" +
            "        }\r\n" +
            "\r\n";
    }
}
