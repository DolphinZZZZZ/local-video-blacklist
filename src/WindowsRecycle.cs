using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace VideoBlacklist
{
    public sealed class FileIdentity
    {
        private readonly uint volume;
        private readonly ulong index;
        private FileIdentity(uint volume, ulong index) { this.volume = volume; this.index = index; }
        public static FileIdentity From(FileStream stream)
        {
            ByHandleInfo info;
            if (!GetFileInformationByHandle(stream.SafeFileHandle, out info))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return new FileIdentity(info.VolumeSerial, ((ulong)info.IndexHigh << 32) | info.IndexLow);
        }
        public void AssertPath(string path)
        {
            using (FileStream check = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                FileIdentity current = From(check);
                if (volume != current.volume || index != current.index)
                    throw new IOException("视频路径已被其他文件替换，已停止回收。 ");
            }
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleInfo
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Created;
            public System.Runtime.InteropServices.ComTypes.FILETIME Accessed;
            public System.Runtime.InteropServices.ComTypes.FILETIME Written;
            public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleInfo info);
    }

    public static class NativeFiles
    {
        public static void CheckPath(string path)
        {
            string root = System.IO.Path.GetPathRoot(path);
            if (path.StartsWith(@"\\", StringComparison.Ordinal) || new DriveInfo(root).DriveType != DriveType.Fixed)
                throw new IOException("仅支持有回收站的本地固定磁盘；不处理网络盘或移动盘。 ");
            FileAttributes attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.Directory) != 0) throw new IOException("请拖入视频文件，不处理文件夹。 ");
            string current = path;
            while (!string.IsNullOrEmpty(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("不处理符号链接或目录联接内的文件。 ");
                current = System.IO.Path.GetDirectoryName(current);
            }
        }
    }

    public sealed class WindowsRecycler : IRecycler
    {
        public IRecycleOperation Prepare(string path)
        {
            NativeFiles.CheckPath(path);
            string sid = WindowsIdentity.GetCurrent().User.Value;
            string bin = System.IO.Path.Combine(System.IO.Path.GetPathRoot(path), "$Recycle.Bin", sid);
            if (!Directory.Exists(bin)) throw new IOException("该磁盘的用户回收站不可用，原文件保留。 ");
            string[] before = Directory.GetFiles(bin, "$I*");
            ShellRecycle.CheckAvailability();
            return new RecycleOperation(path, bin, before);
        }

        private sealed class RecycleOperation : IRecycleOperation
        {
            private readonly string path, bin;
            private readonly HashSet<string> before;
            public RecycleOperation(string path, string bin, string[] before)
            { this.path = path; this.bin = bin; this.before = new HashSet<string>(before, StringComparer.OrdinalIgnoreCase); }

            public string Execute(FileIdentity identity, long size)
            {
                identity.AssertPath(path);
                ShellRecycle.Recycle(path, identity);
                if (File.Exists(path)) throw new IOException("Shell 操作后原文件仍存在，回收未完成。 ");
                for (int attempt = 0; attempt < 20; attempt++)
                {
                    foreach (string entry in Directory.GetFiles(bin, "$I*"))
                    {
                        if (before.Contains(entry)) continue;
                        byte[] metadata = File.ReadAllBytes(entry);
                        if (metadata.Length < 26) continue;
                        long version = BitConverter.ToInt64(metadata, 0);
                        int offset = version == 2 ? 28 : version == 1 ? 24 : -1;
                        if (offset < 0 || metadata.Length <= offset || (metadata.Length - offset) % 2 != 0) continue;
                        string original = Encoding.Unicode.GetString(metadata, offset, metadata.Length - offset).TrimEnd('\0');
                        if (!string.Equals(original, path, StringComparison.OrdinalIgnoreCase)) continue;
                        string recycled = System.IO.Path.Combine(bin, "$R" + System.IO.Path.GetFileName(entry).Substring(2));
                        if (BitConverter.ToInt64(metadata, 8) != size || new FileInfo(recycled).Length != size)
                            throw new IOException("回收站文件大小验证失败；请查看日志。 ");
                        // The original read handle denies writes throughout. The same
                        // volume/file ID after the move proves this is the hashed file.
                        identity.AssertPath(recycled);
                        return recycled;
                    }
                    Thread.Sleep(50);
                }
                throw new IOException("原路径已移除，但回收站验证未完成；请查看日志和回收站。 ");
            }
        }
    }

    internal static class ShellRecycle
    {
        private const uint Flags = 0x00080000 | 0x20000000 | 0x00100000 |
            0x00004000 | 0x00002000 | 0x00000400 | 0x00000010 | 0x00000004 | 0x00000040;

        public static void CheckAvailability()
        { IFileOperation operation = Create(); Marshal.FinalReleaseComObject(operation); }

        public static void Recycle(string path, FileIdentity identity)
        {
            IFileOperation operation = null;
            IShellItem item = null;
            DeleteGuard guard = new DeleteGuard(path, identity);
            try
            {
                operation = Create();
                Guid itemId = typeof(IShellItem).GUID;
                Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref itemId, out item));
                Marshal.ThrowExceptionForHR(operation.DeleteItem(item, guard));
                Marshal.ThrowExceptionForHR(operation.PerformOperations());
                bool aborted;
                Marshal.ThrowExceptionForHR(operation.GetAnyOperationsAborted(out aborted));
                if (aborted || !guard.Recycled || guard.Error != null)
                    throw new IOException(guard.Error ?? "回收已取消，未确认完成。 ");
            }
            finally
            {
                GC.KeepAlive(guard);
                if (item != null) Marshal.FinalReleaseComObject(item);
                if (operation != null) Marshal.FinalReleaseComObject(operation);
            }
        }

        private static IFileOperation Create()
        {
            Type type = Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), true);
            IFileOperation operation = (IFileOperation)Activator.CreateInstance(type);
            try { Marshal.ThrowExceptionForHR(operation.SetOperationFlags(Flags)); return operation; }
            catch { Marshal.FinalReleaseComObject(operation); throw; }
        }

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        private sealed class DeleteGuard : IFileOperationProgressSink
        {
            private readonly string path;
            private readonly FileIdentity identity;
            public bool Recycled;
            public string Error;
            public DeleteGuard(string path, FileIdentity identity) { this.path = path; this.identity = identity; }
            public int PreDeleteItem(uint flags, IShellItem item)
            {
                try
                {
                    // Veto a permanent-delete path; there is no permanent fallback.
                    if ((flags & 0x80) == 0) throw new IOException("Windows 未选择回收站操作，已阻止删除。 ");
                    identity.AssertPath(path);
                    return 0;
                }
                catch (Exception error) { Error = error.Message; return unchecked((int)0x80004004); }
            }
            public int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem created)
            {
                Recycled = result >= 0 && (flags & 0x80) != 0;
                if (result < 0) Error = "Windows 回收失败：0x" + result.ToString("X8");
                return 0;
            }
            public int StartOperations() { return 0; }
            public int FinishOperations(int result) { return 0; }
            public int PreRenameItem(uint flags, IShellItem item, string name) { return 0; }
            public int PostRenameItem(uint flags, IShellItem item, string name, int result, IShellItem created) { return 0; }
            public int PreMoveItem(uint flags, IShellItem item, IShellItem dest, string name) { return 0; }
            public int PostMoveItem(uint flags, IShellItem item, IShellItem dest, string name, int result, IShellItem created) { return 0; }
            public int PreCopyItem(uint flags, IShellItem item, IShellItem dest, string name) { return 0; }
            public int PostCopyItem(uint flags, IShellItem item, IShellItem dest, string name, int result, IShellItem created) { return 0; }
            public int PreNewItem(uint flags, IShellItem dest, string name) { return 0; }
            public int PostNewItem(uint flags, IShellItem dest, string name, string template, uint attrs, int result, IShellItem created) { return 0; }
            public int UpdateProgress(uint total, uint completed) { return 0; }
            public int ResetTimer() { return 0; }
            public int PauseTimer() { return 0; }
            public int ResumeTimer() { return 0; }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, out IShellItem item);

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            [PreserveSig] int BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IntPtr result);
            [PreserveSig] int GetParent(out IShellItem parent);
            [PreserveSig] int GetDisplayName(uint kind, out IntPtr name);
            [PreserveSig] int GetAttributes(uint mask, out uint attributes);
            [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
        }

        [ComVisible(true), Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOperationProgressSink
        {
            [PreserveSig] int StartOperations();
            [PreserveSig] int FinishOperations(int result);
            [PreserveSig] int PreRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name);
            [PreserveSig] int PostRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem created);
            [PreserveSig] int PreMoveItem(uint flags, IShellItem item, IShellItem dest, [MarshalAs(UnmanagedType.LPWStr)] string name);
            [PreserveSig] int PostMoveItem(uint flags, IShellItem item, IShellItem dest, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem created);
            [PreserveSig] int PreCopyItem(uint flags, IShellItem item, IShellItem dest, [MarshalAs(UnmanagedType.LPWStr)] string name);
            [PreserveSig] int PostCopyItem(uint flags, IShellItem item, IShellItem dest, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem created);
            [PreserveSig] int PreDeleteItem(uint flags, IShellItem item);
            [PreserveSig] int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem created);
            [PreserveSig] int PreNewItem(uint flags, IShellItem dest, [MarshalAs(UnmanagedType.LPWStr)] string name);
            [PreserveSig] int PostNewItem(uint flags, IShellItem dest, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, uint attrs, int result, IShellItem created);
            [PreserveSig] int UpdateProgress(uint total, uint completed);
            [PreserveSig] int ResetTimer();
            [PreserveSig] int PauseTimer();
            [PreserveSig] int ResumeTimer();
        }

        [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOperation
        {
            [PreserveSig] int Advise(IntPtr sink, out uint cookie);
            [PreserveSig] int Unadvise(uint cookie);
            [PreserveSig] int SetOperationFlags(uint flags);
            [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
            [PreserveSig] int SetProgressDialog(IntPtr dialog);
            [PreserveSig] int SetProperties(IntPtr properties);
            [PreserveSig] int SetOwnerWindow(IntPtr owner);
            [PreserveSig] int ApplyPropertiesToItem(IShellItem item);
            [PreserveSig] int ApplyPropertiesToItems(IntPtr items);
            [PreserveSig] int RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
            [PreserveSig] int RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string name);
            [PreserveSig] int MoveItem(IShellItem item, IShellItem dest, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
            [PreserveSig] int MoveItems(IntPtr items, IShellItem dest);
            [PreserveSig] int CopyItem(IShellItem item, IShellItem dest, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
            [PreserveSig] int CopyItems(IntPtr items, IShellItem dest);
            [PreserveSig] int DeleteItem(IShellItem item, [MarshalAs(UnmanagedType.Interface)] IFileOperationProgressSink sink);
            [PreserveSig] int DeleteItems(IntPtr items);
            [PreserveSig] int NewItem(IShellItem dest, uint attrs, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, IntPtr sink);
            [PreserveSig] int PerformOperations();
            [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
        }
    }
}
