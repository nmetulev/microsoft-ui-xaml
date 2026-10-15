namespace CacheProbe;

public partial class App : Microsoft.UI.Xaml.Application
{
    public static MainWindow? Window { get; private set; }
    public App() => InitializeComponent();
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        Window = new MainWindow();
        Window.Activate();
    }
}
