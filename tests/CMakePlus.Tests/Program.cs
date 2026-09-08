using System.Diagnostics;
using System.Text;
using CMakePlus.Core;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/tests", Guid.NewGuid().ToString("N"), "中文 space"));
Directory.CreateDirectory(root);
var script = Path.Combine(root, "CMakeLists.txt");
var utf8 = new UTF8Encoding(false);
var target = new CMakeTarget { Name = "demo", Type = "EXECUTABLE", DeclarationFile = script };
int passed = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); passed++; Console.WriteLine("PASS " + message); }
void Reject(Action action, string message)
{
    try { action(); } catch (Exception ex) when (ex is InvalidOperationException || ex is IOException || ex is InvalidDataException) { passed++; Console.WriteLine("PASS " + message); return; }
    throw new Exception("应拒绝：" + message);
}
void Write(string text) => File.WriteAllText(script, text, utf8);
const string source = "cmake_minimum_required(VERSION 3.20)\nproject(Demo LANGUAGES CXX)\n# 中文注释保留\nadd_executable(demo main.cpp)\nenable_testing()\nadd_test(NAME smoke COMMAND demo)\n";
Write(source);
File.WriteAllText(Path.Combine(root, "main.cpp"), "int main() { return 0; }\n", utf8);
Check(CMakeSyntax.Parse("#[=[ fake add_library(no x) ]=]\nadd_executable(\"demo\" main.cpp)\n").Count == 1, "扫描器跳过方括号注释");
Check(CMakeSyntax.Parse("set(X [==[hello ) # world]==])\n")[0].Arguments[1] == "hello ) # world", "方括号字符串包含括号和注释字符");
Reject(() => CMakeSyntax.Parse("add_executable(demo \"unterminated)"), "拒绝未闭合语法");
Reject(() => SourceEditor.Plan(root, target, "../escape.cpp"), "拒绝越出源码目录");
Reject(() => SourceEditor.Plan(root, target, "a;evil.cpp"), "拒绝 CMake 列表注入");
Reject(() => SourceEditor.Plan(root, target, "main.cpp"), "拒绝覆盖现有文件");
Reject(() => SourceEditor.Plan(root, target, "missing/new.cpp"), "拒绝缺失父目录");
Write("if(WIN32)\nadd_executable(demo main.cpp)\nendif()\n");
Reject(() => SourceEditor.Plan(root, target, "new.cpp"), "拒绝条件声明");
Write("function(make_it)\nadd_executable(demo main.cpp)\nendfunction()\n");
Reject(() => SourceEditor.Plan(root, target, "new.cpp"), "拒绝函数内声明");
Write("add_executable(${NAME} main.cpp)\n");
Reject(() => SourceEditor.Plan(root, target, "new.cpp"), "拒绝变量目标名");
Write("add_library(demo ALIAS another)\n");
Reject(() => SourceEditor.Plan(root, target, "new.cpp"), "拒绝别名目标");
Write(source);
var conflict = SourceEditor.Plan(root, target, "conflict.cpp");
Write(source + "# external edit\n");
Reject(() => SourceEditor.Apply(conflict), "预览后脚本变化不会覆盖");
Check(!File.Exists(conflict.SourcePath), "冲突不创建源码");
Write(source);
var collision = SourceEditor.Plan(root, target, "collision.cpp");
File.WriteAllText(collision.SourcePath, "user content", utf8);
Reject(() => SourceEditor.Apply(collision), "预览后新增同名文件不会覆盖");
Check(File.ReadAllText(collision.SourcePath) == "user content" && File.ReadAllText(script) == source, "碰撞保留原内容");
Write(source.Replace("\n", "\r\n"));
var header = SourceEditor.Plan(root, target, "新头文件.hpp");
Check(header.UpdatedScript.Replace("\r\n", "").IndexOf('\n') < 0, "保留 CRLF");
Write(source);
var beforeReturn = source + "return()\n";
Write(beforeReturn);
var early = SourceEditor.Plan(root, target, "early.cpp");
Check(early.UpdatedScript.IndexOf("target_sources", StringComparison.Ordinal) < early.UpdatedScript.IndexOf("return()", StringComparison.Ordinal), "插入位置不落在 return 后");
Write(source.Replace("add_executable(demo main.cpp)", "add_executable(demo main.cpp) # keep comment"));
var inline = SourceEditor.Plan(root, target, "inline.cpp");
Check(inline.UpdatedScript.Contains("add_executable(demo main.cpp) # keep comment\n"), "保留目标声明尾部注释");
File.WriteAllText(script, source.Replace("\n", "\r\n"), new UTF8Encoding(true));
var bomPlan = SourceEditor.Plan(root, target, "bom.cpp");
Check(bomPlan.OriginalScript.StartsWith("cmake_minimum_required") && bomPlan.OriginalScript.Contains("中文注释保留"), "UTF-8 BOM 模板解码保留中文且不把 BOM 当正文");
Check(bomPlan.UpdatedScript.Replace("\r\n", "").IndexOf('\n') < 0, "BOM 模板新增源码保留 CRLF");
var unsavedBom = SourceEditor.Plan(root, target, "unsaved-bom.cpp", bomPlan.OriginalScript + "# 未保存编辑\r\n");
Check(unsavedBom.UpdatedScript.Contains("# 未保存编辑"), "BOM 模板使用当前编辑器内容生成预览");
SourceEditor.Apply(bomPlan);
Check(File.Exists(bomPlan.SourcePath) && File.ReadAllText(script) == bomPlan.UpdatedScript && !File.ReadAllBytes(script).Take(3).SequenceEqual(new byte[] { 239, 187, 191 }), "BOM 模板应用后创建源码并规范为 UTF-8 无 BOM");
Reject(() => ScriptEncoding.ReadUtf8(new byte[] { 255, 254, 97, 0 }), "UTF-16 脚本明确拒绝而非乱码解码");
Reject(() => ScriptEncoding.ReadUtf8(new byte[] { 239, 187, 191, 195, 40 }), "BOM 后无效 UTF-8 明确拒绝");
Write(source);
var plan = SourceEditor.Plan(root, target, "新文件 space.cpp");
SourceEditor.Apply(plan);
Check(File.ReadAllText(script).Replace(plan.Addition, "") == source, "保留原始 CMake 文本及中文注释");
Check(!File.ReadAllBytes(script).Take(3).SequenceEqual(new byte[] { 239, 187, 191 }), "UTF-8 无 BOM");
Check(File.Exists(plan.SourcePath), "源码创建成功");

