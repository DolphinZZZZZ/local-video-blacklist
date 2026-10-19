using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace VideoBlacklist
{
    internal static class AppPaths
    {
        public static string DefaultBlacklist { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "blacklist.txt"); } }
        public static string SupportDirectory { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app"); } }
        public static string Probe { get { return Path.Combine(SupportDirectory, "ffprobe.exe"); } }
        public static string Logs { get { return Path.Combine(SupportDirectory, "logs"); } }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
            AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
            try { SetProcessDPIAware(); } catch (EntryPointNotFoundException) { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string blacklist = AppPaths.DefaultBlacklist;
            List<string> files = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--blacklist" && i + 1 < args.Length) blacklist = args[++i];
                else files.Add(args[i]);
            }
            Application.Run(new MainForm(blacklist, files.ToArray()));
        }
        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    }

    internal sealed class MainForm : Form
    {
        private readonly Color ink = Color.FromArgb(31, 41, 55);
        private readonly Color muted = Color.FromArgb(101, 113, 130);
        private readonly Color accent = Color.FromArgb(30, 91, 202);
        private readonly TextBox blacklist = new TextBox();
        private readonly ListView list = new ListView();
        private readonly Label detail = new Label();
        private readonly Label activity = new Label();
        private readonly Label count = new Label();
        private readonly ProgressBar progress = new ProgressBar();
        private readonly Button start, stop, add, remove, clear, browse;
        private readonly List<QueueItem> queue = new List<QueueItem>();
        private readonly DropPanel drop = new DropPanel();
        private readonly ImageList rowHeight = new ImageList();
        private CancellationTokenSource cancellation;
        private bool busy;

        public MainForm(string blacklistPath, string[] initialFiles)
        {
            Text = "本地视频拉黑";
            Font = new Font("Microsoft YaHei UI", 10F);
            ForeColor = ink;
            BackColor = Color.FromArgb(245, 247, 250);
            ClientSize = new Size(940, 710);
            MinimumSize = new Size(820, 690);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Font;

            TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 9 };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 94));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 12));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            Controls.Add(root);

            Panel heading = new Panel { Dock = DockStyle.Fill };
            heading.Controls.Add(new Label { Text = "本地视频拉黑", AutoSize = true, Font = new Font(Font.FontFamily, 23, FontStyle.Bold), Location = new Point(0, 0) });
            heading.Controls.Add(new Label { Text = "登记内容指纹，再移入回收站。", AutoSize = true, ForeColor = muted, Location = new Point(2, 47) });
            root.Controls.Add(heading, 0, 0);

            TableLayoutPanel pathRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0, 4, 0, 8) };
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            pathRow.Controls.Add(new Label { Text = "黑名单", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            blacklist.Text = blacklistPath;
            blacklist.Dock = DockStyle.Fill;
            blacklist.Margin = new Padding(0, 8, 10, 0);
            blacklist.AccessibleName = "黑名单路径";
            pathRow.Controls.Add(blacklist, 1, 0);
            browse = Button("选择…", false, 94);
            browse.Click += delegate
            {
                using (SaveFileDialog dialog = new SaveFileDialog { Title = "选择黑名单文件", Filter = "文本文件|*.txt", FileName = blacklist.Text, OverwritePrompt = false })
                    if (dialog.ShowDialog(this) == DialogResult.OK) blacklist.Text = dialog.FileName;
            };
            pathRow.Controls.Add(browse, 2, 0);
            root.Controls.Add(pathRow, 0, 1);

            drop.Dock = DockStyle.Fill;
            drop.Margin = new Padding(0, 0, 0, 2);
            Label dropTitle = new Label { Text = "将视频拖到这里", Dock = DockStyle.Top, Height = 47, TextAlign = ContentAlignment.BottomCenter, Font = new Font(Font.FontFamily, 13, FontStyle.Bold) };
            Label dropHint = new Label { Text = "支持多个文件 · 拖入后点击“拉黑并移入回收站”", Dock = DockStyle.Fill, TextAlign = ContentAlignment.TopCenter, Padding = new Padding(0, 6, 0, 0), ForeColor = muted };
            drop.Controls.Add(dropHint);
            drop.Controls.Add(dropTitle);
            root.Controls.Add(drop, 0, 2);
            WireDrop(this);
            WireDrop(drop);
            WireDrop(dropTitle);
            WireDrop(dropHint);

            TableLayoutPanel toolbar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0, 8, 0, 6) };
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            FlowLayoutPanel tools = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            add = Button("添加视频…", false, 118);
            remove = Button("移出队列", false, 108);
            clear = Button("清空队列", false, 108);
            add.Click += delegate { SelectFiles(); };
            remove.Click += delegate
            {
                foreach (ListViewItem item in list.SelectedItems.Cast<ListViewItem>().ToArray()) { queue.Remove((QueueItem)item.Tag); list.Items.Remove(item); }
                UpdateButtons();
            };
            clear.Click += delegate { queue.Clear(); list.Items.Clear(); detail.Text = ""; UpdateButtons(); };
            tools.Controls.AddRange(new Control[] { add, remove, clear });
            toolbar.Controls.Add(tools, 0, 0);
            count.Dock = DockStyle.Fill;
            count.TextAlign = ContentAlignment.MiddleRight;
            count.ForeColor = muted;
            toolbar.Controls.Add(count, 1, 0);
            root.Controls.Add(toolbar, 0, 3);

            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.HideSelection = false;
            list.ShowItemToolTips = true;
            list.BorderStyle = BorderStyle.FixedSingle;
            list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            list.Columns.Add("视频", 390);
            list.Columns.Add("大小", 105, HorizontalAlignment.Right);
            list.Columns.Add("处理结果", 320);
            rowHeight.ImageSize = new Size(1, 30);
            list.SmallImageList = rowHeight;
            list.SelectedIndexChanged += delegate
            {
                detail.Text = list.SelectedItems.Count == 0 ? "" : ((QueueItem)list.SelectedItems[0].Tag).Description;
                UpdateButtons();
            };
            list.SizeChanged += delegate { list.Columns[0].Width = Math.Max(210, list.ClientSize.Width - 432); };
            WireDrop(list);
            root.Controls.Add(list, 0, 4);

            detail.Dock = DockStyle.Fill;
            detail.ForeColor = muted;
            detail.AutoEllipsis = true;
            detail.Padding = new Padding(0, 10, 0, 0);
            root.Controls.Add(detail, 0, 5);
            activity.Text = "就绪。点击拉黑前，文件保持原样。";
            activity.Dock = DockStyle.Fill;
            activity.TextAlign = ContentAlignment.MiddleLeft;
            activity.AutoEllipsis = true;
            root.Controls.Add(activity, 0, 6);
            progress.Dock = DockStyle.Fill;
            progress.Margin = new Padding(0, 2, 0, 2);
            root.Controls.Add(progress, 0, 7);

            TableLayoutPanel footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0, 13, 0, 0) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            LinkLabel logs = new LinkLabel { Text = "查看运行日志", AutoSize = true, LinkColor = muted, Margin = new Padding(0, 14, 0, 0) };
            logs.LinkClicked += delegate
            {
                string path = AppPaths.Logs;
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            };
            footer.Controls.Add(logs, 0, 0);
            FlowLayoutPanel actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            stop = Button("停止", false, 90);
            start = Button("拉黑并移入回收站", true, 210);
            stop.Click += delegate { if (cancellation != null) { cancellation.Cancel(); stop.Enabled = false; activity.Text = "正在停止；已登记的当前文件会完成回收。"; } };
            start.Click += delegate { StartBatch(); };
            actions.Controls.Add(stop);
            actions.Controls.Add(start);
            footer.Controls.Add(actions, 1, 0);
            root.Controls.Add(footer, 0, 8);

            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (busy)
                {
                    e.Cancel = true;
                    cancellation.Cancel();
                    activity.Text = "正在停止；当前操作结束后可关闭窗口。";
                }
            };
            FormClosed += delegate { rowHeight.Dispose(); };
            Shown += delegate { AddFiles(initialFiles); UpdateButtons(); };
        }

        private Button Button(string text, bool primary, int width)
        {
            Button button = new Button { Text = text, Width = width, Height = 40, FlatStyle = FlatStyle.Flat,
                Margin = new Padding(0, 0, 8, 0), BackColor = primary ? accent : Color.White,
                ForeColor = primary ? Color.White : ink, Cursor = Cursors.Hand, UseVisualStyleBackColor = false };
            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.FlatAppearance.BorderColor = Color.FromArgb(218, 224, 232);
            button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(24, 75, 169) : Color.FromArgb(235, 240, 247);
            button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(19, 61, 142) : Color.FromArgb(221, 230, 242);
            return button;
        }

        private void WireDrop(Control target)
        {
            target.AllowDrop = true;
            target.DragEnter += delegate(object sender, DragEventArgs e)
            {
                bool accepted = !busy && e.Data.GetDataPresent(DataFormats.FileDrop);
                e.Effect = accepted ? DragDropEffects.Copy : DragDropEffects.None;
                drop.Highlight = accepted;
                drop.Invalidate();
            };
            target.DragLeave += delegate { drop.Highlight = false; drop.Invalidate(); };
            target.DragDrop += delegate(object sender, DragEventArgs e)
            {
                drop.Highlight = false;
                drop.Invalidate();
                if (!busy && e.Data.GetDataPresent(DataFormats.FileDrop)) AddFiles((string[])e.Data.GetData(DataFormats.FileDrop));
            };
        }

        private void SelectFiles()
        {
            using (OpenFileDialog dialog = new OpenFileDialog { Title = "添加待拉黑的视频", Multiselect = true,
                Filter = "视频文件|*.mp4;*.mkv;*.avi;*.mov;*.flv;*.m4v;*.ts;*.m2ts;*.mts;*.mpg;*.mpeg;*.wmv;*.webm;*.3gp;*.rmvb;*.asf;*.vob|所有文件|*.*" })
                if (dialog.ShowDialog(this) == DialogResult.OK) AddFiles(dialog.FileNames);
        }

        private void AddFiles(string[] paths)
        {
            int skipped = 0;
            foreach (string input in paths)
            {
                try
                {
                    string path = Path.GetFullPath(input);
                    if (!File.Exists(path) || queue.Any(q => string.Equals(q.Path, path, StringComparison.OrdinalIgnoreCase))) { skipped++; continue; }
                    QueueItem item = new QueueItem { Path = path, Description = path };
                    item.Row = new ListViewItem(new[] { Path.GetFileName(path), FormatSize(new FileInfo(path).Length), "待处理" });
                    item.Row.Tag = item;
                    item.Row.ToolTipText = path;
                    queue.Add(item);
                    list.Items.Add(item.Row);
                }
                catch { skipped++; }
            }
            if (skipped > 0) activity.Text = "已跳过 " + skipped + " 个重复、无效或非文件项目。";
            UpdateButtons();
        }

        private void StartBatch()
        {
            string path;
            try
            {
                path = Path.GetFullPath(blacklist.Text.Trim());
                if (!Directory.Exists(Path.GetDirectoryName(path))) throw new IOException("黑名单所在文件夹不存在。 ");
                if (!File.Exists(AppPaths.Probe)) throw new IOException("缺少 app 文件夹中的 ffprobe.exe，请完整解压程序包。 ");
            }
            catch (Exception error) { MessageBox.Show(this, error.Message, "无法开始", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            QueueItem[] batch = queue.Where(q => !q.Recycled).ToArray();
            if (batch.Length == 0) return;
            busy = true;
            cancellation = new CancellationTokenSource();
            UpdateButtons();
            Thread worker = new Thread(delegate()
            {
                int done = 0, failed = 0;
                try
                {
                    using (Journal journal = new Journal(AppPaths.Logs))
                    {
                        Processor processor = new Processor(new BlacklistStore(path, 30000),
                            new Ffprobe(AppPaths.Probe), new WindowsRecycler());
                        foreach (QueueItem item in batch)
                        {
                            if (cancellation.IsCancellationRequested) break;
                            ProcessResult result = processor.Run(item.Path, cancellation.Token, delegate(string stage, int percent)
                            {
                                UI(delegate
                                {
                                    item.Row.SubItems[2].Text = stage;
                                    item.Row.ForeColor = ink;
                                    activity.Text = Path.GetFileName(item.Path) + " · " + stage;
                                    if (percent < 0) progress.Style = ProgressBarStyle.Marquee;
                                    else { progress.Style = ProgressBarStyle.Continuous; progress.Value = Math.Max(0, Math.Min(100, percent)); }
                                });
                            }, journal);
                            if (result.Recycled) done++; else if (!result.Cancelled) failed++;
                            UI(delegate
                            {
                                item.Recycled = result.Recycled;
                                item.Description = item.Path + (string.IsNullOrEmpty(result.Error) ? "" : "\r\n" + result.Error);
                                item.Row.ToolTipText = item.Description;
                                item.Row.SubItems[2].Text = result.Recycled ? (result.Added ? "已登记 · 已回收" : "已在黑名单 · 已回收") :
                                    result.Cancelled ? "已停止" : result.Registered ? "已登记 · 回收未确认，可重试" : "失败 · 未回收";
                                item.Row.ForeColor = result.Recycled ? Color.FromArgb(21, 118, 76) : Color.FromArgb(177, 70, 39);
                                if (item.Row.Selected) detail.Text = item.Description;
                            });
                        }
                    }
                    UI(delegate { activity.Text = "已回收 " + done + " 个，失败 " + failed + " 个" + (cancellation.IsCancellationRequested ? "，已停止后续处理。" : "。") + " 详情可选中项目或查看日志。"; });
                }
                catch (Exception error) { UI(delegate { activity.Text = "操作中止：" + error.Message; }); }
                finally
                {
                    UI(delegate
                    {
                        busy = false;
                        cancellation.Dispose();
                        cancellation = null;
                        progress.Style = ProgressBarStyle.Continuous;
                        progress.Value = 0;
                        UpdateButtons();
                    });
                }
            });
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        }

        private void UI(Action action) { if (!IsDisposed) BeginInvoke(action); }
        private void UpdateButtons()
        {
            if (start == null) return;
            start.Enabled = !busy && queue.Any(q => !q.Recycled);
            stop.Enabled = busy && cancellation != null && !cancellation.IsCancellationRequested;
            add.Enabled = clear.Enabled = browse.Enabled = blacklist.Enabled = !busy;
            remove.Enabled = !busy && list.SelectedItems.Count > 0;
            count.Text = queue.Count + " 个视频";
        }
        private static string FormatSize(long bytes)
        {
            double size = bytes;
            string[] units = { "B", "KiB", "MiB", "GiB", "TiB" };
            int index = 0;
            while (size >= 1024 && index < units.Length - 1) { size /= 1024; index++; }
            return size.ToString("0.##") + " " + units[index];
        }
        private sealed class QueueItem { public string Path, Description; public bool Recycled; public ListViewItem Row; }
    }

    internal sealed class DropPanel : Panel
    {
        public bool Highlight;
        public DropPanel() { DoubleBuffered = true; BackColor = Color.White; }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen pen = new Pen(Highlight ? Color.FromArgb(30, 91, 202) : Color.FromArgb(188, 200, 218), Highlight ? 2 : 1))
            {
                pen.DashStyle = DashStyle.Dash;
                e.Graphics.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
            }
        }
    }
}
