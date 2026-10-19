using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Reflection;
using System.Windows.Forms;
using System.Globalization;
using VideoBlacklist;

internal static class Tests
{
    private static string root, fixture, ffprobe;
    private static readonly List<string> passed = new List<string>();
    private static readonly Action<string, int> Quiet = delegate { };
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "worker") return Worker(args);
        if (args.Length != 3) { Console.Error.WriteLine("Tests.exe <test-directory> <generated-video> <ffprobe>"); return 2; }
        root = Path.GetFullPath(args[0]); fixture = args[1]; ffprobe = args[2];
        Directory.CreateDirectory(root);
        try
        {
            Run("多进程并发追加：288 条独立记录，无丢失或覆盖", delegate { Concurrent(false); });
            Run("多进程并发去重：240 次相同内容登记，只保留 10 条", delegate { Concurrent(true); });
            Run("兼容旧脚本写锁：等待后重读，保留旧脚本最新追加", LegacyLock);
            Run("黑名单占用超时：内容不变，未覆盖", LockTimeout);
            Run("等待写锁时取消：内容不变", LockCancellation);
            Run("追加保留 UTF-8 BOM、UTF-16 BOM、旧内容和末尾换行", Encodings);
            Run("旧路径条目补全指纹；同路径不同内容分别保留", LegacyFingerprint);
            Run("登记失败不回收；非法编码原样保留", RegistrationFailure);
            Run("回收失败后重试：登记保留且不重复", RecycleFailureRetry);
            Run("元数据失败或源文件写入冲突：不登记、不回收", ProbeAndWriteFailure);
            Run("真实 ffprobe 拒绝非视频文件", InvalidVideo);
            Run("真实回收站：中文路径、相同内容改名、大小和 SHA256", RealRecycle);
            Run("真实拖放事件：重复路径去重、未点击不登记不回收", DragDropQueue);
            Run("真实窗口后台流程：登记、回收、结果状态完整", FormWorkflow);
            Run("新登记包含毫秒及 +08:00 时间，重复登记保留首次时间", RegistrationTime);
            Run("拉黑时间在取得写锁后生成，不使用排队前时间", RegistrationTimeAfterLock);
            Run("旧条目没有时间时不伪造；带来源的补写条目仍能去重", LegacyTimes);
            Run("便携目录：默认黑名单位于 EXE 旁，依赖及日志位于 app", PortablePaths);
            string report = new JavaScriptSerializer().Serialize(new { Passed = passed.Count, Failed = 0, Checks = passed,
                Runtime = Environment.Version.ToString(), VerifiedAtUtc = DateTime.UtcNow.ToString("o") });
            File.WriteAllText(Path.Combine(root, "test-results.json"), report, new UTF8Encoding(false));
            Console.WriteLine("ALL PASSED: " + passed.Count);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Run(string name, Action check)
    { check(); passed.Add(name); Console.WriteLine("PASS " + name); }
    private static void Assert(bool success, string message)
    { if (!success) throw new Exception(message); }
    private static string Folder(string name) { string path = Path.Combine(root, name); Directory.CreateDirectory(path); return path; }
    private static string Digest(string value)
    { using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", ""); }
    private static string HashFile(string path)
    { using (FileStream file = File.OpenRead(path)) using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", ""); }
    private static VideoRecord Record(string key, long size)
    { return new VideoRecord { Path = Path.Combine("synthetic-records", key + ".mp4"), Size = size, SHA256 = Digest(key), Duration = 1.234, Width = 1280, Height = 720, Codecs = "h264+aac" }; }

    private static void Concurrent(bool same)
    {
        string dir = Folder(same ? "parallel-same" : "parallel-unique");
        string path = Path.Combine(dir, "blacklist.txt");
        byte[] prefix = Encoding.UTF8.GetBytes("legacy-video.mp4\r\n");
        File.WriteAllBytes(path, prefix);
        string gateName = @"Local\VideoBlacklist.Test." + Guid.NewGuid().ToString("N");
        List<Process> workers = new List<Process>();
        using (EventWaitHandle gate = new EventWaitHandle(false, EventResetMode.ManualReset, gateName))
        {
            for (int i = 0; i < 12; i++)
            {
                ProcessStartInfo start = new ProcessStartInfo(System.Reflection.Assembly.GetExecutingAssembly().Location,
                    "worker \"" + path + "\" " + i + " " + (same ? "same" : "unique") + " " + gateName);
                start.UseShellExecute = false; start.CreateNoWindow = true;
                start.RedirectStandardOutput = true; start.RedirectStandardError = true;
                workers.Add(Process.Start(start));
            }
            gate.Set();
            foreach (Process worker in workers)
            {
                bool finished = worker.WaitForExit(60000);
                if (!finished) worker.Kill();
                Assert(finished, "并发工作进程超时");
                Assert(worker.ExitCode == 0, "并发工作进程失败：" + worker.StandardError.ReadToEnd());
                worker.Dispose();
            }
        }
        byte[] final = File.ReadAllBytes(path);
        Assert(final.Take(prefix.Length).SequenceEqual(prefix), "旧内容被覆盖");
        string[] lines = File.ReadAllLines(path);
        int count = same ? 10 : 288;
        Assert(lines.Length == count + 1, "记录数不匹配：" + lines.Length);
        Assert(lines.Skip(1).Distinct().Count() == count, "存在重复或不完整记录");
        if (same)
        {
            for (int i = 0; i < 10; i++) Assert(lines.Any(line => line.StartsWith(Record("shared-" + i, 100 + i).ToLine() + " | blacklisted_at=")), "共享记录丢失");
        }
        else
        {
            for (int i = 0; i < 12; i++) for (int n = 0; n < 24; n++)
                Assert(lines.Any(line => line.StartsWith(Record("worker-" + i + "-" + n, 1000 + i * 24 + n).ToLine() + " | blacklisted_at=")), "独立记录丢失");
        }
        foreach (string line in lines.Skip(1)) ParseTime(line.Substring(line.LastIndexOf("blacklisted_at=") + 15));
    }
    private static int Worker(string[] args)
    {
        try
        {
            using (EventWaitHandle gate = EventWaitHandle.OpenExisting(args[4])) Assert(gate.WaitOne(15000), "启动闸门超时");
            int id = int.Parse(args[2]); bool same = args[3] == "same";
            BlacklistStore store = new BlacklistStore(args[1], 30000);
            for (int n = 0; n < (same ? 20 : 24); n++)
            {
                VideoRecord record = same ? Record("shared-" + n % 10, 100 + n % 10) : Record("worker-" + id + "-" + n, 1000 + id * 24 + n);
                store.Append(record, CancellationToken.None, Quiet);
            }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void LegacyLock()
    {
        string path = Path.Combine(Folder("legacy-lock"), "blacklist.txt");
        File.WriteAllText(path, "旧条目\r\n", new UTF8Encoding(false));
        Task<bool> append;
        using (FileStream legacy = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            append = Task.Factory.StartNew(() => new BlacklistStore(path, 5000).Append(Record("after-lock", 1), CancellationToken.None, Quiet));
            Thread.Sleep(250);
            Assert(!append.IsCompleted, "未等待旧写入者");
            bool renameDenied = false;
            try { File.Move(path, path + ".moved"); } catch (IOException) { renameDenied = true; }
            Assert(renameDenied && File.Exists(path), "写入锁未阻止替换");
            byte[] addition = Encoding.UTF8.GetBytes("旧任务刚追加\r\n");
            legacy.Position = legacy.Length; legacy.Write(addition, 0, addition.Length); legacy.Flush(true);
        }
        Assert(append.Result, "追加失败");
        Assert(File.ReadAllText(path).StartsWith("旧条目\r\n旧任务刚追加\r\n"), "未在锁内重读最新内容");
    }
    private static void LockTimeout()
    {
        string path = Path.Combine(Folder("timeout"), "blacklist.txt");
        File.WriteAllText(path, "保留内容", new UTF8Encoding(false));
        using (FileStream hold = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            bool failed = false;
            try { new BlacklistStore(path, 200).Append(Record("timeout", 1), CancellationToken.None, Quiet); }
            catch (IOException) { failed = true; }
            Assert(failed, "锁占用应超时");
        }
        Assert(File.ReadAllText(path) == "保留内容", "锁超时改动了文件");
    }
    private static void LockCancellation()
    {
        string path = Path.Combine(Folder("cancel"), "blacklist.txt");
        File.WriteAllText(path, "原内容", new UTF8Encoding(false));
        using (FileStream hold = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        using (CancellationTokenSource cancel = new CancellationTokenSource(150))
        {
            bool cancelled = false;
            try { new BlacklistStore(path, 5000).Append(Record("cancel", 1), cancel.Token, Quiet); }
            catch (OperationCanceledException) { cancelled = true; }
            Assert(cancelled, "取消没有生效");
        }
        Assert(File.ReadAllText(path) == "原内容", "取消改动了文件");
    }
    private static void Encodings()
    {
        Encoding[] encodings = { new UTF8Encoding(false, true), new UTF8Encoding(true, true), new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true) };
        string dir = Folder("encodings");
        for (int i = 0; i < encodings.Length; i++)
        {
            string path = Path.Combine(dir, i + ".txt");
            File.WriteAllText(path, "legacy-video.mp4", encodings[i]);
            byte[] before = File.ReadAllBytes(path);
            VideoRecord record = Record("encoding-" + i, i);
            Assert(new BlacklistStore(path, 2000).Append(record, CancellationToken.None, Quiet), "未追加");
            byte[] after = File.ReadAllBytes(path);
            Assert(after.Take(before.Length).SequenceEqual(before), "编码或旧字节被改动");
            Assert(File.ReadAllText(path, encodings[i]) == "legacy-video.mp4\r\n" + record.ToLine() + "\r\n", "换行或编码错误");
        }
    }
    private static void LegacyFingerprint()
    {
        string path = Path.Combine(Folder("legacy-fingerprint"), "blacklist.txt");
        VideoRecord record = Record("existing-path", 42);
        File.WriteAllText(path, record.Path + "\r\n", new UTF8Encoding(false));
        BlacklistStore store = new BlacklistStore(path, 2000);
        Assert(store.Append(record, CancellationToken.None, Quiet), "旧路径未补全指纹");
        record.Path = Path.Combine(root, "renamed.mp4");
        Assert(!store.Append(record, CancellationToken.None, Quiet), "改名后重复登记");
        record.SHA256 = Digest("different-content");
        Assert(store.Append(record, CancellationToken.None, Quiet), "同路径新内容未登记");
        Assert(File.ReadAllLines(path).Length == 3, "指纹条目数量错误");
    }
    private static ProcessResult Pipeline(string path, string blacklist, IVideoProbe probe, IRecycler recycler)
    {
        using (Journal journal = new Journal(Path.Combine(Path.GetDirectoryName(path), "logs")))
            return new Processor(new BlacklistStore(blacklist, 2000), probe, recycler).Run(path, CancellationToken.None, Quiet, journal);
    }
    private static void RegistrationFailure()
    {
        string dir = Folder("registration-failure"), video = Path.Combine(dir, "样本.mp4"), blacklist = Path.Combine(dir, "blacklist.txt");
        File.WriteAllText(video, "owned test file");
        byte[] invalid = { 0xff, 0x80, 0x81 };
        File.WriteAllBytes(blacklist, invalid);
        FakeRecycler recycler = new FakeRecycler(false);
        ProcessResult result = Pipeline(video, blacklist, new FakeProbe(false, false), recycler);
        Assert(!result.Registered && !result.Recycled && recycler.Executions == 0 && File.Exists(video), "登记失败仍执行回收");
        Assert(File.ReadAllBytes(blacklist).SequenceEqual(invalid), "损坏编码被覆盖");
    }
    private static void RecycleFailureRetry()
    {
        string dir = Folder("retry"), video = Path.Combine(dir, "样本.mp4"), blacklist = Path.Combine(dir, "blacklist.txt");
        File.WriteAllText(video, "owned retry file");
        FakeRecycler recycler = new FakeRecycler(true);
        ProcessResult failed = Pipeline(video, blacklist, new FakeProbe(false, false), recycler);
        Assert(failed.Registered && !failed.Recycled && File.Exists(video), "回收失败状态错误");
        Assert(File.ReadAllLines(blacklist).Length == 1, "失败后登记未保留");
        ProcessResult retried = Pipeline(video, blacklist, new FakeProbe(false, false), recycler);
        Assert(retried.Registered && !retried.Added && retried.Recycled && !File.Exists(video), "重试未完成");
        Assert(File.ReadAllLines(blacklist).Length == 1, "重试重复追加");
    }
    private static void ProbeAndWriteFailure()
    {
        foreach (bool write in new[] { false, true })
        {
            string dir = Folder(write ? "source-write-conflict" : "probe-failure"), video = Path.Combine(dir, "样本.mp4"), blacklist = Path.Combine(dir, "blacklist.txt");
            File.WriteAllText(video, "unchanged test data");
            FakeRecycler recycler = new FakeRecycler(false);
            ProcessResult result = Pipeline(video, blacklist, new FakeProbe(!write, write), recycler);
            Assert(!result.Registered && !result.Recycled && recycler.Executions == 0 && !File.Exists(blacklist), "探测失败仍操作");
            Assert(File.ReadAllText(video) == "unchanged test data", "源文件未锁定写入");
        }
    }
    private static void InvalidVideo()
    {
        string dir = Folder("invalid-video"), video = Path.Combine(dir, "普通文本.mp4"), blacklist = Path.Combine(dir, "blacklist.txt");
        File.WriteAllText(video, "this is not a video");
        FakeRecycler recycler = new FakeRecycler(false);
        ProcessResult result = Pipeline(video, blacklist, new Ffprobe(ffprobe), recycler);
        Assert(!result.Registered && !result.Recycled && recycler.Executions == 0 && File.Exists(video) && !File.Exists(blacklist), "非视频未被拒绝");
    }
    private static void RealRecycle()
    {
        string dir = Folder("real-recycle"), blacklist = Path.Combine(dir, "blacklist.txt");
        string expectedHash = HashFile(fixture);
        for (int i = 0; i < 2; i++)
        {
            string video = Path.Combine(dir, i == 0 ? "中文【回收测试】.mp4" : "改名后的完全相同视频.mp4");
            File.Copy(fixture, video);
            ProcessResult result = Pipeline(video, blacklist, new Ffprobe(ffprobe), new WindowsRecycler());
            Assert(result.Registered && result.Recycled, "真实回收失败：" + result.Error);
            Assert(result.Added == (i == 0), "真实指纹去重错误");
            Assert(!File.Exists(video) && File.Exists(result.RecycledPath), "回收站可恢复性验证失败");
            Assert(HashFile(result.RecycledPath) == expectedHash, "真实回收内容 SHA256 错误");
            Assert(result.Record.Width == 160 && result.Record.Height == 90 && result.Record.Codecs == "h264+aac", "真实元数据错误");
        }
        Assert(File.ReadAllLines(blacklist).Length == 1, "真实改名去重记录错误");
    }

    private static void DragDropQueue()
    {
        string dir = Folder("dragdrop"), video = Path.Combine(dir, "拖入测试.mp4"), blacklist = Path.Combine(dir, "blacklist.txt");
        File.Copy(fixture, video);
        using (MainForm form = new MainForm(blacklist, new string[0]))
        {
            IntPtr handle = form.Handle;
            DropPanel panel = (DropPanel)Field(form, "drop");
            DataObject data = new DataObject();
            data.SetData(DataFormats.FileDrop, new[] { video, video, dir });
            MethodInfo drop = typeof(System.Windows.Forms.Control).GetMethod("OnDragDrop", BindingFlags.Instance | BindingFlags.NonPublic);
            drop.Invoke(panel, new object[] { new DragEventArgs(data, 0, 20, 20, DragDropEffects.Copy, DragDropEffects.Copy) });
            ListView list = (ListView)Field(form, "list");
            Assert(list.Items.Count == 1 && list.Items[0].Text == Path.GetFileName(video), "拖放未正确入队或去重");
            Assert(((Button)Field(form, "start")).Enabled, "有队列时按钮未启用");
            Assert(!File.Exists(blacklist) && File.Exists(video), "拖入即修改了文件");
        }
    }
    private static object Field(object target, string name)
    { return target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target); }
    private static void FormWorkflow()
    {
        string dir = Folder("form-flow"), video = Path.Combine(dir, "完整界面流程.mp4"), blacklist = Path.Combine(dir, "blacklist.txt");
        File.Copy(fixture, video);
        string helper = AppPaths.Probe;
        Directory.CreateDirectory(Path.GetDirectoryName(helper));
        if (!File.Exists(helper)) File.Copy(ffprobe, helper);
        using (MainForm form = new MainForm(blacklist, new string[0]))
        {
            IntPtr handle = form.Handle;
            typeof(MainForm).GetMethod("AddFiles", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, new object[] { new[] { video } });
            typeof(MainForm).GetMethod("StartBatch", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null);
            Stopwatch wait = Stopwatch.StartNew();
            while ((bool)Field(form, "busy") && wait.ElapsedMilliseconds < 20000)
            { Application.DoEvents(); Thread.Sleep(10); }
            Assert(!(bool)Field(form, "busy"), "界面后台任务超时");
            ListView list = (ListView)Field(form, "list");
            string status = list.Items[0].SubItems[2].Text;
            Assert(status == "已登记 · 已回收", "界面完成状态错误：" + status + " / " + list.Items[0].ToolTipText);
            Assert(!File.Exists(video) && File.ReadAllLines(blacklist).Length == 1, "界面真实操作未完成");
            Assert(!((Button)Field(form, "start")).Enabled, "成功项目仍可重复处理");
        }
    }

    private static DateTimeOffset ParseTime(string value)
    {
        DateTimeOffset parsed;
        Assert(DateTimeOffset.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed), "时间格式错误：" + value);
        Assert(parsed.Offset == TimeSpan.FromHours(8), "时区未明确为 +08:00");
        return parsed;
    }
    private static void RegistrationTime()
    {
        string path = Path.Combine(Folder("registration-time"), "blacklist.txt");
        BlacklistStore store = new BlacklistStore(path, 2000);
        VideoRecord record = Record("time", 123);
        DateTimeOffset before = DateTimeOffset.UtcNow;
        Assert(store.Append(record, CancellationToken.None, Quiet), "新记录未添加");
        DateTimeOffset timestamp = ParseTime(record.BlacklistedAt);
        Assert(timestamp >= before.AddMilliseconds(-1) && timestamp <= DateTimeOffset.UtcNow, "登记时间不在操作范围内");
        byte[] original = File.ReadAllBytes(path);
        Thread.Sleep(25);
        VideoRecord renamed = Record("time", 123); renamed.Path = Path.Combine(root, "renamed-video.mp4");
        Assert(!store.Append(renamed, CancellationToken.None, Quiet), "重复记录被添加");
        Assert(renamed.BlacklistedAt == record.BlacklistedAt && File.ReadAllBytes(path).SequenceEqual(original), "重复操作覆盖首次时间");
        VideoRecord fresh = Record("new-time", 124);
        Assert(store.Append(fresh, CancellationToken.None, Quiet) && ParseTime(fresh.BlacklistedAt) > timestamp, "新记录未获取新时间");
    }
    private static void RegistrationTimeAfterLock()
    {
        string path = Path.Combine(Folder("time-after-lock"), "blacklist.txt");
        File.WriteAllText(path, "旧条目\r\n", new UTF8Encoding(false));
        VideoRecord record = Record("after-wait", 5);
        Task<bool> append;
        DateTimeOffset releaseTime;
        using (FileStream hold = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            append = Task.Factory.StartNew(() => new BlacklistStore(path, 5000).Append(record, CancellationToken.None, Quiet));
            Thread.Sleep(300);
            Assert(record.BlacklistedAt == null && !append.IsCompleted, "排队时已经写入时间");
            releaseTime = DateTimeOffset.UtcNow;
        }
        Assert(append.Result && ParseTime(record.BlacklistedAt) >= releaseTime.AddMilliseconds(-1), "时间早于写锁释放");
    }
    private static void LegacyTimes()
    {
        string path = Path.Combine(Folder("legacy-times"), "blacklist.txt");
        VideoRecord old = Record("legacy-time", 8);
        string original = old.ToLine() + "\r\n";
        File.WriteAllText(path, original, new UTF8Encoding(false));
        BlacklistStore store = new BlacklistStore(path, 2000);
        Assert(!store.Append(old, CancellationToken.None, Quiet) && old.BlacklistedAt == null && File.ReadAllText(path) == original,
            "无证据的旧时间被当前时间替代");
        string historical = "2024-01-02T03:04:05.006+08:00";
        string withSource = original.TrimEnd('\r', '\n') + " | blacklisted_at=" + historical + " | time_source=task_log | time_precision=batch\r\n";
        File.WriteAllText(path, withSource, new UTF8Encoding(false));
        Assert(!store.Append(old, CancellationToken.None, Quiet) && old.BlacklistedAt == historical && File.ReadAllText(path) == withSource,
            "历史时间或来源字段被改动");
    }

    private static void PortablePaths()
    {
        string executableDirectory = AppDomain.CurrentDomain.BaseDirectory;
        Assert(AppPaths.DefaultBlacklist == Path.Combine(executableDirectory, "blacklist.txt"), "默认黑名单没有定位到 EXE 所在目录");
        Assert(AppPaths.Probe == Path.Combine(executableDirectory, "app", "ffprobe.exe"), "探测程序未位于二级目录");
        Assert(AppPaths.Logs == Path.Combine(executableDirectory, "app", "logs"), "日志会污染第一层目录");
        using (MainForm form = new MainForm(AppPaths.DefaultBlacklist, new string[0]))
        {
            Assert(form.Text == "本地视频拉黑", "窗口名称不符");
            Assert(((TextBox)Field(form, "blacklist")).Text == AppPaths.DefaultBlacklist, "窗口默认黑名单不符");
        }
    }

    private sealed class FakeProbe : IVideoProbe
    {
        private readonly bool fail, write;
        public FakeProbe(bool fail, bool write) { this.fail = fail; this.write = write; }
        public VideoRecord Read(string path, CancellationToken cancel)
        {
            if (fail) throw new IOException("simulated probe failure");
            if (write) using (FileStream attempt = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) attempt.WriteByte(1);
            VideoRecord record = Record("fake", 0); record.Path = path; return record;
        }
    }
    private sealed class FakeRecycler : IRecycler, IRecycleOperation
    {
        public int Executions;
        private readonly bool failFirst;
        private string path;
        public FakeRecycler(bool failFirst) { this.failFirst = failFirst; }
        public IRecycleOperation Prepare(string path) { this.path = path; return this; }
        public string Execute(FileIdentity identity, long size)
        {
            Executions++;
            if (failFirst && Executions == 1) throw new IOException("simulated recycle failure");
            File.Move(path, path + ".test-moved");
            return path + ".test-moved";
        }
    }
}
