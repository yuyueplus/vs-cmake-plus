using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CMakePlus.Core;

public sealed class TargetEditPlan
{
    public string Root { get; set; } = "";
    public string ScriptPath { get; set; } = "";
    public string Before { get; set; } = "";
    public string After { get; set; } = "";
    public Dictionary<string, string> NewFiles { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public string NewDirectory { get; set; } = "";
    public string Preview => "Modify " + ScriptPath + "\n\n--- Before\n" + Before + "\n+++ After\n" + After
        + string.Concat(NewFiles.Select(f => "\n+++ Create " + f.Key + "\n" + f.Value))
        + "\nApply Changes saves this CMake script as UTF-8 without BOM and waits for VS configuration. Existing unsaved edits require confirmation. Script changes enter VS undo history; undo does not delete newly created files.";
}

public static class TargetEditing
{
    private static readonly string[] Opens = { "if", "foreach", "while", "function", "macro", "block" };
    private static readonly string[] Closes = { "endif", "endforeach", "endwhile", "endfunction", "endmacro", "endblock" };
    private static readonly string[] Options = { "STATIC", "SHARED", "MODULE", "OBJECT", "WIN32", "MACOSX_BUNDLE", "EXCLUDE_FROM_ALL", "PUBLIC", "PRIVATE", "INTERFACE" };
    public static void ValidatePath(string root, string path)
    {
        if (!Paths.Within(root, path)) throw new InvalidOperationException("The path must be inside the source root.");
        SourceEditor.RejectLinks(root, path);
        if (Paths.Relative(root, path).IndexOfAny(new[] { '$', ';', '"', '\r', '\n', '[', ']', ':', '<', '>', '|', '?', '*' }) >= 0)
            throw new InvalidOperationException("The path contains unsupported special characters.");
    }
    private static void ValidateTarget(string root, CMakeTarget target)
    {
        if (!new[] { "EXECUTABLE", "STATIC_LIBRARY", "SHARED_LIBRARY", "OBJECT_LIBRARY", "MODULE_LIBRARY" }.Contains(target.Type))
            throw new InvalidOperationException("This target type is read-only.");
        if (!Regex.IsMatch(target.Name, "^[A-Za-z_][A-Za-z0-9_.+-]*$")) throw new InvalidOperationException("The target name is not a supported literal.");
        if (string.IsNullOrEmpty(target.DeclarationFile)) throw new InvalidOperationException("Cannot locate the target declaration.");
        ValidatePath(root, target.DeclarationFile);
    }
    // Every relevant command must be literal and unconditional. Do not infer variable expansion.
    private static List<CMakeCommand> Relevant(string text, string name)
    {
        var blocks = new Stack<string>();
        var relevant = new List<CMakeCommand>();
        foreach (var c in CMakeSyntax.Parse(text))
        {
            if (Closes.Contains(c.Name) && (blocks.Count == 0 || "end" + blocks.Pop() != c.Name)) throw new InvalidDataException("Incomplete CMake block structure.");
            if (new[] { "add_library", "add_executable", "target_sources" }.Contains(c.Name) && c.Arguments.FirstOrDefault() == name)
            {
                if (blocks.Count != 0 || c.Arguments.Any(a => a.IndexOfAny(new[] { '$', ';', '\\' }) >= 0)
                    || c.Arguments.Skip(1).Any(a => a == "ALIAS" || a == "IMPORTED" || a == "FILE_SET")
                    || (c.Name == "add_library" && c.Arguments.Contains("INTERFACE")))
                    throw new InvalidOperationException("This target uses conditional, variable, function, imported, or FILE_SET declarations. Edit it manually.");
                relevant.Add(c);
            }
            if (Opens.Contains(c.Name)) blocks.Push(c.Name);
        }
        if (blocks.Count != 0 || relevant.Count(c => c.Name != "target_sources") != 1)
            throw new InvalidOperationException("No unique unconditional literal target declaration found.");
        return relevant;
    }
    public static TargetEditPlan Membership(string root, CMakeTarget target, IEnumerable<string> selected, bool add, string text)
    {
        ValidateTarget(root, target);
        var commands = Relevant(text, target.Name);
        var paths = selected.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0) throw new InvalidOperationException("Select at least one file.");
        foreach (var path in paths)
        {
            ValidatePath(root, path);
            if (target.GeneratedSources.Contains(path)) throw new InvalidOperationException("Generated files are read-only.");
            if (add && !File.Exists(path)) throw new InvalidOperationException("File does not exist: " + path);
        }
        var plan = new TargetEditPlan { Root = root, ScriptPath = target.DeclarationFile, Before = text };
        var directory = Path.GetDirectoryName(target.DeclarationFile)!;
        var declared = commands.SelectMany(c => c.Arguments.Skip(1).Where(a => !Options.Contains(a))
            .Select(a => Path.GetFullPath(Path.IsPathRooted(a) ? a : Path.Combine(directory, a)))).ToArray();
        if (add)
        {
            var additions = paths.Where(p => !declared.Any(d => Paths.Equal(d, p)) && !target.Sources.Any(d => Paths.Equal(d, p))).ToArray();
            if (additions.Length == 0) throw new InvalidOperationException("The selected files already belong to this target.");
            var declaration = commands.Single(c => c.Name != "target_sources");
            int at = EndOfLine(text, declaration.EndOffset);
            string nl = text.Contains("\r\n") ? "\r\n" : "\n";
            plan.After = text.Insert(at, nl + "target_sources(" + target.Name + " PRIVATE" + nl
                + string.Join(nl, additions.Select(p => "    \"" + Paths.Relative(directory, p) + "\"")) + nl + ")" + nl);
        }
        else
        {
            if (target.Sources.Count > 0 && !target.Sources.Any(p => !paths.Any(s => Paths.Equal(s, p))))
                throw new InvalidOperationException("Cannot remove all source files from this target.");
            var edits = new List<Tuple<int, int>>();
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in commands)
                for (int i = 1; i < c.Arguments.Count; i++)
                {
                    var value = c.Arguments[i];
                    if (Options.Contains(value)) continue;
                    var absolute = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(directory, value));
                    if (paths.Any(p => Paths.Equal(p, absolute))) { found.Add(absolute); edits.Add(c.ArgumentSpans[i]); }
                }
            if (paths.Any(p => !found.Contains(p))) throw new InvalidOperationException("Some references are not direct source declarations in this script. Editing stopped.");
            plan.After = text;
            foreach (var edit in edits.OrderByDescending(e => e.Item1)) plan.After = plan.After.Remove(edit.Item1, edit.Item2);
        }
        return plan;
    }
    private static int EndOfLine(string text, int at)
    {
        int end = text.IndexOf('\n', at); if (end < 0) end = text.Length;
        var tail = text.Substring(at, end - at).Trim();
        if (tail.Length > 0 && (!tail.StartsWith("#", StringComparison.Ordinal) || tail.StartsWith("#[", StringComparison.Ordinal)))
            throw new InvalidOperationException("Another command or block comment is on the same line. Cannot insert safely.");
        return end < text.Length ? end + 1 : end;
    }
    public static TargetEditPlan NewTarget(string root, string parentScript, string relativeDirectory, string name, string kind, IEnumerable<string> existingNames, string text)
    {
        ValidatePath(root, parentScript);
        if (Path.GetFileName(parentScript) != "CMakeLists.txt") throw new InvalidOperationException("The parent script must be CMakeLists.txt.");
        if (!Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$") || existingNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("The target name is invalid or already exists. Use letters, digits, and underscores.");
        if (!new[] { "EXECUTABLE", "STATIC", "SHARED" }.Contains(kind)) throw new InvalidOperationException("Unsupported target type.");
        if (Path.IsPathRooted(relativeDirectory) || string.IsNullOrWhiteSpace(relativeDirectory)) throw new InvalidOperationException("Enter a new relative directory.");
        var directory = Path.GetFullPath(Path.Combine(root, relativeDirectory));
        ValidatePath(root, directory);
        if (Directory.Exists(directory) || File.Exists(directory)) throw new InvalidOperationException("The new target directory already exists. Choose another directory.");
        if (!Directory.Exists(Path.GetDirectoryName(directory))) throw new InvalidOperationException("The parent of the new directory must already exist.");
        var commands = CMakeSyntax.Parse(text);
        var blocks = new Stack<string>();
        foreach (var command in commands)
        {
            if (Opens.Contains(command.Name)) blocks.Push(command.Name);
            else if (Closes.Contains(command.Name))
            {
                if (blocks.Count == 0 || "end" + blocks.Pop() != command.Name)
                    throw new InvalidOperationException("The parent script has mismatched control-flow blocks. Fix them before adding a target.");
            }
            else if ((command.Name == "else" || command.Name == "elseif") && (blocks.Count == 0 || blocks.Peek() != "if"))
                throw new InvalidOperationException("The parent script has an invalid conditional branch.");
            if (command.Name == "return")
                throw new InvalidOperationException("The parent script contains return(). The new subdirectory might not be reached; edit the script manually.");
        }
        if (blocks.Count != 0) throw new InvalidOperationException("Close all control-flow blocks before adding a target.");
        if (commands.Any(c => (c.Name == "add_library" || c.Name == "add_executable") && c.Arguments.FirstOrDefault() == name))
            throw new InvalidOperationException("The target already exists in the parent script.");
        var relative = Paths.Relative(Path.GetDirectoryName(parentScript)!, directory);
        if (commands.Any(c => c.Name == "add_subdirectory" && c.Arguments.Count > 0
            && (c.Arguments[0].IndexOfAny(new[] { '$', ';', '\\' }) >= 0
                || Paths.Equal(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(parentScript)!, c.Arguments[0])), directory))))
            throw new InvalidOperationException("The parent script already references this subdirectory or contains an unresolved subdirectory reference.");
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var plan = new TargetEditPlan { Root = root, ScriptPath = parentScript, Before = text, After = text + nl + "add_subdirectory(\"" + relative + "\")" + nl, NewDirectory = directory };
        var fileName = kind == "EXECUTABLE" ? "main.cpp" : name + ".cpp";
        plan.NewFiles[Path.Combine(directory, "CMakeLists.txt")] = (kind == "EXECUTABLE" ? "add_executable(" + name : "add_library(" + name + " " + kind) + " " + fileName + ")" + nl;
        plan.NewFiles[Path.Combine(directory, fileName)] = kind == "EXECUTABLE" ? "int main() { return 0; }" + nl
            : (kind == "SHARED" ? "#ifdef _WIN32\n__declspec(dllexport)\n#endif\n" : "") + "int " + name + "_value() { return 0; }" + nl;
        return plan;
    }
    public static void CreateFiles(TargetEditPlan plan)
    {
        foreach (var file in plan.NewFiles.Keys) { ValidatePath(plan.Root, file); if (File.Exists(file) || Directory.Exists(file)) throw new IOException("Path already exists: " + file); }
        if (plan.NewDirectory.Length > 0)
        {
            ValidatePath(plan.Root, plan.NewDirectory);
            if (Directory.Exists(plan.NewDirectory)) throw new IOException("The directory was created after preview. Try again.");
            Directory.CreateDirectory(plan.NewDirectory);
        }
        // CreateNew never overwrites external work. On failure retain artifacts for inspection.
        foreach (var file in plan.NewFiles)
        {
            ValidatePath(plan.Root, file.Key);
            using (var stream = new FileStream(file.Key, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = new UTF8Encoding(false, true).GetBytes(file.Value);
                stream.Write(bytes, 0, bytes.Length);
            }
        }
    }
}
