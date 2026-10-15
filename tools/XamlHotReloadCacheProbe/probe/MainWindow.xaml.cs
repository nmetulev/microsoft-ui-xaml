using System.Reflection;
using System.Runtime.InteropServices;
using CacheProbe.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace CacheProbe;

public sealed partial class MainWindow : Window
{
    private bool _started;

    public MainWindow()
    {
        InitializeComponent();
        Activated += async (_, _) =>
        {
            if (_started)
            {
                return;
            }
            _started = true;
            await new Runner(this).RunAsync();
        };
    }

    public ContentControl OldHost => Old;
    public ContentControl NewHost => New;
    public Frame FrameHost => Host;
    public TextBlock StatusText => Status;
    public TextBlock ResultText => Result;
}

/// <summary>
/// One scenario per process: CacheProbe.exe &lt;scenario&gt; &lt;text|binary&gt; &lt;out-file&gt;. Replaces markup through
/// IXamlHotReloadService, does what a hot reload tool does next, and reads what the live UI shows. A check's
/// expected value is what a clean build of the edited markup shows.
/// </summary>
internal sealed class Runner
{
    private const string MergedUri = "ms-appx:///Dicts/Merged.xaml";
    private const string LooseUri = "ms-appx:///Dicts/Loose.xaml";
    private const string GenericUri = "ms-appx:///Themes/Generic.xaml";
    private const string ResPageUri = "ms-appx:///Pages/ResPage.xaml";
    private const string CachedPageUri = "ms-appx:///Pages/CachedPage.xaml";
    private const string AppUri = "ms-appx:///App.xaml";

    private readonly MainWindow _window;
    private readonly List<string> _lines = [];
    private string _scenario = "";
    private bool _binary;
    private string _mode = "text";
    private IntPtr _service;
    private int _stale;
    private int _errors;

    public Runner(MainWindow window) => _window = window;

