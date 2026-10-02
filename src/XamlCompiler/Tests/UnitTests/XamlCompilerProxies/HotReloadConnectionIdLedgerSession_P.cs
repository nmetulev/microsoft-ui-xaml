// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Reflection;

namespace Win8Xaml.CompilerProxies
{
    public sealed class HotReloadConnectionIdLedgerSession : IDisposable
    {
        private static readonly ProxyHelper SessionType;
        private static readonly MethodInfo OpenMethod;
        private static readonly MethodInfo CleanMethod;
        private static readonly MethodInfo BeginPublicationMethod;
        private static readonly MethodInfo CommitMethod;
        private static readonly MethodInfo DisposeMethod;

        private readonly object instance;

        static HotReloadConnectionIdLedgerSession()
        {
            SessionType = new ProxyHelper(
                "Microsoft.UI.Xaml.Markup.Compiler.HotReloadConnectionIdLedgerSession");
            OpenMethod = SessionType.GetStaticMethod("Open", 2);
            CleanMethod = SessionType.GetStaticMethod("Clean");
            BeginPublicationMethod = SessionType.GetMethod("BeginPublication");
            CommitMethod = SessionType.GetMethod("Commit");
            DisposeMethod = SessionType.GetMethod("Dispose");
        }

        public HotReloadConnectionIdLedgerSession(string outputFolder, int lockTimeoutMilliseconds)
        {
            this.instance = OpenMethod.Invoke(
                null,
                new object[] { outputFolder, lockTimeoutMilliseconds });
        }

        public object Instance
        {
            get { return this.instance; }
        }

        public static void Clean(string outputFolder)
        {
            CleanMethod.Invoke(null, new object[] { outputFolder });
        }

        public void Commit()
        {
            CommitMethod.Invoke(this.instance, null);
        }

        public void BeginPublication()
        {
            BeginPublicationMethod.Invoke(this.instance, null);
        }

        public void Dispose()
        {
            DisposeMethod.Invoke(this.instance, null);
        }
    }
}
