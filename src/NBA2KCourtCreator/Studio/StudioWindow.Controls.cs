using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using TwoK.Studio;

namespace NBA2KCourtCreator.Studio;

public partial class StudioWindow
{
    private sealed record LayerRowControls(Grid Row, CheckBox Toggle, StackPanel Colors, Border Swatch, TextBox Hex, bool Alternate, Func<bool> CommitHex);
    private readonly Dictionary<string, LayerRowControls> _layerRows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LayerRowControls> _pendingLayerHex = new(StringComparer.Ordinal);
    private bool _layerHexFramePending;

    private void QueueLayerHex(StockLayer layer, LayerRowControls controls)
    {
        if (controls.Hex.Text == layer.Color) return;
        _pendingLayerHex[layer.Id] = controls;
        if (_layerHexFramePending) return;
        _layerHexFramePending = true;
        CompositionTarget.Rendering += LayerHexFrame;
    }
    private void LayerHexFrame(object? sender, EventArgs e) => FlushLayerHex();
    private void CancelLayerHex()
    {
        if (_layerHexFramePending) CompositionTarget.Rendering -= LayerHexFrame;
        _layerHexFramePending = false; _pendingLayerHex.Clear();
    }
    private void FlushLayerHex()
    {
        var pending = _pendingLayerHex.ToArray(); CancelLayerHex();
        if (_closed) return;
        var syncing = _syncing; _syncing = true;
        try
        {
            foreach (var (id, controls) in pending)
                if (_layerRows.TryGetValue(id, out var current) && ReferenceEquals(controls, current)
                    && _paints.Concat(_lines).Append(_outside).FirstOrDefault(layer => layer.Id == id) is { } layer)
                    controls.Hex.Text = layer.Color;
        }
        finally { _syncing = syncing; }
    }

