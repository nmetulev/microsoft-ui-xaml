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

        // A Bindings object created before an update that gave its scope tracking (its first OneWay or TwoWay binding) has
        // no tracking object: the field arrives with the update and the constructor doesn't run again. Paths that need
        // tracking create it on first use; paths that release it skip a missing one.
        public string BindingsTrackingAccess
        {
            get { return IsHotReloadCodeGen ? "this.EnsureBindingsTracking()" : "this.bindingsTracking"; }
        }

        public string HotReloadEnsureBindingsTracking(BindUniverse bindUniverse)
        {
            if (!IsHotReloadCodeGen)
            {
                return string.Empty;
            }
            string tracking = bindUniverse.BindingsClassName + "Tracking";
            return
                "\r\n" +
                "            private " + tracking + " EnsureBindingsTracking()\r\n" +
                "            {\r\n" +
                "                if (this.bindingsTracking == null)\r\n" +
                "                {\r\n" +
                "                    this.bindingsTracking = new " + tracking + "(this);\r\n" +
                "                }\r\n" +
                "                return this.bindingsTracking;\r\n" +
                "            }\r\n";
        }    }
}
