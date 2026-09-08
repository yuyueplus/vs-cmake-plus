using System;
using System.Collections.Generic;
using System.Linq;
namespace CMakePlus.Core;

// Watcher threads only mutate this bounded batch. The UI consumes at a fixed cadence.
public sealed class DirectoryChangeBatch
{
    private readonly object gate = new object();
    private readonly HashSet<string> directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private bool pending, invalid, all;
    public bool Invalidated { get { lock (gate) return invalid; } }
    public void Add(bool invalidate, bool refreshAll = false, string? directory = null, string? oldDirectory = null)
    {
        lock (gate)
        {
            pending = true; invalid |= invalidate; all |= refreshAll;
            if (!all)
            {
                if (directory != null) directories.Add(directory);
                if (oldDirectory != null) directories.Add(oldDirectory);
                if (directories.Count > 2048) all = true;
            }
            if (all) directories.Clear();
        }
    }
    public Batch? Take()
    {
        lock (gate)
        {
            if (!pending) return null;
            var result = new Batch { Invalidated = invalid, All = all, Directories = directories.ToArray() };
            directories.Clear(); pending = invalid = all = false;
            return result;
        }
    }
    public sealed class Batch
    {
        public bool Invalidated { get; internal set; }
        public bool All { get; internal set; }
        public string[] Directories { get; internal set; } = Array.Empty<string>();
    }
}
