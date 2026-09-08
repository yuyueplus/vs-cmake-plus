using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Workspace;
using Microsoft.VisualStudio.Workspace.Build;
using Microsoft.VisualStudio.Workspace.Debug;
using Microsoft.VisualStudio.Workspace.Evaluator;
using Microsoft.VisualStudio.Workspace.VSIntegration.Contracts;

namespace CMakePlus;

internal sealed class VsContext
{
    public string Workspace { get; set; } = "";
    public string Source { get; set; } = "";
    public string Build { get; set; } = "";
    public string Preset { get; set; } = "";
    public string Configuration { get; set; } = "";
    public string BuildStatus { get; set; } = "No build events observed yet";
    public string Note { get; set; } = "";
    public string Identity => Workspace + "\n" + Source + "\n" + Build + "\n" + Preset + "\n" + Configuration;
}

// Uses only public Open Folder contracts. Does not load private CMake implementation assemblies.
internal sealed class VsWorkspaceContext : IDisposable
{
    private IVsFolderWorkspaceService? folders;
    private IWorkspace? workspace;
    private IProjectConfigurationService? configurations;
    private IPropertyEvaluatorService? evaluator;
    private IBuildService? builds;
    private readonly VsBuildMonitor buildMonitor = new VsBuildMonitor();
    private bool disposed;
    private string buildStatus = "";
    public event EventHandler? Changed;

    public async Task<VsContext> ReadAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (disposed) return new VsContext();
        buildMonitor.Initialize();
        if (folders == null)
        {
            var componentModel = ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel)) as IComponentModel;
            folders = componentModel?.GetService<IVsFolderWorkspaceService>();
            if (folders == null) return new VsContext { Note = "The VS workspace service is unavailable. Select directories manually." };
            folders.OnActiveWorkspaceChanged += WorkspaceChangedAsync;
        }
        var current = folders.CurrentWorkspace;
        if (!ReferenceEquals(current, workspace))
        {
            DetachWorkspace(); workspace = current;
            if (current != null)
            {
                var config = await current.GetProjectConfigurationServiceAsync();
                var eval = await current.GetPropertyEvaluatorServiceAsync();
                var build = await current.GetBuildServiceAsync();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (disposed || !ReferenceEquals(current, folders.CurrentWorkspace))
                { DetachWorkspace(); return new VsContext { Note = "The workspace is switching." }; }
                configurations = config; evaluator = eval; builds = build;
                if (configurations != null)
                {
                    configurations.OnPropertyChanged += ConfigurationPropertyChangedAsync;
                    configurations.OnBuildConfigurationChanged += BuildConfigurationChangedAsync;
                }
                if (evaluator != null) evaluator.OnPropertyVariablesChanged += VariablesChangedAsync;
                if (builds != null) { builds.BeginProjectBuild += BuildBeginAsync; builds.EndProjectBuild += BuildEndAsync; }
            }
        }
        if (current == null) return new VsContext { Note = "Open a CMake folder in VS, or switch to manual mode." };
        var project = configurations?.CurrentProject;
        var source = current.Location;
        if (project != null && string.Equals(Path.GetFileName(project.FilePath), "CMakeLists.txt", StringComparison.OrdinalIgnoreCase))
            source = Path.GetDirectoryName(current.MakeRooted(project.FilePath))!;
        var scope = Path.Combine(source, "CMakeLists.txt");
        string Evaluate(string expression)
        {
            var result = evaluator?.Evaluate(expression, scope, null, null);
            return result != null && result.IsSuccess && !string.IsNullOrWhiteSpace(result.Content) && !result.Content.Contains("${")
                ? result.Content : "";
        }
        var binary = Evaluate("${cmake.binaryDir}");
        if (binary.Length == 0) binary = Evaluate("${cmake.buildRoot}");
        var name = Evaluate("${cmake.name}");
        var active = configurations?.GetActiveProjectBuildConfiguration(project ?? new ProjectTargetFileContext(scope)) ?? "";
        var context = new VsContext { Workspace = current.Location, Source = source, Build = binary, Preset = name, Configuration = active, BuildStatus = buildStatus.Length > 0 ? buildStatus : buildMonitor.Status };
        if (!File.Exists(scope)) { context.Source = ""; context.Build = ""; context.Note = "The CMake source root is unknown. Select it manually."; }
        else if (binary.Length == 0) context.Note = "VS has not provided an expanded build directory. Wait for configuration services or select it manually.";
        else if (!Core.WorkspacePaths.IsLocalAbsolute(binary)) { context.Build = ""; context.Note = "The build directory is not a local absolute path. Remote builds are not supported."; }
        return context;
    }

    private Task SignalAsync()
    {
        if (!disposed) Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }
    private Task WorkspaceChangedAsync(object? sender, EventArgs e) => SignalAsync();
    private Task ConfigurationPropertyChangedAsync(object? sender, PropertyChangedEventArgs<ProjectConfigurationManagerProperties> e) => SignalAsync();
    private Task BuildConfigurationChangedAsync(object? sender, BuildConfigurationChangedEventArgs e) => SignalAsync();
    private Task VariablesChangedAsync(object? sender, PropertyVariablesChangedEventArgs e) => SignalAsync();
    private Task BuildBeginAsync(object? sender, ProjectBuildBeginEvent e) { buildStatus = "Building: " + e.BuildConfiguration; return SignalAsync(); }
    private Task BuildEndAsync(object? sender, ProjectBuildEndEvent e) { buildStatus = "Build finished: " + e.Result; return SignalAsync(); }
    private void DetachWorkspace()
    {
        if (configurations != null) { configurations.OnPropertyChanged -= ConfigurationPropertyChangedAsync; configurations.OnBuildConfigurationChanged -= BuildConfigurationChangedAsync; }
        if (evaluator != null) evaluator.OnPropertyVariablesChanged -= VariablesChangedAsync;
        if (builds != null) { builds.BeginProjectBuild -= BuildBeginAsync; builds.EndProjectBuild -= BuildEndAsync; }
        configurations = null; evaluator = null; builds = null; workspace = null;
        buildStatus = "";
        buildMonitor.Reset();
    }
    public void Dispose()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        disposed = true;
        if (folders != null) folders.OnActiveWorkspaceChanged -= WorkspaceChangedAsync;
        DetachWorkspace();
        buildMonitor.Dispose();
    }
}
