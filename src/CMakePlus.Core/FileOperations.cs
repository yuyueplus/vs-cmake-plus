using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace CMakePlus.Core;

public sealed class FileOperationPlan
{
    public string Root { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public bool Directory { get; set; }
    public bool Delete { get; set; }
    public string Description { get; set; } = "";
    public string State { get; set; } = "Prepared";
    public Dictionary<string, string> Hashes { get; set; } = new Dictionary<string, string>();
    public List<TargetEditPlan> Scripts { get; set; } = new List<TargetEditPlan>();
    [JsonIgnore]
    public string Preview => Description + (Delete ? "Delete to Recycle Bin\n" + From + "\nContains " + Hashes.Count + " files" : "Move / Rename\n" + From + "\n→ " + To) + "\n\n" + string.Join("\n", Scripts.Select(s => "--- " + s.ScriptPath + "\n" + s.Before + "\n+++\n" + s.After))
        + (Delete ? "\nTo recover, restore the files or directory from Windows Recycle Bin first, then use Recover Last File Operation to restore CMake references.\nCancel if Windows cannot recycle the items or asks to delete them permanently." : "")
        + "\nFiles and CMake scripts will be updated and saved. Use Recover Last File Operation to recover, not Ctrl+Z.\nC/C++ #include directives and paths inside source code are not updated; adjust them manually if needed.";
    [JsonIgnore]
    public string RecoveryPreview => (Delete ? "Restore CMake references for deleted items (restore their original paths from Windows Recycle Bin first)\n" + From : "Recover file operation\n" + To + "\n→ " + From) + "\n\n" + string.Join("\n", Scripts.Select(s => "--- " + s.ScriptPath + "\n" + s.After + "\n+++ Restore to\n" + s.Before)) + "\nRecovery requires unchanged files and scripts. Conflicting edits will not be overwritten.";
}

public static partial class FileOperations
{
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
    private static string Hash(string path) { using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path)) return Convert.ToBase64String(sha.ComputeHash(stream)); }
    private static string Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Take(3).SequenceEqual(new byte[] { 239, 187, 191 })) throw new InvalidOperationException("Convert the script to UTF-8 without BOM first: " + path);
        return Utf8.GetString(bytes);
    }
    private static string Destination(FileOperationPlan plan, string old) => plan.Directory ? Path.Combine(plan.To, Paths.Relative(plan.From, old).Replace('/', Path.DirectorySeparatorChar)) : plan.To;
    public static FileOperationPlan PlanDelete(CMakeSnapshot snapshot, string from) => Plan(snapshot, from, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(from))!, ".cmakeplus-delete-" + Guid.NewGuid().ToString("N")), true);
    public static FileOperationPlan Plan(CMakeSnapshot snapshot, string from, string to, bool delete = false)
    {
        var root = snapshot.SourceDirectory;
        from = Path.GetFullPath(from); to = Path.GetFullPath(to);
        TargetEditing.ValidatePath(root, from); TargetEditing.ValidatePath(root, to);
        if (Paths.Equal(from, to)) throw new InvalidOperationException("The paths are identical. Case-only renames are not supported.");
        if (File.Exists(to) || Directory.Exists(to)) throw new IOException("The destination already exists and will not be overwritten.");
        if (!Directory.Exists(Path.GetDirectoryName(to))) throw new IOException("The destination parent directory does not exist.");
        var directory = Directory.Exists(from);
        if (!directory && !File.Exists(from)) throw new FileNotFoundException("The source path does not exist.", from);
        if (directory && Paths.Within(from, to)) throw new InvalidOperationException("Cannot move a directory into itself.");
        var plan = new FileOperationPlan { Root = root, From = from, To = to, Directory = directory, Delete = delete };
        var files = directory ? Enumerate(from).ToArray() : new[] { from };
        if (files.Any(p => Path.GetFileName(p).Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(p).Equals(".cmake", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(p).StartsWith("CMakePresets", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("CMake scripts and directories containing them cannot be moved or deleted automatically. Their location affects relative path semantics.");
        var targets = snapshot.Configurations.SelectMany(c => c.Targets).ToArray();
        var set = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        if (delete && targets.Any(t => t.Sources.Any(set.Contains) && !t.Sources.Any(p => !set.Contains(p) && new[] { ".c", ".cc", ".cpp", ".cxx", ".m", ".mm", ".cu", ".f", ".f90", ".s", ".asm" }.Contains(Path.GetExtension(p).ToLowerInvariant()))))
            throw new InvalidOperationException("Deletion would leave a target with no compilable source files. Adjust its declaration first.");
        if (targets.Any(t => t.GeneratedSources.Any(set.Contains))) throw new InvalidOperationException("Generated files cannot be moved or deleted.");
        foreach (var file in files) plan.Hashes[file] = Hash(file);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var script in snapshot.Inputs.Concat(targets.Select(t => t.DeclarationFile)).Where(p => p.Length > 0 && Paths.Within(root, p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Path.GetFileName(script) != "CMakeLists.txt" && Path.GetExtension(script) != ".cmake") continue;
            var before = Read(script); var edits = new List<Tuple<int, int, string>>(); int depth = 0;
            foreach (var command in CMakeSyntax.Parse(before))
            {
                if (new[] { "endif", "endforeach", "endwhile", "endfunction", "endmacro", "endblock" }.Contains(command.Name)) depth--;
                bool sourceCommand = new[] { "add_executable", "add_library", "target_sources" }.Contains(command.Name);
                var owner = command.Arguments.FirstOrDefault() ?? "";
                bool affected = targets.Any(t => t.Name == owner && t.Sources.Any(set.Contains));
                if (sourceCommand && affected && (depth != 0 || command.Arguments.Any(a => a.IndexOfAny(new[] { '$', ';', '\\' }) >= 0) || command.Arguments.Contains("FILE_SET")))
                    throw new InvalidOperationException("Cannot safely update references: " + script + " · " + owner + " (conditional, variable, or FILE_SET reference).");
                for (int i = 0; i < command.Arguments.Count; i++)
                {
                    var arg = command.Arguments[i];
                    if (string.IsNullOrEmpty(arg) || arg.IndexOfAny(new[] { '$', ';', '\\', '<', '>', '|', '*', '?' }) >= 0) continue;
                    string absolute;
                    try { absolute = Path.GetFullPath(Path.IsPathRooted(arg) ? arg : Path.Combine(Path.GetDirectoryName(script)!, arg)); } catch (ArgumentException) { continue; }
                    if (!set.Contains(absolute) && !(directory && Paths.Equal(absolute, from))) continue;
                    if (!sourceCommand || i == 0 || depth != 0 || !set.Contains(absolute)) throw new InvalidOperationException("Indirect source references require manual editing: " + script + " · " + command.Name);
                    var span = command.ArgumentSpans[i];
                    edits.Add(Tuple.Create(span.Item1, span.Item2, delete ? "" : "\"" + Paths.Relative(Path.GetDirectoryName(script)!, Destination(plan, absolute)) + "\""));
                    found.Add(owner + "|" + absolute);
                }
                if (new[] { "if", "foreach", "while", "function", "macro", "block" }.Contains(command.Name)) depth++;
            }
            if (edits.Count > 0)
            {
                var after = before;
                foreach (var edit in edits.OrderByDescending(e => e.Item1)) after = after.Remove(edit.Item1, edit.Item2).Insert(edit.Item1, edit.Item3);
                plan.Scripts.Add(new TargetEditPlan { Root = root, ScriptPath = script, Before = before, After = after });
            }
        }
        foreach (var target in targets)
            foreach (var file in target.Sources.Where(set.Contains))
                if (!found.Contains(target.Name + "|" + file)) throw new InvalidOperationException("Cannot locate direct references in all configurations: " + target.Name + " · " + file);
        return plan;
    }
    private static IEnumerable<string> Enumerate(string directory)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            var attr = File.GetAttributes(path);
            if ((attr & FileAttributes.ReparsePoint) != 0) throw new IOException("The directory contains a link. Operation stopped: " + path);
            if ((attr & FileAttributes.Directory) != 0) { foreach (var file in Enumerate(path)) yield return file; }
            else yield return path;
        }
    }
    private static void AtomicWrite(string path, string text)
    {
        var temp = path + ".cmakeplus-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, text, Utf8);
        if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
    }
    public static string JournalDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CMakePlus", "operations");
    public static string Apply(FileOperationPlan plan)
    {
        if (plan.Delete) throw new InvalidOperationException("Deletion requires the Recycle Bin entry point.");
        Validate(plan, false);
        Directory.CreateDirectory(JournalDirectory);
        var journal = Path.Combine(JournalDirectory, DateTime.UtcNow.ToString("yyyyMMddHHmmssfffffff") + "-" + Guid.NewGuid().ToString("N") + ".json");
        AtomicWrite(journal, JsonConvert.SerializeObject(plan));
        try
        {
            if (plan.Directory) Directory.Move(plan.From, plan.To); else File.Move(plan.From, plan.To);
            foreach (var script in plan.Scripts)
            {
                if (Read(script.ScriptPath) != script.Before) throw new IOException("The script changed during the operation: " + script.ScriptPath);
                AtomicWrite(script.ScriptPath, script.After);
            }
            plan.State = "Applied"; AtomicWrite(journal, JsonConvert.SerializeObject(plan));
        }
        catch (Exception ex) { throw new IOException("The operation did not complete. Recovery journal: " + journal + "\n" + ex.Message, ex); }
        return journal;
    }
    public static string ApplyDelete(FileOperationPlan plan, Action<string, bool> recycle)
    {
        if (!plan.Delete) throw new InvalidOperationException("This is not a deletion plan.");
        Validate(plan, false);
        plan.State = "Prepared";
        Directory.CreateDirectory(JournalDirectory);
        var journal = Path.Combine(JournalDirectory, DateTime.UtcNow.ToString("yyyyMMddHHmmssfffffff") + "-" + Guid.NewGuid().ToString("N") + ".json");
        AtomicWrite(journal, JsonConvert.SerializeObject(plan));
        try
        {
            foreach (var script in plan.Scripts)
            {
                if (Read(script.ScriptPath) != script.Before) throw new IOException("The script changed during the operation: " + script.ScriptPath);
                AtomicWrite(script.ScriptPath, script.After);
            }
            Validate(plan, true);
            recycle(plan.From, plan.Directory);
            if (File.Exists(plan.From) || Directory.Exists(plan.From)) throw new IOException("The Recycle Bin operation did not complete.");
            plan.State = "Deleted"; AtomicWrite(journal, JsonConvert.SerializeObject(plan));
        }
        catch (Exception ex)
        {
            // Roll back script edits when recycling was canceled and all source contents remain intact.
            try { Validate(plan, true); Recover(journal); }
            catch { /* Keep the prepared journal for explicit recovery after a partial failure. */ }
            throw new IOException("Deletion did not complete: " + ex.Message + "\nTo recover, restore the original paths from Recycle Bin, then use the recovery menu. Journal: " + journal, ex);
        }
        return journal;
    }
    public static FileOperationPlan LoadJournal(string path) => JsonConvert.DeserializeObject<FileOperationPlan>(File.ReadAllText(path, Utf8)) ?? throw new InvalidDataException("Invalid recovery journal.");
    public static string? Latest(string root) => Directory.Exists(JournalDirectory) ? Directory.EnumerateFiles(JournalDirectory, "*.json").OrderByDescending(p => p).FirstOrDefault(p => { var plan = LoadJournal(p); return Paths.Equal(plan.Root, root) && plan.State != "Recovered"; }) : null;
    private static void Validate(FileOperationPlan plan, bool recovery)
    {
        TargetEditing.ValidatePath(plan.Root, plan.From); TargetEditing.ValidatePath(plan.Root, plan.To);
        var moved = !plan.Delete && (File.Exists(plan.To) || Directory.Exists(plan.To));
        if (plan.Delete && !File.Exists(plan.From) && !Directory.Exists(plan.From)) throw new IOException("Restore the file or directory from Windows Recycle Bin to its original path first: " + plan.From);
        if (!recovery && moved) throw new IOException("The destination path already exists.");
        if (moved && (File.Exists(plan.From) || Directory.Exists(plan.From))) throw new IOException("Both original and destination paths exist. Automatic recovery is unavailable.");
        foreach (var file in plan.Hashes)
        {
            var current = moved ? Destination(plan, file.Key) : file.Key;
            TargetEditing.ValidatePath(plan.Root, current);
            if (!File.Exists(current) || Hash(current) != file.Value) throw new IOException("The file changed. Operation stopped: " + current);
        }
        if (plan.Directory)
        {
            var current = moved ? plan.To : plan.From;
            if (!Directory.Exists(current) || Enumerate(current).Count() != plan.Hashes.Count) throw new IOException("Directory contents changed. Operation stopped.");
        }
        foreach (var script in plan.Scripts)
        {
            TargetEditing.ValidatePath(plan.Root, script.ScriptPath);
            if (!recovery && (File.GetAttributes(script.ScriptPath) & FileAttributes.ReadOnly) != 0) throw new IOException("The script is read-only: " + script.ScriptPath);
            var text = Read(script.ScriptPath);
            if (text != script.Before && (!recovery || text != script.After)) throw new IOException("The script was modified elsewhere. Operation stopped: " + script.ScriptPath);
        }
    }
    public static void Recover(string journal)
    {
        var plan = LoadJournal(journal);
        if (plan.State == "Recovered") throw new InvalidOperationException("This operation has already been recovered.");
        Validate(plan, true);
        // Content checks allow retry after any interrupted recovery step.
        foreach (var script in plan.Scripts)
        {
            var current = Read(script.ScriptPath);
            if (current == script.Before) continue;
            if (current != script.After) throw new IOException("The script changed during recovery: " + script.ScriptPath);
            AtomicWrite(script.ScriptPath, script.Before);
        }
        if (!plan.Delete)
        {
            if (plan.Directory) { if (Directory.Exists(plan.To)) Directory.Move(plan.To, plan.From); }
            else if (File.Exists(plan.To)) File.Move(plan.To, plan.From);
        }
        plan.State = "Recovered"; AtomicWrite(journal, JsonConvert.SerializeObject(plan));
    }
}
