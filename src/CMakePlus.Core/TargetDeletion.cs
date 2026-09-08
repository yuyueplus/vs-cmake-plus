using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CMakePlus.Core;

public static partial class FileOperations
{
    public static FileOperationPlan PlanTargetDirectoryDelete(CMakeSnapshot snapshot, string directory)
    {
        var root = snapshot.SourceDirectory;
        directory = Path.GetFullPath(directory);
        TargetEditing.ValidatePath(root, directory);
        var script = Path.Combine(directory, "CMakeLists.txt");
        if (!File.Exists(script)) throw new IOException("The target directory has no CMakeLists.txt.");
        var files = Enumerate(directory).ToArray();
        if (files.Any(p => !Paths.Equal(p, script) && (Path.GetFileName(p).Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(p).Equals(".cmake", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(p).IndexOf("Presets", StringComparison.OrdinalIgnoreCase) >= 0)))
            throw new InvalidOperationException("Nested CMake scripts or Presets require manual target removal.");
        var all = snapshot.Configurations.SelectMany(c => c.Targets).ToArray();
        var removed = all.Where(t => !string.IsNullOrEmpty(t.DeclarationFile) && Paths.Equal(t.DeclarationFile, script)).ToArray();
        var names = removed.Select(t => t.Name).Distinct(StringComparer.Ordinal).ToArray();
        if (names.Length != 1) throw new InvalidOperationException("Select a directory declaring exactly one configured target. Shared target directories require manual removal.");
        var name = names[0];
        if (removed.Any(t => !new[] { "EXECUTABLE", "STATIC_LIBRARY", "SHARED_LIBRARY" }.Contains(t.Type) || t.Sources.Any(p => !Paths.Within(directory, p))))
            throw new InvalidOperationException("This target type or its external source files require manual removal.");
        if (all.Except(removed).Any(t => t.Sources.Any(p => Paths.Within(directory, p))))
            throw new InvalidOperationException("Another target uses files inside this directory. Remove those source references first.");
        var local = CMakeSyntax.Parse(Read(script));
        var declarations = local.Where(c => c.Name == "add_library" || c.Name == "add_executable").ToArray();
        if (declarations.Length != 1 || declarations[0].Arguments.FirstOrDefault() != name)
            throw new InvalidOperationException("The directory must contain one direct target declaration.");
        var ownCommands = new[] { "add_library", "add_executable", "target_sources", "target_include_directories", "target_compile_features", "target_compile_definitions", "target_compile_options", "target_link_libraries", "target_link_options", "target_link_directories" };
        if (local.Any(c => !ownCommands.Contains(c.Name) || c.Arguments.FirstOrDefault() != name || c.Arguments.Contains("IMPORTED") || c.Arguments.Contains("ALIAS")))
            throw new InvalidOperationException("The target script contains additional commands. Review and remove this directory manually.");
        var plan = new FileOperationPlan { Root = root, From = directory, To = Path.Combine(Path.GetDirectoryName(directory)!, ".cmakeplus-delete-" + Guid.NewGuid().ToString("N")), Directory = true, Delete = true };
        foreach (var file in files) plan.Hashes[file] = Hash(file);
        plan.Description = "Remove target: " + name + "\nFiles to recycle:\n" + string.Join("\n", files.Select(p => Paths.Relative(directory, p))) + "\n\n";
        int parents = 0;
        var inputs = snapshot.Inputs.Concat(all.Select(t => t.DeclarationFile)).Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var input in inputs)
        {
            if (Paths.Within(directory, input) || !Paths.Within(root, input)) continue;
            if (Path.GetFileName(input) != "CMakeLists.txt" && Path.GetExtension(input) != ".cmake") continue;
            var before = Read(input); var edits = new List<Tuple<int, int>>(); var blocks = new Stack<string>();
            foreach (var command in CMakeSyntax.Parse(before))
            {
                var args = command.Arguments;
                if (new[] { "endif", "endforeach", "endwhile", "endfunction", "endmacro", "endblock" }.Contains(command.Name))
                {
                    if (blocks.Count == 0 || "end" + blocks.Pop() != command.Name) throw new InvalidOperationException("Mismatched CMake blocks: " + input);
                }
                bool dependency = command.Name == "target_link_libraries" || command.Name == "add_dependencies";
                bool subdirectory = command.Name == "add_subdirectory";
                if ((dependency || subdirectory) && args.Any(a => a.IndexOfAny(new[] { '$', ';', '\\' }) >= 0))
                    throw new InvalidOperationException("Unresolved dependency or subdirectory arguments require manual removal: " + input);
                bool entry = subdirectory && args.Count > 0 && Paths.Equal(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(input)!, args[0])), directory);
                var references = args.Select((arg, index) => new { arg, index }).Where(a => a.arg == name).ToArray();
                if (entry)
                {
                    if (blocks.Count > 0) throw new InvalidOperationException("Conditional subdirectory references require manual removal: " + input);
                    parents++; edits.Add(Tuple.Create(command.StartOffset, command.EndOffset - command.StartOffset));
                }
                else if (dependency && references.Length > 0)
                {
                    if (blocks.Count > 0 || references.Any(a => a.index == 0) || args.Any(a => a == "debug" || a == "optimized" || a == "general"))
                        throw new InvalidOperationException("Complex target dependencies require manual removal: " + input);
                    var remaining = args.Skip(1).Where(a => a != name && a != "PRIVATE" && a != "PUBLIC" && a != "INTERFACE").ToArray();
                    if (remaining.Length == 0) edits.Add(Tuple.Create(command.StartOffset, command.EndOffset - command.StartOffset));
                    else foreach (var reference in references) edits.Add(command.ArgumentSpans[reference.index]);
                }
                else
                {
                    foreach (var arg in args)
                    {
                        if (arg == name || (arg.Contains("$") && (arg.Contains(name) || arg.Contains(Path.GetFileName(directory)))))
                            throw new InvalidOperationException("Additional target references require manual removal: " + input + " · " + command.Name);
                        if (arg.Length == 0 || arg.IndexOfAny(new[] { '$', ';', '<', '>', '|', '*', '?' }) >= 0) continue;
                        string path;
                        try { path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(input)!, arg)); }
                        catch (ArgumentException) { continue; }
                        if (Paths.Equal(directory, path) || Paths.Within(directory, path)) throw new InvalidOperationException("Another command references the target directory: " + input + " · " + command.Name);
                    }
                }
                if (new[] { "if", "foreach", "while", "function", "macro", "block" }.Contains(command.Name)) blocks.Push(command.Name);
            }
            if (blocks.Count != 0) throw new InvalidOperationException("Unclosed CMake blocks: " + input);
            if (edits.Count > 0)
            {
                var after = before;
                foreach (var edit in edits.OrderByDescending(e => e.Item1)) after = after.Remove(edit.Item1, edit.Item2);
                plan.Scripts.Add(new TargetEditPlan { Root = root, ScriptPath = input, Before = before, After = after });
            }
        }
        if (parents != 1) throw new InvalidOperationException("Cannot locate one unconditional add_subdirectory reference for this target directory.");
        return plan;
    }
}
