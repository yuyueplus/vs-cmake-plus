using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using CMakePlus.Core;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using System.Windows.Automation;
using Window = System.Windows.Window;

namespace CMakePlus;

public sealed partial class ExplorerControl : UserControl, IDisposable
{
    private readonly JoinableTaskFactory uiTasks = ThreadHelper.JoinableTaskContext.CreateFactory(ThreadHelper.JoinableTaskContext.CreateCollection());
    private readonly TextBox rootBox = new TextBox { IsReadOnly = true, MinWidth = 160 };
    private readonly TextBox buildBox = new TextBox { IsReadOnly = true, MinWidth = 160 };
    private readonly TextBlock status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6) };
    private readonly TextBlock details = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6) };
    private readonly ComboBox configurations = new ComboBox { MinWidth = 100 };
    private readonly TreeView targets = new TreeView();
    private CMakeTarget? SelectedTarget => (targets.SelectedItem as TargetNode)?.Target;
    private readonly TreeView tree = new TreeView();
    private readonly DispatcherTimer refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer contextTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
    private readonly CheckBox followWorkspace = new CheckBox { Content = "Follow VS workspace", IsChecked = true, Margin = new Thickness(3) };
    private readonly TextBlock contextInfo = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(3) };
    private readonly VsWorkspaceContext workspaceContext = new VsWorkspaceContext();
    private bool synchronizing;
    private DateTime? pendingConfiguration;
    private int contextRevision;
    private string contextIdentity = "";
    private string replyStamp = "";
    private string activeBuildConfiguration = "";
    private FileSystemWatcher? watcher;
    private FileSystemWatcher? replyWatcher;
    private CMakeSnapshot? snapshot;
    private HashSet<string> inputFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private string root = "";
    private int generation;
    private bool stale;
    private bool disposed;
    private SourceIndex? sourceIndex;
    private CancellationTokenSource? indexCancellation;
    private CancellationTokenSource? readCancellation;
    private int readRevision;
    private readonly DirectoryChangeBatch changes = new DirectoryChangeBatch();
    private bool refreshing;

    public ExplorerControl()
    {
        Theme.Apply(this);
        SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
        SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        followWorkspace.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        foreach (Control control in new Control[] { tree, targets, rootBox, buildBox })
        {
            control.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            control.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        }
        foreach (var itemType in new[] { typeof(TreeViewItem), typeof(ListBoxItem) })
        {
            var itemStyle = new Style(itemType, TryFindResource(itemType) as Style);
            itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(EnvironmentColors.ToolWindowTextBrushKey)));
            if (itemType == typeof(TreeViewItem))
            {
                itemStyle.Setters.Add(new Setter(TreeViewItem.IsExpandedProperty, new Binding("IsExpanded") { Mode = BindingMode.TwoWay }));
                itemStyle.Setters.Add(new Setter(TreeViewItem.IsSelectedProperty, new Binding("IsSelected") { Mode = BindingMode.TwoWay }));
            }
            Resources[itemType] = itemStyle;
        }
        var layout = new DockPanel();
        var top = new StackPanel { Margin = new Thickness(3, 0, 3, 3) };
        var settings = new StackPanel { Margin = new Thickness(5), Visibility = Visibility.Collapsed };
        settings.Children.Add(followWorkspace);
        settings.Children.Add(contextInfo);
        settings.Children.Add(new TextBlock { Text = "Source directory", Margin = new Thickness(2, 6, 2, 2) });
        settings.Children.Add(PathRow(rootBox, Button("Browse…", (s, e) => RunUi(ChooseRootAsync))));
        settings.Children.Add(new TextBlock { Text = "Build directory", Margin = new Thickness(2, 6, 2, 2) });
        settings.Children.Add(PathRow(buildBox, Button("Browse…", ChooseBuild)));
        settings.Children.Add(Row(Button("Prepare File API Query", PrepareQuery)));
        settings.Children.Add(new TextBlock { Text = "VS manages configuration, building, and debugging.", Margin = new Thickness(2, 4, 2, 4) });
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
        toolbar.Children.Add(IconButton("Sync VS Context", KnownMonikers.Sync, (s, e) => RunUi(SynchronizeContextAsync)));
        toolbar.Children.Add(IconButton("Refresh Files", KnownMonikers.Refresh, (s, e) => RunUi(RefreshAsync)));
        toolbar.Children.Add(IconButton("Load CMake Targets", KnownMonikers.OpenFolder, (s, e) => RunUi(ReadTargetsAsync)));
        toolbar.Children.Add(IconButton("Locate Active File", KnownMonikers.GoToSourceCode, (s, e) => RunUi(LocateCurrentAsync)));
        var add = IconButton("Create and Manage…", KnownMonikers.Add, (s, e) =>
        {
            var button = (Button)s; var menu = ManagementMenu();
            menu.PlacementTarget = button; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; menu.IsOpen = true;
        });
        add.Margin = new Thickness(6, 0, 0, 0);
        toolbar.Children.Add(add);
        toolbar.Children.Add(IconButton("Workspace Settings", KnownMonikers.Settings, (s, e) => settings.Visibility = settings.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible));
        top.Children.Add(toolbar);
        top.Children.Add(settings);
        var views = new ComboBox { ItemsSource = new[] { "Files", "Targets" }, SelectedIndex = 0, Width = 78, Margin = new Thickness(0, 2, 6, 2) };
        AutomationProperties.SetName(views, "Explorer View");
        configurations.ToolTip = "View a CMake configuration without changing the active VS build configuration";
        AutomationProperties.SetName(configurations, "View CMake Configuration");
        configurations.MinWidth = 60; configurations.Margin = new Thickness(0, 2, 0, 2);
        var selectors = new DockPanel(); DockPanel.SetDock(views, Dock.Left); selectors.Children.Add(views); selectors.Children.Add(configurations);
        top.Children.Add(selectors);
        InitializeWorkspaceFeatures(top, toolbar, settings, views);
        DockPanel.SetDock(top, Dock.Top);
        layout.Children.Add(top);
        DockPanel.SetDock(status, Dock.Bottom);
        layout.Children.Add(status);
        DockPanel.SetDock(details, Dock.Bottom);
        layout.Children.Add(details);
        foreach (var text in new[] { status, details })
        {
            text.TextWrapping = TextWrapping.NoWrap; text.TextTrimming = TextTrimming.CharacterEllipsis;
            text.MaxHeight = 34; text.Margin = new Thickness(6, 3, 6, 3);
            text.SetBinding(ToolTipProperty, new Binding("Text") { Source = text });
        }
        var trees = new Grid(); trees.Children.Add(tree); trees.Children.Add(targets); targets.Visibility = Visibility.Collapsed;
        AttachEmptyWorkspace(trees);
        views.SelectionChanged += (s, e) => { tree.Visibility = views.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed; targets.Visibility = views.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed; details.Text = ""; if (views.SelectedIndex == 0) ShowFileDetails(); };
        foreach (var browser in new[] { tree, targets })
        {
            browser.BorderThickness = new Thickness(0); browser.Padding = new Thickness(0, 2, 0, 2);
            browser.ContextMenu = ManagementMenu();
            browser.PreviewMouseRightButtonDown += (s, e) =>
            {
                if (TreeItemAt(e.OriginalSource) is TreeViewItem item)
                {
                    item.Focus();
                    item.IsSelected = true;
                    e.Handled = true;
                }
            };
        }
        layout.Children.Add(trees);
        Content = layout;
        var template = new HierarchicalDataTemplate(typeof(FileNode)) { ItemsSource = new Binding(nameof(FileNode.Children)) };
        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty, new Binding(nameof(FileNode.Label)));
        label.SetBinding(TextBlock.ToolTipProperty, new Binding(nameof(FileNode.Path)));
        template.VisualTree = NodeHeader(label);
        tree.ItemTemplate = template;
        VirtualizingStackPanel.SetIsVirtualizing(tree, true);
        VirtualizingStackPanel.SetVirtualizationMode(tree, VirtualizationMode.Recycling);
        VirtualizingStackPanel.SetIsVirtualizing(targets, true);
        VirtualizingStackPanel.SetVirtualizationMode(targets, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(targets, true);
        targets.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler((s, e) =>
        {
            if (e.OriginalSource is TreeViewItem item && item.DataContext is TargetNode node) RunUi(node.LoadAsync);
        }));
        tree.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler((s, e) => RunUi(() => ExpandedAsync(e))));
        tree.SelectedItemChanged += (s, e) => ShowFileDetails();
        tree.MouseDoubleClick += (s, e) => { if (tree.SelectedItem is FileNode node && !node.IsDirectory) OpenFile(node.Path); };
        var targetTemplate = new HierarchicalDataTemplate(typeof(TargetNode)) { ItemsSource = new Binding(nameof(TargetNode.Children)) };
        var targetLabel = new FrameworkElementFactory(typeof(TextBlock));
        targetLabel.SetBinding(TextBlock.TextProperty, new Binding(nameof(TargetNode.Label)));
        targetLabel.SetBinding(TextBlock.ToolTipProperty, new Binding(nameof(TargetNode.Path)));
        targetTemplate.VisualTree = NodeHeader(targetLabel); targets.ItemTemplate = targetTemplate;
        targets.MouseDoubleClick += (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); if (targets.SelectedItem is TargetNode node && File.Exists(node.Path)) OpenFile(node.Path); };
        targets.SelectedItemChanged += (s, e) =>
        {
            if (targets.SelectedItem is TargetNode node)
            {
                var owners = node.Path.Length == 0 ? null : sourceIndex?.Find(node.Path);
                details.Text = node.Target.Name + " · " + node.Target.Sources.Count + " source declarations\n" + node.Path
                    + (owners != null && owners.Any() ? "\nTargets: " + string.Join(", ", owners) : "");
            }
        };
        configurations.SelectionChanged += (s, e) => RunUi(SelectConfigurationAsync);
        refreshTimer.Tick += (s, e) => RunUi(async () =>
        {
            if (refreshing) return;
            var batch = changes.Take();
            if (batch == null) return;
            var dirty = batch.Directories; var all = batch.All;
            stale |= batch.Invalidated;
            refreshing = true;
            try
            {
                if (workspaceNode != null) await workspaceNode.RefreshAsync(all ? null : new HashSet<string>(dirty, StringComparer.OrdinalIgnoreCase));
                if (searchBox.Text.Length > 0) await SearchAsync();
                if (followWorkspace.IsChecked != true) await ReadTargetsAsync();
                if (pendingConfiguration.HasValue) status.Text = configurationRequestError.Length > 0 ? configurationRequestError : "Waiting for VS configuration. Use Retry VS Configuration if it does not start; check CMake Output for errors.";
                else if (snapshot != null) status.Text = "Files synchronized. Model from " + snapshot.GeneratedUtc.ToLocalTime().ToString("HH:mm:ss") + (stale ? "; save modified scripts, reconfigure, and reload targets." : ". Snapshot of the last successful configuration.");
                ShowFileDetails();
            }
            catch (Exception ex) { Report(ex); }
            finally { refreshing = false; }
        });
        status.Text = "Use the toolbar to create or open a CMake project. Workspace settings are under the gear icon.";
        followWorkspace.Checked += (s, e) => { contextRevision++; contextIdentity = ""; RunUi(SynchronizeContextAsync); };
        followWorkspace.Unchecked += (s, e) => { contextRevision++; contextInfo.Text = "Manual mode. Directory selection does not change the active VS workspace."; };
        workspaceContext.Changed += ContextChanged;
        contextTimer.Tick += (s, e) => { if (IsVisible) { UpdateModelBadge(); RunUi(SynchronizeContextAsync); } };
        Loaded += (s, e) => { refreshTimer.Start(); contextTimer.Start(); RunUi(SynchronizeContextAsync); };
        Unloaded += (s, e) => { contextTimer.Stop(); refreshTimer.Stop(); };
    }

    private async Task SelectConfigurationAsync()
    {
        indexCancellation?.Cancel();
        indexCancellation?.Dispose();
        var cancellation = indexCancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        sourceIndex = null; details.Text = ""; targets.ItemsSource = null;
        var config = configurations.SelectedItem as CMakeConfiguration;
        if (config == null) return;
        details.Text = "Loading configuration and source ownership index…";
        var source = root;
        try
        {
            var result = await Task.Run(() => new { Index = new SourceIndex(config, token), Nodes = TargetNode.Build(source, config.Targets).ToArray() }, token);
            if (disposed || token.IsCancellationRequested || configurations.SelectedItem != config) return;
            sourceIndex = result.Index; targets.ItemsSource = result.Nodes; details.Text = "";
            UpdateModelBadge();
            ShowFileDetails();
        }
        catch (OperationCanceledException) { }
    }

    private void ContextChanged(object? sender, EventArgs e)
    {
        if (disposed || Dispatcher.HasShutdownStarted) return;
        RunUi(() =>
        {
            if (disposed || followWorkspace.IsChecked != true) return Task.CompletedTask;
            contextRevision++;
            return SynchronizeContextAsync();
        });
    }

    private void ClearModel()
    {
        searchCancellation?.Cancel();
        pendingConfiguration = null;
        configurationRequestError = "";
        generation++; snapshot = null; stale = true; replyStamp = "";
        inputFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readRevision++; indexCancellation?.Cancel(); sourceIndex = null;
        readCancellation?.Cancel();
        changes.Take();
        configurations.ItemsSource = null; targets.ItemsSource = null; details.Text = "";
        replyWatcher?.Dispose(); replyWatcher = null;
    }

    private async Task SynchronizeContextAsync()
    {
        if (synchronizing || fileOperationRunning || disposed || followWorkspace.IsChecked != true) return;
        synchronizing = true;
        int revision = contextRevision;
        try
        {
            var current = await workspaceContext.ReadAsync();
            if (disposed || revision != contextRevision || followWorkspace.IsChecked != true) return;
            contextInfo.Text = "VS configuration/Preset: " + (current.Preset.Length > 0 ? current.Preset : "Unavailable")
                + (current.Configuration.Length > 0 ? " · Build configuration: " + current.Configuration : "")
                + "\n" + current.BuildStatus + (current.Note.Length > 0 ? "\n" + current.Note : "");
            activeBuildConfiguration = current.Configuration;
            if (current.Identity != contextIdentity)
            {
                ClearModel();
                if (root.Length == 0 || current.Source.Length == 0 || !Paths.Equal(root, current.Source))
                {
                    SaveBrowser(); workspaceNode = null;
                    watcher?.Dispose(); watcher = null; tree.ItemsSource = null;
                    root = ""; rootBox.Text = ""; buildBox.Text = "";
                    if (current.Source.Length > 0) await SetRootAsync(current.Source);
                }
                if (disposed || revision != contextRevision || followWorkspace.IsChecked != true) return;
                buildBox.Text = current.Build;
                contextIdentity = current.Identity;
            }
            if (root.Length == 0 || buildBox.Text.Length == 0) { status.Text = current.Note; return; }
            var build = buildBox.Text;
            if (!Directory.Exists(build)) { status.Text = "Waiting for VS to create the build directory."; return; }
            var query = System.IO.Path.Combine(build, ".cmake", "api", "v1", "query", "client-cmakeplus", "codemodel-v2");
            if (!File.Exists(query)) await Task.Run(() => CMakeFileApi.PrepareQuery(build));
            if (disposed || revision != contextRevision || followWorkspace.IsChecked != true) return;
            var source = root;
            var stamp = await Task.Run(() =>
            {
                WorkspacePaths.ValidateCache(source, build);
                var reply = System.IO.Path.Combine(build, ".cmake", "api", "v1", "reply");
                var index = Directory.Exists(reply) ? Directory.EnumerateFiles(reply, "index-*.json").OrderByDescending(p => p, StringComparer.Ordinal).FirstOrDefault() : null;
                if (index == null) throw new InvalidOperationException("Query prepared. Waiting for VS to reconfigure and produce a File API reply.");
                var file = new FileInfo(index);
                return file.Name + ":" + file.Length + ":" + file.LastWriteTimeUtc.Ticks;
            });
            if (disposed || revision != contextRevision || followWorkspace.IsChecked != true) return;
            if (stamp != replyStamp || snapshot == null || pendingConfiguration.HasValue)
            {
                await ReadTargetsAsync();
                if (snapshot != null) replyStamp = stamp;
            }
            // A new index is the only automatic path back to a fresh model after invalidation.
            var checkedModel = snapshot;
            if (checkedModel != null && await Task.Run(checkedModel.HasChangedInputs) && ReferenceEquals(snapshot, checkedModel))
            {
                stale = true; status.Text = configurationRequestError.Length > 0 ? configurationRequestError : "Configuration inputs changed. Use Retry VS Configuration; check CMake Output for errors.";
            }
        }
        catch (Exception ex) { if (!disposed && revision == contextRevision) { stale = true; Report(ex); } }
        finally { synchronizing = false; }
    }

    private static FrameworkElementFactory NodeHeader(FrameworkElementFactory label)
    {
        var row = new FrameworkElementFactory(typeof(StackPanel));
        row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var icon = new FrameworkElementFactory(typeof(CrispImage));
        icon.SetValue(WidthProperty, 16.0); icon.SetValue(HeightProperty, 16.0);
        icon.SetValue(MarginProperty, new Thickness(0, 0, 5, 0));
        icon.SetBinding(CrispImage.MonikerProperty, new Binding { Converter = new ExplorerImages() });
        row.AppendChild(icon); row.AppendChild(label);
        return row;
    }
    private static DockPanel PathRow(TextBox path, Button browse)
    {
        path.MinWidth = 0;
        var row = new DockPanel(); DockPanel.SetDock(browse, Dock.Right);
        row.Children.Add(browse); row.Children.Add(path); return row;
    }
    private Button IconButton(string label, ImageMoniker icon, RoutedEventHandler action)
    {
        var button = Button(label, action);
        button.Content = new CrispImage { Moniker = icon, Width = 16, Height = 16 };
        button.MinWidth = 0; button.MinHeight = 0; button.Width = 26; button.Height = 26; button.Padding = new Thickness(4);
        button.BorderThickness = new Thickness(0); button.Background = System.Windows.Media.Brushes.Transparent;
        button.ToolTip = label; AutomationProperties.SetName(button, label);
        button.SetResourceReference(StyleProperty, VsResourceKeys.ButtonStyleKey);
        return button;
    }
    private ContextMenu ManagementMenu()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var menu = new ContextMenu();
        menu.SetResourceReference(StyleProperty, VsResourceKeys.ContextMenuStyleKey);
        void Add(string label, Func<Task> action, bool needsTarget = false)
        {
            var item = new MenuItem { Header = label };
            item.Click += (s, e) => RunUi(action);
            item.ToolTip = "Wait for VS configuration to finish before editing targets.";
            menu.Opened += (s, e) => item.IsEnabled = !fileOperationRunning && root.Length > 0 && (!needsTarget || (snapshot != null && targets.IsVisible && SelectedTarget != null));
            menu.Items.Add(item);
        }
        Add("New Source File…", NewSourceAsync); Add("New Target…", NewTargetAsync);
        Add("Retry VS Configuration", RetryConfigurationAsync);
        menu.Items.Add(new Separator());
        Add("Add Existing Files…", () => EditMembershipAsync(true), true);
        Add("Remove Target References…", () => EditMembershipAsync(false), true);
        AddFileActions(menu);
        return menu;
    }
    private static WrapPanel Row(params UIElement[] children)
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
        foreach (var child in children) { if (child is FrameworkElement element) element.Margin = new Thickness(2); panel.Children.Add(child); }
        return panel;
    }
    private static Button Button(string text, RoutedEventHandler action)
    {
        var button = new Button { Content = text, Padding = new Thickness(7, 3, 7, 3) };
        button.Click += action;
        return button;
    }
    private string? PickFolder(string description, string initial = "")
    {
        var dialog = new DirectoryDialog(description, initial) { Owner = Window.GetWindow(this) };
        return dialog.ShowDialog() == true ? dialog.SelectedPath : null;
    }
    private void RunUi(Func<Task> action)
    {
        uiTasks.RunAsync(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (disposed) return;
            try { await action(); } catch (Exception ex) { if (!disposed) Report(ex); }
        }).Task.FileAndForget("CMakePlus/UI");
    }
    private async Task ChooseRootAsync()
    {
        var path = PickFolder("Enter the CMake source root", root);
        if (path == null) return;
        followWorkspace.IsChecked = false;
        await SetRootAsync(path);
    }
    private async Task SetRootAsync(string path)
    {
        try
        {
            if (!File.Exists(System.IO.Path.Combine(path, "CMakeLists.txt"))) throw new InvalidOperationException("This directory has no CMakeLists.txt.");
            SaveBrowser();
            ClearModel();
            watcher?.Dispose(); replyWatcher?.Dispose();
            root = path; rootBox.Text = root; buildBox.Text = "";
            browserSettings = await Task.Run(() => WorkspaceBrowser.Load(root));
            excludedBox.Text = browserSettings.ExcludedDirectories; showExcluded.IsChecked = browserSettings.ShowExcluded;
            await LoadRootAsync();
            watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite };
            watcher.Changed += Changed; watcher.Created += Changed; watcher.Deleted += Changed; watcher.Renamed += Changed;
            watcher.Error += (s, args) => QueueRefresh(true);
            watcher.EnableRaisingEvents = true;
            status.Text = "Source directory loaded. Select a build directory, prepare the query, then reconfigure in VS.";
        }
        catch (Exception ex) { Report(ex); }
    }
    private void ChooseBuild(object sender, RoutedEventArgs e)
    {
        var path = PickFolder("Enter the active VS CMake build directory (containing CMakeCache.txt)", buildBox.Text);
        if (path == null) return;
        followWorkspace.IsChecked = false;
        ClearModel();
        buildBox.Text = path;
        replyWatcher?.Dispose(); replyWatcher = null;
    }
    private void PrepareQuery(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Directory.Exists(buildBox.Text)) throw new InvalidOperationException("Select an existing build directory first.");
            CMakeFileApi.PrepareQuery(buildBox.Text);
            status.Text = "Query written to client-cmakeplus. Reconfigure with VS, then click Load CMake Targets.";
        }
        catch (Exception ex) { Report(ex); }
    }
    private Task ReadTargetsAsync() => ReadTargetsCoreAsync(false);

    // Preflight owns the operation lock but has not changed any files yet.
    // Background refresh stays blocked while an operation is running.
    private async Task ReadTargetsCoreAsync(bool operationPreflight)
    {
        if (fileOperationRunning && !operationPreflight) return;
        var build = buildBox.Text; int ticket = generation; int revision = contextRevision; int read = ++readRevision;
        readCancellation?.Cancel(); readCancellation?.Dispose();
        readCancellation = new CancellationTokenSource();
        var cancellation = readCancellation.Token;
        try
        {
            var source = root;
            var result = await Task.Run(() => { WorkspacePaths.ValidateCache(source, build); return CMakeFileApi.Read(build, cancellation); }, cancellation);
            var changedInputs = await Task.Run(result.HasChangedInputs);
            if (disposed || (fileOperationRunning && !operationPreflight) || read != readRevision || ticket != generation || revision != contextRevision || build != buildBox.Text) return;
            if (!Paths.Equal(root, result.SourceDirectory)) throw new InvalidOperationException("The build directory belongs to another source root. Select another directory.");
            snapshot = result; stale = changedInputs;
            if (!changedInputs && pendingConfiguration.HasValue && result.GeneratedUtc >= pendingConfiguration.Value)
            {
                pendingConfiguration = null;
                configurationRequestError = "";
            }
            inputFiles = new HashSet<string>(result.Inputs, StringComparer.OrdinalIgnoreCase);
            configurations.ItemsSource = result.Configurations;
            configurations.SelectedItem = (followWorkspace.IsChecked == true ? result.Configurations.FirstOrDefault(c => c.Name == activeBuildConfiguration) : null) ?? result.Configurations.FirstOrDefault();
            status.Text = "Loaded " + result.Configurations.Count + " configurations · " + result.GeneratedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + (stale ? ". Configuration inputs changed. Reconfigure the project." : ". Snapshot of the last successful configuration.");
            if (pendingConfiguration.HasValue && configurationRequestError.Length > 0) status.Text = configurationRequestError;
            replyWatcher?.Dispose();
            replyWatcher = new FileSystemWatcher(System.IO.Path.Combine(build, ".cmake", "api", "v1", "reply"), "index-*.json");
            replyWatcher.Created += (s, args) => QueueRefresh(false);
            replyWatcher.Changed += (s, args) => QueueRefresh(false);
            replyWatcher.Error += (s, args) => QueueRefresh(true);
            replyWatcher.EnableRaisingEvents = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!disposed && read == readRevision && ticket == generation) { stale = true; Report(ex); } }
    }
    private async Task RefreshAsync()
    {
        try { await RefreshLoadedNodesAsync(); status.Text = "Files refreshed."; } catch (Exception ex) { Report(ex); }
    }
    private async Task LoadRootAsync()
    {
        if (string.IsNullOrEmpty(root)) return;
        var node = new FileNode(root, true, ignore: browserSettings.Ignore) { IsExpanded = true };
        workspaceNode = node;
        tree.ItemsSource = new[] { node };
        await node.LoadAsync();
        await RestoreExpandedAsync(node);
        if (root == node.Path && searchBox.Text.Length > 0) await SearchAsync();
    }
    private async Task RefreshLoadedNodesAsync()
    {
        if (disposed) return;
        if (workspaceNode != null) await workspaceNode.RefreshAsync();
    }
    private async Task ExpandedAsync(RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem item && item.DataContext is FileNode node)
        {
            try { await node.LoadAsync(); } catch (Exception ex) { Report(ex); }
        }
    }
    private void Changed(object sender, FileSystemEventArgs e)
    {
        if (!ReferenceEquals(sender, watcher)) return;
        var build = snapshot?.BuildDirectory;
        if (!string.IsNullOrEmpty(build) && (Paths.Equal(build!, e.FullPath) || Paths.Within(build!, e.FullPath)) && !(e is RenamedEventArgs)) return;
        var relative = Paths.Relative(root, e.FullPath);
        var ignored = relative.Split('/').Any(browserSettings.Ignore);
        var renamed = e as RenamedEventArgs;
        if (ignored && (renamed == null || Paths.Relative(root, renamed.OldFullPath).Split('/').Any(browserSettings.Ignore))) return;
        var name = System.IO.Path.GetFileName(e.FullPath);
        var invalidates = renamed != null || inputFiles.Contains(e.FullPath) || name.Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase)
            || name.Equals("CMakePresets.json", StringComparison.OrdinalIgnoreCase) || name.Equals("CMakeUserPresets.json", StringComparison.OrdinalIgnoreCase)
            || (e.ChangeType != WatcherChangeTypes.Changed && !Directory.Exists(e.FullPath));
        changes.Add(invalidates, directory: e.ChangeType == WatcherChangeTypes.Changed ? null : System.IO.Path.GetDirectoryName(e.FullPath), oldDirectory: renamed == null ? null : System.IO.Path.GetDirectoryName(renamed.OldFullPath));
    }
    private void QueueRefresh(bool invalidate)
    {
        if (disposed || Dispatcher.HasShutdownStarted) return;
        changes.Add(invalidate, invalidate);
    }
    private void ShowFileDetails()
    {
        if (!(tree.SelectedItem is FileNode node)) return;
        var config = configurations.SelectedItem as CMakeConfiguration;
        var owners = sourceIndex?.Find(node.Path);
        details.Text = node.Path + "\n" + (node.IsDirectory ? "File system directory" : snapshot == null ? "Build model not loaded" : (stale ? "Model out of date · " : "") + (sourceIndex == null ? "Building source ownership index…" : owners?.Count > 0 ? "Targets: " + string.Join(", ", owners) : "Not listed as a source in the current configuration"));
    }
    private async Task LocateCurrentAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var dte = (DTE?)ServiceProvider.GlobalProvider.GetService(typeof(DTE));
            var path = dte?.ActiveDocument?.FullName;
            if (path == null || path.Length == 0) throw new InvalidOperationException("No active document.");
            details.Text = path;
            if (!Paths.Within(root, path)) throw new InvalidOperationException("The active file is outside the selected source directory.");
            searchBox.Clear();
            await SearchAsync();
                try
                {
                    var current = tree.Items.OfType<FileNode>().First();
                    tree.UpdateLayout();
                    var item = tree.ItemContainerGenerator.ContainerFromItem(current) as TreeViewItem;
                    foreach (var segment in Paths.Relative(root, path).Split('/'))
                    {
                        await current.LoadAsync();
                        if (item != null) { item.IsExpanded = true; item.UpdateLayout(); }
                        var next = current.Children.FirstOrDefault(x => string.Equals(System.IO.Path.GetFileName(x.Path), segment, StringComparison.OrdinalIgnoreCase));
                        if (next == null) return;
                        item = item?.ItemContainerGenerator.ContainerFromItem(next) as TreeViewItem;
                        current = next;
                    }
                    if (item != null) { item.IsSelected = true; item.BringIntoView(); }
                }
                catch (Exception ex) { Report(ex); }
        }
        catch (Exception ex) { Report(ex); }
    }
    private async Task NewSourceAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            if (pendingConfiguration.HasValue || stale) await ReadTargetsAsync();
            var config = configurations.SelectedItem as CMakeConfiguration;
            var model = snapshot;
            var revision = contextRevision;
            VsScriptEdit? editor = null;
            TargetEditPlan? plan = null;
            var defaults = SourceDefaults.ForSelection(root, SelectedPath, config?.Targets ?? new List<CMakeTarget>(), targets.IsVisible ? SelectedTarget : null);
            var dialog = new NewSourceDialog(config?.Targets ?? new List<CMakeTarget>(), defaults.Target, async input =>
            {
                await EnsureFreshModelAsync();
                if (config != configurations.SelectedItem || model != snapshot || revision != contextRevision)
                    throw new InvalidOperationException("The configuration changed. Close this dialog and try again.");
                if (input.Target == null) throw new InvalidOperationException("Select a target to add the file to.");
                editor = new VsScriptEdit(input.Target.DeclarationFile);
                var sourcePlan = SourceEditor.Plan(root, input.Target, input.RelativePath, editor.Text);
                plan = new TargetEditPlan { Root = root, ScriptPath = sourcePlan.ScriptPath, Before = editor.Text, After = sourcePlan.UpdatedScript };
                plan.NewFiles[sourcePlan.SourcePath] = sourcePlan.SourceContent;
            }, defaults.RelativePath) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true || editor == null || plan == null) return;
            var preview = new PreviewDialog(plan.Preview) { Owner = Window.GetWindow(this) };
            if (preview.ShowDialog() != true) return;
            await EnsureFreshModelAsync();
            if (!ReferenceEquals(model, snapshot) || revision != contextRevision)
                throw new InvalidOperationException("The workspace or model changed during preview. Try again with the latest model.");
            await ApplyAndSaveAsync(editor, plan);
        }
        catch (Exception ex) { ReportOperationError("New Source File", ex); }
    }
    private async Task EnsureFreshModelAsync()
    {
        if (pendingConfiguration.HasValue || stale) await ReadTargetsCoreAsync(true);
        if (pendingConfiguration.HasValue) throw new InvalidOperationException(configurationRequestError.Length > 0 ? configurationRequestError : "Waiting for a successful VS configuration. Use Retry VS Configuration from the CMake Plus menu, then try again. Check CMake Output for errors.");
        stale |= changes.Invalidated;
        var model = snapshot; var revision = contextRevision;
        if (model == null || stale) throw new InvalidOperationException("Save scripts, reconfigure in VS, and load the latest targets first.");
        var changed = await Task.Run(model.HasChangedInputs);
        stale |= changes.Invalidated;
        if (disposed || changed || stale || model != snapshot || revision != contextRevision) throw new InvalidOperationException("The workspace or configuration inputs changed. Reconfigure and load the latest targets.");
    }
    private async Task ApplyPreviewAsync(VsScriptEdit editor, TargetEditPlan plan)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var model = snapshot; var revision = contextRevision;
        if (new PreviewDialog(plan.Preview) { Owner = Window.GetWindow(this) }.ShowDialog() != true) return;
        await EnsureFreshModelAsync();
        if (!ReferenceEquals(model, snapshot) || revision != contextRevision) throw new InvalidOperationException("The workspace or model changed during preview. Try again.");
        await ApplyAndSaveAsync(editor, plan);
    }
    private async Task ApplyAndSaveAsync(VsScriptEdit editor, TargetEditPlan plan)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (editor.HasUnsavedEdits && MessageBox.Show(Window.GetWindow(this), "This CMake script already contains unsaved edits. Apply Changes will save those edits together with this operation. Continue?", "Save Existing Edits", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        editor.Apply(plan);
        stale = true;
        var savedAfter = DateTime.UtcNow;
        try { editor.Save(); }
        catch (Exception ex) { QueueRefresh(true); throw new IOException("Changes were applied but the script could not be saved. Save it manually, then reconfigure in VS. " + ex.Message, ex); }
        pendingConfiguration = savedAfter;
        QueueRefresh(true); UpdateModelBadge();
        await RequestConfigurationAsync(savedAfter);
    }
    private async Task EditMembershipAsync(bool add)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            await EnsureFreshModelAsync();
            var target = SelectedTarget ?? throw new InvalidOperationException("Select a target or one of its sources in the target tree first.");
            string[] paths;
            if (add)
            {
                var picker = new Microsoft.Win32.OpenFileDialog { Title = "Select existing files to add to " + target.Name + "", InitialDirectory = root, Multiselect = true, CheckFileExists = true, Filter = "All files|*.*" };
                if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
                paths = picker.FileNames;
            }
            else
            {
                var picker = new RemoveSourcesDialog(target, root) { Owner = Window.GetWindow(this) };
                if (picker.ShowDialog() != true) return;
                paths = picker.SelectedPaths;
            }
            var editor = new VsScriptEdit(target.DeclarationFile);
            await ApplyPreviewAsync(editor, TargetEditing.Membership(root, target, paths, add, editor.Text));
        }
        catch (Exception ex) { Report(ex); }
    }
    private async Task NewTargetAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            VsScriptEdit? editor = null;
            TargetEditPlan? plan = null;
            var dialog = new NewTargetDialog(async input =>
            {
                await EnsureFreshModelAsync();
                var script = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, input.ParentScript));
                TargetEditing.ValidatePath(root, script);
                editor = new VsScriptEdit(script);
                plan = TargetEditing.NewTarget(root, script, input.RelativeDirectory, input.TargetName, input.Kind,
                    snapshot!.Configurations.SelectMany(c => c.Targets).Select(t => t.Name), editor.Text);
            }) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true) return;
            if (editor == null || plan == null) return;
            await ApplyPreviewAsync(editor, plan);
        }
        catch (Exception ex) { ReportOperationError("New Target", ex); }
    }
    private void ReportOperationError(string operation, Exception ex)
    {
        Report(ex);
        MessageBox.Show(Window.GetWindow(this), ex.Message, operation, MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    private static void OpenFile(string path)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (File.Exists(path)) VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, path);
    }
    private void Report(Exception ex) { status.Text = ex.Message; }
    public void Dispose()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        disposed = true; generation++;
        SaveBrowser(); searchCancellation?.Cancel(); searchTimer.Stop();
        indexCancellation?.Cancel(); indexCancellation?.Dispose();
        readCancellation?.Cancel(); readCancellation?.Dispose();
        refreshTimer.Stop(); watcher?.Dispose(); replyWatcher?.Dispose();
        contextTimer.Stop(); workspaceContext.Changed -= ContextChanged; workspaceContext.Dispose();
    }
}


