using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Woi.Core;

namespace Woi.Capture;

// Elevated writes stay inside ACL-protected directories pinned against replacement.
public sealed class CaptureStorage : IDisposable
{
    private const int FullControl = 0x1f01ff, ReadExecute = 0x1200a9, ReadControl = 0x20000;
    private readonly List<SafeFileHandle> leases = [];
    private readonly SecurityIdentifier user;
    private readonly SecurityIdentifier writer;
    private readonly bool elevated;
    public string Root { get; }
    public string DisplayRoot { get; }
    public static string DefaultRoot => RootFor(AppContext.BaseDirectory);
    public static string RootFor(string applicationPath)
    {
        using var identity = WindowsIdentity.GetCurrent();
        return Path.Combine(Path.GetPathRoot(Path.GetFullPath(applicationPath))!, "WindowsOpenIntelligence.Collector", identity.User!.Value);
    }

    public static CaptureStorage OpenDefault()
    {
        // The common parent has no user read access; each user can read only their own subtree.
        using var parent = new CaptureStorage(Path.GetDirectoryName(DefaultRoot)!, false);
        return new CaptureStorage(DefaultRoot);
    }

    public CaptureStorage(string root) : this(root, true) { }
    private CaptureStorage(string root, bool readable)
    {
        using var identity = WindowsIdentity.GetCurrent();
        user = identity.User!;
        elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        writer = elevated ? new(WellKnownSidType.BuiltinAdministratorsSid, null) : user;
        DisplayRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        try {
            CaptureController.EnsureLocalDirectory(DisplayRoot);
            var drive = new DriveInfo(Path.GetPathRoot(DisplayRoot)!);
            if (drive.DriveType != DriveType.Fixed || !drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
                throw Invalid("Collector storage requires a fixed local NTFS volume.");
            var ancestors = new Stack<string>();
            for (var current = Directory.GetParent(DisplayRoot); current != null; current = current.Parent) ancestors.Push(current.FullName);
            foreach (var path in ancestors) leases.Add(OpenDirectory(path));
            CreateDirectory(DisplayRoot, readable);
            var handle = OpenDirectory(DisplayRoot, true); leases.Add(handle);
            ValidateSecurity(handle, readable, true);
            var canonical = new StringBuilder(32768);
            var length = Native.GetFinalPathNameByHandleW(handle, canonical, (uint)canonical.Capacity, 1);
            if (length == 0 || length >= canonical.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error());
            Root = canonical.ToString().TrimEnd('\\');
            if (!Root.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase))
                throw Invalid("Collector storage could not be bound to a local volume identity.");
        } catch { Dispose(); throw; }
    }

    public string CreateRecording(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw Invalid("Invalid recording directory identity.");
        var folder = Path.Combine(Root, id);
        CreateDirectory(folder, true);
        var lease = OpenRecording(folder); leases.Add(lease);
        return folder;
    }

    public SafeFileHandle OpenRecording(string folder)
    {
        if (!string.Equals(Path.GetDirectoryName(folder), Root, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(folder), "N", out _)) throw Invalid("Recording path escaped collector storage.");
        var handle = OpenDirectory(folder, true);
        try { ValidateSecurity(handle, true, true); return handle; }
        catch { handle.Dispose(); throw; }
    }

    public string DisplayPath(string folder, string leaf) => Path.Combine(DisplayRoot, Path.GetFileName(folder), leaf);

    public FileStream OpenRead(string folder, string leaf)
    {
        using var directory = OpenRecording(folder);
        if (leaf is not ("profile.wprp" or "capture.json" or "recording.etl")) throw Invalid("Invalid collector file.");
        var handle = Native.CreateFileW(Path.Combine(folder, leaf), unchecked((int)0x80000000), 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        try {
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            ValidateFile(handle);
            return new FileStream(handle, FileAccess.Read, 1024 * 1024, false);
        } catch { handle.Dispose(); throw; }
    }

    public void WriteProfile(string folder, string text)
    {
        using var directory = OpenRecording(folder);
        WriteNew(Path.Combine(folder, "profile.wprp"), text);
    }

    public void WriteMetadata(string folder, string text)
    {
        using var directory = OpenRecording(folder);
        var pending = Path.Combine(folder, "capture-" + Guid.NewGuid().ToString("N") + ".pending");
        try { WriteNew(pending, text); File.Move(pending, Path.Combine(folder, "capture.json"), true); }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }

    private void WriteNew(string path, string text)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        ValidateFile(stream.SafeFileHandle);
        WriteText(stream, text);
    }

