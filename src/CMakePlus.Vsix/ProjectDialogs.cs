using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CMakePlus.Core;
using Microsoft.VisualStudio.PlatformUI;

namespace CMakePlus;

internal sealed class CreateProjectDialog : DialogWindow
{
    public ProjectPlan? Plan { get; private set; }
    public CreateProjectDialog()
    {
        Theme.Apply(this); Title = "Create CMake Project"; Width = 560; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(18) };
        var name = new TextBox { Text = "MyProject" };
        var parent = new TextBox { Text = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) };
        var kind = new ComboBox { ItemsSource = new[] { "Console Application", "Static Library", "Shared Library" }, SelectedIndex = 0 };
        var standard = new ComboBox { ItemsSource = new[] { "C++17", "C++20", "C++23" }, SelectedIndex = 1 };
        var tests = new CheckBox { Content = "Add a basic CTest test", IsChecked = true, Margin = new Thickness(0, 10, 0, 10) };
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 10) };
        void Field(string label, Control field) { panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 3) }); panel.Children.Add(field); }
        Field("Project name", name); Field("Parent directory", parent);
        var browse = new Button { Content = "Browse Parent Directory…", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
        browse.Click += (s, e) => { var picker = new DirectoryDialog("Select the project parent directory", parent.Text) { Owner = this }; if (picker.ShowDialog() == true) parent.Text = picker.SelectedPath; };
        panel.Children.Add(browse); Field("Project type", kind); Field("C++ standard", standard); panel.Children.Add(tests); panel.Children.Add(summary);
        void Update() { try { summary.Text = "Project directory: " + Path.GetFullPath(Path.Combine(parent.Text, name.Text)) + "\nCreates CMakeLists.txt, Presets, src, and include; opens the project in the current VS instance."; } catch (Exception ex) { summary.Text = ex.Message; } }
        name.TextChanged += (s, e) => Update(); parent.TextChanged += (s, e) => Update(); Update();
        var create = new Button { Content = "Preview Project", IsDefault = true, Padding = new Thickness(12, 5, 12, 5), HorizontalAlignment = HorizontalAlignment.Right };
        create.Click += (s, e) =>
        {
            try { Plan = ProjectCreation.Plan(parent.Text, name.Text, new[] { "EXECUTABLE", "STATIC", "SHARED" }[kind.SelectedIndex], new[] { 17, 20, 23 }[standard.SelectedIndex], tests.IsChecked == true); DialogResult = true; }
            catch (Exception ex) { summary.Text = ex.Message; }
        };
        panel.Children.Add(create); Content = panel;
    }
}
internal sealed class PathEntryDialog : DialogWindow
{
    private readonly TextBox field;
    public string Value => this.field.Text.Trim();
    public PathEntryDialog(string title, string prompt, string initial)
    {
        Theme.Apply(this); Title = title; Width = 560; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap });
        field = new TextBox { Text = initial, Margin = new Thickness(0, 10, 0, 10) }; panel.Children.Add(field);
        var ok = new Button { Content = "Continue", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(12, 5, 12, 5) };
        ok.Click += (s, e) => { if (Value.Length > 0) DialogResult = true; }; panel.Children.Add(ok); Content = panel;
        Loaded += (s, e) => { field.Focus(); field.SelectAll(); };
    }
}
