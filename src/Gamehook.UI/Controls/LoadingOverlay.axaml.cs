using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Gamehook.UI.Controls;

public partial class LoadingOverlay : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<LoadingOverlay, string?>(nameof(Text));

    public LoadingOverlay()
    {
        InitializeComponent();
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
