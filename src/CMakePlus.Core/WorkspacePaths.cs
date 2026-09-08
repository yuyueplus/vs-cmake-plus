using System;
using System.IO;

namespace CMakePlus.Core;

public static class WorkspacePaths
{
    public static bool IsLocalAbsolute(string path) => !string.IsNullOrWhiteSpace(path)
        && path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':'
        && (path[2] == '\\' || path[2] == '/') && !path.Contains("${") && !path.Contains("$env{");

    public static void ValidateCache(string source, string build)
    {
        var cache = Path.Combine(build, "CMakeCache.txt");
        if (!File.Exists(cache)) throw new InvalidOperationException("Waiting for the first VS configuration (no CMakeCache.txt yet).");
        foreach (var line in File.ReadLines(cache))
        {
            const string key = "CMAKE_HOME_DIRECTORY:INTERNAL=";
            if (!line.StartsWith(key, StringComparison.Ordinal)) continue;
            if (!Paths.Equal(source, line.Substring(key.Length))) throw new InvalidOperationException("The build cache belongs to another source directory. Reading stopped.");
            return;
        }
        throw new InvalidOperationException("The build cache has no source root. Waiting for configuration to complete.");
    }
}
