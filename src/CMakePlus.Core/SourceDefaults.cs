using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CMakePlus.Core;

public sealed class SourceDefaults
{
    public string RelativePath { get; private set; } = "new_file.cpp";
    public CMakeTarget? Target { get; private set; }
    public static SourceDefaults ForSelection(string root, string? selection, IEnumerable<CMakeTarget> targets, CMakeTarget? explicitTarget = null)
    {
        var all = targets.ToArray();
        var selected = selection;
        if (string.IsNullOrEmpty(selected) && explicitTarget != null) selected = explicitTarget.DeclarationFile;
        var directory = string.IsNullOrEmpty(selected) ? root : Directory.Exists(selected) ? selected! : Path.GetDirectoryName(selected)!;
        if (!Paths.Equal(root, directory) && !Paths.Within(root, directory)) directory = root;
        var result = new SourceDefaults { RelativePath = Paths.Relative(root, Path.Combine(directory, "new_file.cpp")), Target = explicitTarget };
        if (result.Target != null) return result;
        var owners = all.Where(t => selected != null && t.Sources.Any(p => Paths.Equal(p, selected))).ToArray();
        if (owners.Length == 1) { result.Target = owners[0]; return result; }
        if (owners.Length > 1) return result;
        var candidates = all.Where(t => !string.IsNullOrEmpty(t.DeclarationFile))
            .Select(t => new { Target = t, Directory = Path.GetDirectoryName(t.DeclarationFile)! })
            .Where(t => Paths.Equal(t.Directory, directory) || Paths.Within(t.Directory, directory))
            .OrderByDescending(t => t.Directory.Length).ToArray();
        if (candidates.Length > 0 && (candidates.Length == 1 || candidates[0].Directory.Length > candidates[1].Directory.Length)) result.Target = candidates[0].Target;
        return result;
    }
}
