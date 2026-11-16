using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ExecServer.Processes;

/// <summary>Replaces a freshly spawned copy with the target, in its own Unix session.</summary>
public static partial class UnixLauncher
{
    private const int CloseOnExecFlag = 1;
    private const int BadFileDescriptorError = 9;
    private const int LaunchSetupFailureExitCode = 126;
    private const int ExecutableFailureExitCode = 127;
    [LibraryImport("libc", EntryPoint = "setsid", SetLastError = true)]
    private static partial int SetSession();
    [LibraryImport("libc", EntryPoint = "execvp", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Exec(string file, IntPtr arguments);
    [LibraryImport("libSystem.Native", EntryPoint = "SystemNative_FcntlSetFD", SetLastError = true)]
    private static partial int SetDescriptorFlags(IntPtr descriptor, int flags);
    [LibraryImport("libSystem.Native", EntryPoint = "SystemNative_FcntlGetFD", SetLastError = true)]
    private static partial int GetDescriptorFlags(IntPtr descriptor);

    public static ProcessStartInfo StartInfo(string executable, IEnumerable<string> arguments)
    {
        if (OperatingSystem.IsWindows())
        {
            var direct = new ProcessStartInfo(executable);
            foreach (string argument in arguments) direct.ArgumentList.Add(argument);
            return direct;
        }
        string self = Environment.ProcessPath ?? throw new InvalidOperationException("Service executable path is unknown.");
        var start = new ProcessStartInfo(self);
        if (Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Path.GetFullPath(Environment.GetCommandLineArgs()[0]));
        start.ArgumentList.Add("--exec-child");
        start.ArgumentList.Add(executable);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    public static int Run(string[] arguments)
    {
        if (OperatingSystem.IsWindows() || arguments.Length == 0) return LaunchSetupFailureExitCode;
        if (SetSession() < 0)
        {
            Console.Error.WriteLine(new Win32Exception(Marshal.GetLastPInvokeError()).Message);
            return LaunchSetupFailureExitCode;
        }
        IntPtr[] strings = arguments.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
        IntPtr vector = Marshal.AllocHGlobal((strings.Length + 1) * IntPtr.Size);
        try
        {
            for (int index = 0; index < strings.Length; index++) Marshal.WriteIntPtr(vector, index * IntPtr.Size, strings[index]);
            Marshal.WriteIntPtr(vector, strings.Length * IntPtr.Size, IntPtr.Zero);
            // The managed launcher can duplicate stdio descriptors. They must not
            // keep a target's closed stdout/stderr artificially alive after exec.
            string descriptorDirectory = OperatingSystem.IsLinux() ? "/proc/self/fd" : "/dev/fd";
            foreach (string path in Directory.GetFileSystemEntries(descriptorDirectory))
            {
                if (!int.TryParse(Path.GetFileName(path), out int descriptor) || descriptor <= 2) continue;
                // .NET's fixed-signature shim also handles Darwin ARM64 varargs ABI.
                if (SetDescriptorFlags(new IntPtr(descriptor), CloseOnExecFlag) < 0)
                {
                    int error = Marshal.GetLastPInvokeError();
                    // The enumeration's descriptor may already be closed after the snapshot.
                    if (error == BadFileDescriptorError) continue;
                    Console.Error.WriteLine(new Win32Exception(error).Message);
                    return LaunchSetupFailureExitCode;
                }
                int actualFlags = GetDescriptorFlags(new IntPtr(descriptor));
                if (actualFlags < 0)
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error == BadFileDescriptorError) continue;
                    Console.Error.WriteLine(new Win32Exception(error).Message);
                    return LaunchSetupFailureExitCode;
                }
                if ((actualFlags & CloseOnExecFlag) == 0)
                {
                    Console.Error.WriteLine("Unable to verify close-on-exec on an inherited descriptor.");
                    return LaunchSetupFailureExitCode;
                }
            }
            if (Exec(arguments[0], vector) < 0)
            {
                Console.Error.WriteLine(new Win32Exception(Marshal.GetLastPInvokeError()).Message);
                return ExecutableFailureExitCode;
            }
            throw new InvalidOperationException("execvp returned unexpectedly without replacing the process.");
        }
        finally
        {
            Marshal.FreeHGlobal(vector);
            foreach (IntPtr pointer in strings) Marshal.FreeCoTaskMem(pointer);
        }
    }
}
