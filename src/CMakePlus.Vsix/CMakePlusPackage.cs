using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;

namespace CMakePlus;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[InstalledProductRegistration("CMake Plus", "CMake project management", "0.8")]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideToolWindow(typeof(ExplorerWindow))]
[Guid("938571db-9a2f-4a93-8ef3-eec85d081a21")]
public sealed class CMakePlusPackage : AsyncPackage
{
    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commands)
        {
            commands.AddCommand(new MenuCommand((s, e) => JoinableTaskFactory.RunAsync(async () =>
            {
                await ShowToolWindowAsync(typeof(ExplorerWindow), 0, true, DisposalToken);
            }).FileAndForget("CMakePlus/ShowExplorer"), new CommandID(new Guid("b4bec21a-986e-49ba-b84d-282beee14e65"), 0x0100)));
            commands.AddCommand(new MenuCommand((s, e) => JoinableTaskFactory.RunAsync(async () =>
            {
                var window = await ShowToolWindowAsync(typeof(ExplorerWindow), 0, true, DisposalToken);
                if (window.Content is ExplorerControl control) await control.CreateProjectAsync();
            }).FileAndForget("CMakePlus/CreateProject"), new CommandID(new Guid("b4bec21a-986e-49ba-b84d-282beee14e65"), 0x0101)));
        }
    }
}

[Guid("5d821a86-6c35-44a8-9dd7-d354466a4d3b")]
public sealed class ExplorerWindow : ToolWindowPane
{
    public ExplorerWindow() : base(null)
    {
        Caption = "CMake Plus";
        Content = new ExplorerControl();
    }
    protected override void Dispose(bool disposing)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (disposing && Content is ExplorerControl control) control.Dispose();
        base.Dispose(disposing);
    }
}
