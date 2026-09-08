using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CMakePlus.Core;

public sealed class CMakeCommand
{
    public string Name { get; set; } = "";
    public int StartOffset { get; set; }
    public int EndOffset { get; set; }
    public List<string> Arguments { get; } = new List<string>();
    public List<Tuple<int, int>> ArgumentSpans { get; } = new List<Tuple<int, int>>();
}

// A lexical scanner, not a CMake evaluator. It preserves the original document
// and rejects ambiguous declarations instead of guessing variable expansion.
public static class CMakeSyntax
{
    public static IReadOnlyList<CMakeCommand> Parse(string text)
    {
        var commands = new List<CMakeCommand>();
        int i = 0;
        while (true)
        {
            Skip(text, ref i);
            if (i == text.Length) return commands;
            int start = i;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
            if (i == start) throw new InvalidDataException("Unrecognized CMake command. Automatic editing stopped.");
            var command = new CMakeCommand { Name = text.Substring(start, i - start).ToLowerInvariant(), StartOffset = start };
            Skip(text, ref i);
            if (i == text.Length || text[i++] != '(') throw new InvalidDataException("A CMake command is missing an opening parenthesis.");
            int depth = 1;
            while (depth > 0)
            {
                Skip(text, ref i);
                if (i == text.Length) throw new InvalidDataException("A CMake command is not closed.");
                if (text[i] == ')') { i++; depth--; continue; }
                if (text[i] == '(') { i++; depth++; continue; }
                int tokenStart = i;
                command.Arguments.Add(Token(text, ref i));
                command.ArgumentSpans.Add(Tuple.Create(tokenStart, i - tokenStart));
            }
            command.EndOffset = i;
            commands.Add(command);
        }
    }

    private static void Skip(string text, ref int i)
    {
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }
            if (text[i] != '#') return;
            i++;
            if (Bracket(text, ref i, out _)) continue;
            while (i < text.Length && text[i] != '\n') i++;
        }
    }

    private static string Token(string text, ref int i)
    {
        if (Bracket(text, ref i, out var bracket)) return bracket;
        var result = new StringBuilder();
        bool quoted = text[i] == '"';
        if (quoted) i++;
        while (i < text.Length)
        {
            char c = text[i];
            if (quoted && c == '"') { i++; return result.ToString(); }
            if (!quoted && (char.IsWhiteSpace(c) || c == '(' || c == ')' || c == '#')) return result.ToString();
            if (c == '\\')
            {
                i++;
                if (i == text.Length) throw new InvalidDataException("Incomplete CMake escape sequence.");
                // Keep escapes recognizable: escaped/expanded target names are unsupported.
                result.Append('\\').Append(text[i++]);
            }
            else { result.Append(c); i++; }
        }
        if (quoted) throw new InvalidDataException("Unterminated CMake string.");
        return result.ToString();
    }

    private static bool Bracket(string text, ref int i, out string value)
    {
        value = "";
        if (i >= text.Length || text[i] != '[') return false;
        int end = i + 1;
        while (end < text.Length && text[end] == '=') end++;
        if (end >= text.Length || text[end] != '[') return false;
        var close = "]" + new string('=', end - i - 1) + "]";
        int closeAt = text.IndexOf(close, end + 1, StringComparison.Ordinal);
        if (closeAt < 0) throw new InvalidDataException("Unterminated CMake bracket string.");
        value = text.Substring(end + 1, closeAt - end - 1);
        i = closeAt + close.Length;
        return true;
    }
}

public sealed class SourceEditPlan
{
    public string Root { get; internal set; } = "";
    public string SourcePath { get; internal set; } = "";
    public string ScriptPath { get; internal set; } = "";
    public string OriginalScript { get; internal set; } = "";
    public string UpdatedScript { get; internal set; } = "";
    public string SourceContent { get; internal set; } = "";
    public string Addition { get; internal set; } = "";
    internal byte[] OriginalBytes { get; set; } = Array.Empty<byte>();
    public string Preview => "Create " + SourcePath + Environment.NewLine + Environment.NewLine + "Insert after the target declaration in " + ScriptPath + Environment.NewLine + Addition;
}

public static class SourceEditor
{
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

