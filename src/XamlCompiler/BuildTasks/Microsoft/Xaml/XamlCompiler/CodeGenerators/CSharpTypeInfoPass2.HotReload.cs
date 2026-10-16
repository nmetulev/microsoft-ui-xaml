// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Microsoft.UI.Xaml.Markup.Compiler.CodeGen
{
    using System;
    using System.Linq;
    using System.Text;

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

        // Each user type's IXamlType is built in its own method, and CreateXamlType's case only calls it. Adding a type
        // mid-session then changes CreateXamlType by one short case (with the type order kept, see
        // XamlSchemaCodeInfo.SortForHotReload) instead of a method with every type's body, which .NET hot reload has to
        // diff and recompile whole (seconds on a large app). The case bodies are captured from the output as written,
        // so the code is the same as a normal build's, only moved.
        private readonly System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, string>> _hotReloadTypeMethods =
            new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, string>>();

        private int HotReloadBeginTypeBody()
        {
            return EmitsHotReloadReset ? this.GenerationEnvironment.Length : -1;
        }

        private void HotReloadEndTypeBody(InternalXamlUserTypeInfo entry, int start)
        {
            if (start < 0)
            {
                return;
            }
            string body = this.GenerationEnvironment.ToString(start, this.GenerationEnvironment.Length - start);
            this.GenerationEnvironment.Length = start;
            string name = Model.CreateTypeName(entry);
            _hotReloadTypeMethods.Add(new System.Collections.Generic.KeyValuePair<string, string>(name, body));
            this.Write("                userType = " + name + "(typeName, type);\r\n");
        }

        private void HotReloadTypeMethods()
        {
            string userType = Globalize(ProjectInfo.XamlTypeInfoNamespace) + ".XamlUserType";
            foreach (var method in _hotReloadTypeMethods)
            {
                this.Write("\r\n        private " + userType + " " + method.Key + "(string typeName, global::System.Type type)\r\n" +
                    "        {\r\n" +
                    "            " + userType + " userType;\r\n" +
                    method.Value.Replace("\r\n                ", "\r\n            ").Substring(4) +
                    "            return userType;\r\n" +
                    "        }\r\n");
            }
            _hotReloadTypeMethods.Clear();
        }

        // The type tables and CreateXamlType in blocks of TypeBlockSize indices: InitTypeTables and CreateXamlType only
        // dispatch to InitTypeTables_<block> and CreateXamlType_<block>. A type appended to the table (see
        // XamlSchemaCodeInfo.SortForHotReload) changes the last block and the dispatchers, not every type's lines.
        private const int TypeBlockSize = 64;

        private int HotReloadBeginCapture()
        {
            return EmitsHotReloadReset && SchemaInfo.TypeTable.Count > 0 ? this.GenerationEnvironment.Length : -1;
        }

        private string EndCapture(int start)
        {
            string text = this.GenerationEnvironment.ToString(start, this.GenerationEnvironment.Length - start);
            this.GenerationEnvironment.Length = start;
            return text;
        }

        private void HotReloadEndTypeTables(int start)
        {
            if (start < 0)
            {
                return;
            }
            string text = EndCapture(start);
            const string initStart = "        private void InitTypeTables()";
            const string initEnd = "        private int LookupTypeIndexByName(";
            int from = text.IndexOf(initStart, StringComparison.Ordinal);
            int to = text.IndexOf(initEnd, StringComparison.Ordinal);
            if (from < 0 || to < from)
            {
                this.Write(text);
                return;
            }
            int count = SchemaInfo.TypeTableFromAllAssemblies.Count;
            var blocks = SchemaInfo.TypeTable.GroupBy(e => e.TypeIndex / TypeBlockSize).OrderBy(g => g.Key).ToList();
            var init = new StringBuilder();
            init.Append(initStart).Append("\r\n        {\r\n");
            init.Append("            _typeNameTable = new string[").Append(count).Append("];\r\n");
            init.Append("            _typeTable = new global::System.Type[").Append(count).Append("];\r\n");
            foreach (var block in blocks)
            {
                init.Append("            InitTypeTables_").Append(block.Key).Append("();\r\n");
            }
            init.Append("        }\r\n\r\n");
            foreach (var block in blocks)
            {
                init.Append("        private void InitTypeTables_").Append(block.Key).Append("()\r\n        {\r\n");
                foreach (InternalTypeEntry entry in block.OrderBy(e => e.TypeIndex))
                {
                    init.Append("            _typeNameTable[").Append(entry.TypeIndex).Append("] = \"").Append(this.ToStringHelper.ToStringWithCulture(entry.StandardName)).Append("\";\r\n");
                    if (entry.IsDeprecated)
                    {
                        init.Append("#pragma warning disable 0612, 0618  //   Warning on Deprecated usage\r\n");
                    }
                    init.Append("            _typeTable[").Append(entry.TypeIndex).Append("] = typeof(").Append(this.ToStringHelper.ToStringWithCulture(entry.FullName)).Append(");\r\n");
                    if (entry.IsDeprecated)
                    {
                        init.Append("#pragma warning restore 0612, 0618\r\n");
                    }
                }
                init.Append("        }\r\n\r\n");
            }
            this.Write(text.Substring(0, from));
            this.Write(init.ToString());
            this.Write(text.Substring(to));
        }

        private void HotReloadEndCreateXamlType(int start)
        {
            if (start < 0)
            {
                HotReloadTypeMethods();
                return;
            }
            EndCapture(start);
            string ns = Globalize(ProjectInfo.XamlTypeInfoNamespace);
            var blocks = SchemaInfo.TypeTable.GroupBy(e => e.TypeIndex / TypeBlockSize).OrderBy(g => g.Key).ToList();
            var code = new StringBuilder();
            code.Append("        private ").Append(Globalize(KnownTypes.IXamlType)).Append(" CreateXamlType(int typeIndex)\r\n        {\r\n");
            code.Append("            string typeName = _typeNameTable[typeIndex];\r\n");
            code.Append("            global::System.Type type = _typeTable[typeIndex];\r\n");
            code.Append("            switch (typeIndex / ").Append(TypeBlockSize).Append(")\r\n            {\r\n");
            foreach (var block in blocks)
            {
                code.Append("            case ").Append(block.Key).Append(": return CreateXamlType_").Append(block.Key).Append("(typeIndex, typeName, type);\r\n");
            }
            code.Append("            }\r\n            return null;\r\n        }\r\n");
            foreach (var block in blocks)
            {
                code.Append("\r\n        private ").Append(ns).Append(".XamlSystemBaseType CreateXamlType_").Append(block.Key)
                    .Append("(int typeIndex, string typeName, global::System.Type type)\r\n        {\r\n            switch (typeIndex)\r\n            {\r\n");
                foreach (InternalTypeEntry entry in block.OrderBy(e => e.TypeIndex))
                {
                    code.Append("            case ").Append(entry.TypeIndex).Append(":   //  ").Append(this.ToStringHelper.ToStringWithCulture(entry.StandardName)).Append("\r\n");
                    code.Append(entry.IsSystemType
                        ? "                return new " + ns + ".XamlSystemBaseType(typeName, type);\r\n"
                        : "                return " + Model.CreateTypeName(entry.UserTypeInfo) + "(typeName, type);\r\n");
                }
                code.Append("            }\r\n            return null;\r\n        }\r\n");
            }
            this.Write(code.ToString());
            HotReloadTypeMethods();
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
