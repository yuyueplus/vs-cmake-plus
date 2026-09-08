using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CMakePlus.Core;

namespace CMakePlus;

internal sealed class TargetNode : INotifyPropertyChanged
{
    public string Label { get; set; } = "";
    public string Path { get; set; } = "";
    public CMakeTarget Target { get; set; } = null!;
    public bool IsExpanded { get; set; }
    public bool IsSelected { get; set; }
    public IReadOnlyList<TargetNode> Children { get; private set; } = Array.Empty<TargetNode>();
    private Func<TargetNode[]>? loader;
    private Task? loading;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Defer(Func<TargetNode[]> factory)
    {
        loader = factory;
        Children = new[] { new TargetNode { Label = "Loading…", Target = Target } };
    }
    public Task LoadAsync() => loading == null || loading.IsFaulted ? (loading = LoadCoreAsync()) : loading;
    private async Task LoadCoreAsync()
    {
        if (loader == null) return;
        Children = await Task.Run(loader);
        loader = null;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Children)));
    }
    private static TargetNode[] Files(string root, CMakeTarget target, string[] sources)
    {
        if (sources.Length > 500)
            return Enumerable.Range(0, (sources.Length + 499) / 500).Select(page =>
            {
                var node = new TargetNode { Label = $"Files {page * 500 + 1}–{Math.Min(sources.Length, (page + 1) * 500)}", Target = target };
                node.Defer(() => Files(root, target, sources.Skip(page * 500).Take(500).ToArray()));
                return node;
            }).ToArray();
        return sources.Select(source => new TargetNode { Label = System.IO.Path.GetFileName(source) + (target.GeneratedSources.Contains(source) ? " [generated]" : "") + (!Paths.Within(root, source) ? " [external]" : ""), Path = source, Target = target }).ToArray();
    }
    public static IEnumerable<TargetNode> Build(string root, IEnumerable<CMakeTarget> targets)
    {
        foreach (var target in targets.OrderBy(t => t.Name))
        {
            var node = new TargetNode { Label = target.ToString(), Path = target.DeclarationFile, Target = target };
            if (target.Sources.Count > 0) node.Defer(() => target.Sources.GroupBy(p => System.IO.Path.GetDirectoryName(p)).OrderBy(g => g.Key).Select(group =>
            {
                var directory = group.Key!;
                var folder = new TargetNode { Label = Paths.Equal(root, directory) ? "." : Paths.Within(root, directory) ? Paths.Relative(root, directory) : "External directory · " + directory, Path = directory, Target = target };
                var sources = group.OrderBy(p => p).ToArray();
                folder.Defer(() => Files(root, target, sources));
                return folder;
            }).ToArray());
            yield return node;
        }
    }
}
