using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Automation;

internal static class PortableSmoke
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 3) { Console.Error.WriteLine("PortableSmoke.exe <portable-directory> <fixture> <scratch-directory>"); return 2; }
        try
        {
            string portable = Path.GetFullPath(args[0]), scratch = Path.GetFullPath(args[2]);
            string executable = Path.Combine(portable, "本地视频拉黑.exe");
            string blacklist = Path.Combine(portable, "blacklist.txt");
            Assert(File.Exists(blacklist) && new FileInfo(blacklist).Length == 0, "仅能测试空白发布副本");
            Assert(!Directory.Exists(Path.Combine(portable, "app", "logs")), "测试副本已有日志");
            Directory.CreateDirectory(scratch);
            string first = Path.Combine(scratch, "first.mp4"), second = Path.Combine(scratch, "second.mp4");
            File.Copy(args[1], first); File.Copy(args[1], second);
            Run(executable, first, blacklist, scratch);
            byte[] original = File.ReadAllBytes(blacklist);
            Assert(File.ReadAllLines(blacklist).Length == 1 && Encoding.UTF8.GetString(original).Contains(" | blacklisted_at="), "首次登记不完整");
            Run(executable, second, blacklist, scratch);
            Assert(File.ReadAllBytes(blacklist).SequenceEqual(original), "重复回收改动了登记或首次时间");
            Assert(!File.Exists(first) && !File.Exists(second), "测试视频未回收");
            string[] rootItems = Directory.GetFileSystemEntries(portable).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Assert(rootItems.SequenceEqual(new[] { "app", "blacklist.txt", "本地视频拉黑.exe" }), "运行后第一层出现多余文件");
            Assert(Directory.GetFiles(Path.Combine(portable, "app", "logs"), "*.jsonl").Length == 2, "日志没有写到二级目录");
            Console.WriteLine("PASS portable executable: name, default path from a different working directory, registration, duplicate recycle, first timestamp, root layout and app/logs");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Run(string executable, string video, string blacklist, string workingDirectory)
    {
        ProcessStartInfo start = new ProcessStartInfo(executable, "\"" + video + "\"");
        start.UseShellExecute = false; start.WorkingDirectory = workingDirectory;
        using (Process process = Process.Start(start))
        {
            try
            {
                Assert(process.WaitForInputIdle(15000), "窗口初始化超时");
                Stopwatch wait = Stopwatch.StartNew();
                while (process.MainWindowHandle == IntPtr.Zero && wait.ElapsedMilliseconds < 10000) { Thread.Sleep(50); process.Refresh(); }
                Assert(process.MainWindowTitle == "本地视频拉黑", "程序窗口名称不符");
                AutomationElement window = AutomationElement.FromHandle(process.MainWindowHandle);
                AutomationElement path = window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "黑名单路径"));
                Assert(path != null && ((ValuePattern)path.GetCurrentPattern(ValuePattern.Pattern)).Current.Value == blacklist, "实际 EXE 的默认黑名单位置错误");
                AutomationElement button = window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "拉黑并移入回收站"));
                Assert(button != null, "未找到处理按钮");
                ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                wait.Restart();
                while (wait.ElapsedMilliseconds < 20000)
                {
                    Thread.Sleep(50);
                    if (!File.Exists(video) && button.Current.IsEnabled == false)
                    {
                        AutomationElement stop = window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "停止"));
                        if (stop != null && !stop.Current.IsEnabled) break;
                    }
                }
                Assert(!File.Exists(video), "实际 EXE 未完成回收");
                process.CloseMainWindow();
                Assert(process.WaitForExit(10000) && process.ExitCode == 0, "程序未正常退出");
            }
            finally { if (!process.HasExited) { process.CloseMainWindow(); if (!process.WaitForExit(3000)) process.Kill(); } }
        }
    }

    private static void Assert(bool success, string message) { if (!success) throw new Exception(message); }
}