Check(WorkspacePaths.IsLocalAbsolute(@"D:\中文 space\build"), "识别本机绝对构建路径");
Check(!WorkspacePaths.IsLocalAbsolute("/home/user/build") && !WorkspacePaths.IsLocalAbsolute("build/debug") && !WorkspacePaths.IsLocalAbsolute(@"D:relative"), "不把远程或相对路径当成本机目录");
Check(!WorkspacePaths.IsLocalAbsolute(@"D:\${sourceDir}\build"), "拒绝尚未展开的 VS 属性");
var cacheFixture = Path.Combine(root, "cache-check");
Directory.CreateDirectory(cacheFixture);
Reject(() => WorkspacePaths.ValidateCache(root, cacheFixture), "未配置目录保持等待状态");
File.WriteAllText(Path.Combine(cacheFixture, "CMakeCache.txt"), "CMAKE_HOME_DIRECTORY:INTERNAL=" + root + "\n", utf8);
WorkspacePaths.ValidateCache(root, cacheFixture);
Check(true, "构建缓存验证源码归属");
Reject(() => WorkspacePaths.ValidateCache(Path.Combine(root, "other"), cacheFixture), "拒绝其他工作区的构建缓存");
File.WriteAllText(Path.Combine(cacheFixture, "CMakeCache.txt"), "CMAKE_BUILD_TYPE:STRING=Debug\n", utf8);
Reject(() => WorkspacePaths.ValidateCache(root, cacheFixture), "不读取缺少源码归属的部分缓存");

if (args.Contains("--integration"))
{
    string cmake = Environment.GetEnvironmentVariable("CMAKEPLUS_CMAKE") ?? "cmake";
    string build = Path.Combine(root, "out");
    CMakeFileApi.PrepareQuery(build);
    Run(cmake, "-S", root, "-B", build, "-G", "Visual Studio 17 2022", "-A", "x64");
    var snapshot = CMakeFileApi.Read(build);
    Check(Paths.Equal(snapshot.SourceDirectory, root), "真实 File API 源码根目录");
    Check(snapshot.Configurations.Count > 1, "读取多配置生成器");
    var demo = snapshot.Configurations.First(x => x.Name == "Debug").Targets.Single(x => x.Name == "demo");
    Check(demo.Sources.Any(x => Paths.Equal(x, plan.SourcePath)), "新增中文空格路径进入真实目标模型");
    Check(Paths.Equal(demo.DeclarationFile, script), "File API 目标声明回溯准确");
    Check(!snapshot.HasChangedInputs(), "刚配置的输入快照有效");
    File.SetLastWriteTimeUtc(script, snapshot.GeneratedUtc.AddSeconds(2));
    Check(snapshot.HasChangedInputs(), "读取旧索引仍能发现脚本过期");
    File.SetLastWriteTimeUtc(script, snapshot.GeneratedUtc.AddSeconds(-1));
    var second = SourceEditor.Plan(root, demo, "second.cpp");
    SourceEditor.Apply(second);
    Run(cmake, "-S", root, "-B", build);
    Run(cmake, "--build", build, "--config", "Debug");
    var ctest = Path.Combine(Path.GetDirectoryName(FindOnPath(cmake))!, "ctest.exe");
    Run(ctest, "--test-dir", build, "-C", "Debug", "--output-on-failure");
    Check(CMakeFileApi.Read(build).Configurations.First(x => x.Name == "Debug").Targets.Single(x => x.Name == "demo").Sources.Any(x => Paths.Equal(x, second.SourcePath)), "模型读取→新增源码→重新配置→构建→CTest 闭环");
}
TargetEditingChecks.Run(root, Check, Reject, args.Contains("--integration") ? Run : null, Environment.GetEnvironmentVariable("CMAKEPLUS_CMAKE") ?? "cmake");
WorkspaceChecks.Run(root, Check, Reject, args.Contains("--integration") ? Run : null, Environment.GetEnvironmentVariable("CMAKEPLUS_CMAKE") ?? "cmake");
Console.WriteLine($"PASS {passed} checks. Fixtures: {root}");

static string FindOnPath(string name)
{
    if (Path.IsPathRooted(name)) return name;
    foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        if (File.Exists(Path.Combine(dir, name + ".exe"))) return Path.Combine(dir, name + ".exe");
    throw new FileNotFoundException(name);
}
static void Run(string file, params string[] arguments)
{
    var start = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    foreach (var arg in arguments) start.ArgumentList.Add(arg);
    using var process = Process.Start(start)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    if (!process.WaitForExit(120000)) { process.Kill(true); throw new TimeoutException(file); }
    Task.WaitAll(stdout, stderr);
    Console.WriteLine(stdout.Result);
    if (process.ExitCode != 0) throw new Exception(file + " failed: " + stderr.Result);
}
