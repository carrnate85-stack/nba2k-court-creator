using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace NBA2KCourtCreator.Studio;

public partial class LogoImportWindow : Window
{
    public LogoCleanupImage? Cleanup { get; private set; }
    public string? SourcePath { get; private set; }
    public string? PreparedPath { get; private set; }
    internal string? PreparedSourceRevision { get; private set; }
    private string? _sourceRevision;
    private bool _sourceNeedsNormalization;
    private StudioFileSafety.FileIdentity? _preparedIdentity;
    private bool _wand, _busy, _closed, _loading, _preparing;
    private Color _background = Colors.White;
    private long _revision;
    private readonly bool _testing;
    private readonly Func<string, CancellationToken, Task<BitmapSource>> _loadImage;
    private readonly Action<BitmapSource, Stream> _writeImage;
    private readonly SemaphoreSlim _imageDecoder = new(1, 1);
    private CancellationTokenSource? _imageLoadCancellation;
    public LogoImportWindow(Window? owner = null, bool testing = false)
        : this(owner, testing, null, null) { }
    internal LogoImportWindow(Window? owner, bool testing, Func<string, CancellationToken, Task<BitmapSource>>? loadImage, Action<BitmapSource, Stream>? writeImage)
    {
        _testing = testing;
        _loadImage = loadImage ?? StudioImages.LoadLogoAsync;
        _writeImage = writeImage ?? StudioImages.WriteLogoPng;
        InitializeComponent(); Style = (Style)FindResource(typeof(Window)); if (owner is not null) Owner = owner; if (!testing) StudioWindowBounds.Attach(this);
        Closed += (_, _) => { _closed = true; _revision++; _imageLoadCancellation?.Cancel(); if (DialogResult != true) ReleaseTemporaryOutput(); };
    }
    private bool CanEdit => !_closed && !_busy && !_loading && !_preparing;
    private void RefreshControls()
    {
        if (_closed) return;
        ChooseImageButton.IsEnabled = CanEdit;
        CleanupControls.IsEnabled = PlaceButton.IsEnabled = CanEdit && Cleanup is not null;
    }
    public async Task LoadImageAsync(string path)
    {
        if (_closed) throw new ObjectDisposedException(nameof(LogoImportWindow));
        path = Path.GetFullPath(path);
        var revision = ++_revision;
        _imageLoadCancellation?.Cancel(); using var cancellation = new CancellationTokenSource(); _imageLoadCancellation = cancellation;
        _loading = true; RefreshControls();
        try
        {
            await _imageDecoder.WaitAsync(cancellation.Token);
            LogoCleanupImage image; string sourceRevision; bool normalize;
            try
            {
                var bitmap = await _loadImage(path, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                sourceRevision = StudioImages.SourceRevision(bitmap) ?? throw new InvalidDataException("Logo pixels have no retained source revision.");
                normalize = StudioImages.NeedsNormalization(bitmap);
                image = await Task.Run(() => { cancellation.Token.ThrowIfCancellationRequested(); return new LogoCleanupImage(bitmap); }, cancellation.Token);
            }
            finally { _imageDecoder.Release(); }
            if (_closed || revision != _revision) return;
            ReleaseTemporaryOutput();
            if (_closed || revision != _revision) return;
            Cleanup = image; SourcePath = path; _sourceRevision = sourceRevision; _sourceNeedsNormalization = normalize;
            SourceText.Text = Path.GetFileName(path) + $"  /  {image.Width} x {image.Height}";
            _background = image.Pixel(0, 0); if (_background.A == 0) _background = Colors.White;
            EmptyHint.Visibility = Visibility.Collapsed;
            AllMatchingCheck.IsChecked = false; SetWand(false); CleanupStatus.Text = "Existing transparency is preserved."; RefreshPreview();
        }
        catch (Exception) when (_closed || revision != _revision) { }
        finally { if (ReferenceEquals(_imageLoadCancellation, cancellation)) { _imageLoadCancellation = null; _loading = false; RefreshControls(); } }
    }
    private void RefreshPreview()
    {
        if (Cleanup is null) return;
        PreviewImage.Source = Cleanup.Bitmap(); CleanupUndoButton.IsEnabled = Cleanup.CanUndo; ResetButton.IsEnabled = Cleanup.HasChanges;
        BackgroundSwatch.Background = new SolidColorBrush(_background); BackgroundHex.Text = $"#{_background.R:X2}{_background.G:X2}{_background.B:X2}";
    }
    private async void ChooseClick(object sender, RoutedEventArgs e)
    {
        if (!CanEdit) return;
        var file = new OpenFileDialog { Filter = "Logo images|*.dds;*.png;*.jpg;*.jpeg;*.bmp;*.tga;*.gif;*.tif;*.tiff;*.webp" };
        if (file.ShowDialog(this) != true) return;
        try { await LoadImageAsync(file.FileName); }
        catch (Exception error) { if (!_closed) CleanupStatus.Text = error.Message; }
    }
    private void SetWand(bool wand)
    {
        _wand = wand; InspectButton.SetResourceReference(BackgroundProperty, wand ? "PanelRaisedBrush" : "AccentDarkBrush"); WandButton.SetResourceReference(BackgroundProperty, wand ? "AccentDarkBrush" : "PanelRaisedBrush");
        ModeText.Text = wand ? "Remove color" : "Sample color"; PreviewImage.Cursor = Cursors.Cross;
        ToolHint.Text = wand ? "Click a region to remove it. Click enclosed holes separately." : "Click the preview to sample a color.";
    }
    private void InspectClick(object sender, RoutedEventArgs e) { if (CanEdit) SetWand(false); }
    private void WandClick(object sender, RoutedEventArgs e) { if (CanEdit) SetWand(true); }
    private void ToleranceChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (ToleranceText is not null) ToleranceText.Text = ((int)e.NewValue).ToString(); }
    private async void PreviewClick(object sender, MouseButtonEventArgs e)
    {
        if (!CanEdit || Cleanup is not { } cleanup) return;
        var revision = _revision;
        var pixel = LogoCleanupImage.MapPreview(e.GetPosition(PreviewImage), new Size(PreviewImage.ActualWidth, PreviewImage.ActualHeight), new Size(cleanup.Width, cleanup.Height));
        if (pixel is null) return;
        if (!_wand) { _background = cleanup.Pixel((int)pixel.Value.X, (int)pixel.Value.Y); RefreshPreview(); return; }
        var tolerance = (int)ToleranceSlider.Value; var all = AllMatchingCheck.IsChecked == true;
        await Run(async () => { var removed = await Task.Run(() => cleanup.RemoveRegion((int)pixel.Value.X, (int)pixel.Value.Y, tolerance, all)); if (!_closed && revision == _revision && ReferenceEquals(Cleanup, cleanup)) { CleanupStatus.Text = $"Removed {removed:N0} pixels."; RefreshPreview(); } });
    }
    private async void RemoveBackgroundClick(object sender, RoutedEventArgs e)
    {
        if (!CanEdit || Cleanup is not { } cleanup) return; var tolerance = (int)ToleranceSlider.Value; var color = _background; var revision = _revision;
        await Run(async () => { var removed = await Task.Run(() => cleanup.RemoveBackground(color, tolerance)); if (!_closed && revision == _revision && ReferenceEquals(Cleanup, cleanup)) { CleanupStatus.Text = $"Removed {removed:N0} edge pixels."; RefreshPreview(); } });
    }
    private void UndoClick(object sender, RoutedEventArgs e) { if (!CanEdit) return; Cleanup?.Undo(); RefreshPreview(); CleanupStatus.Text = "Cleanup undone."; }
    private void ResetClick(object sender, RoutedEventArgs e) { if (!CanEdit) return; Cleanup?.Reset(); RefreshPreview(); CleanupStatus.Text = "Original pixels restored. Reset can also be undone."; }
    private async void PlaceClick(object sender, RoutedEventArgs e)
    {
        await Run(async () => { if (await PreparePlacementCoreAsync(true) && !_closed) DialogResult = true; });
    }
    public Task<bool> PreparePlacementAsync() => PreparePlacementCoreAsync(false);
    private async Task<bool> PreparePlacementCoreAsync(bool fromRun)
    {
        if (_closed || _loading || _preparing || _busy && !fromRun || Cleanup is null || SourcePath is null || _sourceRevision is null) return false;
        _preparing = true; RefreshControls();
        var revision = _revision; var cleanup = Cleanup; var source = SourcePath; var sourceRevision = _sourceRevision;
        var changed = cleanup.HasChanges; var normalize = _sourceNeedsNormalization;
        var prepared = source;
        StudioFileSafety.FileIdentity? identity = null;
        var pinned = false;
        bool Current() => !_closed && revision == _revision && ReferenceEquals(cleanup, Cleanup);
        try
        {
            if (!changed && await Task.Run(() => StudioImages.FileRevision(source)) != sourceRevision)
                throw new InvalidDataException("The logo changed after its preview. Choose the image again before placing it.");
            if (changed || normalize)
            {
                var pixels = cleanup.Bitmap();
                prepared = Path.Combine(Path.GetTempPath(), "court-logo-" + Guid.NewGuid().ToString("N") + ".png");
                await Task.Run(() =>
                {
                    using var stream = new FileStream(prepared, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    identity = StudioFileSafety.StagingIdentity(stream.SafeFileHandle);
                    _writeImage(pixels, stream); stream.Flush(true);
                });
                if (!Current()) return false;
                StudioFileSafety.EnsureOwnedStaging(prepared, identity!.Value);
                sourceRevision = await Task.Run(() => StudioImages.FileRevision(prepared));
                pinned = true;
                StudioFileSafety.EnsureOwnedStaging(prepared, identity.Value);
            }
            if (!Current()) return false;
            ReleaseTemporaryOutput();
            if (!Current()) return false;
            PreparedPath = prepared; PreparedSourceRevision = sourceRevision; _preparedIdentity = identity; return true;
        }
        catch (Exception) when (!Current()) { return false; }
        finally
        {
            try
            {
                if (prepared != PreparedPath) StudioProjectStore.CleanupStaging(prepared, identity,
                    validateContent: pinned ? () => RequirePreparedRevision(prepared, sourceRevision) : null);
            }
            finally { _preparing = false; RefreshControls(); }
        }
    }
    public void ReleaseTemporaryOutput()
    {
        var prepared = PreparedPath; var identity = _preparedIdentity; var revision = PreparedSourceRevision;
        PreparedPath = null; PreparedSourceRevision = null; _preparedIdentity = null;
        if (prepared is not null) StudioProjectStore.CleanupStaging(prepared, identity,
            validateContent: revision is null ? null : () => RequirePreparedRevision(prepared, revision));
    }
    private static void RequirePreparedRevision(string path, string revision)
    {
        if (StudioImages.FileRevision(path) != revision)
            throw new InvalidDataException("Prepared logo bytes changed; the external file was retained: " + path);
    }
    private async Task Run(Func<Task> action)
    {
        if (!CanEdit) return; _busy = true; var revision = _revision; RefreshControls();
        try { await action(); } catch (Exception error) { if (!_closed && revision == _revision) CleanupStatus.Text = error.Message; }
        finally { _busy = false; RefreshControls(); }
    }
}