internal sealed class NewSourceDialog : DialogWindow
{
    private readonly TextBox path = new TextBox { Text = "new_file.cpp", Margin = new Thickness(0, 5, 0, 12) };
    private readonly ComboBox target = new ComboBox { Margin = new Thickness(0, 5, 0, 12) };
    public string RelativePath => path.Text;
    public CMakeTarget? Target => target.SelectedItem as CMakeTarget;
    public NewSourceDialog(IEnumerable<CMakeTarget> targets, CMakeTarget? selected, Func<NewSourceDialog, Task> validate, string initialPath = "new_file.cpp")
    {
        Theme.Apply(this);
        path.Text = initialPath;
        Title = "New Source File"; Width = 520; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Path relative to the source root (parent directory must exist)", TextWrapping = TextWrapping.Wrap }); panel.Children.Add(path);
        panel.Children.Add(new TextBlock { Text = "Add to target" }); panel.Children.Add(target);
        target.ItemsSource = targets; target.SelectedItem = selected;
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), Visibility = Visibility.Collapsed };
        System.Windows.Automation.AutomationProperties.SetLiveSetting(error, System.Windows.Automation.AutomationLiveSetting.Assertive);
        panel.Children.Add(error);
        var ok = new Button { Content = "Preview Changes", IsDefault = true, Padding = new Thickness(12, 5, 12, 5), HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (s, e) => ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            ok.IsEnabled = false;
            try { await validate(this); if (IsVisible) DialogResult = true; }
            catch (Exception ex) { error.Text = ex.Message; error.Visibility = Visibility.Visible; }
            finally { ok.IsEnabled = true; }
        }).FileAndForget("CMakePlus/PreviewNewSource");
        panel.Children.Add(ok); Content = panel;
    }
}

