using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Gamehook.UI.Controls;

public partial class FieldCard : UserControl
{
    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<FieldCard, string?>(nameof(Label));

    public static readonly StyledProperty<string?> ValueTextProperty =
        AvaloniaProperty.Register<FieldCard, string?>(nameof(ValueText));

    public static readonly StyledProperty<bool> AccentProperty =
        AvaloniaProperty.Register<FieldCard, bool>(nameof(Accent));

    public FieldCard()
    {
        InitializeComponent();
    }

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string? ValueText
    {
        get => GetValue(ValueTextProperty);
        set => SetValue(ValueTextProperty, value);
    }

    public bool Accent
    {
        get => GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
