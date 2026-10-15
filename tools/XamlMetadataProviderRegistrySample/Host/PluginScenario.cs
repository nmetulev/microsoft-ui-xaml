// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace PluginHost
{
    // The plugin walkthrough from docs\api-specs\XamlMetadataProviderRegistry\XamlMetadataProviderRegistry.md:
    // load a XAML control library while the app runs, register its provider, create its controls from markup,
    // unregister it, and check that markup naming its controls then fails like any unknown type.
    internal static class PluginScenario
    {
        private const string BadgeMarkup =
            "<p:Badge xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
            "xmlns:p='using:PluginControls' Label='hello' Count='3'/>";

        private const string RatingMarkup =
            "<StackPanel xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
            "xmlns:p='using:PluginControls'><p:StarRating Stars='4'/></StackPanel>";

        private static readonly List<string> s_lines = new();
        private static int s_failures;
        private static int s_checks;

        public static void Log(string line)
        {
            s_lines.Add(line);
            Console.WriteLine(line);
        }

        public static void Flush()
        {
            File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "plugin-host-results.txt"), s_lines);
        }

        private static void Check(bool condition, string what)
        {
            s_checks++;
            if (!condition) s_failures++;
            Log($"{(condition ? "PASS" : "FAIL")}  {what}");
        }

        private static (object? result, Exception? error) TryLoad(string markup)
        {
            try
            {
                return (XamlReader.Load(markup), null);
            }
            catch (Exception ex)
            {
                return (null, ex);
            }
        }

        private static string Describe(Exception ex) =>
            $"{ex.GetType().FullName} 0x{ex.HResult:X8}: {ex.Message.Replace("\r", " ").Replace("\n", " ").Trim()}";

        private static object? Get(object target, string property) =>
            target.GetType().GetProperty(property)!.GetValue(target);

        private static async Task<bool> AddAndWaitForLoadedAsync(Panel host, FrameworkElement element)
        {
            var loaded = new TaskCompletionSource<bool>();
            element.Loaded += (s, e) => loaded.TrySetResult(true);
            host.Children.Add(element);
            var winner = await Task.WhenAny(loaded.Task, Task.Delay(5000));
            await Task.Delay(100);   // one more layout pass
            return winner == loaded.Task;
        }

        public static async Task<int> RunAsync(StackPanel host)
        {
            Log($"=== XamlMetadataProviderRegistry plugin sample, pid {Environment.ProcessId} ===");
            Log($"WinUI: {typeof(XamlReader).Assembly.Location}");

            // 0. Baseline: the host knows nothing about the plugin's types.
            var (before, beforeError) = TryLoad(BadgeMarkup);
            Check(before == null && beforeError != null, "before loading the plugin, <p:Badge> does not parse");
            if (beforeError != null) Log("      " + Describe(beforeError));

            // 1. Load the library while the app runs. The host has no compile-time reference to it.
            string pluginPath = Path.Combine(AppContext.BaseDirectory, "plugins", "PluginControls.dll");
            Assembly plugin = AssemblyLoadContext.Default.LoadFromAssemblyPath(pluginPath);
            Type providerType = plugin.GetExportedTypes()
                .Single(t => typeof(IXamlMetadataProvider).IsAssignableFrom(t) && !t.IsAbstract);
            Log($"loaded {plugin.GetName().Name}; provider {providerType.FullName}");
            var provider = (IXamlMetadataProvider)Activator.CreateInstance(providerType)!;

            // 2. Register its provider.
            XamlMetadataProviderRegistry.Register(provider);
            Check(XamlMetadataProviderRegistry.IsRegistered(provider), "Register: the provider is registered");

            // 3. Markup naming the plugin's controls now parses, and the controls work.
            var (badgeObject, badgeError) = TryLoad(BadgeMarkup);
            if (badgeError != null) Log("      " + Describe(badgeError));
            Check(badgeObject?.GetType().FullName == "PluginControls.Badge", "after Register, <p:Badge> parses into a PluginControls.Badge");
            var badge = badgeObject as FrameworkElement;
            if (badge != null)
            {
                Check((string?)Get(badge, "Label") == "hello" && (int?)Get(badge, "Count") == 3, "markup set Label='hello' and Count='3'");
                Check(await AddAndWaitForLoadedAsync(host, badge), "the badge loads in the live tree");
                Check(badge.ActualWidth > 0 && (string?)Get(badge, "RenderedText") == "hello (3)",
                    $"the badge renders 'hello (3)' (ActualWidth={badge.ActualWidth:0.#})");
            }

            var (ratingObject, ratingError) = TryLoad(RatingMarkup);
            if (ratingError != null) Log("      " + Describe(ratingError));
            var rating = (ratingObject as StackPanel)?.Children.FirstOrDefault();
            Check(rating?.GetType().FullName == "PluginControls.StarRating" && (rating as Panel)?.Children.Count == 4,
                "a plugin panel nested in WinUI markup parses with Stars='4' (4 children)");

            // 4. Registering again is a no-op.
            XamlMetadataProviderRegistry.Register(provider);
            Check(XamlMetadataProviderRegistry.IsRegistered(provider), "Register again: still registered, no error");

            // 5. Unregister.
            Check(XamlMetadataProviderRegistry.Unregister(provider), "Unregister returns true");
            Check(!XamlMetadataProviderRegistry.IsRegistered(provider), "the provider is no longer registered");

            // 6. Markup naming the plugin's controls fails the way any unknown type does.
            var (afterBadge, afterBadgeError) = TryLoad(BadgeMarkup);
            Check(afterBadge == null && afterBadgeError != null, "after Unregister, <p:Badge> fails to parse");
            if (afterBadgeError != null) Log("      " + Describe(afterBadgeError));
            var (afterRating, afterRatingError) = TryLoad(RatingMarkup);
            Check(afterRating == null && afterRatingError != null, "after Unregister, nested <p:StarRating> fails to parse");
            if (afterRatingError != null) Log("      " + Describe(afterRatingError));

            // 7. Objects created earlier keep working.
            if (badge != null)
            {
                badge.GetType().GetProperty("Label")!.SetValue(badge, "still alive");
                await Task.Delay(100);
                Check((string?)Get(badge, "RenderedText") == "still alive (3)" && host.Children.Contains(badge),
                    "the badge created before Unregister is still in the tree and still updates");
            }

            // 8. Unregistering twice, and null.
            Check(!XamlMetadataProviderRegistry.Unregister(provider), "Unregister again returns false");
            try
            {
                XamlMetadataProviderRegistry.Register(null!);
                Check(false, "Register(null) throws");
            }
            catch (Exception ex)
            {
                Check(ex is ArgumentException || ex.HResult == unchecked((int)0x80070057), $"Register(null) throws ({ex.GetType().Name})");
            }

            // 9. Registering it again brings the controls back.
            XamlMetadataProviderRegistry.Register(provider);
            var (again, againError) = TryLoad(BadgeMarkup);
            if (againError != null) Log("      " + Describe(againError));
            Check(again?.GetType().FullName == "PluginControls.Badge" && (string?)Get(again, "Label") == "hello",
                "after registering again, <p:Badge Label='hello'> parses again");
            XamlMetadataProviderRegistry.Unregister(provider);

            Log($"{s_checks} check(s), {s_failures} failed");
            return s_failures == 0 ? 0 : 1;
        }
    }
}
