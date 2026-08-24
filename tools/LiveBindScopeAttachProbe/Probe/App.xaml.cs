// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LiveBindScopeProbe
{
    public partial class App : Application
    {
        private Window _window;

        public App()
        {
            InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            _window = new Window { Title = "LiveBindScopeProbe" };

            var host = new StackPanel();
            _window.Content = host;
            _window.Activate();

            // Self driving: run once the tree is live, then exit. The probe never waits for input.
            host.Loaded += (s, e) =>
            {
                var queue = DispatcherQueue.GetForCurrentThread();
                queue.TryEnqueue(DispatcherQueuePriority.Low, () =>
                {
                    int exitCode = 1;
                    try
                    {
                        exitCode = ProbeRunner.Run(host);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine("PROBE FATAL: " + ex);
                        ProbeRunner.WriteFatal(ex);
                    }
                    finally
                    {
                        Environment.ExitCode = exitCode;
                        _window.Close();
                    }
                });
            };
        }

        [STAThread]
        public static void Main(string[] args)
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(p =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                _ = new App();
            });
            Environment.Exit(Environment.ExitCode);
        }
    }
}
