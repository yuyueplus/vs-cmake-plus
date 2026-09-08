using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CMakePlus.Core;
namespace CMakePlus;
public sealed class FileNode : INotifyPropertyChanged
{
    public string Path { get; }
    public bool IsDirectory { get; }
    private bool expanded, selected;
    public bool IsExpanded { get => expanded; set { if (expanded == value) return; expanded = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded))); } }
    public bool IsSelected { get => selected; set { if (selected == value) return; selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); } }
    public string? DisplayLabel { get; set; }
    public string Label => DisplayLabel ?? System.IO.Path.GetFileName(Path);
    private readonly Func<string, bool> ignore;
    public ObservableCollection<FileNode> Children { get; private set; } = new ObservableCollection<FileNode>();
    private bool loaded;
    private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
    public event PropertyChangedEventHandler? PropertyChanged;
    public FileNode(string path, bool directory, bool placeholder = false, Func<string, bool>? ignore = null)
    {
        this.ignore = ignore ?? IsIgnored;
        Path = path; IsDirectory = directory;
        if (directory && !placeholder) Children.Add(new FileNode("Loading…", false, true));
    }
    public static bool IsIgnored(string name) => name == ".git" || name == ".vs" || name == ".cmake" || name == "out" || name == "build" || name.StartsWith("cmake-build-", StringComparison.OrdinalIgnoreCase);
    public async Task LoadAsync()
    {
        if (!IsDirectory) return;
        await gate.WaitAsync();
        try { if (!loaded) await LoadCoreAsync(); }
        finally { gate.Release(); }
    }
    private async Task LoadCoreAsync()
    {
            var entries = await Task.Run(() =>
            {
                if (!Directory.Exists(Path)) return Array.Empty<Tuple<string, bool>>();
                return Directory.EnumerateFileSystemEntries(Path)
                    .Select(p => new { Path = p, Attributes = File.GetAttributes(p) })
                    .Where(p => (p.Attributes & FileAttributes.ReparsePoint) == 0)
                    .Where(p => (p.Attributes & FileAttributes.Directory) == 0 || !ignore(System.IO.Path.GetFileName(p.Path)))
                    .Select(p => Tuple.Create(p.Path, (p.Attributes & FileAttributes.Directory) != 0))
                    .OrderByDescending(p => p.Item2).ThenBy(p => p.Item1, StringComparer.OrdinalIgnoreCase).ToArray();
            });
            var existing = Children.ToDictionary(n => n.Path, StringComparer.OrdinalIgnoreCase);
            var desired = await Task.Run(() => entries.Select(p => existing.TryGetValue(p.Item1, out var node) && node.IsDirectory == p.Item2 ? node : new FileNode(p.Item1, p.Item2, ignore: ignore)).ToArray());
            bool changed = !Children.SequenceEqual(desired);
            if (changed) Children = new ObservableCollection<FileNode>(desired);
            loaded = true;
            if (changed) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Children)));
    }
    public async Task RefreshAsync(HashSet<string>? dirty = null)
    {
        if (!loaded) return;
        await gate.WaitAsync();
        try { if (dirty == null || dirty.Contains(Path)) await LoadCoreAsync(); }
        finally { gate.Release(); }
        foreach (var child in Children.Where(c => c.IsDirectory).ToArray())
            if (dirty == null || dirty.Any(p => Paths.Equal(child.Path, p) || Paths.Within(child.Path, p))) await child.RefreshAsync(dirty);
    }
}
