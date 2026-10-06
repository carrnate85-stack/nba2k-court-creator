using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using TwoK.Studio;

namespace NBA2KCourtCreator.Studio;

public partial class StudioWindow
{
    private JsonObject? _logoOpacityBefore;
    private ArtworkLayer? _logoOpacityLayer;
    private double _logoOpacityStartValue;
    private bool BeginLogoOpacity()
    {
        if(!CanChangeDocument || CourtCanvas.SelectedLayer is not {} logo || !CourtCanvas.Layers.Contains(logo))return false;
        if(_logoOpacityBefore is not null)return ReferenceEquals(logo,_logoOpacityLayer);
        CourtCanvas.CancelGesture();
        if(!CanChangeDocument || !ReferenceEquals(logo,CourtCanvas.SelectedLayer) || !CourtCanvas.Layers.Contains(logo))return false;
        _logoOpacityBefore=CreateProject();_logoOpacityLayer=logo;_logoOpacityStartValue=logo.Opacity;
        return true;
    }
    private void FinishLogoOpacity()
    {
        var before=_logoOpacityBefore;var layer=_logoOpacityLayer;
        _logoOpacityBefore=null;_logoOpacityLayer=null;
        if(before is not null && layer is not null && CourtCanvas.Layers.Contains(layer) && layer.Opacity!=_logoOpacityStartValue){RecordUndo(before);Changed();}
    }
    private void RefreshLogoOpacityValue()
    {
        var syncing=_syncing;_syncing=true;
        try{var value=CourtCanvas.SelectedLayer?.Opacity ?? 100;LogoOpacitySlider.Value=value;LogoOpacityValue.Text=$"{value:0}%";}
        finally{_syncing=syncing;}
    }
    private void LogoOpacityStart(object sender,MouseButtonEventArgs e)=>BeginLogoOpacity();
    private void LogoOpacityEnd(object sender,RoutedEventArgs e)=>FinishLogoOpacity();
    private static bool IsOpacityKey(Key key)=>key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown;
    private void LogoOpacityKeyDown(object sender,KeyEventArgs e)
    {
        if(IsOpacityKey(e.Key))BeginLogoOpacity();
        else if(e.Key==Key.Escape && CanChangeDocument && _logoOpacityLayer is {} layer && ReferenceEquals(layer,CourtCanvas.SelectedLayer) && CourtCanvas.Layers.Contains(layer))
        {
            layer.Opacity=_logoOpacityStartValue;_logoOpacityBefore=null;_logoOpacityLayer=null;
            RefreshLogoOpacityValue();
            e.Handled=true;
        }
    }
    private void LogoOpacityKeyUp(object sender,KeyEventArgs e){if(IsOpacityKey(e.Key))FinishLogoOpacity();}
    private void LogoOpacityChanged(object sender,RoutedPropertyChangedEventArgs<double> e)
    {
        if(_syncing)return;
        if(!ReferenceEquals(sender,LogoOpacitySlider) || e.NewValue!=LogoOpacitySlider.Value || !CanChangeDocument || CourtCanvas.SelectedLayer is not {} logo || !CourtCanvas.Layers.Contains(logo))
        {RefreshLogoOpacityValue();return;}
        LogoOpacityValue.Text=$"{e.NewValue:0}%";
        if(logo.Opacity==e.NewValue)return;
        var discrete=_logoOpacityBefore is null;
        if(!BeginLogoOpacity()){RefreshLogoOpacityValue();return;}
        _logoOpacityLayer!.Opacity=e.NewValue;
        if(discrete)FinishLogoOpacity();
    }
}
