using CMakePlus.Core;
using System.Text;

internal static class DeletionChecks
{
    public static void Run(string parent, Action<bool, string> check, Action<Action, string> reject)
    {
        var root = Path.Combine(parent, "deletion"); Directory.CreateDirectory(root);
        var utf8 = new UTF8Encoding(false);
        var file = Path.Combine(root, "remove.cpp"); File.WriteAllText(file, "// 中文\n", utf8);
        var keep = Path.Combine(root, "keep.cpp"); File.WriteAllText(keep, "int value() { return 1; }", utf8);
        var model = new CMakeSnapshot { SourceDirectory = root };
        var config = new CMakeConfiguration(); model.Configurations.Add(config);
        foreach (var name in new[] { "one", "two" })
        {
            var script = Path.Combine(root, name == "one" ? "CMakeLists.txt" : "extra.cmake");
            File.WriteAllText(script, "add_library(" + name + " STATIC keep.cpp remove.cpp)\n", utf8);
            model.Inputs.Add(script);
            var target = new CMakeTarget { Name = name, Type = "STATIC_LIBRARY", DeclarationFile = script };
            target.Sources.AddRange(new[] { keep, file }); config.Targets.Add(target);
        }
        var plan = FileOperations.PlanDelete(model, file);
        check(plan.Scripts.Count == 2 && plan.Scripts.All(s => !s.After.Contains("remove.cpp") && s.After.Contains("keep.cpp")), "删除预览更新全部共享引用");
        reject(() => FileOperations.ApplyDelete(plan, (p, d) => throw new IOException("取消回收")), "回收取消报告失败");
        check(File.Exists(file) && plan.Scripts.All(s => File.ReadAllText(s.ScriptPath) == s.Before), "回收取消恢复全部脚本");
        var touched = false;
        File.AppendAllText(plan.Scripts[0].ScriptPath, "# changed", utf8);
        reject(() => FileOperations.ApplyDelete(plan, (p, d) => touched = true), "删除预览后脚本变化拒绝执行");
        check(!touched && File.Exists(file), "冲突不调用回收站");
        File.WriteAllText(plan.Scripts[0].ScriptPath, plan.Scripts[0].Before, utf8);
        var recycled = Path.Combine(parent, "recycled-source.cpp");
        var journal = FileOperations.ApplyDelete(plan, (p, d) => File.Move(p, recycled));
        check(!File.Exists(file) && FileOperations.LoadJournal(journal).State == "Deleted", "删除完成记录持久状态");
        reject(() => FileOperations.Recover(journal), "文件未还原时拒绝恢复引用");
        File.Move(recycled, file); FileOperations.Recover(journal);
        check(plan.Scripts.All(s => File.ReadAllText(s.ScriptPath) == s.Before), "还原文件后恢复全部引用");
        foreach (var target in config.Targets) target.Sources.Remove(keep);
        reject(() => FileOperations.PlanDelete(model, file), "拒绝删除最后可编译源码");
        foreach (var target in config.Targets) target.Sources.Add(keep);
        reject(() => FileOperations.PlanDelete(model, root), "拒绝删除工程根目录");
        reject(() => FileOperations.PlanDelete(model, model.Inputs[0]), "拒绝删除 CMake 脚本");
        File.WriteAllText(model.Inputs[0], "set(S remove.cpp)\nadd_library(one STATIC keep.cpp ${S})\n", utf8);
        reject(() => FileOperations.PlanDelete(model, file), "变量源码引用拒绝删除");
        File.WriteAllText(model.Inputs[0], plan.Scripts[0].Before, utf8);
        var directory = Path.Combine(root, "assets"); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "note.txt"), "asset", utf8);
        var folderPlan = FileOperations.PlanDelete(model, directory);
        var folderBin = Path.Combine(parent, "recycled-assets");
        var folderLog = FileOperations.ApplyDelete(folderPlan, (p, d) => { if (!d) throw new Exception("不是目录"); Directory.Move(p, folderBin); });
        Directory.Move(folderBin, directory); FileOperations.Recover(folderLog);
        check(File.Exists(Path.Combine(directory, "note.txt")), "目录整体删除与还原流程");
    }
}