    public async Task RunAsync()
    {
        var args = Environment.GetCommandLineArgs();
        _scenario = args.Length > 1 ? args[1] : "merged-new";
        _binary = args.Length > 2 && args[2] == "binary";
        _mode = args.Length > 2 ? args[2] : "text";
        var outFile = args.Length > 3 ? args[3] : Path.Combine(Path.GetTempPath(), "cacheprobe.txt");
        var mode = _mode;
        try
        {
            _service = _mode == "nodiag" ? IntPtr.Zero : await AcquireServiceAsync();
            await RunScenarioAsync(_scenario);
        }
        catch (Exception ex)
        {
            _errors++;
            Log($"ERROR {ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message.Replace('\n', ' ').Replace('\r', ' ')} @ {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
        }
        var verdict = _errors > 0 ? "ERROR" : _stale > 0 ? "STALE" : "PASS";
        Log($"SCENARIO {_scenario} {mode} {verdict}");
        _window.ResultText.Text = string.Join("\n", _lines.TakeLast(12));
        await Task.Delay(400);
        File.WriteAllLines(outFile, _lines);
        if (!args.Contains("--stay"))
        {
            Application.Current.Exit();
        }
    }

    private void Log(string line)
    {
        _lines.Add(line);
        _window.StatusText.Text = line;
    }

    private void Check(string name, object? actual, string expected)
    {
        var text = actual?.ToString() ?? "<null>";
        var ok = text == expected;
        if (!ok)
        {
            _stale++;
        }
        Log($"CHECK {name}: expected={expected} actual={text} => {(ok ? "ok" : "STALE")}");
    }

    private void Note(string name, object? actual) => Log($"NOTE {name}: {actual?.ToString() ?? "<null>"}");

    private async Task RunScenarioAsync(string scenario)
    {
        switch (scenario)
        {
            case "merged-new": await MergedNewInstance(); break;
            case "merged-swap": await MergedInApp(swap: true); break;
            case "merged-resetsource": await MergedInApp(swap: false); break;
            case "negcache-swap": await NegativeCache(swap: true); break;
            case "negcache-resetsource": await NegativeCache(swap: false); break;
            case "generic": await GenericStyle(); break;
            case "page-new": await PageNew(); break;
            case "page-reinit": await PageReinit(resetResources: false); break;
            case "page-reinit-reset": await PageReinit(resetResources: true); break;
            case "loose": await Loose(); break;
            case "cycle": await Cycle(); break;
            case "frame-cache": await FrameCache(); break;
            case "app-reload": await AppReload(); break;
            case "resetsource-plain": await ResetSourcePlain(); break;
            case "implicit-code-swap": await ImplicitCodeSwap(); break;
            case "dict-reinit": await DictReinit(); break;
            case "type-member-added": await TypeMemberAdded(targeted: false); break;
            case "type-member-added-targeted": await TypeMemberAdded(targeted: true); break;
            case "reload-pages": await ReloadPages(offThread: false); break;
            case "reload-offthread": await ReloadPages(offThread: true); break;
            case "reload-window": await ReloadWindow(); break;
            case "reload-broken": await ReloadBroken(); break;
            case "deferral": await Deferral(); break;
            case "page-reinit-broken": await PageReinitBroken(); break;
            case "reinit-no-hotreload": await ReinitWithoutHotReload(); break;
            default: throw new ArgumentException("unknown scenario " + scenario);
        }
    }

    // Compiled markup defers every keyed resource (ThemeDictionaries too) until it is looked up; text markup creates
    // small dictionaries eagerly. A clean build loads compiled markup.
    private async Task Deferral()
    {
        const string uri = "ms-appx:///Dicts/Eager.xaml";
        var built = new ResourceDictionary { Source = new Uri(uri) };
        Note("built .xbf: resources created by load", CountingResource.Created);
        CountingResource.Created = 0;
        SetMarkup(uri, "V2", "Eager.xaml");
        var replaced = new ResourceDictionary { Source = new Uri(uri) };
        Check("replaced markup: resources created by load (a clean build creates none)", CountingResource.Created, "0");
        Check("replaced markup value", replaced["EagerText"], "eager-v2");
        GC.KeepAlive(built);
        await Task.CompletedTask;
    }

    // IXamlHotReloadService.ReloadComponents: every live page from the URI loads the replaced markup, in place.
    private async Task ReloadPages(bool offThread)
    {
        var first = new ResPage();
        var second = new ResPage();
        _window.OldHost.Content = first;
        _window.NewHost.Content = second;
        await Settle();
        Check("baseline root Tag", first.Tag, "root-v1");
        var live = GetLiveComponents(ResPageUri);
        Check("GetLiveComponents count", live.Count, "2");
        Check("GetLiveComponents returns the live pages", live.Contains(first) && live.Contains(second), "True");
        SetMarkup(ResPageUri, "V2", "ResPage.xaml");
        var (hr, count) = offThread ? await Task.Run(() => ReloadComponents(ResPageUri)) : ReloadComponents(ResPageUri);
        Log($"RELOAD {ResPageUri} {(offThread ? "from a background thread" : "on the UI thread")} hr=0x{hr:X8} count={count}");
        Check("ReloadComponents hr", $"0x{hr:X8}", "0x00000000");
        Check("ReloadComponents count", count, "2");
        await Settle();
        foreach (var (page, name) in new[] { (first, "first"), (second, "second") })
        {
            CheckPageMarkup(page, $"{name} reloaded page");
            Check($"{name} root Tag removed from markup", page.Tag, "<null>");
            Check($"{name} key removed from markup", page.Resources.ContainsKey("OnlyV1"), "False");
            Check($"{name} key added in markup", page.Resources.ContainsKey("OnlyV2"), "True");
        }
        Check("same instances", ReferenceEquals(_window.OldHost.Content, first) && ReferenceEquals(_window.NewHost.Content, second), "True");
    }

    private async Task ReloadWindow()
    {
        var window = new ProbeWindow();
        window.Activate();
        await Settle();
        Check("baseline window marker", Text((FrameworkElement)window.Content, "WindowMarker"), "window-v1");
        Check("baseline window title", window.Title, "probe-v1");
        const string uri = "ms-appx:///Pages/ProbeWindow.xaml";
        if (_binary)
        {
            SetMarkup(uri, "V2", "ProbeWindow.xaml");
        }
        else
        {
            // As saved by the user: the variant omits x:Class only so the build doesn't compile a second class.
            var text = File.ReadAllText(Path.Combine(ProjectDir, "Variants", "V2", "ProbeWindow.xaml"))
                .Replace("<Window", "<Window x:Class=\"CacheProbe.Pages.ProbeWindow\"");
            var temp = Path.Combine(Path.GetTempPath(), "ProbeWindow.v2.xaml");
            File.WriteAllText(temp, text);
            SetMarkupFile(uri, temp, binary: false);
        }
        var (hr, count) = ReloadComponents(uri);
        Log($"RELOAD {uri} hr=0x{hr:X8} count={count}");
        if (hr < 0)
        {
            try
            {
                Application.LoadComponent(window, new Uri(uri));
                Log("NOTE direct LoadComponent succeeded");
            }
            catch (Exception ex)
            {
                Log($"NOTE direct LoadComponent: {ex.Message.Replace('\n', ' ').Replace('\r', ' ')}");
            }
        }
        Check("ReloadComponents count (a Window)", count, "1");
        await Task.Delay(300);
        window.Content?.UpdateLayout();
        Check("window marker after reload", Text((FrameworkElement)window.Content!, "WindowMarker"), "window-v2");
        Check("window title after reload", window.Title, "probe-v2");
        window.Close();
    }

    private async Task ReloadBroken()
    {
        var page = new ResPage();
        _window.OldHost.Content = page;
        await Settle();
        SetMarkup(ResPageUri, "Broken", "ResPage.xaml");
        var (hr, count) = ReloadComponents(ResPageUri);
        Log($"RELOAD broken markup hr=0x{hr:X8} count={count}");
        Check("ReloadComponents reports the failure", hr < 0, "True");
        Check("nothing counted as loaded", count, "0");
        await Settle();
        Check("old content kept", Text(page, "PageMarker"), "marker-v1");
        Check("old resources kept", page.Resources.TryGetValue("PageText", out var value) ? value : "<missing>", "page-v1");
        Check("old root Tag kept", page.Tag, "root-v1");
        SetMarkup(ResPageUri, "V2", "ResPage.xaml");
        (hr, count) = ReloadComponents(ResPageUri);
        Log($"RELOAD fixed markup hr=0x{hr:X8} count={count}");
        await Settle();
        Check("fixed markup loads after a failure", Text(page, "PageMarker"), "marker-v2");
        Check("root Tag removed after the failure then fix", page.Tag, "<null>");
    }

    // A type that already resolved gains a member (as .NET hot reload adds a DependencyProperty and the regenerated
    // XamlTypeInfo describes it). Markup that sets the new member must work after the tool says the type changed, and
    // what resolved before (an instance, a style setter, a member) must keep working.
    private async Task TypeMemberAdded(bool targeted)
    {
        const string ns = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:local=\"using:CacheProbe\"";
        var provider = new DynProvider();
        var providerPtr = WinRT.MarshalInterface<Microsoft.UI.Xaml.Markup.IXamlMetadataProvider>.FromManaged(provider);
        int hr;
        unsafe
        {
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Vtbl[7])(_service, providerPtr);
        }
        Log($"REGISTER DynProvider (version 1: Label) hr=0x{hr:X8}");
        Marshal.ThrowExceptionForHR(hr);

        var before = (DynCard)XamlReader.Load($"<local:DynCard {ns} Label=\"first\" />");
        var style = (Style)XamlReader.Load($"<Style {ns} TargetType=\"local:DynCard\"><Setter Property=\"Label\" Value=\"styled\" /></Style>");
        _window.OldHost.Content = before;
        await Settle();
        Check("baseline Label from markup", before.Label, "first");
        try
        {
            XamlReader.Load($"<local:DynCard {ns} Hint=\"too early\" />");
            Log("NOTE Hint before the update loaded (unexpected)");
        }
        catch (Exception ex)
        {
            Log($"NOTE Hint before the update fails as in a build without it: {ex.Message.Split('\n').Last().Trim()}");
        }

        DynProvider.Version = 2;
        if (targeted)
        {
            hr = InvalidateTypes(["CacheProbe.DynCard"], out var marked);
            Log($"INVALIDATE InvalidateTypes(CacheProbe.DynCard) hr=0x{hr:X8} marked={marked}");
            Check("InvalidateTypes marked", marked, "1");
        }
        else
        {
            unsafe
            {
                hr = ((delegate* unmanaged[Stdcall]<IntPtr, int>)Vtbl[8])(_service);
            }
            Log($"INVALIDATE InvalidateTypeCaches hr=0x{hr:X8}");
        }

        DynCard? after = null;
        try
        {
            after = (DynCard)XamlReader.Load($"<local:DynCard {ns} Label=\"second\" Hint=\"from page\" />");
        }
        catch (Exception ex)
        {
            Log($"NOTE Hint after the update: {ex.Message.Replace('\n', ' ').Replace('\r', ' ')}");
        }
        Check("new member set from markup after the update", after?.Hint, "from page");
        Check("existing member still set from markup", after?.Label, "second");

        _window.NewHost.Content = after;
        await Settle();
        before.Hint = "set in code";
        Check("live instance still works (new DP set in code)", before.Hint, "set in code");
        before.Label = "changed";
        Check("live instance still works (old member)", before.Label, "changed");
        var styled = new DynCard { Style = style };
        _window.NewHost.Content = styled;
        await Settle();
        Check("style parsed before the update still applies", styled.Label, "styled");
        var styleAfter = (Style)XamlReader.Load($"<Style {ns} TargetType=\"local:DynCard\"><Setter Property=\"Hint\" Value=\"styled hint\" /></Style>");
        var styled2 = new DynCard { Style = styleAfter };
        _window.NewHost.Content = styled2;
        await Settle();
        Check("style setter for the new member", styled2.Hint, "styled hint");
        GC.KeepAlive(provider);
    }

