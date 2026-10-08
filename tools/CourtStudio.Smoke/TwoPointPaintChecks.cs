using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NBA2KCourtCreator.Studio;

internal static partial class Program
{
    private static async Task CheckTwoPointPaintBoundaries(StudioWindow window, string output, Point left, Point right, Point key,
        Point nbaCollege, Point collegeSchool, Func<Point, Color> pixel)
    {
        await window.RestoreProjectAsync(window.CreateProject());
        var before = window.CreateProject();
        var groups = ((StackPanel)window.FindName("LayersHost")).Children.OfType<Expander>().ToArray();
        Assert(groups.Length == 2 && groups[0].Header is TextBlock { Text: "Colors" } && groups[1].Header is TextBlock { Text: "Lines" }, "Colors retains an Outside category or its previous heading.");
        Assert(((StackPanel)groups[0].Content).Children.OfType<Grid>().Last().Tag as string == "stock-outside", "Outside Color is not the final Colors row.");
        var lines = ((StackPanel)groups[1].Content).Children.OfType<Grid>().Select(row => (string)row.Tag).ToArray();
        Assert(Array.IndexOf(lines, "line_center_circle_inner_lowShape") + 1 == Array.IndexOf(lines, "line_center_circle_outer_lowShape"), "Center Circle - Inner is not directly above Outer.");
        window.StoreSampledColor(Colors.Yellow);
        Assert(window.FillCourtRegion(left) == "two-point-left" && window.FillCourtRegion(right) == "two-point-right", "Bucket cannot identify both two-point paint regions.");
        Assert(pixel(nbaCollege) == Colors.Yellow && pixel(collegeSchool) == Colors.Yellow && pixel(key) == Colors.Green, "NBA paint does not fill its enclosure independently of the key.");
        window.SetLayerSettings("NBA_line_three_point_lowShape", visible: false);
        window.SetLayerSettings("high-school-three", visible: true);
        Assert(pixel(left) == Colors.Yellow && pixel(right) == Colors.Yellow && pixel(nbaCollege) == Colors.Red && pixel(collegeSchool) == Colors.Red, "High School paint extends past its enabled three-point line.");
        var school = window.CreateProject();
        await window.UndoAsync(); Assert(!window.LineLayers.Single(line => line.Id == "high-school-three").Visible, "Boundary undo did not restore line visibility.");
        await window.UndoAsync(true); Assert(JsonNode.DeepEquals(school, window.CreateProject()) && pixel(left) == Colors.Yellow && pixel(collegeSchool) == Colors.Red, "Boundary redo lost stored paint or its clipping.");
        window.StoreSampledColor(Colors.Magenta);
        Assert(window.FillCourtRegion(collegeSchool) == "main-court-area", "Click outside the High School arc still selects the old NBA two-point region.");
        Assert(pixel(collegeSchool) == Colors.Magenta && pixel(left) == Colors.Yellow, "Outer region fill leaks inside the enabled three-point line.");
        var png = Path.Combine(output, "two-point-paint-high-school.png");
        await window.ExportToAsync(png, false);
        CheckExportPixel(png, left, Colors.Yellow); CheckExportPixel(png, right, Colors.Yellow);
        CheckExportPixel(png, collegeSchool, Colors.Magenta); CheckExportPixel(png, nbaCollege, Colors.Magenta); CheckExportPixel(png, key, Colors.Green);
        var project = Path.Combine(output, "two-point-paint-high-school.court.json");
        await window.SaveProjectToAsync(project); await window.OpenProjectFromAsync(project);
        Assert(pixel(left) == Colors.Yellow && pixel(collegeSchool) == Colors.Magenta, "Save/reopen did not restore the selected paint boundary.");
        window.SetLayerSettings("college-three", visible: true);
        Assert(pixel(collegeSchool) == Colors.Yellow && pixel(nbaCollege) == Colors.Magenta, "College was not selected as the outermost enabled paint boundary.");
        window.SetLayerSettings("NBA_line_three_point_lowShape", visible: true);
        Assert(pixel(nbaCollege) == Colors.Yellow, "NBA boundary does not expand existing paint.");
        window.SetLayerSettings("NBA_line_three_point_lowShape", visible: false);
        window.SetLayerSettings("college-three", visible: false);
        window.SetTwoPointHardwoodEnabled(false);
        Assert(pixel(left) == Colors.Yellow && pixel(nbaCollege) == Colors.Magenta, "Paint clipping depends on the second hardwood being enabled.");
        window.SetLayerSettings("high-school-three", visible: false);
        Assert(pixel(left) == Colors.Magenta && window.PaintLayers.Single(layer => layer.Id == "two-point-left").Color == "#FFFF00", "No-line state draws two-point paint or forgets its color.");
        await window.RestoreProjectAsync(before);
        Console.WriteLine("PASS two-point paint: NBA/College/High School boundary, both sides, independent key paint, dynamic outer-region hit testing, no lines, hardwood independence, undo/redo, portable reopen and PNG export; Colors/Lines groups with Outside last.");
    }
}
