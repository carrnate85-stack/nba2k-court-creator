using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;


namespace TwoK.Studio;

/// <summary>One optically consistent 24-unit outline icon family, shared by the toolbar and tool flyouts.</summary>
public sealed class ToolIcon : Viewbox
{
    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(nameof(Mode), typeof(ToolMode),
        typeof(ToolIcon), new PropertyMetadata(ToolMode.Move, (sender, _) => ((ToolIcon)sender).Rebuild()));

    public ToolMode Mode { get => (ToolMode)GetValue(ModeProperty); set => SetValue(ModeProperty, value); }

    public ToolIcon() { Width = Height = 22; IsHitTestVisible = false; Rebuild(); }

    private void Rebuild()
    {
        // A fixed viewport keeps every glyph's proportions and apparent stroke weight stable.
        Canvas viewport = new() { Width = 24, Height = 24 };
        Path path = new()
        {
            Data = Geometry.Parse(Mode switch
            {
                ToolMode.Move => "M5,3 L19,13 L12,14 L9,21 Z M12,14 L16,21",
                ToolMode.RectSelect => "M4,4 H8 M11,4 H14 M17,4 H20 V8 M20,11 V14 M20,17 V20 H16 M13,20 H10 M7,20 H4 V16 M4,13 V10 M4,7 V4",
                ToolMode.Brush => "M10,13 L18,4 Q19,3 20,4 Q21,5 20,6 L13,15 Z M10,13 C7,12 6,14 6,16 C6,18 4,19 3,19 C8,21 12,19 13,15",
                ToolMode.Eraser => "M4,13 L13,4 Q14,3 15,4 L21,10 L12,20 H7 L3,16 Q2,15 4,13 Z M7,10 L16,17 M12,20 H21",
                ToolMode.Bucket => "M5,10 L12,3 L21,12 L13,20 Q12,21 11,20 L3,12 Z M5,10 L14,19 M8,12 L6,6 Q5,2 8,2 Q10,2 12,4 M20,15 C19,17 17,19 17,20 A3,3 0 0 0 23,20 C23,19 21,17 20,15 Z",
                ToolMode.Lasso => "M17,17 C21,14 21,8 18,5 C14,1 7,3 4,7 C1,11 3,17 8,18 C11,19 14,17 14,15 C14,13 10,13 10,16 C10,19 12,21 15,21",
                ToolMode.PolygonLasso => "M5,5 L17,3 L21,12 L14,19 L4,17 Z M4,17 L9,14 M9,14 L10,21",
                ToolMode.MagicWand => "M4,19 L15,8 L18,11 L7,22 Z M12,11 L15,14 M8,2 V6 M6,4 H10 M19,2 V6 M17,4 H21 M20,15 V19 M18,17 H22",
                ToolMode.ColorRange => "M4,8 V4 H8 M16,4 H20 V8 M20,16 V20 H16 M8,20 H4 V16 M8,10 A3,3 0 1 0 8,16 A3,3 0 1 0 8,10 M15,7 A3,3 0 1 0 15,13 A3,3 0 1 0 15,7",
                ToolMode.Crop => "M7,2 V17 H22 M2,7 H17 V22 M10,4 H20 V14",
                ToolMode.Eyedropper => "M4,17 L14,7 L17,10 L7,20 L3,21 Z M12,5 L19,12 M14,7 L17,4 Q19,2 21,4 Q23,6 21,8 L18,11",
                ToolMode.SpotHealing => "M4,13 L13,4 A4.25,4.25 0 0 1 19,10 L10,19 A4.25,4.25 0 0 1 4,13 Z M8,9 L15,16 M10,7 L17,14 M6,14 L6.1,14 M8,16 L8.1,16 M15,6 L15.1,6 M17,8 L17.1,8",
                ToolMode.Type => "M4,7 V4 H20 V7 M12,4 V21 M8,21 H16",
                ToolMode.Rectangle => "M4,5 H20 V19 H4 Z",
                ToolMode.Hand => "M7,12 V6 Q7,4 9,4 Q11,4 11,6 V11 M11,6 V4 Q11,2 13,2 Q15,2 15,4 V11 M15,6 Q15,4 17,4 Q19,4 19,6 V12 M19,9 Q21,7 22,9 V14 C22,19 19,22 14,22 C10,22 9,20 7,18 L3,13 Q2,11 4,10 Q5,10 7,12",
                ToolMode.Zoom => "M10,3 A7,7 0 1 0 10,17 A7,7 0 1 0 10,3 M15,15 L21,21 M7,10 H13 M10,7 V13",
                _ => "M5,12 H19"
            }),
            StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };
        path.SetResourceReference(Shape.StrokeProperty, "TextBrush");
        viewport.Children.Add(path); Child = viewport;
    }
}
