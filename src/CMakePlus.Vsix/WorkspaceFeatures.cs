using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CMakePlus.Core;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Window = System.Windows.Window;

namespace CMakePlus;

public sealed partial class ExplorerControl
{
    private BrowserSettings browserSettings = new BrowserSettings();
    private FileNode? workspaceNode;
    private readonly TextBox searchBox = new TextBox { Margin = new Thickness(0, 3, 0, 1), ToolTip = "Search by file name or relative path (up to 500 results). Clear to return to the directory tree." };
    private readonly TextBox excludedBox = new TextBox { Margin = new Thickness(2) };
    private readonly CheckBox showExcluded = new CheckBox { Content = "Show excluded directories", Margin = new Thickness(2) };
    private readonly DispatcherTimer searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
    private CancellationTokenSource? searchCancellation;
    private bool fileOperationRunning;
    private string? dragPath;
    private Point dragStart;
    private FrameworkElement? emptyWorkspaceView;
    private readonly TextBlock modelBadge = new TextBlock { Text = "No CMake project open", Margin = new Thickness(3, 2, 3, 1), TextTrimming = TextTrimming.CharacterEllipsis };

    private void InitializeWorkspaceFeatures(StackPanel top, StackPanel toolbar, StackPanel settings, ComboBox views)
    {
        toolbar.Children.Insert(0, IconButton("Create CMake Project…", KnownMonikers.Application, (s, e) => RunUi(CreateProjectAsync)));
        toolbar.Children.Insert(1, IconButton("Open CMake Project…", KnownMonikers.OpenFolder, (s, e) => RunUi(OpenProjectAsync)));
        System.Windows.Automation.AutomationProperties.SetName(searchBox, "Search project files or paths");
        var searchPanel = new Grid(); searchPanel.Children.Add(searchBox);
        var hint = new TextBlock { Text = "Search files or paths…", IsHitTestVisible = false, Opacity = 0.65, Margin = new Thickness(5, 5, 3, 2) };
        searchPanel.Children.Add(hint); top.Children.Add(searchPanel); top.Children.Add(modelBadge);
        searchBox.TextChanged += (s, e) => hint.Visibility = searchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        settings.Children.Add(new TextBlock { Text = "Excluded directories (semicolon-separated; supports * and ?)", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 6, 2, 2) });
        settings.Children.Add(excludedBox); settings.Children.Add(showExcluded);
        settings.Children.Add(Button("Apply Display Rules", (s, e) => RunUi(async () =>
        {
            if (root.Length == 0) return;
            SaveBrowser(); browserSettings.ExcludedDirectories = excludedBox.Text; browserSettings.ShowExcluded = showExcluded.IsChecked == true;
            WorkspaceBrowser.Save(root, browserSettings); await LoadRootAsync(); await SearchAsync();
        })));
        searchBox.TextChanged += (s, e) => { searchCancellation?.Cancel(); searchTimer.Stop(); searchTimer.Start(); if (searchBox.Text.Length > 0) views.SelectedIndex = 0; };
        searchTimer.Tick += (s, e) => { searchTimer.Stop(); RunUi(SearchAsync); };
        tree.AllowDrop = true;
        tree.PreviewMouseLeftButtonDown += (s, e) => { dragStart = e.GetPosition(tree); dragPath = FileNodeAt(e.OriginalSource)?.Path; };
        tree.MouseMove += (s, e) =>
        {
            var point = e.GetPosition(tree);
            if (e.LeftButton != MouseButtonState.Pressed || dragPath == null || fileOperationRunning || Math.Abs(point.X - dragStart.X) + Math.Abs(point.Y - dragStart.Y) < 8) return;
            var path = dragPath; dragPath = null;
            DragDrop.DoDragDrop(tree, new DataObject("CMakePlus.Move", path), DragDropEffects.Move);
        };
        tree.DragOver += (s, e) =>
        {
            var node = FileNodeAt(e.OriginalSource);
            e.Effects = !fileOperationRunning && e.Data.GetDataPresent("CMakePlus.Move") && node?.IsDirectory == true ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true;
        };
        tree.Drop += (s, e) =>
        {
            e.Handled = true;
            var node = FileNodeAt(e.OriginalSource);
            if (node?.IsDirectory != true || !(e.Data.GetData("CMakePlus.Move") is string from)) return;
            RunUi(() => MovePathAsync(from, Path.Combine(node.Path, Path.GetFileName(from))));
        };
    }
    private static FileNode? FileNodeAt(object source) => TreeItemAt(source)?.DataContext as FileNode;
    private static TreeViewItem? TreeItemAt(object source)
    {
        // Nested tree items belong to their parent item, not directly to the TreeView.
        // Use the nearest container for both right-click selection and dragging.
        for (var current = source as DependencyObject; current != null;)
        {
            if (current is TreeViewItem item) return item;
            current = current is System.Windows.Media.Visual
                ? System.Windows.Media.VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }
    private void AttachEmptyWorkspace(Grid host)
    {
        var panel = new StackPanel { Margin = new Thickness(18), VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = "Get started with CMake Plus", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(Button("Create CMake Project…", (s, e) => RunUi(CreateProjectAsync)));
        panel.Children.Add(Button("Open Existing Project…", (s, e) => RunUi(OpenProjectAsync)));
        emptyWorkspaceView = panel; host.Children.Add(panel);
    }
    private void UpdateModelBadge()
    {
        modelBadge.Text = pendingConfiguration.HasValue ? "Model · Waiting for VS configuration…" : fileOperationRunning ? "File operation in progress…" : root.Length == 0 ? "No CMake project open" : snapshot == null ? "Model · Waiting for configuration" : stale ? "Model · Reconfiguration required" : sourceIndex == null ? "Model · Loading index…" : "Model · Loaded " + snapshot.GeneratedUtc.ToLocalTime().ToString("HH:mm:ss");
        if (emptyWorkspaceView != null) emptyWorkspaceView.Visibility = root.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private async Task SearchAsync()
    {
        searchCancellation?.Cancel(); searchCancellation?.Dispose(); searchCancellation = new CancellationTokenSource();
        var token = searchCancellation.Token; var source = root; var query = searchBox.Text.Trim();
        if (source.Length == 0 || workspaceNode == null) return;
        if (query.Length == 0) { tree.ItemsSource = new[] { workspaceNode }; return; }
        var options = new BrowserSettings { ExcludedDirectories = browserSettings.ExcludedDirectories, ShowExcluded = browserSettings.ShowExcluded };
        try
        {
            var results = await Task.Run(() => WorkspaceBrowser.Search(source, query, options, token), token);
            if (disposed || token.IsCancellationRequested || root != source || searchBox.Text.Trim() != query) return;
            tree.ItemsSource = results.Take(500).Select(p => new FileNode(p, false) { DisplayLabel = Paths.Relative(source, p) }).ToArray();
            details.Text = results.Length > 500 ? "More than 500 matches. Narrow your search." : "Found " + results.Length + " files";
        }
        catch (OperationCanceledException) { }
    }
    private void SaveBrowser()
    {
        if (root.Length == 0 || workspaceNode == null) return;
        var expanded = new List<string>();
        void Visit(FileNode node)
        {
            if (!node.IsDirectory) return;
            if (node.IsExpanded && expanded.Count < 200) expanded.Add(node.Path);
            foreach (var child in node.Children.Where(n => n.IsDirectory)) Visit(child);
        }
        Visit(workspaceNode); browserSettings.Expanded = expanded;
        try { WorkspaceBrowser.Save(root, browserSettings); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    private async Task RestoreExpandedAsync(FileNode node)
    {
        foreach (var child in node.Children.Where(n => n.IsDirectory).ToArray())
        {
            if (!browserSettings.Expanded.Contains(child.Path, StringComparer.OrdinalIgnoreCase)) continue;
            child.IsExpanded = true; await child.LoadAsync(); await RestoreExpandedAsync(child);
        }
    }
    public async Task CreateProjectAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (fileOperationRunning) throw new InvalidOperationException("Wait for the file operation to finish before creating a project.");
        var dialog = new CreateProjectDialog { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.Plan == null) return;
        var plan = dialog.Plan;
        if (new PreviewDialog(plan.Preview) { Owner = Window.GetWindow(this) }.ShowDialog() != true) return;
        await Task.Run(() => ProjectCreation.Create(plan));
        OpenWorkspace(plan.Directory);
        status.Text = "Project created. Waiting for VS configuration.";
    }
    private async Task OpenProjectAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (fileOperationRunning) throw new InvalidOperationException("Wait for the file operation to finish before opening a project.");
        var path = PickFolder("Open a project directory containing CMakeLists.txt", root);
        if (path == null) return;
        if (!File.Exists(Path.Combine(path, "CMakeLists.txt"))) throw new IOException("The selected directory has no CMakeLists.txt.");
        OpenWorkspace(path);
    }
    private void OpenWorkspace(string path)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var solution = ServiceProvider.GlobalProvider.GetService(typeof(SVsSolution)) as IVsSolution7 ?? throw new InvalidOperationException("The VS folder service is unavailable. The project is preserved; open it manually.");
        solution.OpenFolder(path); followWorkspace.IsChecked = true; contextIdentity = "";
    }
    private string? SelectedPath => tree.IsVisible ? (tree.SelectedItem as FileNode)?.Path : (targets.SelectedItem as TargetNode)?.Path;
    private void AddFileActions(ContextMenu menu)
    {
        menu.Items.Add(new Separator());
        void Add(string text, Func<Task> action, bool requiresSelection = true)
        {
            var item = new MenuItem { Header = text }; item.Click += (s, e) => RunUi(action);
            menu.Opened += (s, e) => item.IsEnabled = root.Length > 0 && !fileOperationRunning && (!requiresSelection || !string.IsNullOrEmpty(SelectedPath));
            menu.Items.Add(item);
        }
        Add("New Directory…", NewDirectoryAsync, false);
        Add("Rename…", () => PromptMoveAsync(true));
        Add("Move To…", () => PromptMoveAsync(false));
        Add("Delete to Recycle Bin…", DeletePathAsync);
        Add("Recover Last File Operation…", RecoverFileOperationAsync, false);
        Add("Open Target Declaration", async () => { await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(); if (SelectedTarget != null) OpenFile(SelectedTarget.DeclarationFile); });
    }
    private async Task NewDirectoryAsync()
    {
        var parent = SelectedPath; if (string.IsNullOrEmpty(parent)) parent = root;
        if (!Directory.Exists(parent)) parent = Path.GetDirectoryName(parent)!;
        var dialog = new PathEntryDialog("New Directory", "Directory name (relative to the selected directory)", "new_folder") { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        var path = Path.GetFullPath(Path.Combine(parent, dialog.Value)); TargetEditing.ValidatePath(root, path);
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException("The path already exists.");
        Directory.CreateDirectory(path); await RefreshAsync();
    }
    private async Task PromptMoveAsync(bool rename)
    {
        var selected = SelectedPath; if (string.IsNullOrEmpty(selected)) return;
        var from = selected!;
        var dialog = new PathEntryDialog(rename ? "Rename" : "Move File or Directory", rename ? "New name" : "Destination path (relative to source root, including file name)", rename ? Path.GetFileName(from) : Paths.Relative(root, from)) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        if (rename && Path.GetFileName(dialog.Value) != dialog.Value) throw new IOException("Enter only a name to rename. Use Move To to change directories.");
        await MovePathAsync(from, Path.GetFullPath(Path.Combine(rename ? Path.GetDirectoryName(from)! : root, dialog.Value)));
    }
    private void CheckSaved(IEnumerable<string> paths, bool close)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var affected = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        var dte = ServiceProvider.GlobalProvider.GetService(typeof(DTE)) as DTE ?? throw new InvalidOperationException("The VS document service is unavailable.");
        var docs = new List<Document>();
        foreach (Document document in dte.Documents)
            if (affected.Contains(document.FullName))
            {
                if (!document.Saved) throw new InvalidOperationException("Save related source files and CMake scripts first. Disk operations cannot overwrite unsaved edits.");
                docs.Add(document);
            }
        if (close) foreach (var document in docs) document.Close(vsSaveChanges.vsSaveChangesNo);
    }
    private async Task MovePathAsync(string from, string to)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (fileOperationRunning) return; fileOperationRunning = true;
        try
        {
            await EnsureFreshModelAsync(); var model = snapshot!; var revision = contextRevision;
            CheckSaved(model.Inputs, false);
            var plan = await Task.Run(() => FileOperations.Plan(model, from, to));
            if (new PreviewDialog(plan.Preview) { Owner = Window.GetWindow(this) }.ShowDialog() != true) return;
            await EnsureFreshModelAsync();
            if (snapshot != model || revision != contextRevision) throw new IOException("The project changed during preview.");
            CheckSaved(model.Inputs.Concat(plan.Hashes.Keys), false);
            CheckSaved(plan.Scripts.Select(s => s.ScriptPath).Concat(plan.Hashes.Keys), true);
            var changedAt = DateTime.UtcNow;
            try { await Task.Run(() => FileOperations.Apply(plan)); }
            catch { stale = true; QueueRefresh(true); throw; }
            stale = true; QueueRefresh(true); await RefreshAsync();
            fileOperationRunning = false;
            await RequestConfigurationAsync(changedAt);
            if (File.Exists(to)) OpenFile(to);
        }
        finally { fileOperationRunning = false; }
    }
    private async Task DeletePathAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var path = SelectedPath;
        if (targets.IsVisible && targets.SelectedItem is TargetNode targetNode && targetNode.Target != null && targetNode.Path.Length > 0 && !string.IsNullOrEmpty(targetNode.Target.DeclarationFile) && Paths.Equal(targetNode.Path, targetNode.Target.DeclarationFile))
            path = Path.GetDirectoryName(targetNode.Target.DeclarationFile);
        if (string.IsNullOrEmpty(path) || fileOperationRunning) return;
        fileOperationRunning = true;
        try
        {
            if (new DriveInfo(Path.GetPathRoot(path)).DriveType != DriveType.Fixed)
                throw new IOException("Deletion to Recycle Bin is supported only on local fixed drives.");
            await EnsureFreshModelAsync(); var model = snapshot!; var revision = contextRevision;
            CheckSaved(model.Inputs, false);
            var plan = await Task.Run(() => Directory.Exists(path) && File.Exists(Path.Combine(path!, "CMakeLists.txt"))
                ? FileOperations.PlanTargetDirectoryDelete(model, path!) : FileOperations.PlanDelete(model, path!));
            if (new PreviewDialog(plan.Preview, "Confirm Deletion to Recycle Bin", "Delete to Recycle Bin") { Owner = Window.GetWindow(this) }.ShowDialog() != true) return;
            await EnsureFreshModelAsync();
            if (snapshot != model || revision != contextRevision) throw new IOException("The workspace changed during preview. Generate a new preview.");
            CheckSaved(model.Inputs.Concat(plan.Hashes.Keys), false);
            CheckSaved(plan.Scripts.Select(s => s.ScriptPath).Concat(plan.Hashes.Keys), true);
            var changedAt = DateTime.UtcNow;
            var completion = new TaskCompletionSource<string>();
            var worker = new System.Threading.Thread(() =>
            {
                try
                {
                    completion.SetResult(FileOperations.ApplyDelete(plan, (source, directory) =>
                    {
                        if (directory) Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(source, Microsoft.VisualBasic.FileIO.UIOption.AllDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin, Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
                        else Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(source, Microsoft.VisualBasic.FileIO.UIOption.AllDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin, Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
                    }));
                }
                catch (Exception ex) { completion.SetException(ex); }
            }) { IsBackground = true };
            worker.SetApartmentState(ApartmentState.STA); worker.Start();
            try { await completion.Task; }
            finally { stale = true; QueueRefresh(true); await RefreshAsync(); }
            fileOperationRunning = false;
            await RequestConfigurationAsync(changedAt);
        }
        catch (Exception ex)
        {
            Report(ex);
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Deletion Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { fileOperationRunning = false; }
    }
    private async Task RecoverFileOperationAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (fileOperationRunning) return; fileOperationRunning = true;
        try
        {
            var source = root; var revision = contextRevision;
            var journal = await Task.Run(() => FileOperations.Latest(source));
            if (journal == null) throw new InvalidOperationException("No recoverable file operation for this project.");
            var plan = FileOperations.LoadJournal(journal);
            if (new PreviewDialog(plan.RecoveryPreview) { Owner = Window.GetWindow(this) }.ShowDialog() != true) return;
            if (root != source || revision != contextRevision) throw new IOException("The workspace changed.");
            var paths = plan.Scripts.Select(s => s.ScriptPath).Concat(plan.Hashes.Keys).Concat(plan.Hashes.Keys.Select(p => plan.Directory ? Path.Combine(plan.To, Paths.Relative(plan.From, p)) : plan.To)).ToArray();
            CheckSaved(paths, true);
            var changedAt = DateTime.UtcNow;
            await Task.Run(() => FileOperations.Recover(journal));
            stale = true; QueueRefresh(true); await RefreshAsync();
            fileOperationRunning = false;
            await RequestConfigurationAsync(changedAt);
        }
        finally { fileOperationRunning = false; }
    }

    private async Task RequestConfigurationAsync(DateTime changedAt)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        pendingConfiguration = changedAt;
        UpdateModelBadge();
        var source = root;
        try
        {
            var current = await workspaceContext.ReadAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (disposed || root != source) return;
            if (!Paths.Equal(source, current.Source)) throw new InvalidOperationException("Open this source folder in VS first.");
            var dte = ServiceProvider.GlobalProvider.GetService(typeof(DTE)) as DTE ?? throw new InvalidOperationException("VS command service is unavailable.");
            var command = dte.Commands.Cast<Command>().FirstOrDefault(c =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return c.Name?.EndsWith(".GenerateCache", StringComparison.OrdinalIgnoreCase) == true;
            });
            if (command == null || !command.IsAvailable) throw new InvalidOperationException("VS Generate Cache is currently unavailable.");
            dte.ExecuteCommand(command.Name);
            status.Text = "Changes saved. VS cache generation requested; targets will refresh when configuration succeeds. Check CMake Output if it fails.";
        }
        catch (Exception ex)
        {
            status.Text = "Changes saved. " + ex.Message + " Use VS Project > Generate Cache, then Load CMake Targets.";
        }
    }
}
