using System.Text;
using CMakePlus.Core;

internal static class TargetEditingChecks
{
    public static void Run(string root, Action<bool, string> check, Action<Action, string> reject, Action<string, string[]>? run, string cmake)
    {
        root = Path.Combine(root, "targets"); Directory.CreateDirectory(root);
        var script = Path.Combine(root, "CMakeLists.txt");
        var text = "cmake_minimum_required(VERSION 3.20)\nproject(TargetTests LANGUAGES CXX)\n";
        text += "if(POLICY CMP0141)\n  cmake_policy(SET CMP0141 NEW)\nendif()\nif(CMAKE_VERSION VERSION_GREATER 3.12)\n  set(CMAKE_CXX_STANDARD 20)\nendif()\n";
        File.WriteAllText(script, text, new UTF8Encoding(false));
        var names = new List<string>();
        foreach (var pair in new[] { ("tool", "EXECUTABLE"), ("base", "STATIC"), ("shared", "SHARED") })
        {
            var plan = TargetEditing.NewTarget(root, script, pair.Item1, pair.Item1, pair.Item2, names, text);
            TargetEditing.CreateFiles(plan); text = plan.After; names.Add(pair.Item1);
            check(plan.NewFiles.Count == 2 && text.Contains("add_subdirectory(\"" + pair.Item1 + "\")"), "新建目标和父目录连接：" + pair.Item2);
        }
        File.WriteAllText(script, text, new UTF8Encoding(false));
        reject(() => TargetEditing.NewTarget(root, script, "another", "base", "STATIC", names, text), "拒绝重复目标名");
        reject(() => TargetEditing.NewTarget(root, script, "../escape", "escape", "STATIC", names, text), "新目标不能越界");
        var conditional = text + "if(FALSE)\n  if(WIN32)\n  endif()\nelse()\nendif()\n";
        var outside = TargetEditing.NewTarget(root, script, "another", "another", "STATIC", names, conditional);
        check(outside.After.StartsWith(conditional) && outside.After.EndsWith("add_subdirectory(\"another\")\n"), "新目标追加在完整条件块之外且保留模板内容");
        reject(() => TargetEditing.NewTarget(root, script, "another", "another", "STATIC", names, text + "if(WIN32)\n"), "拒绝未闭合条件块");
        reject(() => TargetEditing.NewTarget(root, script, "another", "another", "STATIC", names, text + "if(WIN32)\nendfunction()\n"), "拒绝不匹配的块结束命令");
        reject(() => TargetEditing.NewTarget(root, script, "another", "another", "STATIC", names, text + "else()\n"), "拒绝游离条件分支");
        reject(() => TargetEditing.NewTarget(root, script, "another", "another", "STATIC", names, text + "if(WIN32)\nreturn()\nendif()\n"), "拒绝可能阻止子目录执行的 return");
        reject(() => TargetEditing.NewTarget(root, script, "another", "another", "STATIC", names, text + "add_subdirectory(another)\n"), "拒绝重复子目录引用");
        var source1 = Path.Combine(root, "中文 space.cpp"); var source2 = Path.Combine(root, "second.cpp");
        File.WriteAllText(source1, "int extra_value() { return 1; }\n");
        File.WriteAllText(source2, "int second_value() { return 2; }\n");
        foreach (var name in new[] { "base", "shared" })
        {
            var targetScript = Path.Combine(root, name, "CMakeLists.txt");
            var target = new CMakeTarget { Name = name, Type = name == "base" ? "STATIC_LIBRARY" : "SHARED_LIBRARY", DeclarationFile = targetScript };
            target.Sources.Add(Path.Combine(root, name, name + ".cpp"));
            var original = File.ReadAllText(targetScript) + "# 编辑器未保存的中文注释\n";
            var addition = TargetEditing.Membership(root, target, new[] { source1, source2, source1 }, true, original);
            check(addition.After.Contains("# 编辑器未保存的中文注释") && addition.After.Split(new[] { "../中文 space.cpp" }, StringSplitOptions.None).Length == 2, "批量加入去重并保留缓冲区内容：" + name);
            target.Sources.AddRange(new[] { source1, source2 });
            reject(() => TargetEditing.Membership(root, target, new[] { source1 }, true, addition.After), "重复加入同一目标被拒绝：" + name);
            var removal = TargetEditing.Membership(root, target, new[] { source1, source2 }, false, addition.After);
            check(!removal.After.Contains("../中文 space.cpp") && File.Exists(source1) && File.Exists(source2), "批量移除引用保留实体文件：" + name);
            reject(() => TargetEditing.Membership(root, target, target.Sources, false, addition.After), "不能移除全部源码：" + name);
            reject(() => TargetEditing.Membership(root, target, new[] { source1 }, true, "if(WIN32)\n" + original + "endif()\n"), "拒绝条件目标引用修改：" + name);
            reject(() => TargetEditing.Membership(root, target, new[] { source1 }, false, addition.After + "target_sources(" + name + " PRIVATE ${EXTRA})\n"), "变量引用保持只读：" + name);
            target.GeneratedSources.Add(source1);
            reject(() => TargetEditing.Membership(root, target, new[] { source1 }, false, addition.After), "生成源码引用只读：" + name);
            File.WriteAllText(targetScript, name == "base" ? addition.After : removal.After, new UTF8Encoding(false));
        }
        var collision = TargetEditing.NewTarget(root, script, "collision", "collision", "STATIC", names, text);
        Directory.CreateDirectory(collision.NewDirectory);
        reject(() => TargetEditing.CreateFiles(collision), "预览期间目录被创建时停止写入");
        if (run != null)
        {
            var build = Path.Combine(root, "out"); CMakeFileApi.PrepareQuery(build);
            run(cmake, new[] { "-S", root, "-B", build, "-G", "Visual Studio 17 2022", "-A", "x64" });
            var configuration = CMakeFileApi.Read(build).Configurations.First(c => c.Name == "Debug");
            check(names.All(n => configuration.Targets.Any(t => t.Name == n)), "三种新建目标进入真实 File API 模型");
            check(configuration.Targets.Single(t => t.Name == "base").Sources.Any(p => Paths.Equal(p, source1))
                && !configuration.Targets.Single(t => t.Name == "shared").Sources.Any(p => Paths.Equal(p, source1)), "批量加入和移除与 CMake 实际模型一致");
            run(cmake, new[] { "--build", build, "--config", "Debug" });
            check(true, "新建程序、静态库、动态库实际 MSVC 构建成功");
        }
    }
}
