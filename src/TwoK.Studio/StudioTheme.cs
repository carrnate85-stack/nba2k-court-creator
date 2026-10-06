using System.Windows;

namespace TwoK.Studio;

/// <summary>Canvas owns the palette; these aliases retain the court shell's resource names.</summary>
public static class StudioTheme
{
    public static bool IsDark => TextureStudio.StudioTheme.IsDark;

    public static void Apply(bool dark)
    {
        TextureStudio.StudioTheme.Apply(dark);
        var resources = Application.Current.Resources;
        resources["TopChromeBrush"] = resources["HeaderBrush"];
        resources["DocumentChromeBrush"] = resources["DocumentBarBrush"];
        resources["ChromeDividerBrush"] = resources["HeaderDividerBrush"];
    }
}
