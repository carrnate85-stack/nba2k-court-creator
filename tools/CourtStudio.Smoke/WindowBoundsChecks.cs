using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NBA2KCourtCreator.Studio;

internal static partial class Program
{
    private static void CheckWindowBounds(string output)
    {
        var automatic = StudioWindowBounds.FitSize(new Size(double.NaN, double.NaN), new Size(800, 600));
        Assert(double.IsFinite(automatic.Width) && double.IsFinite(automatic.Height) && automatic.Width > 0 && automatic.Height > 0, "Automatic-size windows produced nonfinite work-area fitting dimensions.");
        Assert(automatic == new Size(776, 576) && StudioWindowBounds.FitSize(new Size(double.PositiveInfinity, 200), new Size(800, 600)) == new Size(776, 200), "Automatic/infinite fitting did not use bounded available dimensions.");
        Assert(StudioWindowBounds.FitSize(new Size(410, 330), new Size(10, 10)) == new Size(1, 1), "Tiny work areas produced invalid dimensions.");
        var areas = new[]
        {
            new StudioWindowBounds.MonitorArea(new Rect(0, 0, 1920, 1080), new Rect(0, 0, 1920, 1040)),
            new StudioWindowBounds.MonitorArea(new Rect(-1920, 0, 1920, 1080), new Rect(-1880, 0, 1880, 1080)),
            new StudioWindowBounds.MonitorArea(new Rect(2560, -1440, 2560, 1440), new Rect(2560, -1400, 2560, 1400)),
            new StudioWindowBounds.MonitorArea(new Rect(-1280, -1024, 1280, 1024), new Rect(-1280, -1024, 1280, 984))
        };
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMinMax>());
        try
        {
            var sentinel = new NativeMinMax { Reserved = new NativePoint { X = 17, Y = 23 }, MaxSize = new NativePoint { X = 31, Y = 37 }, MaxTrackSize = new NativePoint { X = 10001, Y = 10002 } };
            Marshal.StructureToPtr(sentinel, memory, false);
            var before = new byte[Marshal.SizeOf<NativeMinMax>()]; Marshal.Copy(memory, before, 0, before.Length);
            Assert(!StudioWindowBounds.ApplyMinMax(memory, null, new Size(960, 680), new DpiScale(1, 1)), "Missing monitor data was handled instead of leaving native defaults.");
            Assert(!StudioWindowBounds.ApplyMinMax(IntPtr.Zero, areas[0], new Size(960, 680), new DpiScale(1, 1)), "Null native message memory was accepted.");
            var invalid = new StudioWindowBounds.MonitorArea(areas[0].Monitor, new Rect(3000, 0, 500, 500));
            Assert(!StudioWindowBounds.ApplyMinMax(memory, invalid, new Size(960, 680), new DpiScale(1, 1)), "Out-of-monitor work area was accepted.");
            var after = new byte[before.Length]; Marshal.Copy(memory, after, 0, after.Length);
            Assert(before.SequenceEqual(after), "Failed monitor fitting changed native message memory.");
            foreach (var area in areas)
            foreach (var scale in new[] { 1d, 1.25, 1.5, 2 })
            {
                Marshal.StructureToPtr(sentinel, memory, false);
                Assert(StudioWindowBounds.ApplyMinMax(memory, area, new Size(960, 680), new DpiScale(scale, scale)), "Valid monitor fitting was not handled.");
                var bounds = Marshal.PtrToStructure<NativeMinMax>(memory);
                Assert(bounds.MaxPosition.X == area.Work.Left - area.Monitor.Left && bounds.MaxPosition.Y == area.Work.Top - area.Monitor.Top && bounds.MaxSize.X == area.Work.Width && bounds.MaxSize.Y == area.Work.Height, "Maximize ignored taskbar-relative monitor coordinates.");
                Assert(bounds.MinTrackSize.X == Math.Min(area.Work.Width, Math.Ceiling(960 * scale)) && bounds.MinTrackSize.Y == Math.Min(area.Work.Height, Math.Ceiling(680 * scale)), "Minimum tracking did not respect DPI/work-area limits.");
                Assert(bounds.Reserved.X == 17 && bounds.Reserved.Y == 23 && bounds.MaxTrackSize.X == 10001 && bounds.MaxTrackSize.Y == 10002, "Monitor fitting overwrote unrelated native tracking fields.");
                var placement = StudioWindowBounds.ClampPlacement(new Rect(area.Work.Right + 300, area.Work.Bottom + 200, 410 * scale, 330 * scale), area.Work);
                Assert(area.Work.Contains(placement) && placement.Right == area.Work.Right && placement.Bottom == area.Work.Bottom, "Popup physical-pixel fitting failed on a displaced/DPI-scaled monitor.");
                var oversized = StudioWindowBounds.ClampPlacement(new Rect(area.Work.Left - 500, area.Work.Top - 500, 8192, 4096), area.Work);
                Assert(oversized == area.Work, "Oversized window placement escaped its work area.");
            }
        }
        finally { Marshal.FreeHGlobal(memory); }
        var owner = new Window { Width = 800, Height = 600, Left = -1200, Top = 100 };
        var picker = new StudioColorWindow(owner, "#19583F", [], testing: true);
        try
        {
            var content = (FrameworkElement)picker.Content;
            picker.Width = 350; picker.Height = 200;
            content.Measure(new Size(350, 200)); content.Arrange(new Rect(0, 0, 350, 200)); content.UpdateLayout();
            var apply = Descendants<Button>(content).Single(button => Equals(button.Content, "Apply"));
            var teamColors = Descendants<Button>(content).Single(button => Equals(button.Content, "Team Colors"));
            var viewport = new Rect(0, 0, 350, 200);
            Assert(viewport.Contains(apply.TransformToAncestor(content).TransformBounds(new Rect(apply.RenderSize))) && viewport.Contains(teamColors.TransformToAncestor(content).TransformBounds(new Rect(teamColors.RenderSize))), "Compact color picker clipped its Apply or Team Colors action.");
            Assert(StudioWindowBounds.IsAttached(picker), "Color picker did not use shared work-area fitting.");
            StudioWindowBounds.Attach(picker);
            Assert(Descendants<ScrollViewer>(content).Single(scroll => scroll.Content is StackPanel).ScrollableHeight > 0, "Compact color picker did not make its overflowing body scrollable.");
            foreach (var scale in new[] { 1d, 1.25, 1.5, 2 }) RenderDpi(picker, Path.Combine(output, $"color-picker-compact-{scale * 100:0}.png"), 350, 200, scale);
            RenderDpi(picker, Path.Combine(output, "color-picker-normal.png"), 410, 330, 1);
        }
        finally { picker.Close(); owner.Close(); }
        File.WriteAllText(Path.Combine(output, "window-bounds-audit.json"), new JsonObject { ["monitorConfigurations"] = 4, ["dpiScales"] = "100,125,150,200%", ["missingMonitorLeavesMessageUntouched"] = true, ["automaticSizingFinite"] = true, ["physicalPlacementContained"] = true, ["compactColorActionsVisible"] = true, ["compactColorBodyScrollable"] = true, ["nativeWindowsOpened"] = false, ["actualMonitorTransitionTested"] = false }.ToJsonString());
        Console.WriteLine("PASS window bounds: finite automatic sizes; 4 monitor layouts/4 DPI scales, negative origins/taskbar edges, bounded native minima, absent/invalid monitor leaves messages intact; compact color body scrolls with fixed actions. No native windows opened.");
    }
}
