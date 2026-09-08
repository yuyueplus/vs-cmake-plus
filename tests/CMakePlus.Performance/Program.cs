using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CMakePlus;
using CMakePlus.Core;
using Newtonsoft.Json;

internal static class Program
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    [STAThread]
    private static void Main(string[] args)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        dispatcher.InvokeAsync(async () =>
        {
            try { await Run(args.Length == 0 ? "artifacts/performance" : args[0]); }
            catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
            finally { dispatcher.InvokeShutdown(); }
        });
        Dispatcher.Run();
    }
    private static async Task Run(string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var reports = new List<object>();
        var changes = new DirectoryChangeBatch();
        await Task.Run(() => Parallel.For(0, 100000, i => changes.Add(true, directory: "D:\\source")));
        var batch = changes.Take();
        Check(batch != null && batch.Invalidated && !batch.All && batch.Directories.Length == 1, "100k concurrent events coalesced");
        Check(changes.Take() == null, "batch drains once");
        for (int i = 0; i < 100000; i++) changes.Add(true, directory: "D:\\source" + i);
        batch = changes.Take();
        Check(batch != null && batch.All && batch.Directories.Length == 0, "overflow bounded fallback");
        changes.Add(true, directory: "D:\\new", oldDirectory: "D:\\old");
        Check(changes.Take()!.Directories.Length == 2, "rename both parents");
        changes.Add(true);
        Check(changes.Take()!.Directories.Length == 0, "content save avoids enumeration");
        foreach (var count in new[] { 10000, 100000 })
        {
            var root = Path.Combine(output, "files-" + count);
            Console.WriteLine("准备真实文件：" + count);
            await Task.Run(() =>
            {
                Directory.CreateDirectory(root);
                for (int i = 0; i < count; i++)
                {
                    var p = Path.Combine(root, "source" + i.ToString("D6") + ".cpp");
                    if (!File.Exists(p)) File.WriteAllText(p, "", new UTF8Encoding(false));
                }
            });
            var config = new CMakeConfiguration();
            var target = new CMakeTarget { Name = "large", DeclarationFile = Path.Combine(root, "CMakeLists.txt") };
            target.Sources.AddRange(Enumerable.Range(0, count).Select(i => Path.Combine(root, "source" + i.ToString("D6") + ".cpp")));
            config.Targets.Add(target);
            var shared = new CMakeTarget { Name = "shared" }; shared.Sources.Add(target.Sources[0]); shared.Sources.Add(target.Sources[0]); config.Targets.Add(shared);
            var clock = Stopwatch.StartNew(); double last = 0, maxGap = 0;
            var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (s, e) => { var now = clock.Elapsed.TotalMilliseconds; maxGap = Math.Max(maxGap, now - last); last = now; };
            timer.Start();
            var before = GC.GetTotalMemory(true);
            var watch = Stopwatch.StartNew();
            var index = await Task.Run(() => new SourceIndex(config));
            var indexMs = watch.Elapsed.TotalMilliseconds;
            Check(index.Find(target.Sources[0].ToUpperInvariant()).Count == 2, "shared/case/duplicate ownership");
            Check(index.Find(Path.Combine(root, "missing.cpp")).Count == 0, "missing ownership");
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel(); bool rejected = false;
                try { new SourceIndex(config, cancelled.Token); } catch (OperationCanceledException) { rejected = true; }
                Check(rejected, "cancelled index");
            }
            watch.Restart();
            var nodes = await Task.Run(() => TargetNode.Build(root, config.Targets).ToArray());
            var rootsMs = watch.Elapsed.TotalMilliseconds;
            Check(nodes.Length == 2 && nodes[0].Children.Count == 1, "collapsed tree is lazy");
            watch.Restart(); await nodes[0].LoadAsync(); await nodes[0].Children[0].LoadAsync();
            var expandMs = watch.Elapsed.TotalMilliseconds;
            var pages = nodes[0].Children[0].Children;
            Check(pages.Count == (count + 499) / 500, "wide folder pages");
            await Task.WhenAll(pages[0].LoadAsync(), pages[0].LoadAsync());
            Check(pages[0].Children.Count == 500 && pages[0].Children[0].Path == target.Sources[0], "page content / concurrent expansion");
            watch.Restart();
            for (int i = 0; i < 10000; i++) index.Find(target.Sources[i % count]);
            var lookupMs = watch.Elapsed.TotalMilliseconds;
            var directory = new FileNode(root, true);
            watch.Restart(); await directory.LoadAsync(); var directoryMs = watch.Elapsed.TotalMilliseconds;
            Check(directory.Children.Count == count, "real directory count");
            var oldCollection = directory.Children; var oldNode = directory.Children[0];
            watch.Restart(); await directory.RefreshAsync(); var refreshMs = watch.Elapsed.TotalMilliseconds;
            Check(ReferenceEquals(oldCollection, directory.Children), "unchanged refresh preserves collection");
            var extra = Path.Combine(root, "added.tmp"); File.WriteAllText(extra, "", new UTF8Encoding(false));
            await directory.RefreshAsync(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            Check(directory.Children.Count == count, "unrelated event does not enumerate");
            await directory.RefreshAsync(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root });
            Check(directory.Children.Count == count + 1 && directory.Children.Contains(oldNode), "targeted refresh preserves nodes");
            File.Delete(extra); await directory.RefreshAsync();
            await Task.Delay(30); timer.Stop();
            var retainedBytes = GC.GetTotalMemory(true) - before;
            // Same scan shape as 0.3, bounded to 100 lookups to avoid a very long baseline.
            watch.Restart();
            for (int i = 0; i < 100; i++) config.Targets.Where(t => t.Sources.Any(p => Paths.Equal(p, target.Sources[count - 1]))).Select(t => t.Name).ToArray();
            var baseline100Ms = watch.Elapsed.TotalMilliseconds;
            var report = new { count, indexMs, rootsMs, expandMs, lookup10000Ms = lookupMs, baselineLookup100Ms = baseline100Ms, directoryMs, unchangedRefreshMs = refreshMs, maxDispatcherGapMs = maxGap, retainedBytes };
            reports.Add(report); Console.WriteLine(JsonConvert.SerializeObject(report));
        }
        File.WriteAllText(Path.Combine(output, "results.json"), JsonConvert.SerializeObject(reports, Formatting.Indented), new UTF8Encoding(false));
        Console.WriteLine("性能与行为检查通过；结果：" + Path.Combine(output, "results.json"));
    }
}
