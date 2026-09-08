using System;
using System.IO;
using System.Linq;
using System.Text;
using CMakePlus.Core;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.TextManager.Interop;

namespace CMakePlus;

internal sealed class VsScriptEdit
{
    private readonly ITextBuffer buffer;
    private readonly byte[] disk;
    private readonly string path;
    public string Text { get; }
    public bool HasUnsavedEdits => Text != ScriptEncoding.ReadUtf8(disk);
    public void Save()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var component = (IComponentModel?)ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel)) ?? throw new InvalidOperationException("The VS editor service is unavailable.");
        if (!component.GetService<ITextDocumentFactoryService>().TryGetTextDocument(buffer, out var document))
            throw new InvalidOperationException("Cannot save the CMake script. Save it manually in VS.");
        document.Save();
    }
    public VsScriptEdit(string path)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        this.path = path;
        disk = File.ReadAllBytes(path);
        ScriptEncoding.ReadUtf8(disk);
        VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, path, VSConstants.LOGVIEWID.Code_guid,
            out _, out _, out _, out IVsTextView view);
        ErrorHandler.ThrowOnFailure(view.GetBuffer(out var lines));
        var component = (IComponentModel?)ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel)) ?? throw new InvalidOperationException("The VS editor service is unavailable.");
        buffer = component.GetService<IVsEditorAdaptersFactoryService>().GetDocumentBuffer((IVsTextBuffer)lines)
            ?? throw new InvalidOperationException("Cannot read the VS text buffer.");
        Text = buffer.CurrentSnapshot.GetText();
    }
    public void Apply(TargetEditPlan plan)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!Paths.Equal(path, plan.ScriptPath) || Text != plan.Before || buffer.CurrentSnapshot.GetText() != Text
            || !disk.SequenceEqual(File.ReadAllBytes(path)))
            throw new InvalidOperationException("The script changed after preview. Generate a new preview.");
        int start = 0, end = Text.Length, nextEnd = plan.After.Length;
        while (start < end && start < nextEnd && Text[start] == plan.After[start]) start++;
        while (end > start && nextEnd > start && Text[end - 1] == plan.After[nextEnd - 1]) { end--; nextEnd--; }
        var span = new Span(start, end - start);
        if (buffer.IsReadOnly(span)) throw new InvalidOperationException("The script is read-only and cannot be modified.");
        var component = (IComponentModel?)ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel)) ?? throw new InvalidOperationException("The VS editor service is unavailable.");
        var registry = component.GetService<ITextUndoHistoryRegistry>();
        if (!component.GetService<ITextDocumentFactoryService>().TryGetTextDocument(buffer, out var document))
            throw new InvalidOperationException("Cannot set the script save encoding. Editing stopped.");
        var history = registry.RegisterHistory(buffer);
        using (var transaction = history.CreateTransaction("CMake Plus: Edit Target"))
        using (var edit = buffer.CreateEdit())
        {
            if (!edit.Replace(span, plan.After.Substring(start, nextEnd - start)))
                throw new InvalidOperationException("The editor rejected the change.");
            TargetEditing.CreateFiles(plan);
            edit.Apply();
            if (edit.Canceled) throw new InvalidOperationException("The editor canceled the change. New files remain for inspection.");
            document.Encoding = new UTF8Encoding(false, true);
            transaction.Complete();
        }
    }
}
