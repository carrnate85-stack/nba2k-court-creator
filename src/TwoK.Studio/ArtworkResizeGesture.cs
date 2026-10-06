using System.Windows;

namespace TwoK.Studio;

/// <summary>Opposite-corner anchored resize, with continuous modifier changes.</summary>
public sealed class ArtworkResizeGesture
{
    private ArtworkState _segment;
    private Point _pointer;
    private readonly int _sx, _sy;
    private bool _locked;
    public ArtworkResizeGesture(ArtworkState state, Point pointer, int corner, bool locked)
    { _segment=state; _pointer=pointer; _locked=locked; _sx=corner is 0 or 3 ? -1:1; _sy=corner is 0 or 1 ? -1:1; }
    public ArtworkState Update(Point pointer, bool locked, ArtworkLayer current) => Update(pointer, locked, locked != _locked ? current.Capture() : _segment);
    public ArtworkState Update(Point pointer, bool locked, ArtworkState current)
    {
        // Rebase only the working segment. The canvas keeps the original state for one undo action.
        if(locked!=_locked){_segment=current;_pointer=pointer;_locked=locked;return current;}
        var delta=TransformGeometry.Rotate(pointer-_pointer,-_segment.Rotation);
        var width=Math.Clamp(_segment.Width+delta.X*_sx,1,32768);
        var height=Math.Clamp(_segment.Height+delta.Y*_sy,1,32768);
        if(locked)
        {
            var ratio=Math.Abs(delta.X/_segment.Width)>=Math.Abs(delta.Y/_segment.Height)?width/_segment.Width:height/_segment.Height;
            var size=TransformGeometry.ScaleDimensions(_segment.Width,_segment.Height,ratio);
            width=size.Width;height=size.Height;
        }
        var oldCenter=new Point(_segment.X+_segment.Width/2,_segment.Y+_segment.Height/2);
        var anchor=oldCenter+TransformGeometry.Rotate(new Vector(-_sx*_segment.Width/2,-_sy*_segment.Height/2),_segment.Rotation);
        var center=anchor+TransformGeometry.Rotate(new Vector(_sx*width/2,_sy*height/2),_segment.Rotation);
        return new ArtworkState(center.X-width/2,center.Y-height/2,width,height,_segment.Rotation);
    }
}
