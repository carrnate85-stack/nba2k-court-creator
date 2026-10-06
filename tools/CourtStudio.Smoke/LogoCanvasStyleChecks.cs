using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NBA2KCourtCreator.Studio;

internal static partial class Program
{
    private static async Task CheckLogoCanvasStyle(StudioWindow window,string output)
    {
        await window.NewProjectAsync();window.SwitchSection("logos");
        var details=(Expander)window.FindName("LogoDetailsExpander");details.IsExpanded=false;
        var list=(ListBox)window.FindName("LogoList");var actions=(FrameworkElement)window.FindName("LogoActions");
        var slider=(Slider)window.FindName("LogoOpacitySlider");
        var file=Path.Combine(output,"canvas-style-logo.png");WriteLogoExample(file);
        foreach(var (width,height) in new[]{(1440,900),(1000,680)})
        {
            Layout(window,width,height);var column=(ColumnDefinition)window.FindName("InspectorWidth");Assert(Math.Abs(column.ActualWidth-400)<1,$"Canvas inspector width differs: {column.ActualWidth}, configured {column.Width}.");
            var viewport=(Border)window.FindName("ViewportCard");Assert(viewport.Margin==new Thickness(12)&&viewport.CornerRadius==new CornerRadius(8)&&viewport.BorderThickness==new Thickness(1),"Canvas viewport gutter/border/radius differs.");
            var root=(Visual)window.Content;var baseline=actions.TransformToAncestor(root).TransformBounds(new Rect(actions.RenderSize));
            for(var count=0;count<=4;count++)
            {
                if(count>0)await window.AddLogoAsync(file,$"Logo {count}");Layout(window,width,height);
                var current=actions.TransformToAncestor(root).TransformBounds(new Rect(actions.RenderSize));
                Assert(list.ActualHeight==224 && current==baseline,"Layer count shifts fixed list/actions.");
                Assert(list.BorderThickness==new Thickness(1) && ((SolidColorBrush)list.Background).Color==((SolidColorBrush)window.FindResource("PanelBrush")).Color,"List border/background differs from Canvas.");
                Assert(details.TransformToAncestor(root).Transform(new Point()).Y>current.Bottom,"Details overlap layer actions.");
                Assert(!details.IsExpanded && details.Visibility == Visibility.Visible && (!_allowNativeWindows || details.IsVisible),"Details not collapsed/visible.");Assert(((ScrollViewer)window.FindName("LogoPropertiesScroll")).ScrollableHeight<1,$"Collapsed panel unnecessarily scrolls at {width} / {count}: {((ScrollViewer)window.FindName("LogoPropertiesScroll")).ScrollableHeight}, viewport {((ScrollViewer)window.FindName("LogoPropertiesScroll")).ViewportHeight}, extent {((ScrollViewer)window.FindName("LogoPropertiesScroll")).ExtentHeight}.");
                Assert(slider.IsEnabled==(count>0),"Opacity empty/selected state wrong.");
                foreach(var item in Descendants<ListBoxItem>(list)){var row=Descendants<Border>(item).First(border=>border.Name=="LayerRow");Assert(row.BorderThickness==new Thickness(0,0,0,1)&&row.Padding==new Thickness(6,4,6,4),"Canvas row separators/padding differ.");}
                if(count is 0 or 1 or 4)RenderDpi(window,Path.Combine(output,$"canvas-logos-{count}-{width}.png"),width,height,1);
            }
            var flags=BindingFlags.NonPublic|BindingFlags.Instance;
            var undo=(List<JsonObject>)typeof(StudioWindow).GetField("_undo",flags)!.GetValue(window)!;
            var logo=window.Canvas.SelectedLayer!;var before=logo.Opacity;var history=undo.Count;
            typeof(StudioWindow).GetMethod("BeginLogoOpacity",flags)!.Invoke(window,null);
            for(var step=0;step<80;step++)slider.Value=40+step*.25;
            Assert(logo.Opacity==slider.Value && undo.Count==history,"Opacity preview added history or failed.");
            typeof(StudioWindow).GetMethod("FinishLogoOpacity",flags)!.Invoke(window,null);
            Assert(undo.Count==history+1,"Opacity gesture isn't one undo action.");
            await window.UndoAsync();Assert(window.Canvas.SelectedLayer!.Opacity==before && slider.Value==before,"Opacity undo/control sync failed.");await window.UndoAsync(true);Assert(window.Canvas.Layers.Single(item=>item.Id==logo.Id).Opacity==59.75,"Opacity redo failed.");
            window.Canvas.SelectedLayer=window.Canvas.Layers.Single(item=>item.Id==logo.Id);details.IsExpanded=true;Layout(window,width,height);RenderDpi(window,Path.Combine(output,$"canvas-logos-details-{width}.png"),width,height,1);
            Assert(Descendants<TextBox>((DependencyObject)window.FindName("LogoProperties")).Any(box=>Equals(box.Tag,"Width")),"Transform fields missing after expansion.");
            details.IsExpanded=false;Layout(window,width,height);foreach(var scale in new[]{1d,1.25,1.5,2d})RenderDpi(window,Path.Combine(output,$"canvas-logos-dpi-{width}-{scale:0.##}.png"),width,height,scale);await window.NewProjectAsync();window.SwitchSection("logos");
        }
        Console.WriteLine("PASS Canvas logo panel: fixed 224-DIP list across 0–4 layers, bordered Canvas row template/spacing, actions stable below, collapsed/expanded transforms, matching 400-DIP inspector and 12-DIP viewport gutters, opacity preview + one undo/redo; normal/compact renders.");
    }
}
