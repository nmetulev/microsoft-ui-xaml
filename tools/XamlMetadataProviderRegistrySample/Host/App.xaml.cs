// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PluginHost
{
    public partial class App : Application
    {
        private Window? _window;

        public App()
        {
            InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            var host = new StackPanel { Padding = new Thickness(12) };
            _window = new Window { Title = "XamlMetadataProviderRegistry sample", Content = host };
            _window.Activate();

            // Self-driving: runs once the window's tree is live, then exits. Never waits for input.
            host.Loaded += async (s, e) =>
            {
                int exitCode = 1;
                try
                {
                    exitCode = await PluginScenario.RunAsync(host);
                }
                catch (Exception ex)
                {
                    PluginScenario.Log("FATAL " + ex);
                }
                finally
                {
                    PluginScenario.Flush();
                    Environment.ExitCode = exitCode;
                    _window.Close();
                }
            };
        }

        [STAThread]
        public static void Main(string[] args)
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(p =>
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                _ = new App();
            });
            Environment.Exit(Environment.ExitCode);
        }
    }
}