internal sealed class PreviewDialog : DialogWindow
{
    public PreviewDialog(string preview, string title = "Review File and CMake Changes", string action = "Apply Changes")
    {
        Theme.Apply(this);
        Title = title; Width = 700; Height = 440; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(12) };
        var apply = new Button { Content = action, IsDefault = true, Padding = new Thickness(12, 5, 12, 5), HorizontalAlignment = HorizontalAlignment.Right };
        apply.Click += (s, e) => DialogResult = true; DockPanel.SetDock(apply, Dock.Bottom); panel.Children.Add(apply);
        panel.Children.Add(new TextBox { Text = preview, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); Content = panel;
    }
}

internal sealed class DirectoryDialog : DialogWindow
{
    private readonly TextBox path;
    public string SelectedPath => System.IO.Path.GetFullPath(path.Text.Trim());
    public DirectoryDialog(string description, string initial)
    {
        Theme.Apply(this);
        Title = "Select Directory"; Width = 620; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap });
        path = new TextBox { Text = initial, Margin = new Thickness(0, 10, 0, 10) };
        panel.Children.Add(path);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(error);
        var ok = new Button { Content = "Open Directory", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(12, 5, 12, 5) };
        ok.Click += (s, e) =>
        {
            try
            {
                if (!System.IO.Path.IsPathRooted(path.Text.Trim()) || !Directory.Exists(SelectedPath)) throw new IOException("Enter the full path of an existing directory.");
                DialogResult = true;
            }
            catch (Exception ex) { error.Text = ex.Message; }
        };
        panel.Children.Add(ok); Content = panel;
        Loaded += (s, e) => { path.Focus(); path.SelectAll(); };
    }
}
