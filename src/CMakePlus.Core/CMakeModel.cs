using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace CMakePlus.Core;

public sealed class CMakeTarget
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string DeclarationFile { get; set; } = "";
    public List<string> Sources { get; } = new List<string>();
    public HashSet<string> GeneratedSources { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public override string ToString() => Name + "  [" + Type + "]";
}

public sealed class CMakeConfiguration
{
    public string Name { get; set; } = "";
    public List<CMakeTarget> Targets { get; } = new List<CMakeTarget>();
    public override string ToString() => string.IsNullOrEmpty(Name) ? "Default configuration" : Name;
}

public sealed class CMakeSnapshot
{
    public string SourceDirectory { get; set; } = "";
    public string BuildDirectory { get; set; } = "";
    public DateTime GeneratedUtc { get; set; }
    public List<CMakeConfiguration> Configurations { get; } = new List<CMakeConfiguration>();
    public List<string> Inputs { get; } = new List<string>();
    public bool HasChangedInputs() => Inputs.Any(path => !File.Exists(path) || File.GetLastWriteTimeUtc(path) > GeneratedUtc);
}

public static class CMakeFileApi
{
    // Client-specific queries coexist with the queries owned by Visual Studio.
    public static void PrepareQuery(string buildDirectory)
    {
        var query = Path.Combine(Path.GetFullPath(buildDirectory), ".cmake", "api", "v1", "query", "client-cmakeplus");
        Directory.CreateDirectory(query);
        File.WriteAllText(Path.Combine(query, "codemodel-v2"), "", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(query, "cmakeFiles-v1"), "", new UTF8Encoding(false));
    }

    public static CMakeSnapshot Read(string buildDirectory, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var build = Path.GetFullPath(buildDirectory);
        var reply = Path.Combine(build, ".cmake", "api", "v1", "reply");
        if (!Directory.Exists(reply)) throw new InvalidOperationException("No CMake File API data. Prepare the query, then reconfigure in VS.");
        var indexFile = Directory.GetFiles(reply, "index-*.json").OrderByDescending(Path.GetFileName, StringComparer.Ordinal).FirstOrDefault();
        if (indexFile == null) throw new InvalidOperationException("No File API index from a successful configuration. Reconfigure in VS.");
        var index = ReadJson(indexFile);
        var modelRef = (index["objects"] as JArray)?.OfType<JObject>().FirstOrDefault(x => (string?)x["kind"] == "codemodel" && (int?)x["version"]?["major"] == 2);
        if (modelRef == null) throw new InvalidOperationException("The configuration has no codemodel v2. Prepare the query and reconfigure.");
        var model = ReadJson(ReplyPath(reply, (string?)modelRef["jsonFile"]));
        var snapshot = new CMakeSnapshot
        {
            SourceDirectory = Path.GetFullPath(Required(model["paths"]?["source"], "source")),
            BuildDirectory = Path.GetFullPath(Required(model["paths"]?["build"], "build")),
            GeneratedUtc = File.GetLastWriteTimeUtc(indexFile)
        };
        if (!Paths.Equal(build, snapshot.BuildDirectory)) throw new InvalidDataException("The File API build directory does not match the selected directory.");
        var inputRef = (index["objects"] as JArray)?.OfType<JObject>().FirstOrDefault(x => (string?)x["kind"] == "cmakeFiles" && (int?)x["version"]?["major"] == 1);
        if (inputRef != null)
        {
            var inputs = ReadJson(ReplyPath(reply, (string?)inputRef["jsonFile"]));
            foreach (var input in inputs["inputs"] ?? new JArray())
                if ((bool?)input["isCMake"] != true && (bool?)input["isGenerated"] != true)
                    snapshot.Inputs.Add(Resolve(snapshot.SourceDirectory, Required(input["path"], "input path")));
        }
        var inputPaths = new HashSet<string>(snapshot.Inputs, StringComparer.OrdinalIgnoreCase);
        foreach (var config in model["configurations"] ?? new JArray())
        {
            var configuration = new CMakeConfiguration { Name = (string?)config["name"] ?? "" };
            foreach (var targetRef in config["targets"] ?? new JArray())
            {
                cancellation.ThrowIfCancellationRequested();
                var json = ReadJson(ReplyPath(reply, (string?)targetRef["jsonFile"]));
                var target = new CMakeTarget { Id = Required(json["id"], "id"), Name = Required(json["name"], "name"), Type = Required(json["type"], "type") };
                var graph = json["backtraceGraph"];
                var backtrace = (int?)json["backtrace"];
                if (backtrace.HasValue && graph?["nodes"] is JArray nodes && backtrace.Value >= 0 && backtrace.Value < nodes.Count)
                {
                    var fileIndex = (int?)nodes[backtrace.Value]?["file"];
                    if (fileIndex.HasValue && graph["files"] is JArray files && fileIndex.Value >= 0 && fileIndex.Value < files.Count)
                        target.DeclarationFile = Resolve(snapshot.SourceDirectory, Required(files[fileIndex.Value], "backtrace file"));
                }
                foreach (var source in json["sources"] ?? new JArray())
                {
                    cancellation.ThrowIfCancellationRequested();
                    var path = Resolve(snapshot.SourceDirectory, Required(source["path"], "source path"));
                    target.Sources.Add(path);
                    if ((bool?)source["isGenerated"] == true) target.GeneratedSources.Add(path);
                }
                configuration.Targets.Add(target);
                if (target.DeclarationFile.Length > 0 && inputPaths.Add(target.DeclarationFile)) snapshot.Inputs.Add(target.DeclarationFile);
            }
            snapshot.Configurations.Add(configuration);
        }
        return snapshot;
    }

    private static string Resolve(string root, string path) => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
    private static string Required(JToken? token, string name) => (string?)token ?? throw new InvalidDataException("Missing File API field: " + name);
    private static JObject ReadJson(string path) => JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
    private static string ReplyPath(string reply, string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name || name == "." || name == "..") throw new InvalidDataException("The File API references an invalid reply path.");
        return Path.Combine(reply, name);
    }
}

public static class Paths
{
    public static bool Equal(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    public static bool Within(string root, string path)
    {
        var prefix = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
    public static string Relative(string root, string path)
    {
        var rootUri = new Uri(Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar);
        return Uri.UnescapeDataString(rootUri.MakeRelativeUri(new Uri(Path.GetFullPath(path))).ToString());
    }
}
