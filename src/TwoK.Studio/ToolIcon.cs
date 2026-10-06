using System.Windows;
using System.Windows.Controls;

namespace TwoK.Studio;

/// <summary>Compatibility adapter for the court toolbar's tool enum, using Canvas's actual icon control.</summary>
public sealed class ToolIcon : Viewbox
{
    private readonly TextureStudio.ToolIcon _icon = new();
    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(nameof(Mode), typeof(ToolMode),
        typeof(ToolIcon), new PropertyMetadata(ToolMode.Move, (sender, _) => ((ToolIcon)sender).UpdateMode()));

    public ToolMode Mode { get => (ToolMode)GetValue(ModeProperty); set => SetValue(ModeProperty, value); }

    public ToolIcon()
    {
        Width = Height = 22; IsHitTestVisible = false; Child = _icon; UpdateMode();
    }

    private void UpdateMode() => _icon.Mode = Enum.Parse<TextureStudio.Models.ToolMode>(Mode.ToString());
}
