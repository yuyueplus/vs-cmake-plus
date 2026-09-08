using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;

namespace CMakePlus.Core;

public sealed class BrowserSettings
{
    public string ExcludedDirectories { get; set; } = ".git;.vs;.cmake;out;build;cmake-build-*";
    public bool ShowExcluded { get; set; }
    public List<string> Expanded { get; set; } = new List<string>();
    public bool Ignore(string name) => !ShowExcluded && ExcludedDirectories.Split(';').Where(p => p.Length > 0).Any(p => Regex.IsMatch(name, "^" + Regex.Escape(p.Trim()).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
}
public static class WorkspaceBrowser
{
    private static string SettingsPath(string root)
    {
        using (var sha = SHA256.Create())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CMakePlus", "workspaces", BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant()))).Replace("-", "") + ".json");
    }
    public static BrowserSettings Load(string root)
    {
        var path = SettingsPath(root);
        try { return File.Exists(path) ? JsonConvert.DeserializeObject<BrowserSettings>(File.ReadAllText(path, Encoding.UTF8)) ?? new BrowserSettings() : new BrowserSettings(); }
        catch (JsonException) { return new BrowserSettings(); }
    }
    public static void Save(string root, BrowserSettings settings)
    {
        var path = SettingsPath(root); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonConvert.SerializeObject(settings), new UTF8Encoding(false));
    }
    public static string[] Search(string root, string query, BrowserSettings settings, CancellationToken cancellation, int limit = 501)
    {
        var found = new List<string>(); var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0 && found.Count < limit)
        {
            cancellation.ThrowIfCancellationRequested();
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(pending.Pop()); } catch (IOException) { continue; } catch (UnauthorizedAccessException) { continue; }
            foreach (var path in entries)
            {
                cancellation.ThrowIfCancellationRequested();
                FileAttributes attributes;
                try { attributes = File.GetAttributes(path); } catch (IOException) { continue; } catch (UnauthorizedAccessException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!settings.Ignore(Path.GetFileName(path))) pending.Push(path);
                }
                else if (Paths.Relative(root, path).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                { found.Add(path); if (found.Count == limit) break; }
            }
        }
        return found.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
