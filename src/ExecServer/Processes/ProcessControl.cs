using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ExecServer.Contracts;

namespace ExecServer.Processes;

public static partial class ProcessControl
{
    private const uint SnapshotProcesses = 0x00000002;
    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int pid, int signal);

    public static void Signal(int pid, int signal)
    {
        if (OperatingSystem.IsWindows()) throw new ApiFault(501, "unsupported_signal", "Unix signals are not supported on Windows.");
        if (pid <= 0 || signal <= 0) throw new ApiFault(400, "invalid_signal", "PID and signal must be positive; process-group selectors are not accepted.");
        if (Kill(pid, signal) != 0) throw new ApiFault(400, "signal_failed", new Win32Exception(Marshal.GetLastPInvokeError()).Message);
    }

    public static List<ProcessInfo> Tree(int rootPid)
    {
        List<ProcessInfo> all = OperatingSystem.IsWindows() ? WindowsProcesses() : UnixProcesses();
        var selected = new HashSet<int> { rootPid };
        bool changed;
        do
        {
            changed = false;
            foreach (var process in all)
                if (selected.Contains(process.Ppid) && selected.Add(process.Pid)) changed = true;
        } while (changed);
        return all.Where(process => selected.Contains(process.Pid)).ToList();
    }

    private static List<ProcessInfo> UnixProcesses()
    {
        var result = new List<ProcessInfo>();
        if (OperatingSystem.IsLinux())
        {
            foreach (string directory in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(directory), out int pid)) continue;
                try
                {
                    string stat = File.ReadAllText(Path.Combine(directory, "stat"));
                    int left = stat.IndexOf('('), right = stat.LastIndexOf(')');
                    string[] fields = stat[(right + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    result.Add(new ProcessInfo { Pid = pid, Ppid = int.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture), Name = stat[(left + 1)..right] });
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException) { /* best-effort snapshot */ }
            }
            return result;
        }
        // macOS ships ps as part of the operating system; no shell is involved.
        using var ps = new Process();
        ps.StartInfo = new ProcessStartInfo("/bin/ps") { RedirectStandardOutput = true, UseShellExecute = false };
        ps.StartInfo.ArgumentList.Add("-axo");
        ps.StartInfo.ArgumentList.Add("pid=,ppid=,comm=");
        ps.Start();
        string? line;
        while ((line = ps.StandardOutput.ReadLine()) is not null)
        {
            string[] fields = line.Trim().Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 3 && int.TryParse(fields[0], out int pid) && int.TryParse(fields[1], out int ppid))
                result.Add(new ProcessInfo { Pid = pid, Ppid = ppid, Name = Path.GetFileName(fields[2]) });
        }
        ps.WaitForExit();
        return result;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, Pid;
        public UIntPtr DefaultHeap;
        public uint ModuleId, Threads, ParentPid;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
    }

    private static List<ProcessInfo> WindowsProcesses()
    {
        var result = new List<ProcessInfo>();
        IntPtr snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot == new IntPtr(-1)) throw new IOException("Unable to snapshot processes.");
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Name = "" };
            if (Process32First(snapshot, ref entry))
                do { result.Add(new ProcessInfo { Pid = (int)entry.Pid, Ppid = (int)entry.ParentPid, Name = entry.Name }); }
                while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            if (!CloseHandle(snapshot))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to release process snapshot.");
        }
        return result;
    }
}
