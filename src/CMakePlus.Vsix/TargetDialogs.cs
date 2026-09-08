using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using CMakePlus.Core;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace CMakePlus;

internal sealed class NewTargetDialog : DialogWindow
{
    private readonly TextBox name = new TextBox { Text = "my_target" };
    private readonly TextBox directory = new TextBox { Text = "my_target" };
    private readonly TextBox parent = new TextBox { Text = "CMakeLists.txt" };
    private readonly ComboBox kind = new ComboBox { ItemsSource = new[] { "Application (EXECUTABLE)", "Static Library (STATIC)", "Shared Library (SHARED)" }, SelectedIndex = 0 };
    public string TargetName => name.Text.Trim();
    public string RelativeDirectory => directory.Text.Trim();
    public string ParentScript => parent.Text.Trim();
    public string Kind => new[] { "EXECUTABLE", "STATIC", "SHARED" }[kind.SelectedIndex];
    public NewTargetDialog(Func<NewTargetDialog, Task> validate)
    {
        Theme.Apply(this);
        Title = "New CMake Target"; Width = 550; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(18) };
        Add(panel, "Target name", name); Add(panel, "Target type", kind);
        Add(panel, "New directory (relative to source root; must not exist)", directory);
        Add(panel, "Parent CMakeLists.txt (relative to source root)", parent);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), Visibility = Visibility.Collapsed };
        panel.Children.Add(error);
        var ok = new Button { Content = "Preview Changes", IsDefault = true, Padding = new Thickness(12, 5, 12, 5), HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (s, e) => ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            ok.IsEnabled = false;
            try { await validate(this); if (IsVisible) DialogResult = true; }
            catch (Exception ex) { error.Text = ex.Message; error.Visibility = Visibility.Visible; }
            finally { ok.IsEnabled = true; }
        }).FileAndForget("CMakePlus/PreviewNewTarget");
        panel.Children.Add(ok); Content = panel;
    }
    private static void Add(Panel panel, string label, Control field)
    {
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }); field.Margin = new Thickness(0, 5, 0, 12); panel.Children.Add(field);
    }
}

internal sealed class RemoveSourcesDialog : DialogWindow
{
    private readonly ListBox files = new ListBox { SelectionMode = SelectionMode.Extended };
    public string[] SelectedPaths => files.SelectedItems.Cast<string>().ToArray();
    public RemoveSourcesDialog(CMakeTarget target, string root)
    {
        Theme.Apply(this);
        Title = "Remove source references from " + target.Name + ""; Width = 680; Height = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(18) };
        var note = new TextBlock { Text = "Use Ctrl/Shift to select multiple files. Only target references are removed; files remain on disk. Generated and external files are read-only.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(note, Dock.Top); panel.Children.Add(note);
        var ok = new Button { Content = "Preview Changes", IsDefault = true, Padding = new Thickness(12, 5, 12, 5), HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (s, e) => DialogResult = true; DockPanel.SetDock(ok, Dock.Bottom); panel.Children.Add(ok);
        files.ItemsSource = target.Sources.Where(p => Paths.Within(root, p) && !target.GeneratedSources.Contains(p)).Distinct().OrderBy(p => p).ToArray();
        panel.Children.Add(files); Content = panel;
    }
}
