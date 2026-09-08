using System.Text;
using CMakePlus.Core;

internal static class TargetDeletionChecks
{
    public static void Run(string parent, Action<bool, string> check, Action<Action, string> reject, Action<string, string[]>? run, string cmake)
    {
        var root = Path.Combine(parent, "target-deletion");
        var directory = Path.Combine(root, "my_target"); Directory.CreateDirectory(directory);
        var script = Path.Combine(root, "CMakeLists.txt");
        var childScript = Path.Combine(directory, "CMakeLists.txt");
        var childSource = Path.Combine(directory, "my_target.cpp");
        var utf8 = new UTF8Encoding(false);
        var text = "cmake_minimum_required(VERSION 3.20)\nproject(DeleteTarget LANGUAGES CXX)\nif(POLICY CMP0141)\ncmake_policy(SET CMP0141 NEW)\nendif()\nadd_library(keep STATIC keep.cpp)\nadd_subdirectory(my_target)\ntarget_link_libraries(keep PRIVATE my_target)\nadd_dependencies(keep my_target)\n";
        File.WriteAllText(script, text, utf8);
        File.WriteAllText(Path.Combine(root, "keep.cpp"), "int keep() { return 0; }\n", utf8);
        File.WriteAllText(childScript, "add_library(my_target STATIC my_target.cpp)\n", utf8);
        File.WriteAllText(childSource, "int value() { return 1; }\n", utf8);
        var model = new CMakeSnapshot { SourceDirectory = root };
        model.Inputs.AddRange(new[] { script, childScript });
        var config = new CMakeConfiguration(); model.Configurations.Add(config);
        var child = new CMakeTarget { Name = "my_target", Type = "STATIC_LIBRARY", DeclarationFile = childScript }; child.Sources.Add(childSource); config.Targets.Add(child);
        var keep = new CMakeTarget { Name = "keep", Type = "STATIC_LIBRARY", DeclarationFile = script }; keep.Sources.Add(Path.Combine(root, "keep.cpp")); config.Targets.Add(keep);
        var plan = FileOperations.PlanTargetDirectoryDelete(model, directory);
        check(plan.Scripts.Count == 1 && !plan.Scripts[0].After.Contains("my_target") && plan.Scripts[0].After.Contains("CMP0141"), "删除目标预览同步移除子目录及直接链接依赖");
        check(plan.Preview.Contains("Remove target: my_target") && plan.Hashes.ContainsKey(childScript), "删除目标预览包含名称及完整目录文件");
        reject(() => FileOperations.ApplyDelete(plan, (p, d) => throw new IOException("cancel")), "删除目标取消");
        check(File.ReadAllText(script) == text && Directory.Exists(directory), "取消删除目标恢复父脚本");
        File.WriteAllText(script, text + "target_link_libraries(keep PRIVATE ${OTHER})\n", utf8);
        reject(() => FileOperations.PlanTargetDirectoryDelete(model, directory), "拒绝无法解析的依赖");
        File.WriteAllText(script, text.Replace("add_subdirectory(my_target)", "if(WIN32)\nadd_subdirectory(my_target)\nendif()"), utf8);
        reject(() => FileOperations.PlanTargetDirectoryDelete(model, directory), "拒绝条件子目录入口");
        File.WriteAllText(script, text, utf8); keep.Sources.Add(childSource);
        reject(() => FileOperations.PlanTargetDirectoryDelete(model, directory), "其他目标共享目录内源码时拒绝删除"); keep.Sources.Remove(childSource);
        File.AppendAllText(childScript, "include(extra.cmake)\n", utf8);
        reject(() => FileOperations.PlanTargetDirectoryDelete(model, directory), "复杂子脚本保持只读");
        File.WriteAllText(childScript, "add_library(my_target STATIC my_target.cpp)\n", utf8);
        reject(() => FileOperations.PlanTargetDirectoryDelete(model, root), "目标删除不能作用于根工程");
        var build = Path.Combine(root, "out");
        if (run != null)
        {
            CMakeFileApi.PrepareQuery(build);
            run(cmake, new[] { "-S", root, "-B", build, "-G", "Visual Studio 17 2022", "-A", "x64" });
            model = CMakeFileApi.Read(build);
            plan = FileOperations.PlanTargetDirectoryDelete(model, directory);
        }
        var recycled = Path.Combine(parent, "recycled-target");
        var journal = FileOperations.ApplyDelete(plan, (p, d) => Directory.Move(p, recycled));
        check(!Directory.Exists(directory) && File.ReadAllText(script) == plan.Scripts[0].After, "删除目标目录与父脚本一致");
        if (run != null)
        {
            run(cmake, new[] { "-S", root, "-B", build });
            run(cmake, new[] { "--build", build, "--config", "Debug" });
            check(CMakeFileApi.Read(build).Configurations.All(c => c.Targets.All(t => t.Name != "my_target")), "删除目标后真实配置构建成功且模型不再含目标");
            var fresh = CMakeFileApi.Read(build);
            check(!fresh.HasChangedInputs(), "删除后新配置模型可继续编辑");
            var recreate = TargetEditing.NewTarget(root, script, "my_target", "my_target", "STATIC",
                fresh.Configurations.SelectMany(c => c.Targets).Select(t => t.Name), File.ReadAllText(script));
            check(recreate.NewFiles.Count > 0, "删除并刷新模型后允许预览同名目标");
            TargetEditing.CreateFiles(recreate);
            File.WriteAllText(script, recreate.After, utf8);
            var savedAt = File.GetLastWriteTimeUtc(script);
            run(cmake, new[] { "-S", root, "-B", build });
            var recreatedModel = CMakeFileApi.Read(build);
            check(!recreatedModel.HasChangedInputs() && recreatedModel.GeneratedUtc >= savedAt,
                "新建目标后完成配置的模型满足解除等待条件");
            var deleteAgain = FileOperations.PlanTargetDirectoryDelete(recreatedModel, directory);
            FileOperations.ApplyDelete(deleteAgain, (p, d) => Directory.Move(p, Path.Combine(parent, "recreated-target")));
            run(cmake, new[] { "-S", root, "-B", build });
            check(!Directory.Exists(directory) && CMakeFileApi.Read(build).Configurations.All(c => c.Targets.All(t => t.Name != "my_target")),
                "新建同名目标后可再次删除并重新配置");
            File.WriteAllText(script, plan.Scripts[0].After, utf8);
        }
        Directory.Move(recycled, directory); FileOperations.Recover(journal);
        check(File.ReadAllText(script) == text && File.Exists(childScript), "还原目标目录后恢复全部依赖");
    }
}
