using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NBA2KCourtCreator.Studio;
using TwoK.Studio;

internal static partial class Program
{
    private static async Task CheckLogoCanvasStyle(StudioWindow window,string output)
    {
        await window.NewProjectAsync();window.SwitchSection("logos");
        var options=(Border)window.FindName("TransformOptionsBar");
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
                Assert(options.Visibility==Visibility.Collapsed && window.FindName("LogoDetailsExpander") is null,"Default logo workflow shows transform details.");
                Assert(((ScrollViewer)window.FindName("LogoPropertiesScroll")).ScrollableHeight<1,"Logo panel unnecessarily scrolls without transform details.");
                Assert(slider.IsEnabled==(count>0),"Opacity empty/selected state wrong.");
                Assert(((TextBlock)window.FindName("LogoLayerCount")).Text==(count==1?"1 layer":$"{count} layers"),"Layer count does not track imports/New.");
                foreach(var item in Descendants<ListBoxItem>(list)){var row=Descendants<Border>(item).First(border=>border.Name=="LayerRow");Assert(row.BorderThickness==new Thickness(1)&&row.CornerRadius==new CornerRadius(4)&&row.Padding==new Thickness(6,4,6,4),"Reference-inspired row outline/padding differs.");}
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
            window.Canvas.SelectedLayer=window.Canvas.Layers.Single(item=>item.Id==logo.Id);window.SelectCanvasTool(ArtworkTool.Transform);Layout(window,width,height);
            var properties=(StackPanel)window.FindName("LogoProperties");
            var inputs=Descendants<TextBox>(properties).ToArray();
            Assert(options.Visibility==Visibility.Visible && options.IsEnabled && options.ActualHeight<=44
                && properties.Orientation==Orientation.Horizontal && !properties.IsDescendantOf((DependencyObject)window.FindName("LogoPanel")),"Transform options are not a slim contextual bar outside the sidebar.");
            Assert(((ScrollViewer)window.FindName("LogoPropertiesScroll")).ScrollableHeight<1,"Transform mode reduced sidebar space and hid logo actions.");
            Assert(inputs.Select(input=>input.Tag).SequenceEqual(new[]{"X","Y","Width","Height","Rotation (°)"})
                && properties.Children.OfType<Border>().Count()==0 && !Descendants<ComboBox>(properties).Any(),"Transform fields retained large boxes or alignment controls.");
            foreach(var input in inputs)
            {
                var bounds=input.TransformToAncestor(options).TransformBounds(new Rect(input.RenderSize));
                Assert(bounds.Left>=0 && bounds.Right<=options.ActualWidth && bounds.Top>=0 && bounds.Bottom<=options.ActualHeight,"Transform field clipped or wrapped: "+input.Tag);
            }
            var sizeLock=Descendants<Button>(properties).Single(button=>button.Content is Viewbox);
            var lockBounds=sizeLock.TransformToAncestor(options).TransformBounds(new Rect(sizeLock.RenderSize));
            Assert(inputs[2].TransformToAncestor(options).Transform(new Point()).X<lockBounds.Left
                && lockBounds.Right<inputs[3].TransformToAncestor(options).Transform(new Point()).X,"Aspect lock is not between W and H.");
            var saving=typeof(StudioWindow).GetField("_saving",flags)!;saving.SetValue(window,true);
            typeof(StudioWindow).GetMethod("RefreshMutationState",flags)!.Invoke(window,null);Layout(window,width,height);
            Assert(!options.IsEnabled,"Options bar escaped save-time mutation guard.");saving.SetValue(window,false);
            typeof(StudioWindow).GetMethod("RefreshMutationState",flags)!.Invoke(window,null);Layout(window,width,height);
            Assert(options.IsEnabled,"Options bar stayed disabled after save.");
            foreach(var dark in new[]{false,true})
            {
                StudioTheme.Apply(dark);typeof(StudioWindow).GetMethod("RefreshLogoInspector",flags)!.Invoke(window,null);Layout(window,width,height);
                foreach(var scale in new[]{1d,1.25,1.5,2d})RenderDpi(window,Path.Combine(output,$"canvas-transform-{(dark?"dark":"light")}-{width}-{scale:0.##}.png"),width,height,scale);
            }
            StudioTheme.Apply(false);window.SelectCanvasTool(ArtworkTool.Hand);Assert(options.Visibility==Visibility.Collapsed,"Hand tool left transform options visible.");
            window.SelectCanvasTool(ArtworkTool.Transform);window.Canvas.SelectedLayer=null;Assert(options.Visibility==Visibility.Collapsed,"Deselection left transform options visible.");
            window.Canvas.SelectedLayer=window.Canvas.Layers[0];Assert(options.Visibility==Visibility.Visible,"Selecting a logo did not restore active transform options.");
            window.SwitchSection("paint");Assert(options.Visibility==Visibility.Collapsed,"Paint tab left transform options visible.");
            await window.NewProjectAsync();window.SwitchSection("logos");Assert(options.Visibility==Visibility.Collapsed,"New document retained transform options.");
        }
        Console.WriteLine("PASS Canvas logo panel: fixed 224-DIP list across 0–4 layers, live count/outlined rows and icon actions; no sidebar transform section; slim X/Y/W/H/Angle bar only for selected logos in Transform, lock between W/H, save-time guards; no clipping at compact/full-size and 100–200% DPI light/dark renders; Hand/deselection/Paint/New hide options; opacity retains one undo/redo.");
    }
}
