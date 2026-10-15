using Microsoft.UI.Xaml.Controls;
namespace CacheProbe;

public sealed class ProbeControl : Control
{
    public ProbeControl() => DefaultStyleKey = typeof(ProbeControl);
}

public sealed class ImplicitProbe : Control
{
}

public sealed class CountingResource : Microsoft.UI.Xaml.DependencyObject
{
    public static int Created;
    public CountingResource() => Created++;
}
