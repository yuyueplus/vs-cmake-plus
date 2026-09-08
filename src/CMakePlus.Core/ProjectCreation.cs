using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace CMakePlus.Core;

public sealed class ProjectPlan
{
    public string Directory { get; set; } = "";
    public Dictionary<string, string> Files { get; } = new Dictionary<string, string>();
    public string Preview => "Create project: " + Directory + "\n\n" + string.Join("\n", Files.Keys) + "\n\n" + string.Join("\n", Files.Select(f => "+++ " + f.Key + "\n" + f.Value));
}

public static class ProjectCreation
{
    public static ProjectPlan Plan(string parent, string name, string kind, int standard, bool tests)
    {
        if (!Path.IsPathRooted(parent) || !Directory.Exists(parent)) throw new IOException("Select an existing parent directory.");
        if (!Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$")) throw new InvalidOperationException("Use letters, digits, and underscores for the project name. It cannot start with a digit.");
        if (Regex.IsMatch(name, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase)) throw new InvalidOperationException("The project name cannot be a reserved Windows device name.");
        if (!new[] { "EXECUTABLE", "STATIC", "SHARED" }.Contains(kind) || !new[] { 17, 20, 23 }.Contains(standard)) throw new InvalidOperationException("Invalid project type or language standard.");
        var target = Path.GetFullPath(Path.Combine(parent, name));
        TargetEditing.ValidatePath(parent, target);
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("The destination directory already exists. Existing contents will not be overwritten.");
        var plan = new ProjectPlan { Directory = target };
        var lib = kind != "EXECUTABLE";
        var cmake = "cmake_minimum_required(VERSION 3.20)\nproject(" + name + " LANGUAGES CXX)\n\n"
            + (lib ? "add_library(" + name + " " + kind + " src/" + name + ".cpp)\n" : "add_executable(" + name + " src/main.cpp)\n")
            + "target_compile_features(" + name + " PUBLIC cxx_std_" + standard + ")\n"
            + "target_include_directories(" + name + " PUBLIC \"${CMAKE_CURRENT_SOURCE_DIR}/include\")\n";
        if (kind == "SHARED") cmake += "set_target_properties(" + name + " PROPERTIES WINDOWS_EXPORT_ALL_SYMBOLS ON)\n";
        plan.Files[lib ? "src/" + name + ".cpp" : "src/main.cpp"] = lib ? "#include \"" + name + ".h\"\nint " + name + "_value() { return 42; }\n" : "#include <iostream>\nint main() { std::cout << \"Hello from " + name + "!\\n\"; return 0; }\n";
        if (lib) plan.Files["include/" + name + ".h"] = "#pragma once\nint " + name + "_value();\n";
        else plan.Files["include/.gitkeep"] = "";
        if (tests)
        {
            cmake += "\ninclude(CTest)\n";
            if (lib)
            {
                cmake += "add_executable(" + name + "_smoke tests/smoke.cpp)\ntarget_link_libraries(" + name + "_smoke PRIVATE " + name + ")\nadd_test(NAME smoke COMMAND " + name + "_smoke)\n";
                plan.Files["tests/smoke.cpp"] = "#include \"" + name + ".h\"\nint main() { return " + name + "_value() == 42 ? 0 : 1; }\n";
            }
            else cmake += "add_test(NAME smoke COMMAND " + name + ")\n";
        }
        plan.Files["CMakeLists.txt"] = cmake;
        plan.Files[".gitignore"] = "/out/\n/.vs/\n/CMakeUserPresets.json\n";
        var configure = new JArray(); var build = new JArray();
        foreach (var mode in new[] { "Debug", "Release" })
        {
            var preset = mode.ToLowerInvariant();
            configure.Add(new JObject { ["name"] = preset, ["displayName"] = mode, ["generator"] = "Ninja", ["binaryDir"] = "${sourceDir}/out/build/${presetName}", ["cacheVariables"] = new JObject { ["CMAKE_BUILD_TYPE"] = mode } });
            build.Add(new JObject { ["name"] = preset, ["configurePreset"] = preset });
        }
        plan.Files["CMakePresets.json"] = new JObject { ["version"] = 2, ["configurePresets"] = configure, ["buildPresets"] = build }.ToString() + "\n";
        return plan;
    }
    public static void Create(ProjectPlan plan)
    {
        if (Directory.Exists(plan.Directory) || File.Exists(plan.Directory)) throw new IOException("The destination directory was created after preview. Creation stopped.");
        var parent = Path.GetDirectoryName(plan.Directory)!;
        TargetEditing.ValidatePath(parent, plan.Directory);
        var staging = Path.Combine(parent, ".cmakeplus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        foreach (var file in plan.Files)
        {
            var path = Path.GetFullPath(Path.Combine(staging, file.Key));
            TargetEditing.ValidatePath(staging, path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file.Value, new UTF8Encoding(false, true));
        }
        Directory.Move(staging, plan.Directory);
    }
}
