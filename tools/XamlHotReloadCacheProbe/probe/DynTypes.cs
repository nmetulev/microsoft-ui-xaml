using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace CacheProbe;

/// <summary>A control whose metadata (IXamlType) gains a member while the app runs, as .NET hot reload does to a type.</summary>
public sealed class DynCard : UserControl
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(DynCard), new PropertyMetadata(null));

    public static readonly DependencyProperty HintProperty =
        DependencyProperty.Register(nameof(Hint), typeof(string), typeof(DynCard), new PropertyMetadata(null));

    public string? Label { get => (string?)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string? Hint { get => (string?)GetValue(HintProperty); set => SetValue(HintProperty, value); }
}

/// <summary>
/// Describes DynCard the way a regenerated XamlTypeInfo does: version 1 knows Label; version 2 (a new IXamlType object)
/// knows Label and Hint.
/// </summary>
public sealed class DynProvider : IXamlMetadataProvider
{
    public static int Version = 1;
    private DynCardType? _type;

    public IXamlType? GetXamlType(Type type) => type == typeof(DynCard) ? Current() : null;

    public IXamlType? GetXamlType(string fullName) => fullName == "CacheProbe.DynCard" ? Current() : null;

    public XmlnsDefinition[] GetXmlnsDefinitions() => [];

    private DynCardType Current()
    {
        if (_type is null || _type.Version != Version)
        {
            _type = new DynCardType(Version);
        }
        return _type;
    }
}

internal sealed class SystemType(Type type) : IXamlType
{
    public IXamlType? BaseType => null;
    public IXamlType? BoxedType => null;
    public IXamlMember? ContentProperty => null;
    public string FullName => type.FullName!;
    public bool IsArray => false;
    public bool IsBindable => false;
    public bool IsCollection => false;
    public bool IsConstructible => false;
    public bool IsDictionary => false;
    public bool IsMarkupExtension => false;
    public IXamlType? ItemType => null;
    public IXamlType? KeyType => null;
    public Type UnderlyingType => type;
    public object ActivateInstance() => throw new NotSupportedException();
    public void AddToMap(object instance, object key, object value) => throw new NotSupportedException();
    public void AddToVector(object instance, object value) => throw new NotSupportedException();
    public object CreateFromString(string value) => value;
    public IXamlMember? GetMember(string name) => null;
    public void RunInitializer() { }
}

internal sealed class DynCardType(int version) : IXamlType
{
    public int Version { get; } = version;
    private static readonly IXamlType UserControlType = new SystemType(typeof(UserControl));
    private static readonly IXamlType StringType = new SystemType(typeof(string));

    public IXamlType BaseType => UserControlType;
    public IXamlType? BoxedType => null;
    public IXamlMember? ContentProperty => null;
    public string FullName => "CacheProbe.DynCard";
    public bool IsArray => false;
    public bool IsBindable => false;
    public bool IsCollection => false;
    public bool IsConstructible => true;
    public bool IsDictionary => false;
    public bool IsMarkupExtension => false;
    public IXamlType? ItemType => null;
    public IXamlType? KeyType => null;
    public Type UnderlyingType => typeof(DynCard);
    public object ActivateInstance() => new DynCard();
    public void AddToMap(object instance, object key, object value) => throw new NotSupportedException();
    public void AddToVector(object instance, object value) => throw new NotSupportedException();
    public object CreateFromString(string value) => throw new NotSupportedException();
    public void RunInitializer() => System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(DynCard).TypeHandle);

    public IXamlMember? GetMember(string name) => name switch
    {
        "Label" => new DynMember(this, "Label", DynCard.LabelProperty),
        "Hint" when Version >= 2 => new DynMember(this, "Hint", DynCard.HintProperty),
        _ => null,
    };

    private sealed class DynMember(IXamlType target, string name, DependencyProperty property) : IXamlMember
    {
        public bool IsAttachable => false;
        public bool IsDependencyProperty => true;
        public bool IsReadOnly => false;
        public string Name => name;
        public IXamlType TargetType => target;
        public IXamlType Type => StringType;
        public object GetValue(object instance) => ((DependencyObject)instance).GetValue(property);
        public void SetValue(object instance, object value) => ((DependencyObject)instance).SetValue(property, value);
    }
}
