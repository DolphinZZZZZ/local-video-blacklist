using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace VideoBlacklist
{
    public sealed class VideoRecord
    {
        public string Path { get; set; }
        public long Size { get; set; }
        public double Duration { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string Codecs { get; set; }
        public string SHA256 { get; set; }
        public string BlacklistedAt { get; set; }

        public string ToLine()
        {
            if (string.IsNullOrEmpty(Path) || Path.IndexOfAny(new[] { '\r', '\n', '|' }) >= 0 ||
                Size < 0 || Duration <= 0 || double.IsInfinity(Duration) || double.IsNaN(Duration) ||
                Width <= 0 || Height <= 0 || !Regex.IsMatch(Codecs ?? "", @"^[a-zA-Z0-9_+]+$") ||
                !Regex.IsMatch(SHA256 ?? "", @"\A[0-9A-Fa-f]{64}\z"))
                throw new InvalidDataException("视频指纹或元数据不完整，未登记。 ");
            DateTimeOffset parsedTime;
            if (!string.IsNullOrEmpty(BlacklistedAt) && !DateTimeOffset.TryParseExact(BlacklistedAt,
                "yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsedTime))
                throw new InvalidDataException("拉黑时间格式无效。 ");
            string line = string.Format(CultureInfo.InvariantCulture,
                "{0} | size={1} | duration={2:0.000} | {3}x{4} | {5} | sha256={6}",
                Path, Size, Duration, Width, Height, Codecs, SHA256.ToUpperInvariant());
            return line + (string.IsNullOrEmpty(BlacklistedAt) ? "" : " | blacklisted_at=" + BlacklistedAt);
        }
    }

    // A Windows sharing lock protects the actual blacklist inode, including against
    // older writers that do not know this application. Never rewrite a cached copy.
    public sealed class BlacklistStore
    {
        private static readonly Regex SizeField = new Regex(@"(?:^|\|)\s*size=(\d+)\s*(?=\||$)");
        private static readonly Regex HashField = new Regex(@"(?:^|\|)\s*sha256=([0-9a-fA-F]{64})\s*(?=\||$)");
        private static readonly Regex TimeField = new Regex(@"(?:^|\|)\s*blacklisted_at=([^|\r\n]+)");
        public readonly string Path;
        public readonly int WaitMilliseconds;

        public BlacklistStore(string path, int waitMilliseconds)
        {
            Path = System.IO.Path.GetFullPath(path);
            WaitMilliseconds = waitMilliseconds;
        }

        public bool Append(VideoRecord record, CancellationToken cancel, Action<string, int> progress)
        {
            record.ToLine();
            using (FileStream stream = OpenForAppend(cancel, progress))
            {
                if (stream.Length > int.MaxValue) throw new IOException("黑名单过大，未写入。 ");
                byte[] original = new byte[(int)stream.Length];
                int offset = 0;
                while (offset < original.Length)
                {
                    int read = stream.Read(original, offset, original.Length - offset);
                    if (read == 0) throw new EndOfStreamException("黑名单读取不完整。 ");
                    offset += read;
                }
                int preamble;
                Encoding encoding = DetectEncoding(original, out preamble);
                string text = encoding.GetString(original, preamble, original.Length - preamble);
                foreach (string oldLine in text.Split('\n'))
                {
                    Match size = SizeField.Match(oldLine);
                    Match hash = HashField.Match(oldLine);
                    long parsedSize;
                    if (size.Success && hash.Success && long.TryParse(size.Groups[1].Value, out parsedSize) &&
                        parsedSize == record.Size && string.Equals(hash.Groups[1].Value, record.SHA256, StringComparison.OrdinalIgnoreCase))
                    {
                        // A preceding writer may have crashed after Write but before
                        // Flush. Commit even a duplicate before permitting recycling.
                        stream.Flush(true);
                        Match originalTime = TimeField.Match(oldLine);
                        record.BlacklistedAt = originalTime.Success ? originalTime.Groups[1].Value.Trim() : null;
                        return false;
                    }
                }
                cancel.ThrowIfCancellationRequested();
                // Stamp inside the write lock, after hashing and any lock wait.
                // This is the first registration time, not a later retry time.
                record.BlacklistedAt = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8))
                    .ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
                string line = record.ToLine();
                string separator = text.Length > 0 && !text.EndsWith("\n", StringComparison.Ordinal) ? "\r\n" : "";
                byte[] addition = encoding.GetBytes(separator + line + "\r\n");
                long originalLength = stream.Length;
                try
                {
                    stream.Position = originalLength;
                    stream.Write(addition, 0, addition.Length);
                    stream.Flush(true); // Commit before any recycling.
                    stream.Position = originalLength;
                    byte[] check = new byte[addition.Length];
                    int readTotal = 0;
                    while (readTotal < check.Length)
                    {
                        int read = stream.Read(check, readTotal, check.Length - readTotal);
                        if (read == 0) break;
                        readTotal += read;
                    }
                    if (readTotal != addition.Length || !check.SequenceEqual(addition))
                        throw new IOException("追加内容未能读回验证。 ");
                }
                catch
                {
                    // Only undo our own partial tail while still owning the write lock.
                    stream.SetLength(originalLength);
                    stream.Flush(true);
                    throw;
                }
                return true;
            }
        }

        private FileStream OpenForAppend(CancellationToken cancel, Action<string, int> progress)
        {
            Stopwatch wait = Stopwatch.StartNew();
            bool announced = false;
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    // Share Read denies other Write and Delete handles, so a writer
                    // or an atomic-replace attempt must wait until this handle closes.
                    return new FileStream(Path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                }
                catch (IOException error)
                {
                    int code = error.HResult & 0xffff;
                    if (code != 32 && code != 33) throw;
                    if (wait.ElapsedMilliseconds >= WaitMilliseconds)
                        throw new IOException("黑名单正被其他程序占用，等待超时；视频未回收。", error);
                    if (!announced && progress != null) { progress("等待黑名单写入锁…", -1); announced = true; }
                    if (cancel.WaitHandle.WaitOne(100)) cancel.ThrowIfCancellationRequested();
                }
            }
        }

        private static Encoding DetectEncoding(byte[] bytes, out int preamble)
        {
            preamble = 0;
            if (bytes.Length >= 4 && ((bytes[0] == 0xff && bytes[1] == 0xfe && bytes[2] == 0 && bytes[3] == 0) ||
                (bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xfe && bytes[3] == 0xff)))
                throw new InvalidDataException("不支持 UTF-32 黑名单；未改动原内容。 ");
            if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf)
            { preamble = 3; return new UTF8Encoding(false, true); }
            if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe)
            { preamble = 2; return new UnicodeEncoding(false, false, true); }
            if (bytes.Length >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff)
            { preamble = 2; return new UnicodeEncoding(true, false, true); }
            return new UTF8Encoding(false, true);
        }
    }

    public interface IVideoProbe { VideoRecord Read(string path, CancellationToken cancel); }

    public sealed class Ffprobe : IVideoProbe
    {
        private readonly string executable;
        public Ffprobe(string executablePath) { executable = executablePath; }

        public VideoRecord Read(string path, CancellationToken cancel)
        {
            if (!File.Exists(executable)) throw new FileNotFoundException("缺少 ffprobe.exe，请完整解压程序包。", executable);
            ProcessStartInfo start = new ProcessStartInfo(executable,
                "-v error -show_entries format=duration:stream=codec_type,codec_name,width,height -of json \"" + path + "\"");
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.StandardOutputEncoding = Encoding.UTF8;
            start.StandardErrorEncoding = Encoding.UTF8;
            using (Process process = Process.Start(start))
            {
                Task<string> output = process.StandardOutput.ReadToEndAsync();
                Task<string> errors = process.StandardError.ReadToEndAsync();
                Stopwatch timer = Stopwatch.StartNew();
                try
                {
                    while (!process.WaitForExit(100))
                    {
                        cancel.ThrowIfCancellationRequested();
                        if (timer.Elapsed.TotalSeconds > 60) throw new TimeoutException("读取视频信息超时，原文件保留。 ");
                    }
                    if (process.ExitCode != 0) throw new InvalidDataException("视频信息读取失败：" + errors.Result.Trim());
                    ProbeData data = new JavaScriptSerializer().Deserialize<ProbeData>(output.Result);
                    ProbeStream video = data.streams == null ? null : data.streams.FirstOrDefault(s => s.codec_type == "video");
                    ProbeStream audio = data.streams == null ? null : data.streams.FirstOrDefault(s => s.codec_type == "audio");
                    double duration;
                    if (video == null || data.format == null ||
                        !double.TryParse(data.format.duration, NumberStyles.Float, CultureInfo.InvariantCulture, out duration))
                        throw new InvalidDataException("没有有效的视频轨或时长，原文件保留。 ");
                    return new VideoRecord { Path = path, Duration = duration, Width = video.width, Height = video.height,
                        Codecs = video.codec_name + (audio == null ? "" : "+" + audio.codec_name) };
                }
                finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(); } }
            }
        }
        public sealed class ProbeData { public ProbeStream[] streams { get; set; } public ProbeFormat format { get; set; } }
        public sealed class ProbeFormat { public string duration { get; set; } }
        public sealed class ProbeStream
        {
            public string codec_name { get; set; }
            public string codec_type { get; set; }
            public int width { get; set; }
            public int height { get; set; }
        }
    }

    public interface IRecycleOperation { string Execute(FileIdentity identity, long size); }
    public interface IRecycler { IRecycleOperation Prepare(string path); }

    public sealed class ProcessResult
    {
        public string Path { get; set; }
        public bool Registered { get; set; }
        public bool Added { get; set; }
        public bool Recycled { get; set; }
        public bool Cancelled { get; set; }
        public string Error { get; set; }
        public string RecycledPath { get; set; }
        public VideoRecord Record { get; set; }
    }

    public sealed class Journal : IDisposable
    {
        private readonly StreamWriter writer;
        public readonly string Path;
        public Journal(string directory)
        {
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".jsonl");
            writer = new StreamWriter(new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
        }
        public void Write(string stage, object data)
        {
            writer.WriteLine(new JavaScriptSerializer().Serialize(new { TimeUtc = DateTime.UtcNow.ToString("o"), Stage = stage, Data = data }));
            writer.Flush();
            ((FileStream)writer.BaseStream).Flush(true);
        }
        public void Dispose() { writer.Dispose(); }
    }

    public sealed class Processor
    {
        private readonly BlacklistStore store;
        private readonly IVideoProbe probe;
        private readonly IRecycler recycler;
        public Processor(BlacklistStore store, IVideoProbe probe, IRecycler recycler)
        { this.store = store; this.probe = probe; this.recycler = recycler; }

        public ProcessResult Run(string input, CancellationToken cancel, Action<string, int> progress, Journal journal)
        {
            ProcessResult result = new ProcessResult { Path = input };
            try
            {
                string path = System.IO.Path.GetFullPath(input);
                result.Path = path;
                if (string.Equals(path, store.Path, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("黑名单本身不能作为视频处理。 ");
                NativeFiles.CheckPath(path);
                using (SourceLock sourceLock = SourceLock.Acquire(path, cancel, progress))
                // Deny writes during probing, hashing and recycling. Delete sharing is
                // needed by Shell; file IDs detect a renamed/replaced source path.
                using (FileStream video = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                {
                    FileIdentity identity = FileIdentity.From(video);
                    progress("读取视频信息…", -1);
                    VideoRecord record = probe.Read(path, cancel);
                    record.Size = video.Length;
                    record.SHA256 = Hash(video, cancel, progress);
                    record.ToLine();
                    identity.AssertPath(path);
                    IRecycleOperation operation = recycler.Prepare(path);
                    result.Record = record;
                    journal.Write("Prepared", record);
                    result.Added = store.Append(record, cancel, progress);
                    result.Registered = true;
                    // After commit, finish this recycle even if Stop was clicked.
                    journal.Write(result.Added ? "Registered" : "AlreadyRegistered", record);
                    progress(result.Added ? "已登记，正在移入回收站…" : "已在黑名单，正在移入回收站…", -1);
                    identity.AssertPath(path);
                    result.RecycledPath = operation.Execute(identity, record.Size);
                    result.Recycled = true;
                    journal.Write("Recycled", result);
                }
            }
            catch (OperationCanceledException) { result.Cancelled = true; result.Error = "已停止，原文件保留。"; }
            catch (Exception error) { result.Error = error.Message; }
            // A logging failure must not turn a successful recycle into a retryable failure.
            try { journal.Write("Result", result); } catch (Exception error) { result.Error = (result.Error ?? "日志保存失败：") + error.Message; }
            return result;
        }

        private static string Hash(FileStream stream, CancellationToken cancel, Action<string, int> progress)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] buffer = new byte[1024 * 1024];
                int read;
                long total = 0;
                int last = -1;
                progress("计算 SHA256…", 0);
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    sha.TransformBlock(buffer, 0, read, buffer, 0);
                    total += read;
                    int percent = stream.Length == 0 ? 100 : (int)(total * 100.0 / stream.Length);
                    if (percent != last) { progress("计算 SHA256… " + percent + "%", percent); last = percent; }
                }
                cancel.ThrowIfCancellationRequested();
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", "");
            }
        }
    }

    internal sealed class SourceLock : IDisposable
    {
        private readonly Mutex mutex;
        private SourceLock(Mutex mutex) { this.mutex = mutex; }
        public static SourceLock Acquire(string path, CancellationToken cancel, Action<string, int> progress)
        {
            string key;
            using (SHA256 sha = SHA256.Create())
                key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(path.ToUpperInvariant()))).Replace("-", "");
            Mutex mutex = new Mutex(false, @"Local\VideoBlacklist.File." + key);
            try
            {
                Stopwatch timer = Stopwatch.StartNew();
                while (true)
                {
                    cancel.ThrowIfCancellationRequested();
                    bool entered;
                    try { entered = mutex.WaitOne(100); }
                    catch (AbandonedMutexException) { entered = true; }
                    if (entered) return new SourceLock(mutex);
                    if (timer.ElapsedMilliseconds > 30000) throw new IOException("该视频正在被另一个实例处理，等待超时。 ");
                    progress("等待另一个实例处理该视频…", -1);
                }
            }
            catch { mutex.Dispose(); throw; }
        }
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }
}
