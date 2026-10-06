using System.IO;
using System.Windows;
using System.Windows.Controls;
using NBA2KCourtCreator.Studio;
using TwoK.Studio;

internal static partial class Program
{
    private static async Task CheckLogoActions(string output)
    {
        var file=Path.Combine(output,"logo-actions-source.png");WriteLogoExample(file);
        var sourceRevision=StudioImages.FileRevision(file);
        var window=new StudioWindow(true);
        void Click(string name) => ((MenuItem)window.FindName(name)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        async Task<ArtworkLayer> Prepare()
        {
            await window.NewProjectAsync();await window.AddLogoAsync(file,"Axis logo");window.SwitchSection("logos");
            var logo=window.Canvas.SelectedLayer!;
            logo.X=1200;logo.Y=900;logo.Width=500;logo.Height=280;logo.Rotation=37.5;
            logo.FlipX=true;logo.FlipY=false;logo.Opacity=74;logo.ScaleLocked=false;
            return logo;
        }
        try
        {
            await window.InitializeAsync();
            foreach(var vertical in new[]{false,true})
            {
                var logo=await Prepare();var pose=logo.Capture();var before=window.CreateProject().ToJsonString();
                Click(vertical?"FlipYMenu":"FlipXMenu");
                Assert(window.Canvas.Layers.Count==1 && logo.Capture()==pose && logo.FlipX==vertical && logo.FlipY==vertical,
                    "Flip menu changed placement, rotation, count or the wrong axis.");
                var after=window.CreateProject().ToJsonString();await window.UndoAsync();
                Assert(window.CreateProject().ToJsonString()==before,"Flip undo did not restore the exact original.");
                await window.UndoAsync(true);Assert(window.CreateProject().ToJsonString()==after,"Flip redo differs.");
                Click(vertical?"FlipYMenu":"FlipXMenu");Assert(window.CreateProject().ToJsonString()==before,"Double flip did not restore the artwork.");
            }
            foreach(var x in new[]{true,false})
            {
                var logo=await Prepare();var pose=logo.Capture();var before=window.CreateProject().ToJsonString();
                var center=window.Canvas.Anchors.Single(anchor=>anchor.Id=="court-center").Position;
                Click(x?"CopyXMenu":"CopyYMenu");var copy=window.Canvas.SelectedLayer!;
                var expected=new ArtworkState(x?2*center.X-pose.X-pose.Width:pose.X,
                    x?pose.Y:2*center.Y-pose.Y-pose.Height,pose.Width,pose.Height,pose.Rotation);
                Assert(window.Canvas.Layers.Count==2 && copy.Id!=logo.Id && copy.Capture()==expected && logo.Capture()==pose,
                    "Mirror copy changed the source or reflected the wrong coordinate.");
                Assert(copy.FlipX==logo.FlipX && copy.FlipY==logo.FlipY && copy.Opacity==logo.Opacity && copy.Visible==logo.Visible
                    && copy.ScaleLocked==logo.ScaleLocked && copy.Path==logo.Path && ReferenceEquals(copy.Image,logo.Image),
                    "Mirror did not retain artwork orientation/appearance/source.");
                var after=window.CreateProject().ToJsonString();await window.UndoAsync();
                Assert(window.CreateProject().ToJsonString()==before,"Mirror undo failed.");await window.UndoAsync(true);
                Assert(window.CreateProject().ToJsonString()==after,"Mirror redo failed.");
                var project=Path.Combine(output,x?"mirror-x.court.json":"mirror-y.court.json");
                await window.SaveProjectToAsync(project);await window.NewProjectAsync();await window.OpenProjectFromAsync(project);
                var reopened=window.Canvas.Layers.Single(layer=>layer.Id==copy.Id);
                Assert(reopened.Capture()==expected && reopened.FlipX==copy.FlipX && reopened.FlipY==copy.FlipY,
                    "Portable reopen lost mirror placement or flip state.");
            }
            await Prepare();for(var i=1;i<4;i++)await window.AddLogoAsync(file,$"Logo {i+1}");
            Assert(!((Button)window.FindName("MirrorLogoButton")).IsEnabled && !((MenuItem)window.FindName("CopyXMenu")).IsEnabled
                && !((MenuItem)window.FindName("CopyYMenu")).IsEnabled && ((Button)window.FindName("FlipLogoButton")).IsEnabled,
                "Four-slot capacity disabled flip or left mirror available.");
            var full=window.CreateProject().ToJsonString();Click("CopyXMenu");Click("CopyYMenu");
            Assert(window.CreateProject().ToJsonString()==full,"Mirror exceeded the four-logo limit.");
            window.Canvas.SelectedLayer=null;
            Assert(!((Button)window.FindName("FlipLogoButton")).IsEnabled && !((Button)window.FindName("MirrorLogoButton")).IsEnabled,
                "Empty selection left the axis actions enabled.");
            foreach(var dark in new[]{false,true})
            {
                StudioTheme.Apply(dark);window.Canvas.SelectedLayer=window.Canvas.Layers[0];window.SwitchSection("logos");
                foreach(var width in new[]{1000,1440})
                {
                    Layout(window,width,680);
                    var actions=(System.Windows.Controls.Primitives.UniformGrid)window.FindName("LogoActions");
                    Assert(actions.Children.Count==5 && !actions.Children.OfType<Button>().Any(button=>Equals(button.Content,"Center")),
                        "Logo action row retained Center or extra actions.");
                    foreach(var name in new[]{"FlipLogoButton","MirrorLogoButton"})
                    {
                        var button=(Button)window.FindName(name);var content=(FrameworkElement)button.Content;
                        Assert(button.ContextMenu.Items.OfType<MenuItem>().Count()==2
                            && content.DesiredSize.Width<=button.ActualWidth-button.Padding.Left-button.Padding.Right+0.1,
                            "Axis dropdown menu or compact label bounds are incorrect.");
                    }
                    RenderDpi(window,Path.Combine(output,$"logo-actions-{(dark?"dark":"light")}-{width}.png"),width,680,1);
                }
            }
            Assert(!window.IsVisible && StudioImages.FileRevision(file)==sourceRevision,"Logo checks opened the app or changed source artwork.");
        }
        finally { StudioTheme.Apply(false);window.Close(); }
        Console.WriteLine("PASS logo actions: compact Flip/Mirror X/Y dropdowns, no Center action; in-place flips, opposite-side copies retaining orientation; exact undo/redo and portable reopen; capacity/selection guards; light/dark layout. No native windows opened.");
    }
}
