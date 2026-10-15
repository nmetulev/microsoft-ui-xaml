namespace CacheProbe.Pages;
public sealed partial class CachedPage : Microsoft.UI.Xaml.Controls.Page
{
    public static int Constructed;
    public CachedPage() { Constructed++; InitializeComponent(); }
}