    private unsafe int InvalidateTypes(string[] names, out uint marked)
    {
        marked = 0;
        var iid3 = new Guid("85B7546C-46F3-49C0-8AAD-D866BC9E86FC");
        if (Marshal.QueryInterface(_service, in iid3, out var service3) < 0)
        {
            return NoInterface;
        }
        var pointers = names.Select(Marshal.StringToCoTaskMemUni).ToArray();
        try
        {
            uint result;
            int hr;
            fixed (IntPtr* p = pointers)
            {
                hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, uint*, int>)(*(void***)service3)[11])(service3, (uint)names.Length, p, &result);
            }
            marked = result;
            return hr;
        }
        finally
        {
            foreach (var p in pointers)
            {
                Marshal.FreeCoTaskMem(p);
            }
            Marshal.Release(service3);
        }
    }

    // A ResourceDictionary with x:Class (its own InitializeComponent) re-initialized with replaced markup.
    private async Task DictReinit()
    {
        var dictionary = new CodeDict();
        Check("baseline CodeDictText", dictionary["CodeDictText"], "codedict-v1");
        SetMarkup("ms-appx:///Dicts/CodeDict.xaml", "V2", "CodeDict.xaml");
        dictionary.Reinitialize();
        Check("CodeDictText after re-init", dictionary["CodeDictText"], "codedict-v2");
        Check("key removed from markup", dictionary.ContainsKey("OnlyV1"), "False");
        Check("key added in markup", dictionary.ContainsKey("OnlyV2"), "True");
        await Task.CompletedTask;
    }

    // Replaced markup that fails to load must leave the live page's resources as they were.
    private async Task PageReinitBroken()
    {
        var page = new ResPage();
        _window.OldHost.Content = page;
        await Settle();
        SetMarkup(ResPageUri, "Broken", "ResPage.xaml");
        try
        {
            page.Reinitialize();
            Log("NOTE re-init did not fail");
        }
        catch (Exception ex)
        {
            Log($"NOTE re-init failed as expected: 0x{ex.HResult:X8}");
        }
        await Settle();
        Check("Page.Resources kept after a failed load", page.Resources.TryGetValue("PageText", out var value) ? value : "<missing>", "page-v1");
        Check("old key kept after a failed load", page.Resources.ContainsKey("OnlyV1"), "True");
    }

    // Control: InitializeComponent run twice with no hot reload at all keeps today's behavior.
    private async Task ReinitWithoutHotReload()
    {
        var page = new ResPage();
        _window.OldHost.Content = page;
        await Settle();
        try
        {
            page.Reinitialize();
            Log("NOTE second InitializeComponent succeeded");
        }
        catch (Exception ex)
        {
            Log($"NOTE second InitializeComponent failed: 0x{ex.HResult:X8} {ex.Message.Split('\n')[0].Trim()}");
        }
        await Settle();
        Note("PageText after second load", page.Resources.TryGetValue("PageText", out var value) ? value : "<missing>");
    }

    // Control: setting Source again on the app's merged dictionary, with no markup replaced.
    private async Task ResetSourcePlain()
    {
        ReloadAppMerged(swap: false);
        await Settle();
        Check("MergedText after Source set again", Application.Current.Resources["MergedText"], "merged-v1");
    }

    // Control: replace the app's merged dictionary with one built in code (no markup involved). Run with and without
    // diagnostics ("nodiag" mode) to see whether implicit styles re-apply to live elements.
    private async Task ImplicitCodeSwap()
    {
        var live = new ResPage();
        _window.OldHost.Content = live;
        await Settle();
        Check("baseline live Implicit Tag", Find<FrameworkElement>(live, "Implicit")?.Tag, "implicit-v1");
        var dictionary = new ResourceDictionary();
        var style = new Style(typeof(ImplicitProbe));
        style.Setters.Add(new Setter(FrameworkElement.TagProperty, "implicit-code"));
        dictionary[typeof(ImplicitProbe)] = style;
        var merged = Application.Current.Resources.MergedDictionaries;
        merged[merged.Count - 1] = dictionary;
        Log("RELOAD app merged[last] replaced with a dictionary built in code");
        await Settle();
        Check("live Implicit Tag after swap", Find<FrameworkElement>(live, "Implicit")?.Tag, "implicit-code");
    }

    // ---- scenarios ----

    private async Task MergedNewInstance()
    {
        Check("baseline new RD", new ResourceDictionary { Source = new Uri(MergedUri) }["MergedText"], "merged-v1");
        SetMarkup(MergedUri, "V2", "Merged.xaml");
        var rd = new ResourceDictionary { Source = new Uri(MergedUri) };
        Check("new RD MergedText", rd["MergedText"], "merged-v2");
        Check("new RD Light ThemedText", ((ResourceDictionary)rd.ThemeDictionaries["Light"])["ThemedText"], "light-v2");
        Check("new RD Dark ThemedText", ((ResourceDictionary)rd.ThemeDictionaries["Dark"])["ThemedText"], "dark-v2");
        await Task.CompletedTask;
    }

    private async Task MergedInApp(bool swap)
    {
        var live = new ResPage();
        _window.OldHost.Content = live;
        await Settle();
        Check("baseline page StaticMerged", Text(live, "StaticMerged"), "merged-v1");
        Check("baseline live Implicit Tag", Find<FrameworkElement>(live, "Implicit")?.Tag, "implicit-v1");

        SetMarkup(MergedUri, "V2", "Merged.xaml");
        ReloadAppMerged(swap);
        await Settle();

        Check("app lookup MergedText", Application.Current.Resources["MergedText"], "merged-v2");
        var fresh = new ResPage();
        _window.NewHost.Content = fresh;
        await Settle();
        CheckPageResources(fresh, "new page", "v1", "v2");

        // The live page: StaticResource values were resolved when it loaded (a tool re-runs its markup). ThemeResource
        // references re-resolve on a theme change.
        Note("live page StaticMerged (resolved at load)", Text(live, "StaticMerged"));
        // Implicit styles are live: WinUI re-applies them to existing elements when the app's dictionaries change.
        Check("live page Implicit Tag (implicit styles re-apply)", Find<FrameworkElement>(live, "Implicit")?.Tag, "implicit-v2");
        var light = Find<Grid>(live, "LightGrid")!;
        light.RequestedTheme = ElementTheme.Dark;
        await Settle();
        light.RequestedTheme = ElementTheme.Light;
        await Settle();
        Check("live page ThemedLight after theme toggle", Text(live, "ThemedLight"), "light-v2");
    }

    private async Task NegativeCache(bool swap)
    {
        var app = Application.Current.Resources;
        Check("baseline LateKey missing", app.TryGetValue("LateKey", out _), "False");
        // A page whose lookup misses LateKey as well (ThemeResource lookups go through the not-found caches).
        Check("baseline ContainsKey LateKey", app.ContainsKey("LateKey"), "False");
        SetMarkup(MergedUri, "V3", "Merged.xaml");
        ReloadAppMerged(swap);
        await Settle();
        Check("LateKey after reload", app.TryGetValue("LateKey", out var value) ? value : "<missing>", "late-v3");
    }

    private async Task GenericStyle()
    {
        var before = new ProbeControl();
        _window.OldHost.Content = before;
        await Settle();
        Check("baseline template", Text(before, "GenericText"), "generic-v1");

        SetMarkup(GenericUri, "V2", "Generic.xaml");
        var after = new ProbeControl();
        _window.NewHost.Content = after;
        await Settle();
        Check("new control template", Text(after, "GenericText"), "generic-v2");
        Note("live control template (applied before)", Text(before, "GenericText"));
    }

    private async Task PageNew()
    {
        SetMarkup(ResPageUri, "V2", "ResPage.xaml");
        var page = new ResPage();
        _window.NewHost.Content = page;
        await Settle();
        CheckPageMarkup(page, "new page");
    }

    private async Task PageReinit(bool resetResources)
    {
        var page = new ResPage();
        _window.OldHost.Content = page;
        await Settle();
        Check("baseline marker", Text(page, "PageMarker"), "marker-v1");
        Check("baseline root Tag", page.Tag, "root-v1");
        SetMarkup(ResPageUri, "V2", "ResPage.xaml");
        if (resetResources)
        {
            // What today's hot reload agents do first (XamlReload.PrepareForLoad).
            page.Resources = new ResourceDictionary();
            Log("RESET page.Resources = new ResourceDictionary() before re-init");
        }
        page.Reinitialize();
        await Settle();
        CheckPageMarkup(page, "reinitialized page");
        Check("root Tag removed from markup", page.Tag, "<null>");
        Check("Page.Resources key removed from markup", page.Resources.ContainsKey("OnlyV1"), "False");
        Check("Page.Resources key added in markup", page.Resources.ContainsKey("OnlyV2"), "True");
    }

    private async Task Loose()
    {
        Check("baseline loose", new ResourceDictionary { Source = new Uri(LooseUri) }["LooseText"], "loose-v1");
        SetMarkup(LooseUri, "V2", "Loose.xaml");
        Check("loose after edit", new ResourceDictionary { Source = new Uri(LooseUri) }["LooseText"], "loose-v2");
        RemoveMarkup(LooseUri);
        Check("loose after remove", new ResourceDictionary { Source = new Uri(LooseUri) }["LooseText"], "loose-v1");
        await Task.CompletedTask;
    }

    private async Task Cycle()
    {
        var steps = new[] { "V2", "V3", "V2", "-", "V3", "V2", "V3" };
        foreach (var (step, i) in steps.Select((s, i) => (s, i)))
        {
            if (step == "-")
            {
                RemoveMarkup(MergedUri);
            }
            else
            {
                SetMarkup(MergedUri, step, "Merged.xaml");
            }
            var expected = step == "-" ? "v1" : step.ToLowerInvariant();
            var rd = new ResourceDictionary { Source = new Uri(MergedUri) };
            Check($"step {i} ({step}) MergedText", rd["MergedText"], "merged-" + expected);
            Check($"step {i} ({step}) Dark ThemedText", ((ResourceDictionary)rd.ThemeDictionaries["Dark"])["ThemedText"], "dark-" + expected);
            ReloadAppMerged(swap: true);
            var page = new ResPage();
            _window.NewHost.Content = page;
            await Settle();
            Check($"step {i} ({step}) page MergedTemplate", Text(page, "MergedTemplateText"), "mtmpl-" + expected);
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    private async Task FrameCache()
    {
        var frame = _window.FrameHost;
        frame.Navigate(typeof(CachedPage));
        await Settle();
        Check("baseline cached page", Text((FrameworkElement)frame.Content, "CachedMarker"), "cached-v1");
        frame.Navigate(typeof(ResPage));
        await Settle();
        SetMarkup(CachedPageUri, "V2", "CachedPage.xaml");
        frame.GoBack();
        await Settle();
        Check("cached page after edit + back", Text((FrameworkElement)frame.Content, "CachedMarker"), "cached-v2");
        Note("CachedPage constructed", CachedPage.Constructed);
    }

    private async Task AppReload()
    {
        if (_binary)
        {
            Log("NOTE App.xaml compiled variant not built (its x:Class would clash); text only");
            return;
        }
        var page = new ResPage();
        _window.OldHost.Content = page;
        await Settle();
        Check("baseline AppText", Application.Current.Resources["AppText"], "app-v1");
        Check("baseline live Implicit Tag", Find<FrameworkElement>(page, "Implicit")?.Tag, "implicit-v1");
        SetMarkup(MergedUri, "V2", "Merged.xaml");
        SetMarkupFile(AppUri, Path.Combine(ProjectDir, "Variants", "V2", "App.xaml.txt"), binary: false);
        Application.LoadComponent(Application.Current, new Uri(AppUri));
        await Settle();
        Check("AppText after App LoadComponent", Application.Current.Resources["AppText"], "app-v2");
        Check("MergedText after App LoadComponent", Application.Current.Resources["MergedText"], "merged-v2");
        var fresh = new ResPage();
        _window.NewHost.Content = fresh;
        await Settle();
        CheckPageResources(fresh, "new page after App reload", "v1", "v2");
        Check("live page Implicit Tag after App reload", Find<FrameworkElement>(page, "Implicit")?.Tag, "implicit-v2");
    }

    // ---- shared checks ----

    private void CheckPageResources(FrameworkElement page, string what, string _, string v)
    {
        Check($"{what} StaticMerged", Text(page, "StaticMerged"), "merged-" + v);
        Check($"{what} ThemedLight", Text(page, "ThemedLight"), "light-" + v);
        Check($"{what} ThemedDark", Text(page, "ThemedDark"), "dark-" + v);
        Check($"{what} Implicit Tag", Find<FrameworkElement>(page, "Implicit")?.Tag, "implicit-" + v);
        Check($"{what} BasedOn", Text(page, "BasedOn"), "based-" + v);
        Check($"{what} MergedTemplate", Text(page, "MergedTemplateText"), "mtmpl-" + v);
    }

    private void CheckPageMarkup(ResPage page, string what)
    {
        Check($"{what} marker", Text(page, "PageMarker"), "marker-v2");
        Check($"{what} Page.Resources", Text(page, "PageResource"), "page-v2");
        Check($"{what} page DataTemplate", Text(page, "PageTemplateText"), "ptmpl-v2");
        var deferred = page.FindName("DeferredText") as TextBlock;
        page.UpdateLayout();
        Check($"{what} x:Load realized", deferred?.Text, "deferred-v2");
        Check($"{what} StaticMerged unchanged", Text(page, "StaticMerged"), "merged-v1");
    }

    // ---- hot reload service ----

    private static string ProjectDir => typeof(Runner).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "ProjectDir").Value!;

    private void SetMarkup(string uri, string version, string file)
    {
        if (_binary)
        {
            SetMarkupFile(uri, Path.Combine(AppContext.BaseDirectory, "Variants", version, Path.ChangeExtension(file, ".xbf")), binary: true);
        }
        else
        {
            SetMarkupFile(uri, Path.Combine(ProjectDir, "Variants", version, file), binary: false);
        }
    }

    private unsafe void SetMarkupFile(string uri, string path, bool binary)
    {
        var bytes = File.ReadAllBytes(path);
        int hr;
        fixed (char* u = uri)
        fixed (byte* c = bytes)
        {
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, char*, int, uint, byte*, int>)Vtbl[3])(_service, u, binary ? 1 : 0, (uint)bytes.Length, c);
        }
        Log($"SET {uri} <- {Path.GetFileName(Path.GetDirectoryName(path))}/{Path.GetFileName(path)} ({(binary ? "binary" : "text")}, {bytes.Length} bytes) hr=0x{hr:X8}");
        Marshal.ThrowExceptionForHR(hr);
    }

    private unsafe void RemoveMarkup(string uri)
    {
        int hr;
        fixed (char* u = uri)
        {
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, char*, int>)Vtbl[4])(_service, u);
        }
        Log($"REMOVE {uri} hr=0x{hr:X8}");
        Marshal.ThrowExceptionForHR(hr);
    }

    private static bool s_hasService2;
    private const int NoInterface = unchecked((int)0x80004002);

    private unsafe (int Hr, uint Count) ReloadComponents(string uri)
    {
        if (!s_hasService2)
        {
            return (NoInterface, 0);
        }
        uint count;
        int hr;
        fixed (char* u = uri)
        {
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, char*, uint*, int>)Vtbl[9])(_service, u, &count);
        }
        return (hr, count);
    }

    private unsafe List<object> GetLiveComponents(string uri)
    {
        Marshal.ThrowExceptionForHR(s_hasService2 ? 0 : NoInterface);
        uint count;
        IntPtr* array;
        int hr;
        fixed (char* u = uri)
        {
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, char*, uint*, IntPtr**, int>)Vtbl[10])(_service, u, &count, &array);
        }
        Marshal.ThrowExceptionForHR(hr);
        var result = new List<object>();
        for (var i = 0; i < count; i++)
        {
            result.Add(WinRT.MarshalInspectable<object>.FromAbi(array[i]));
            Marshal.Release(array[i]);
        }
        Marshal.FreeCoTaskMem((IntPtr)array);
        return result;
    }

    private unsafe void** Vtbl => *(void***)_service;

    /// <summary>What a tool does after a merged dictionary's markup changed: re-load the app's copy of it.</summary>
    private void ReloadAppMerged(bool swap)
    {
        var merged = Application.Current.Resources.MergedDictionaries;
        for (var i = 0; i < merged.Count; i++)
        {
            if (merged[i].Source is { } source && source.AbsolutePath.EndsWith("Dicts/Merged.xaml", StringComparison.OrdinalIgnoreCase))
            {
                if (swap)
                {
                    merged[i] = new ResourceDictionary { Source = new Uri(MergedUri) };
                }
                else
                {
                    merged[i].Source = new Uri(MergedUri);
                }
                Log($"RELOAD app merged[{i}] ({(swap ? "replaced with a new dictionary" : "Source set again")}; was {source})");
                return;
            }
        }
        throw new InvalidOperationException("the app doesn't merge Dicts/Merged.xaml");
    }

    private static async Task<IntPtr> AcquireServiceAsync()
    {
        var baseDir = AppContext.BaseDirectory;
        var tapPath = Path.Combine(baseDir, "ProbeTap.dll");
        var udkPath = Path.Combine(baseDir, "Microsoft.Internal.FrameworkUdk.dll");
        var tap = NativeLibrary.Load(tapPath);
        var getSite = NativeLibrary.GetExport(tap, "ProbeTap_GetSite");

        var injected = new TaskCompletionSource<int>();
        var thread = new Thread(() =>
        {
            try
            {
                injected.SetResult(Inject(udkPath, tapPath));
            }
            catch (Exception ex)
            {
                injected.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.MTA);
        thread.IsBackground = true;
        thread.Start();
        var hr = await injected.Task;
        Marshal.ThrowExceptionForHR(hr);

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            if (GetSite(getSite, out var site) >= 0)
            {
                // IXamlHotReloadService2 (ReloadComponents, GetLiveComponents) when the runtime has it.
                var iid2 = new Guid("873A3D6E-441A-44AC-AC29-8D6F357FAC81");
                var iid = new Guid("73A07052-1BFE-40CD-8DC4-FB3D7A5BCECF");
                s_hasService2 = Marshal.QueryInterface(site, in iid2, out var service) >= 0;
                if (!s_hasService2)
                {
                    Marshal.ThrowExceptionForHR(Marshal.QueryInterface(site, in iid, out service));
                }
                Marshal.Release(site);
                return service;
            }
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("the diagnostics tap was never sited");
            }
            await Task.Delay(50);
        }
    }

    private static unsafe int GetSite(IntPtr export, out IntPtr site)
    {
        IntPtr result;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr*, int>)export)(&result);
        site = result;
        return hr;
    }

    private static unsafe int Inject(string udkPath, string tapPath)
    {
        var udk = NativeLibrary.Load(udkPath);
        var init = (delegate* unmanaged[Stdcall]<char*, uint, char*, char*, Guid, char*, int>)NativeLibrary.GetExport(udk, "InitializeXamlDiagnosticsEx");
        var clsid = new Guid("6A1C0F2E-3B4D-4E5F-8A9B-0C1D2E3F4A5B");
        fixed (char* endpoint = "WinUIVisualDiagConnection1")
        fixed (char* udkp = udkPath)
        fixed (char* tapp = tapPath)
        fixed (char* data = "")
        {
            return init(endpoint, (uint)Environment.ProcessId, udkp, tapp, clsid, data);
        }
    }

    // ---- live tree helpers ----

    private static async Task Settle()
    {
        await Task.Delay(150);
        App.Window?.Content?.UpdateLayout();
        await Task.Delay(150);
    }

    private static string? Text(FrameworkElement root, string id) => Find<TextBlock>(root, id)?.Text;

    private static T? Find<T>(DependencyObject root, string id) where T : class
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current is FrameworkElement fe && AutomationProperties.GetAutomationId(fe) == id && current is T match)
            {
                return match;
            }
            var count = VisualTreeHelper.GetChildrenCount(current);
            for (var i = 0; i < count; i++)
            {
                queue.Enqueue(VisualTreeHelper.GetChild(current, i));
            }
        }
        return null;
    }
}
