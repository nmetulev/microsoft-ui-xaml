namespace CacheProbe;
public sealed partial class CodeDict : Microsoft.UI.Xaml.ResourceDictionary
{
    public CodeDict() => InitializeComponent();

    public void Reinitialize()
    {
        _contentLoaded = false;
        InitializeComponent();
    }
}