    private static void WriteText(FileStream stream, string text)
    {
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true);
        writer.Write(text); writer.Flush(); stream.Flush(true);
    }

    private void ValidateFile(SafeFileHandle handle)
    {
        var info = Information(handle);
        if ((info.Attributes & (0x10u | 0x400u)) != 0 || info.Links != 1) throw Invalid("Collector files cannot be directories, redirects or hard links.");
        ValidateSecurity(handle, true, false);
    }

    private void CreateDirectory(string path, bool readable)
    {
        var sddl = $"O:{writer.Value}D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;{writer.Value})(A;OICI;RC;;;OW)";
        if (readable && elevated && user != writer && !user.IsWellKnown(WellKnownSidType.LocalSystemSid))
            sddl += $"(A;OICI;0x{ReadExecute:x};;;{user.Value})";
        if (!readable) sddl += "(A;;0x20;;;AU)";
        var descriptor = new RawSecurityDescriptor(sddl);
        var bytes = new byte[descriptor.BinaryLength]; descriptor.GetBinaryForm(bytes, 0);
        var memory = Marshal.AllocHGlobal(bytes.Length);
        try {
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            var attributes = new Native.SecurityAttributes { Length = Marshal.SizeOf<Native.SecurityAttributes>(), Descriptor = memory };
            if (!Native.CreateDirectoryW(path, ref attributes) && Marshal.GetLastWin32Error() != 183)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create protected collector storage.");
        } finally { Marshal.FreeHGlobal(memory); }
    }

    private void ValidateSecurity(SafeFileHandle handle, bool readable, bool directory)
    {
        var result = Native.GetSecurityInfo(handle, 1, 5, out _, out _, out _, out _, out var memory);
        if (result != 0) throw new Win32Exception((int)result);
        try {
            var length = Native.GetSecurityDescriptorLength(memory);
            if (length == 0 || length > 65536) throw Invalid("Invalid collector security descriptor.");
            var bytes = new byte[length]; Marshal.Copy(memory, bytes, 0, bytes.Length);
            var security = new RawSecurityDescriptor(bytes, 0);
            if (security.Owner == null || security.Owner != writer && (directory || security.Owner != user && !security.Owner.IsWellKnown(WellKnownSidType.LocalSystemSid)))
                throw Invalid("Collector storage has an unexpected owner; choose a clean location or ask an administrator to inspect it.");
            if (directory && !security.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected))
                throw Invalid("Collector directories must have protected permissions.");
            var expected = new Dictionary<string, int>(StringComparer.Ordinal) {
                [writer.Value] = FullControl, [new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value] = FullControl,
                [new SecurityIdentifier("S-1-3-4").Value] = ReadControl
            };
            if (readable && elevated && user != writer && !user.IsWellKnown(WellKnownSidType.LocalSystemSid)) expected[user.Value] = ReadExecute;
            if (!readable) expected[new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null).Value] = 0x20;
            if (security.DiscretionaryAcl == null || security.DiscretionaryAcl.Count != expected.Count)
                throw Invalid("Collector storage permissions do not match the protected policy.");
            foreach (var entry in security.DiscretionaryAcl) {
                if (entry is not CommonAce ace || ace.AceQualifier != AceQualifier.AccessAllowed || ace.IsCallback ||
                    !expected.Remove(ace.SecurityIdentifier.Value, out var mask) || ace.AccessMask != mask ||
                    ace.AceFlags.HasFlag(AceFlags.InheritOnly)) throw Invalid("Collector storage grants unexpected access.");
                if (directory) {
                    var inherited = ace.SecurityIdentifier.IsWellKnown(WellKnownSidType.AuthenticatedUserSid) ? AceFlags.None : AceFlags.ContainerInherit | AceFlags.ObjectInherit;
                    if (ace.AceFlags != inherited) throw Invalid("Collector directory permission inheritance is unsafe.");
                }
            }
        } finally { Native.LocalFree(memory); }
    }

    private static SafeFileHandle OpenDirectory(string path, bool protectedDirectory = false)
    {
        // FILE_LIST_DIRECTORY makes the handle participate in rename/write share checks.
        // Validated private directories permit writer access for atomic child-file replacement.
        var handle = Native.CreateFileW(path, ReadControl | 0x81, protectedDirectory ? 3 : 1, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        try {
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var info = Information(handle);
            if ((info.Attributes & 0x10) == 0 || (info.Attributes & 0x400) != 0) throw Invalid("Collector directory ancestors cannot be redirected.");
            return handle;
        } catch { handle.Dispose(); throw; }
    }
    private static Native.FileInformation Information(SafeFileHandle handle)
    {
        if (!Native.GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return info;
    }
    private static WoiException Invalid(string message) => new("InvalidRecordingStorage", message);
    public void Dispose() { for (var i = leases.Count - 1; i >= 0; i--) leases[i].Dispose(); leases.Clear(); }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
        [StructLayout(LayoutKind.Sequential)] internal struct FileInformation {
            public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateDirectoryW(string path, ref SecurityAttributes attributes);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(string path, int access, int share, IntPtr security, int disposition, int flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
        [DllImport("advapi32.dll")] internal static extern uint GetSecurityInfo(SafeFileHandle handle, int type, uint information,
            out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
        [DllImport("advapi32.dll")] internal static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
        [DllImport("kernel32.dll")] internal static extern IntPtr LocalFree(IntPtr memory);
    }
}
