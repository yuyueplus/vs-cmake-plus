using System;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using EnvDTE;

namespace CMakePlus;

internal sealed class VsBuildMonitor : IVsUpdateSolutionEvents, IDisposable
{
    private IVsSolutionBuildManager? manager;
    private uint cookie;
    private BuildEvents? dteBuildEvents;
    private DTE? dte;
    public string Status { get; private set; } = "Native build result unavailable. Check VS Output.";
    public bool IsBuilding { get; private set; }
    public event EventHandler? Changed;
    public void Reset() { IsBuilding = false; Status = "Native build result unavailable. Check VS Output."; }
    public void Initialize()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (manager != null) return;
        manager = ServiceProvider.GlobalProvider.GetService(typeof(SVsSolutionBuildManager)) as IVsSolutionBuildManager;
        if (manager != null) ErrorHandler.ThrowOnFailure(manager.AdviseUpdateSolutionEvents(this, out cookie));
        dte = ServiceProvider.GlobalProvider.GetService(typeof(DTE)) as DTE;
        dteBuildEvents = dte?.Events.BuildEvents;
        if (dteBuildEvents != null) { dteBuildEvents.OnBuildBegin += BuildBegin; dteBuildEvents.OnBuildDone += BuildDone; }
    }
    private void BuildBegin(vsBuildScope scope, vsBuildAction action) { IsBuilding = true; Status = "VS is building"; Changed?.Invoke(this, EventArgs.Empty); }
    private void BuildDone(vsBuildScope scope, vsBuildAction action)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        IsBuilding = false;
        Status = "VS build finished (check native output for the result)";
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public int UpdateSolution_Begin(ref int pfCancelUpdate) { IsBuilding = true; Status = "VS is building"; Changed?.Invoke(this, EventArgs.Empty); return VSConstants.S_OK; }
    public int UpdateSolution_Done(int fSucceeded, int fModified, int fCancelCommand) { IsBuilding = false; Status = fCancelCommand != 0 ? "VS build canceled" : fSucceeded != 0 ? "VS build succeeded" : "VS build failed"; Changed?.Invoke(this, EventArgs.Empty); return VSConstants.S_OK; }
    public int UpdateSolution_Cancel() { IsBuilding = false; Status = "VS build canceled"; Changed?.Invoke(this, EventArgs.Empty); return VSConstants.S_OK; }
    public int UpdateSolution_StartUpdate(ref int pfCancelUpdate) => VSConstants.S_OK;
    public int OnActiveProjectCfgChange(IVsHierarchy pIVsHierarchy) { Changed?.Invoke(this, EventArgs.Empty); return VSConstants.S_OK; }
    public void Dispose()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (manager != null && cookie != 0) manager.UnadviseUpdateSolutionEvents(cookie);
        if (dteBuildEvents != null) { dteBuildEvents.OnBuildBegin -= BuildBegin; dteBuildEvents.OnBuildDone -= BuildDone; }
        dteBuildEvents = null; dte = null;
        manager = null; cookie = 0;
    }
}
