using System.IO;
using System.Windows;
using System.Windows.Input;

namespace NBA2KCourtCreator.Studio;

public partial class StudioWindow
{
    private string _projectName = "Untitled court";
    private bool _renaming;
    public string ProjectName => _projectName;
    public bool RenameProject(string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > 120 || name.Any(char.IsControl)) { SetStatus("Enter a project name of 1–120 characters."); return false; }
        if(!Change(() => _projectName = name))return false;
        UpdateProjectLabel();return true;
    }
    private void UpdateProjectLabel() { ProjectText.Text = _projectName; ProjectTitleButton.ToolTip = _projectName + " — click to rename"; Title = _projectName + " — 2K Court Creator"; }
    private void RenameProjectClick(object sender, RoutedEventArgs e)
    {
        if(!CanChangeDocument)return;
        _renaming = true; ProjectNameInput.Text = _projectName; ProjectTitleButton.Visibility = Visibility.Collapsed; ProjectNameInput.Visibility = Visibility.Visible; ProjectNameInput.Focus(); ProjectNameInput.SelectAll();
    }
    private void FinishRename(bool apply)
    {
        if (!_renaming) return;
        if(apply && !CanChangeDocument)return;
        if (apply && !RenameProject(ProjectNameInput.Text)) { ProjectNameInput.Focus(); ProjectNameInput.SelectAll(); return; }
        _renaming = false; ProjectNameInput.Visibility = Visibility.Collapsed; ProjectTitleButton.Visibility = Visibility.Visible; ProjectTitleButton.Focus();
    }
    private void ProjectNameKeyDown(object sender, KeyEventArgs e) { if (e.Key is Key.Enter or Key.Escape) { FinishRename(e.Key == Key.Enter); e.Handled = true; } }
    private void ProjectNameLostFocus(object sender, KeyboardFocusChangedEventArgs e) { if (_renaming) FinishRename(true); }
    private string SuggestedProjectFileName() { var invalid = Path.GetInvalidFileNameChars(); var safe = new string(_projectName.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).TrimEnd('.', ' '); return (string.IsNullOrWhiteSpace(safe) ? "My Court" : safe) + ".court.json"; }
    private void ImportSectionClick(object sender, RoutedEventArgs e) => SwitchSection("import");
}
