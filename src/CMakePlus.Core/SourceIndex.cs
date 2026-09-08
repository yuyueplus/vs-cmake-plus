using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
namespace CMakePlus.Core;
public sealed class SourceIndex
{
    private readonly Dictionary<string, List<string>> owners = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    public SourceIndex(CMakeConfiguration configuration, CancellationToken cancellation = default)
    {
        foreach (var target in configuration.Targets)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in target.Sources)
            {
                cancellation.ThrowIfCancellationRequested();
                var key = Path.GetFullPath(source);
                if (!seen.Add(key)) continue;
                if (!owners.TryGetValue(key, out var names)) owners.Add(key, names = new List<string>());
                names.Add(target.Name);
            }
        }
    }
    public IReadOnlyList<string> Find(string path) => owners.TryGetValue(Path.GetFullPath(path), out var names) ? names : (IReadOnlyList<string>)Array.Empty<string>();
}
