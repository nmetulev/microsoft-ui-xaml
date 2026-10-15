// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Microsoft.UI.Xaml.Markup.Compiler.CodeGen
{
    // Hot reload builds only (EnableXamlHotReload). Nothing is emitted for other builds, so their output is unchanged.
    //
    // The provider builds its type tables once and caches every IXamlType and IXamlMember it hands out. After a hot reload
    // update adds a type or a member, those caches are stale. The runtime calls MetadataUpdateHandler types after each
    // update; the provider's handler bumps a generation counter, and each provider rebuilds its tables on its next lookup.
    internal partial class CSharpTypeInfoPass2
    {
        private bool EmitsHotReloadReset
        {
            get { return IsHotReloadCodeGen && !Model.IsPass1 && Model.GenerateTypeInfo && !ProjectInfo.EnableTypeInfoReflection; }
        }

        public string HotReloadMetadataUpdateHandler()
        {
            if (!EmitsHotReloadReset)
            {
                return string.Empty;
            }
            return "[assembly: global::System.Reflection.Metadata.MetadataUpdateHandler(typeof(" +
                Globalize(ProjectInfo.XamlTypeInfoNamespace) + ".XamlTypeInfoProvider))]\r\n\r\n";
        }

        public string HotReloadResetCall
        {
            get { return EmitsHotReloadReset ? "            HotReloadResetIfUpdated();\r\n" : string.Empty; }
        }

        // The library providers (OtherProviders) are created on first use. An update that adds a package or project
        // reference adds one to the generated list, so the live provider has to rebuild it. Not when the list is filled by
        // AddOtherProvider (DoNotGenerateOtherProviders): those providers come from outside and would be lost.
        private string ResetOtherProviders
        {
            get
            {
                return SchemaInfo.OtherMetadataProviders.Count > 0 && ProjectInfo.GenerateProviderCode
                    ? "                    _otherProviders = null;\r\n"
                    : string.Empty;
            }
        }

        public string HotReloadProviderMembers()
        {
            if (!EmitsHotReloadReset)
            {
                return string.Empty;
            }
            return
                "        // Hot reload: an update can add or change XAML types and members, so the tables and caches are rebuilt after one.\r\n" +
                "        private static int s_hotReloadGeneration;\r\n" +
                "        private int _hotReloadGeneration;\r\n" +
                "\r\n" +
                "        internal static void ClearCache(global::System.Type[] updatedTypes)\r\n" +
                "        {\r\n" +
                "            global::System.Threading.Interlocked.Increment(ref s_hotReloadGeneration);\r\n" +
                "        }\r\n" +
                "\r\n" +
                "        private void HotReloadResetIfUpdated()\r\n" +
                "        {\r\n" +
                "            int generation = global::System.Threading.Volatile.Read(ref s_hotReloadGeneration);\r\n" +
                "            if (_hotReloadGeneration == generation)\r\n" +
                "            {\r\n" +
                "                return;\r\n" +
                "            }\r\n" +
                "            lock (_xamlTypeCacheByType)\r\n" +
                "            {\r\n" +
                "                lock (_xamlMembers)\r\n" +
                "                {\r\n" +
                "                    _xamlTypeCacheByName.Clear();\r\n" +
                "                    _xamlTypeCacheByType.Clear();\r\n" +
                "                    _xamlMembers.Clear();\r\n" +
                "                    _typeNameTable = null;\r\n" +
                "                    _typeTable = null;\r\n" +
                ResetOtherProviders +
                "                    _hotReloadGeneration = generation;\r\n" +
                "                }\r\n" +
                "            }\r\n" +
                "        }\r\n" +
                "\r\n";
        }
    }
}
