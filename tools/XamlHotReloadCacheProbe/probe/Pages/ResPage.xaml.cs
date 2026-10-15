namespace CacheProbe.Pages;
public sealed partial class ResPage : Microsoft.UI.Xaml.Controls.Page
{
    public ResPage() => InitializeComponent();

    // What a hot reload tool does to re-run a live page's markup (the generated guard would skip it).
    public void Reinitialize()
    {
        _contentLoaded = false;
        InitializeComponent();
    }
}
