using CMakePlus.Core;
using Newtonsoft.Json;
using System.Text;

internal static class WorkspaceChecks
{
    public static void Run(string parent, Action<bool, string> check, Action<Action, string> reject, Action<string, string[]>? run, string cmake)
    {
        var utf8 = new UTF8Encoding(false);
        var defaultsRoot = Path.Combine(parent, "source-defaults");
        var child = Path.Combine(defaultsRoot, "my_target"); Directory.CreateDirectory(child);
        var nested = Path.Combine(child, "src"); Directory.CreateDirectory(nested);
        var parentTarget = new CMakeTarget { Name = "root", DeclarationFile = Path.Combine(defaultsRoot, "CMakeLists.txt") };
        var childTarget = new CMakeTarget { Name = "my_target", DeclarationFile = Path.Combine(child, "CMakeLists.txt") };
        var choices = new[] { parentTarget, childTarget };
        var selectedDefaults = SourceDefaults.ForSelection(defaultsRoot, child, choices);
        check(selectedDefaults.RelativePath == "my_target/new_file.cpp" && selectedDefaults.Target == childTarget, "右键子目录默认源码路径和子目标");
        check(SourceDefaults.ForSelection(defaultsRoot, nested, choices).Target == childTarget, "嵌套目录使用最近声明目标");
        var sharedTarget = new CMakeTarget { Name = "second", DeclarationFile = childTarget.DeclarationFile };
        check(SourceDefaults.ForSelection(defaultsRoot, child, new[] { childTarget, sharedTarget }).Target == null, "同目录多个目标不猜测选择");
        check(SourceDefaults.ForSelection(defaultsRoot, child, choices, parentTarget).Target == parentTarget, "目标视图显式选择优先");
        var selectedFile = Path.Combine(child, "owned.cpp"); parentTarget.Sources.Add(selectedFile);
        check(SourceDefaults.ForSelection(defaultsRoot, selectedFile, choices).Target == parentTarget, "源码实际归属优先于目录推断");
        check(SourceDefaults.ForSelection(defaultsRoot, parent, choices).RelativePath == "new_file.cpp", "外部选择回退源码根目录");
        foreach (var kind in new[] { "EXECUTABLE", "STATIC", "SHARED" })
        {
            var plan = ProjectCreation.Plan(parent, "Created_" + kind, kind, 20, true);
            ProjectCreation.Create(plan);
            check(File.Exists(Path.Combine(plan.Directory, "CMakeLists.txt")) && File.Exists(Path.Combine(plan.Directory, "CMakePresets.json")), "创建完整工程：" + kind);
            reject(() => ProjectCreation.Create(plan), "工程创建不覆盖已有目录：" + kind);
            if (run != null)
            {
                var build = Path.Combine(plan.Directory, "out"); CMakeFileApi.PrepareQuery(build);
                run(cmake, new[] { "--list-presets=all", "-S", plan.Directory });
                run(cmake, new[] { "-S", plan.Directory, "-B", build, "-G", "Visual Studio 17 2022", "-A", "x64" });
                run(cmake, new[] { "--build", build, "--config", "Debug" });
                run(Path.Combine(Path.GetDirectoryName(cmake)!, "ctest.exe"), new[] { "--test-dir", build, "-C", "Debug", "--output-on-failure" });
                var model = CMakeFileApi.Read(build);
                var source = Path.Combine(plan.Directory, "src", kind == "EXECUTABLE" ? "main.cpp" : "Created_" + kind + ".cpp");
                var renamed = Path.Combine(plan.Directory, "src", "重命名.cpp");
                var move = FileOperations.Plan(model, source, renamed);
                var journal = FileOperations.Apply(move);
                check(File.Exists(renamed) && !File.Exists(source), "真实工程源码重命名：" + kind);
                run(cmake, new[] { "-S", plan.Directory, "-B", build });
                run(cmake, new[] { "--build", build, "--config", "Debug" });
                check(CMakeFileApi.Read(build).Configurations.SelectMany(c => c.Targets).Any(t => t.Sources.Any(p => Paths.Equal(p, renamed))), "重命名进入实际模型并构建：" + kind);
                FileOperations.Recover(journal);
                check(File.Exists(source) && !File.Exists(renamed), "恢复真实工程源码：" + kind);
                var disposable = Path.Combine(plan.Directory, "src", "delete_me.cpp");
                File.WriteAllText(disposable, "// disposable source\n", utf8);
                File.AppendAllText(Path.Combine(plan.Directory, "CMakeLists.txt"), "\ntarget_sources(Created_" + kind + " PRIVATE src/delete_me.cpp)\n", utf8);
                run(cmake, new[] { "-S", plan.Directory, "-B", build });
                var deletion = FileOperations.PlanDelete(CMakeFileApi.Read(build), disposable);
                var recycled = Path.Combine(parent, "recycled-" + kind + ".cpp");
                FileOperations.ApplyDelete(deletion, (path, dir) => File.Move(path, recycled));
                run(cmake, new[] { "-S", plan.Directory, "-B", build });
                run(cmake, new[] { "--build", build, "--config", "Debug" });
                check(!File.Exists(disposable) && !CMakeFileApi.Read(build).Configurations.SelectMany(c => c.Targets).Any(t => t.Sources.Any(p => Paths.Equal(p, disposable))), "删除源码后真实模型及构建一致：" + kind);
            }
        }
        reject(() => ProjectCreation.Plan(parent, "../outside", "EXECUTABLE", 20, false), "工程名称不能越界");
        DeletionChecks.Run(parent, check, reject);
        TargetDeletionChecks.Run(parent, check, reject, run, cmake);
        reject(() => ProjectCreation.Plan(parent, "CON", "EXECUTABLE", 20, false), "工程名称拒绝保留设备名");
        var root = Path.Combine(parent, "operations"); Directory.CreateDirectory(root);
        var sourceFile = Path.Combine(root, "shared.cpp"); File.WriteAllText(sourceFile, "int value() { return 0; }", utf8);
        var script1 = Path.Combine(root, "CMakeLists.txt"); var script2 = Path.Combine(root, "extra.cmake");
        File.WriteAllText(script1, "add_library(one STATIC shared.cpp)\n", utf8);
        File.WriteAllText(script2, "add_library(two STATIC \"shared.cpp\")\n", utf8);
        var snapshot = new CMakeSnapshot { SourceDirectory = root, GeneratedUtc = DateTime.UtcNow.AddSeconds(1) };
        snapshot.Inputs.AddRange(new[] { script1, script2 });
        var configuration = new CMakeConfiguration(); snapshot.Configurations.Add(configuration);
        foreach (var pair in new[] { ("one", script1), ("two", script2) })
        { var target = new CMakeTarget { Name = pair.Item1, DeclarationFile = pair.Item2, Type = "STATIC_LIBRARY" }; target.Sources.Add(sourceFile); configuration.Targets.Add(target); }
        var to = Path.Combine(root, "renamed.cpp");
        var operation = FileOperations.Plan(snapshot, sourceFile, to);
        check(operation.Scripts.Count == 2 && operation.Scripts.All(s => s.After.Contains("renamed.cpp")), "跨两个脚本更新共享源码引用");
        File.SetAttributes(script2, FileAttributes.ReadOnly);
        try { reject(() => FileOperations.Apply(operation), "只读脚本在移动前拒绝操作"); }
        finally { File.SetAttributes(script2, FileAttributes.Normal); }
        File.AppendAllText(script2, "# concurrent", utf8);
        reject(() => FileOperations.Apply(operation), "预览后脚本变化拒绝移动");
        check(File.Exists(sourceFile) && !File.Exists(to), "冲突没有移动实体文件");
        File.WriteAllText(script2, operation.Scripts.Single(s => s.ScriptPath == script2).Before, utf8);
        var log = FileOperations.Apply(operation);
        check(File.Exists(to) && operation.Scripts.All(s => File.ReadAllText(s.ScriptPath) == s.After), "移动与多脚本写入一致");
        File.AppendAllText(to, "// changed", utf8);
        reject(() => FileOperations.Recover(log), "恢复不覆盖后续源码编辑");
        File.WriteAllText(to, "int value() { return 0; }", utf8);
        FileOperations.Recover(log);
        check(File.Exists(sourceFile) && operation.Scripts.All(s => File.ReadAllText(s.ScriptPath) == s.Before), "恢复多脚本和实体路径");
        reject(() => FileOperations.Recover(log), "不能重复恢复");
        // Simulate termination after path move and only one script replacement.
        var interrupted = Path.Combine(FileOperations.JournalDirectory, "interrupted-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(interrupted, JsonConvert.SerializeObject(operation), utf8);
        File.Move(sourceFile, to); File.WriteAllText(operation.Scripts[0].ScriptPath, operation.Scripts[0].After, utf8);
        FileOperations.Recover(interrupted);
        check(File.Exists(sourceFile) && !File.Exists(to) && operation.Scripts.All(s => File.ReadAllText(s.ScriptPath) == s.Before), "中断后混合状态可恢复");
        File.WriteAllText(script1, "set(S shared.cpp)\nadd_library(one STATIC ${S})\n", utf8);
        reject(() => FileOperations.Plan(snapshot, sourceFile, to), "变量引用不猜测替换");
        File.WriteAllText(script1, "if(WIN32)\nadd_library(one STATIC shared.cpp)\nendif()\n", utf8);
        reject(() => FileOperations.Plan(snapshot, sourceFile, to), "条件引用不猜测替换");
        File.WriteAllText(script1, operation.Scripts.Single(s => s.ScriptPath == script1).Before, utf8);
        File.WriteAllText(to, "existing", utf8);
        reject(() => FileOperations.Plan(snapshot, sourceFile, to), "目标碰撞拒绝移动");
        reject(() => FileOperations.Plan(snapshot, sourceFile, Path.Combine(parent, "outside.cpp")), "移动不能越出工程");
        var folder = Path.Combine(root, "folder"); Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "untracked.cpp"), "", utf8);
        var folderLog = FileOperations.Apply(FileOperations.Plan(snapshot, folder, Path.Combine(root, "newfolder")));
        FileOperations.Recover(folderLog); check(Directory.Exists(folder), "纯源码目录移动和恢复");
        reject(() => FileOperations.Plan(snapshot, script1, Path.Combine(root, "other.txt")), "CMake 脚本移动保持只读");
        var options = new BrowserSettings(); Directory.CreateDirectory(Path.Combine(root, "build")); File.WriteAllText(Path.Combine(root, "build", "hidden.cpp"), "", utf8);
        check(!WorkspaceBrowser.Search(root, "hidden", options, default).Any(), "搜索遵守排除规则");
        options.ShowExcluded = true;
        check(WorkspaceBrowser.Search(root, "hidden", options, default).Length == 1, "显示排除目录参与搜索");
        check(WorkspaceBrowser.Search(root, ".cpp", options, default, 1).Length == 1, "搜索结果上限");
        using (var cancellation = new CancellationTokenSource())
        { cancellation.Cancel(); bool stopped = false; try { WorkspaceBrowser.Search(root, "x", options, cancellation.Token); } catch (OperationCanceledException) { stopped = true; } check(stopped, "搜索取消生效"); }
        options.Expanded.Add(folder); WorkspaceBrowser.Save(root, options);
        check(WorkspaceBrowser.Load(root).Expanded.Contains(folder) && WorkspaceBrowser.Load(root).ShowExcluded, "规则和展开状态持久化");
    }
}
