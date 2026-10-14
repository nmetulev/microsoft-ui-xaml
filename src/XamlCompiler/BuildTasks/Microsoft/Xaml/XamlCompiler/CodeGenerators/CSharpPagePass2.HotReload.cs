// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Microsoft.UI.Xaml.Markup.Compiler.CodeGen
{
    // Hot reload builds only (EnableXamlHotReload). Nothing is emitted for other builds, so their output is unchanged.
    internal partial class CSharpPagePass2
    {
        // A root Bindings object stays subscribed to its root's Loading (or its Window's Activated) after hot reload re-ran
        // InitializeComponent and replaced it. StopTracking resets 'initialized', so the next event would start it again.
        // The guard ignores the event unless this object is still the owner's current Bindings.
        public string HotReloadRetiredBindingsGuard(string senderExpression)
        {
            if (!IsHotReloadCodeGen)
            {
                return string.Empty;
            }
            return
                "                if ((object)" + senderExpression + " is " + Model.CodeInfo.ClassName.ShortName + " hotReloadOwner && !object.ReferenceEquals(hotReloadOwner.Bindings, this))\r\n" +
                "                {\r\n" +
                "                    return;\r\n" +
                "                }\r\n";
        }
    }
}