    private void RefreshLayerSelection()
    {
        foreach (var (id, controls) in _layerRows)
            controls.Row.SetResourceReference(Panel.BackgroundProperty, id == _colorLayerId ? "AccentDarkBrush" : controls.Alternate ? "PanelRaisedBrush" : "PanelBrush");
    }
    private void RefreshLayerRow(StockLayer layer)
    {
        if (!_layerRows.TryGetValue(layer.Id, out var controls)) return;
        var syncing = _syncing;
        _syncing = true;
        try
        {
            controls.Toggle.IsChecked = layer.Visible;
            controls.Colors.Visibility = layer.Visible ? Visibility.Visible : Visibility.Collapsed;
            QueueLayerHex(layer, controls);
            controls.Swatch.Background = StudioImages.Brush(layer.Color);
        }
        finally { _syncing = syncing; }
    }
    private void RebuildLayerRows()
    {
        CancelLayerHex(); _layerRows.Clear(); LayersHost.Children.Clear(); if (!_initialized) return;
        var query = "";
        foreach (var (name, layers) in new[] { ("Paint Colors", _paints.AsEnumerable()), ("Lines", _lines.AsEnumerable()), ("Outside", new[] { _outside }.AsEnumerable()) })
        {
            var filtered = layers.Where(layer => layer.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (name == "Lines") filtered = filtered.OrderBy(layer => layer.Id switch { "NBA_line_three_point_lowShape" => -30, "college-three" => -20, "high-school-three" => -10, _ => _lines.IndexOf(layer) }).ToArray();
            if (filtered.Length == 0) continue;
            var rows = new StackPanel();
            var header = new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 9, 6, 9) };
            var accordion = new Expander { Header = header, Content = rows, IsExpanded = !_collapsed.Contains(name) };
            accordion.SetResourceReference(Control.BackgroundProperty, "PanelBrush");
            accordion.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            accordion.Expanded += (_, _) => _collapsed.Remove(name); accordion.Collapsed += (_, _) => _collapsed.Add(name);
            LayersHost.Children.Add(accordion);
            foreach (var layer in filtered)
            {
                var row = new Grid { MinHeight = 34, Tag = layer.Id, Margin = new Thickness(0, 0, 0, 1) };
                var alternate = rows.Children.Count % 2 == 0;
                row.SetResourceReference(Panel.BackgroundProperty, layer.Id == _colorLayerId ? "AccentDarkBrush" : alternate ? "PanelRaisedBrush" : "PanelBrush");
                row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(186) });
                var layerName = new TextBlock { Text = layer.Name, ToolTip = layer.Name, FontSize = 11, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 3, 3, 3) };
                row.Children.Add(layerName);
                bool CurrentRow() => !_syncing && _layerRows.TryGetValue(layer.Id, out var current) && ReferenceEquals(current.Row, row);
                void SelectRow()
                {
                    if (!CurrentRow()) return;
                    _colorLayerId = layer.Id;
                    RefreshLayerSelection();
                    RefreshSelectedColor(); RefreshToolState();
                }
                row.PreviewMouseDown += (_, _) => SelectRow();
                row.GotKeyboardFocus += (_, _) => SelectRow();
                var toggle = new CheckBox { Style = (Style)FindResource("StudioToggle"), IsChecked = layer.Visible, ToolTip = "Toggle " + layer.Name };
                Grid.SetColumn(toggle, 1); row.Children.Add(toggle);
                void Toggle() { if (CurrentRow()) SetLayerSettings(layer.Id, visible: toggle.IsChecked == true); }
                toggle.Checked += (_, _) => Toggle(); toggle.Unchecked += (_, _) => Toggle();
                var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
                    Visibility = layer.Visible ? Visibility.Visible : Visibility.Collapsed };
                var swatch = new Border { Width = 16, Height = 16, Background = StudioImages.Brush(layer.Color), BorderBrush = (Brush)FindResource("StrongBorderBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3) };
                var color = new Button { Content = swatch, Tag = "PickColor:" + layer.Id, Width = 24, MinHeight = 26, Padding = new Thickness(3), Margin = new Thickness(0), ToolTip = "Pick " + layer.Name + " color" };
                System.Windows.Automation.AutomationProperties.SetName(color, "Pick " + layer.Name + " color");
                color.Click += (_, _) => PickLayerColor(layer, false, CurrentRow);
                var hex = new TextBox { Text = layer.Color, Tag = "Color:" + layer.Id, Width = 74, MinHeight = 26, FontSize = 10, Padding = new Thickness(4, 3, 4, 3), Margin = new Thickness(3, 0, 3, 0), ToolTip = "Hex color" };
                bool ApplyHex(bool strict=false)
                {
                    if (!CurrentRow() || !layer.Visible || !CanChangeDocument) return false;
                    if (_pendingLayerHex.ContainsKey(layer.Id)) FlushLayerHex();
                    var value = StudioImages.Hex(hex.Text);
                    if (value is null) { if(!strict)hex.Text = layer.Color; SetStatus("Enter a valid three- or six-digit hex code."); return false; }
                    hex.Text = value; SetLayerSettings(layer.Id, color: value);
                    return CanChangeDocument && CurrentRow();
                }
                hex.TextChanged += (_, _) => { if (CurrentRow()) { _pendingLayerHex.Remove(layer.Id); if (_pendingLayerHex.Count == 0) CancelLayerHex(); } };
                hex.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ApplyHex(); e.Handled = true; } }; hex.LostKeyboardFocus += (_, _) => ApplyHex();
                var teamColors = new Button { Content = "Team Colors", Tag = "TeamColors:" + layer.Id, Width = 74, MinHeight = 26, FontSize = 10, Padding = new Thickness(3), Margin = new Thickness(0, 0, 4, 0), ToolTip = "Choose " + layer.Name + " color from a team palette" };
                System.Windows.Automation.AutomationProperties.SetName(teamColors, "Team colors for " + layer.Name);
                teamColors.Click += (_, _) => PickLayerColor(layer, true, CurrentRow);
                controls.Children.Add(color); controls.Children.Add(hex); controls.Children.Add(teamColors); Grid.SetColumn(controls, 2); row.Children.Add(controls);
                row.MouseDown += (_, e) => { if (CurrentRow() && e.ChangedButton == MouseButton.Left && e.ClickCount == 2
                    && e.OriginalSource is DependencyObject source && (ReferenceEquals(source, layerName) || layerName.IsAncestorOf(source)))
                    { SetLayerSettings(layer.Id, visible: !layer.Visible); e.Handled = true; } };
                _layerRows.Add(layer.Id, new LayerRowControls(row, toggle, controls, swatch, hex, alternate, ()=>ApplyHex(true)));
                rows.Children.Add(row);
            }
        }
        RefreshSelectedColor();
    }
    private long _logoInspectorRevision;
    private readonly Dictionary<TextBox,Func<bool>> _logoInlineCommits=[];
    private void RefreshLogoInspector()
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            FinishLogoOpacity();
            _refreshLiveLogoFields=null;
            _logoInlineCommits.Clear();
            var revision = ++_logoInspectorRevision;
            var logo = CourtCanvas.SelectedLayer; LogoList.SelectedItem = logo; LogoProperties.Children.Clear();
            LogoOpacitySlider.IsEnabled=logo is not null; LogoOpacitySlider.Value=logo?.Opacity ?? 100; LogoOpacityValue.Text=$"{logo?.Opacity ?? 100:0}%";
            if (logo is null)
            { LogoProperties.Children.Add(new TextBlock { Text = "Import a logo to place and edit it on the court.", FontSize = 11, Margin = new Thickness(4), TextWrapping = TextWrapping.Wrap }); RefreshToolState(); return; }
            bool CurrentInspector() => revision == _logoInspectorRevision && ReferenceEquals(logo, CourtCanvas.SelectedLayer) && CourtCanvas.Layers.Contains(logo);
            var numericFields = new List<(TextBox Input, Func<double> Get)>();
            var displays = new Dictionary<TextBox,string>();
            TextBox Field(Panel parent, string label, Func<double> getter, Action<double> setter)
            {
                var stack = new StackPanel { Margin = new Thickness(0, 0, 6, 0) };
                stack.Children.Add(new TextBlock { Text = label, FontSize = 11, Margin = new Thickness(0,0,0,4) });
                var input = new TextBox { Text = getter().ToString("0.##", CultureInfo.InvariantCulture), Tag = label, FontSize = 11, MinHeight = 28, Padding = new Thickness(6,4,6,4) };
                displays[input]=input.Text; stack.Children.Add(input); parent.Children.Add(stack); numericFields.Add((input,getter));
                bool Apply(bool strict=false)
                {
                    if (!CanChangeDocument || !CurrentInspector()) return false;
                    // Formatting is presentation only: unchanged rounded text must never overwrite stored precision.
                    if(input.Text==displays[input])return true;
                    if (!double.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number) || (label is "X" or "Y" && Math.Abs(number)>1_000_000))
                    { if(!strict)input.Text = displays[input]; return false; }
                    if(number!=getter() && !Change(()=>{if(CurrentInspector())setter(number);}))return false;
                    if (!CurrentInspector() || !CanChangeDocument) return false;
                    foreach(var field in numericFields){field.Input.Text=field.Get().ToString("0.##",CultureInfo.InvariantCulture);displays[field.Input]=field.Input.Text;}
                    return true;
                }
                _logoInlineCommits.Add(input,()=>Apply(true));
                input.LostKeyboardFocus += (_,_)=>Apply();input.KeyDown+=(_,e)=>{if(!CanChangeDocument || !CurrentInspector())return;if(e.Key==Key.Enter){Apply();e.Handled=true;}else if(e.Key==Key.Escape){input.Text=displays[input];e.Handled=true;}};
                return input;
            }
            System.Windows.Controls.Primitives.UniformGrid Group(string title,int columns=2)
            {
                var group=new StackPanel { Margin=new Thickness(4,0,0,10) };
                group.Children.Add(new Border { Height=1, Margin=new Thickness(0,0,4,8), Background=(Brush)FindResource("BorderBrush") });
                group.Children.Add(new TextBlock { Text=title, FontSize=13,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,6) });
                var fields=new System.Windows.Controls.Primitives.UniformGrid { Columns=columns };group.Children.Add(fields);LogoProperties.Children.Add(group);return fields;
            }
            var position=Group("Position");Field(position,"X",()=>logo.X,value=>logo.X=value);Field(position,"Y",()=>logo.Y,value=>logo.Y=value);
            void Resize(double value,bool width)
            {
                if(!logo.ScaleLocked){if(width)logo.Width=value;else logo.Height=value;return;}
                var requestedScale=value/(width?logo.Width:logo.Height);
                var scale=TransformGeometry.ConstrainScale(logo.Width,logo.Height,requestedScale);
                var dimensions=TransformGeometry.ScaleDimensions(logo.Width,logo.Height,scale);
                var newWidth=width && scale==requestedScale?value:dimensions.Width;
                var newHeight=!width && scale==requestedScale?value:dimensions.Height;
                logo.Width=newWidth;logo.Height=newHeight;
            }
            var size=Group("Size");Field(size,"Width",()=>logo.Width,value=>Resize(value,true));Field(size,"Height",()=>logo.Height,value=>Resize(value,false));
            _refreshLiveLogoFields=()=>
            {
                if(!CurrentInspector())return;
                foreach(var field in numericFields)
                {
                    if(field.Input.Tag is not string key || key is not ("X" or "Y" or "Width" or "Height") || field.Input.IsKeyboardFocusWithin)continue;
                    var text=field.Get().ToString("0.##",CultureInfo.InvariantCulture);
                    if(field.Input.Text!=text)field.Input.Text=text;
                    displays[field.Input]=text;
                }
            };
            var lockRow=new StackPanel { Orientation=Orientation.Horizontal,Margin=new Thickness(4,-2,0,10) };
            var lockButton=new Button { Style=(Style)FindResource("CanvasActionButton"),Width=28,Height=28,Padding=new Thickness(6),ToolTip="Aspect ratio: proportional by default. Hold Shift to temporarily invert the lock." };
            var lockIcon=new System.Windows.Shapes.Path { Stroke=(Brush)FindResource("TextBrush"),StrokeThickness=1.5,Data=Geometry.Parse("M4,10 H18 V21 H4 Z M7,10 V6 A4,4 0 0 1 15,6 V10") };
            lockButton.Content=new Viewbox { Width=16,Height=16,Child=lockIcon };
            var lockText=new TextBlock { Text=logo.ScaleLocked?"Aspect ratio locked":"Free stretch",FontSize=11,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(6,0,0,0) };
            lockButton.SetResourceReference(Control.BackgroundProperty,logo.ScaleLocked?"AccentDarkBrush":"PanelBrush");
            lockButton.Click+=(_,_)=>{if(!CanChangeDocument || !CurrentInspector())return;Change(()=>{if(CurrentInspector())logo.ScaleLocked=!logo.ScaleLocked;});if(CurrentInspector())RefreshLogoInspector();};lockRow.Children.Add(lockButton);lockRow.Children.Add(lockText);LogoProperties.Children.Add(lockRow);
            var appearance=Group("Appearance");Field(appearance,"Rotation (°)",()=>logo.Rotation,value=>logo.Rotation=value);
            var anchorRow=new Grid { Margin=new Thickness(4,0,0,6) };anchorRow.ColumnDefinitions.Add(new ColumnDefinition());anchorRow.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
            var anchor=new ComboBox { ItemsSource=CourtCanvas.Anchors,DisplayMemberPath="Name",SelectedIndex=0,FontSize=11,MinHeight=28,Margin=new Thickness(0,0,6,0),ToolTip="Choose alignment anchor" };
            var align=new Button { Content="Align",Style=(Style)FindResource("InspectorAction"),ToolTip="Center selected logo at this anchor" };Grid.SetColumn(align,1);anchorRow.Children.Add(anchor);anchorRow.Children.Add(align);
            align.Click+=(_,_)=>{if(!CanChangeDocument || !CurrentInspector())return;if(anchor.SelectedItem is StudioAnchor target)Change(()=>{if(!CurrentInspector())return;logo.X=target.Position.X-logo.Width/2;logo.Y=target.Position.Y-logo.Height/2;});if(CurrentInspector())RefreshLogoInspector();};LogoProperties.Children.Add(anchorRow);
            RefreshToolState();
        }
        finally { _syncing=false; }
    }

    public async Task AddLogoAsync(string path, string? displayName = null)
    {
        await TryAddLogoAsync(path, displayName);
    }
    public void DeleteSelectedLogo()
    {
        if(!CanChangeDocument || CourtCanvas.SelectedLayer is not { } logo || !CourtCanvas.Layers.Contains(logo))return;
        if(Change(()=>{if(CourtCanvas.Layers.Remove(logo))CourtCanvas.SelectedLayer=CourtCanvas.Layers.LastOrDefault();}))RefreshLogoInspector();
    }
    private void DuplicateLogo(bool? x)
    {
        if (!CanChangeDocument || CourtCanvas.SelectedLayer is not { } logo || !CourtCanvas.Layers.Contains(logo) || CourtCanvas.Layers.Count + PendingLogoImports >= 4) return;
        CourtCanvas.CancelGesture();
        if(!CanChangeDocument || !CourtCanvas.Layers.Contains(logo))return;
        var center = CourtCanvas.Anchors.FirstOrDefault(anchor => anchor.Id == "court-center")?.Position ?? new Point(4096, 2048);
        var copy = new ArtworkLayer { Name = logo.Name + " Copy", Path = logo.Path, Image = logo.Image, X = x is null ? logo.X + 40 : x == true ? logo.X : center.X * 2 - logo.X - logo.Width,
            Y = x is null ? logo.Y + 40 : x == true ? center.Y * 2 - logo.Y - logo.Height : logo.Y, Width = logo.Width, Height = logo.Height, Rotation = logo.Rotation, Opacity = logo.Opacity,
            Visible = logo.Visible, ScaleLocked = logo.ScaleLocked, FlipX = logo.FlipX, FlipY = logo.FlipY };
        if(Change(() => { CourtCanvas.Layers.Add(copy); if (_logoArtwork.TryGetValue(logo.Id, out var artwork)) _logoArtwork[copy.Id] = (JsonObject)artwork.DeepClone(); }) && CourtCanvas.Layers.Contains(copy))
        {CourtCanvas.SelectedLayer=copy;RefreshLogoInspector();}
    }
    private void ReorderLogo(int direction)
    {
        if(!CanChangeDocument || CourtCanvas.SelectedLayer is not { } logo || !CourtCanvas.Layers.Contains(logo))return;
        var destination=CourtCanvas.Layers.IndexOf(logo)+direction;
        if(destination<0 || destination>=CourtCanvas.Layers.Count)return;
        if(Change(()=>
        {
            var index=CourtCanvas.Layers.IndexOf(logo);var target=index+direction;
            if(index>=0 && target>=0 && target<CourtCanvas.Layers.Count)CourtCanvas.Layers.Move(index,target);
        }))RefreshToolState();
    }

    private bool _catalogBusy;
    private async void CatalogClick(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (!_ready || _catalogBusy || PendingLogoImports > 0) return;
        _catalogBusy = true;
        RefreshToolState(); RefreshMutationState();
        try
        {
        var catalog = new FloorCatalogWindow(this, _floors, _floor, _favorites, _recent);
        if (catalog.ShowDialog() == true && catalog.SelectedFloor is not null)
        { var before = CreateProject(); await SelectFloorAsync(catalog.SelectedFloor); RecordUndo(before); Changed(); }
        else if (catalog.AddRequested)
        {
            var dialog = new OpenFileDialog { Filter = "Floor image|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff" };
            if (dialog.ShowDialog(this) != true) return;
            var response = await _engine.RequestAsync(["add-stock-floor", "--source", dialog.FileName]);
            var floor = StockFloor.Read(response["image"]!.AsObject(), _engine.ProjectRoot); _floors.Add(floor);
            var before = CreateProject(); await SelectFloorAsync(floor); RecordUndo(before); Changed();
        }
        SavePreferences();
        }
        finally { _catalogBusy = false; RefreshToolState(); RefreshMutationState(); }
    });
    private void SectionClick(object sender, RoutedEventArgs e) => SwitchSection((string)((Button)sender).Tag);

    private void LogoSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if(_syncing)return;
        if(Keyboard.FocusedElement is CheckBox { Tag: ArtworkLayer })
        { _syncing=true;try{LogoList.SelectedItem=CourtCanvas.SelectedLayer;}finally{_syncing=false;}return; }
        CourtCanvas.SelectedLayer=LogoList.SelectedItem as ArtworkLayer;
    }
    private void LogoVisibilityClick(object sender,RoutedEventArgs e)
    { if(sender is CheckBox {Tag:ArtworkLayer logo} eye){CourtCanvas.CancelGesture();Change(()=>logo.Visible=eye.IsChecked==true);e.Handled=true;} }
    private async void ImportLogoClick(object sender, RoutedEventArgs e) => await OpenLogoImporterAsync();
    private void CopyXClick(object sender, RoutedEventArgs e) => DuplicateLogo(true);
    private void CopyYClick(object sender, RoutedEventArgs e) => DuplicateLogo(false);
    private void ForwardClick(object sender, RoutedEventArgs e) => ReorderLogo(1);
    private void BackwardClick(object sender, RoutedEventArgs e) => ReorderLogo(-1);
    private void DeleteLogoClick(object sender, RoutedEventArgs e) => DeleteSelectedLogo();
    private async void NewClick(object sender, RoutedEventArgs e) => await Guard(async () => { if (ConfirmReplace()) await NewProjectAsync(); });
    private async void OpenClick(object sender, RoutedEventArgs e) => await Guard(async () => { if (!ConfirmReplace()) return; var dialog = new OpenFileDialog { Filter = "Court project|*.json" }; if (dialog.ShowDialog(this) != true) return; await OpenProjectFromAsync(dialog.FileName); });
    private async void SaveClick(object sender, RoutedEventArgs e) => await SaveProjectFromUiAsync(false);
    private async void SaveAsClick(object sender, RoutedEventArgs e) => await SaveProjectFromUiAsync(true);
    private async Task SaveProjectFromUiAsync(bool saveAs) { try { await SaveProjectAsync(saveAs); } catch (Exception error) { if (!_closed) SetStatus("Save failed: " + error.Message); } }
    private async Task SaveProjectAsync(bool saveAs=false)
    {
        CommitPendingDocumentInput();
        var path = saveAs?null:_projectPath;
        if (path is null) { var dialog = new SaveFileDialog { FileName = SuggestedProjectFileName(), Filter = "Court project|*.json" }; if (dialog.ShowDialog(this) != true) return; path = dialog.FileName; }
        await SaveProjectToAsync(path);
    }
    private bool ConfirmReplace() => !_dirty || MessageBox.Show(this, "The current court has unsaved changes. Continue? A recovery copy is kept locally.", "Court Creator", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    private async void RefreshClick(object sender, RoutedEventArgs e) => await Guard(async () => { if (_section == "import") await RefreshImportAsync(); else { await SelectFloorAsync(_floor); SetStatus("Court refreshed."); } });
    private void ThemeClick(object sender, RoutedEventArgs e) { StudioTheme.Apply(!StudioTheme.IsDark); SwitchSection(_section); RebuildLayerRows(); RefreshLogoInspector(); SavePreferences(); }
    private void ViewClick(object sender, RoutedEventArgs e) { CourtCanvas.Viewport = (string)((FrameworkElement)sender).Tag switch { "left" => new Rect(0, 0, 4096, 4096), "right" => new Rect(4096, 0, 4096, 4096), _ => null }; CourtCanvas.Fit(); }
    private void FitClick(object sender, RoutedEventArgs e) => CourtCanvas.Fit();
    private void ActualSizeClick(object sender, RoutedEventArgs e) => CourtCanvas.ActualSize();
    private void ZoomMenuClick(object sender, RoutedEventArgs e) { ZoomMenuButton.ContextMenu.PlacementTarget = ZoomMenuButton; ZoomMenuButton.ContextMenu.IsOpen = true; }
    private void LogoMoreClick(object sender, RoutedEventArgs e) { LogoMoreButton.ContextMenu.PlacementTarget = LogoMoreButton; LogoMoreButton.ContextMenu.IsOpen = true; }
    private bool CurrentLogoNameInput(TextBox input) => CanChangeDocument && input.Tag is ArtworkLayer logo && CourtCanvas.Layers.Contains(logo) && input.IsDescendantOf(LogoList) && ReferenceEquals(input.DataContext,logo);
    private void LogoNameFocus(object sender, KeyboardFocusChangedEventArgs e) { var input=(TextBox)sender; if(!CurrentLogoNameInput(input))return; CourtCanvas.SelectedLayer=(ArtworkLayer)input.Tag; input.SelectAll(); }
    private void LogoNameKeyDown(object sender, KeyEventArgs e) { var input=(TextBox)sender; if(!CurrentLogoNameInput(input))return; var logo=(ArtworkLayer)input.Tag; if(e.Key==Key.Escape){input.Text=logo.Name;Keyboard.ClearFocus();e.Handled=true;}else if(e.Key==Key.Enter){ApplyLogoName(input);Keyboard.ClearFocus();e.Handled=true;} }
    private void LogoNameLostFocus(object sender, KeyboardFocusChangedEventArgs e) => ApplyLogoName((TextBox)sender);
    private bool ApplyLogoName(TextBox input,bool strict=false)
    {
        if(!CurrentLogoNameInput(input))return false;var logo=(ArtworkLayer)input.Tag;var value=input.Text.Trim();
        if(value==logo.Name)return true;
        if(value.Length is 0 or >120 || value.Any(char.IsControl)){if(!strict)input.Text=logo.Name;return false;}
        return Change(()=>{if(CurrentLogoNameInput(input) && ReferenceEquals(input.Tag,logo))logo.Name=value;}) && CurrentLogoNameInput(input) && ReferenceEquals(input.Tag,logo);
    }
    private void DuplicateLogoClick(object sender,RoutedEventArgs e) => DuplicateLogo(null);
    private void ZoomOutClick(object sender, RoutedEventArgs e) => CourtCanvas.ChangeZoom(1 / 1.25);
    private void ZoomInClick(object sender, RoutedEventArgs e) => CourtCanvas.ChangeZoom(1.25);
    private async void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (!CanChangeDocument) return;
        if (SaveShortcut(e.Key,Keyboard.Modifiers) is {} saveAs) { if(saveAs)SaveAsClick(sender,e);else SaveClick(sender,e); e.Handled = true; return; }
        if (e.OriginalSource is TextBox || e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase) return;
        if (HistoryShortcut(e.Key,Keyboard.Modifiers) is {} redo) { await Guard(() => UndoAsync(redo)); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N) { NewClick(sender, e); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O) { OpenClick(sender, e); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.D0) { CourtCanvas.Fit(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.D1) { CourtCanvas.ActualSize(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.OemPlus or Key.Add) { ZoomInClick(sender,e); e.Handled=true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.OemMinus or Key.Subtract) { ZoomOutClick(sender,e); e.Handled=true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.T && CourtCanvas.SelectedLayer is not null) { SelectCanvasTool(ArtworkTool.Transform); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.None && e.Key is Key.V or Key.H or Key.Z or Key.I) { SelectCanvasTool(e.Key switch { Key.H => ArtworkTool.Hand, Key.Z => ArtworkTool.Zoom, Key.I => ArtworkTool.Eyedropper, _ => ArtworkTool.Move }); e.Handled = true; }
    }
    private static bool? HistoryShortcut(Key key,ModifierKeys modifiers) => modifiers==ModifierKeys.Control && key==Key.Z?false:
        (modifiers==ModifierKeys.Control && key==Key.Y) || (modifiers==(ModifierKeys.Control|ModifierKeys.Shift) && key==Key.Z)?true:null;
    private static bool? SaveShortcut(Key key,ModifierKeys modifiers) => key!=Key.S?null:modifiers==ModifierKeys.Control?false:modifiers==(ModifierKeys.Control|ModifierKeys.Shift)?true:null;
}