    public static SourceEditPlan Plan(string root, CMakeTarget target, string relativePath, string? documentText = null)
    {
        root = Path.GetFullPath(root);
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.IndexOfAny(new[] { '$', ';', '"', '\r', '\n', '[', ']', ':' }) >= 0)
            throw new InvalidOperationException("Enter a workspace-relative path. CMake special characters are not supported.");
        var source = Path.GetFullPath(Path.Combine(root, relativePath));
        var extension = Path.GetExtension(source).ToLowerInvariant();
        if (!new[] { ".c", ".cc", ".cpp", ".cxx", ".h", ".hh", ".hpp", ".hxx" }.Contains(extension)) throw new InvalidOperationException("Only C/C++ source and header files are supported.");
        if (!Paths.Within(root, source)) throw new InvalidOperationException("The file must be inside the current workspace.");
        if (File.Exists(source) || Directory.Exists(source)) throw new IOException("The destination path already exists.");
        if (!Directory.Exists(Path.GetDirectoryName(source))) throw new DirectoryNotFoundException("Select an existing parent directory. Directories are not created implicitly.");
        if (target.Type != "EXECUTABLE" && target.Type != "STATIC_LIBRARY" && target.Type != "SHARED_LIBRARY" && target.Type != "MODULE_LIBRARY" && target.Type != "OBJECT_LIBRARY")
            throw new InvalidOperationException("Adding sources to this target type is not supported.");
        if (!Regex.IsMatch(target.Name, @"^[A-Za-z0-9_.+\-]+$")) throw new InvalidOperationException("The target name is not a supported literal.");
        var script = target.DeclarationFile;
        if (string.IsNullOrEmpty(script) || !Paths.Within(root, script) || !string.Equals(Path.GetFileName(script), "CMakeLists.txt", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only direct target declarations in a workspace CMakeLists.txt can be edited.");
        RejectLinks(root, source);
        RejectLinks(root, script);
        var bytes = File.ReadAllBytes(script);
        var diskText = ScriptEncoding.ReadUtf8(bytes);
        var original = documentText ?? diskText;
        int nesting = 0, declarations = 0, insertAt = 0;
        foreach (var command in CMakeSyntax.Parse(original))
        {
            if (new[] { "endif", "endforeach", "endwhile", "endfunction", "endmacro", "endblock" }.Contains(command.Name)) nesting--;
            if (nesting < 0) throw new InvalidDataException("Incomplete CMake block structure.");
            if ((command.Name == "add_library" || command.Name == "add_executable") && command.Arguments.FirstOrDefault() == target.Name)
            {
                if (nesting != 0 || command.Arguments.Skip(1).Any(x => x == "ALIAS" || x == "IMPORTED" || x == "INTERFACE"))
                    throw new InvalidOperationException("Conditional, function, macro, alias, or imported targets cannot be edited automatically.");
                declarations++;
                insertAt = command.EndOffset;
            }
            if (new[] { "if", "foreach", "while", "function", "macro", "block" }.Contains(command.Name)) nesting++;
        }
        if (nesting != 0 || declarations != 1) throw new InvalidOperationException("No unique unconditional target declaration found. Edit CMake manually.");
        var newline = original.Contains("\r\n") ? "\r\n" : "\n";
        var path = Paths.Relative(Path.GetDirectoryName(script)!, source);
        if (path.IndexOfAny(new[] { '$', ';', '"', '\r', '\n', '[', ']' }) >= 0) throw new InvalidOperationException("The generated CMake relative path contains unsupported characters.");
        // Insert before later return()/directory logic, while keeping a trailing comment
        // attached to its declaration. Another command on the same line is ambiguous.
        var lineEnd = original.IndexOf('\n', insertAt);
        if (lineEnd < 0) lineEnd = original.Length;
        var trailing = original.Substring(insertAt, lineEnd - insertAt).Trim();
        if (trailing.Length > 0 && !trailing.StartsWith("#", StringComparison.Ordinal)) throw new InvalidOperationException("Another command follows the target declaration on the same line. Automatic editing stopped.");
        if (trailing.StartsWith("#[", StringComparison.Ordinal)) throw new InvalidOperationException("A bracket comment follows the target declaration. Automatic editing stopped.");
        insertAt = lineEnd < original.Length ? lineEnd + 1 : lineEnd;
        var addition = (insertAt > 0 && original[insertAt - 1] == '\n' ? "" : newline) + newline + "target_sources(" + target.Name + " PRIVATE" + newline + "    \"" + path + "\"" + newline + ")" + newline;
        return new SourceEditPlan { Root = root, SourcePath = source, ScriptPath = script, OriginalBytes = bytes, OriginalScript = original, UpdatedScript = original.Insert(insertAt, addition), Addition = addition, SourceContent = extension.StartsWith(".h", StringComparison.Ordinal) ? "#pragma once" + newline : "// Source file." + newline };
    }

    public static void Apply(SourceEditPlan plan)
    {
        RejectLinks(plan.Root, plan.SourcePath);
        RejectLinks(plan.Root, plan.ScriptPath);
        // Exclusive script access prevents cooperating writers from racing the edit.
        using (var script = new FileStream(plan.ScriptPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var current = new byte[script.Length];
            int read = 0;
            while (read < current.Length) { int n = script.Read(current, read, current.Length - read); if (n == 0) throw new EndOfStreamException(); read += n; }
            if (!current.SequenceEqual(plan.OriginalBytes)) throw new IOException("CMakeLists.txt changed after preview. Generate a new preview.");
            using (var source = new FileStream(plan.SourcePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                try
                {
                    var sourceBytes = Utf8.GetBytes(plan.SourceContent);
                    source.Write(sourceBytes, 0, sourceBytes.Length);
                    source.Flush();
                    var updated = Utf8.GetBytes(plan.UpdatedScript);
                    script.Position = 0;
                    script.Write(updated, 0, updated.Length);
                    script.SetLength(updated.Length);
                    script.Flush();
                }
                catch
                {
                    script.Position = 0;
                    script.Write(plan.OriginalBytes, 0, plan.OriginalBytes.Length);
                    script.SetLength(plan.OriginalBytes.Length);
                    script.Flush();
                    // Keep the newly created source rather than risk deleting later user work.
                    throw;
                }
            }
        }
    }

    public static void RejectLinks(string root, string path)
    {
        var current = Path.GetFullPath(path);
        while (Paths.Within(root, current) || Paths.Equal(root, current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Editing files through directory links is not supported.");
            if (Paths.Equal(root, current)) break;
            current = Path.GetDirectoryName(current)!;
        }
    }
}
