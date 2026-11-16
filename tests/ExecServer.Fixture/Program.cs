using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

// Deterministic child processes for the HTTP harness. No shell or OS utilities.
var mode = args.Length == 0 ? "emit" : args[0];
var count = args.Length > 1 && int.TryParse(args[1], CultureInfo.InvariantCulture, out var parsed) ? parsed : 500;
const int InheritedInputHoldMilliseconds = 6000;
const int RootExitDelayMilliseconds = 2000;
switch (mode)
{
    case "retain-inherited-stdin":
        string self = Environment.ProcessPath ?? throw new InvalidOperationException("Fixture executable is unknown.");
        var start = new ProcessStartInfo(self)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Path.GetFullPath(Environment.GetCommandLineArgs()[0]));
        start.ArgumentList.Add("sleep");
        start.ArgumentList.Add(InheritedInputHoldMilliseconds.ToString(CultureInfo.InvariantCulture));
        using (var child = Process.Start(start) ?? throw new IOException("Fixture descendant did not start."))
        {
            await Console.Out.WriteAsync("ready\n");
            await Console.Out.FlushAsync();
            await Task.Delay(RootExitDelayMilliseconds);
        }
        break;
    case "emit":
        await Console.Out.WriteAsync("OUT\n");
        await Console.Error.WriteAsync("ERR\n");
        break;
    case "sleep":
        await Task.Delay(count);
        break;
    case "echo":
        await Console.OpenStandardInput().CopyToAsync(Console.OpenStandardOutput());
        break;
    case "binary":
        await Console.OpenStandardOutput().WriteAsync(new byte[] { 0, 255, 254, 128, 10 });
        break;
    case "large-output":
        var block = Encoding.UTF8.GetBytes(new string('x', 8192));
        var stdout = Console.OpenStandardOutput();
        for (var remaining = count; remaining > 0; remaining -= block.Length)
            await stdout.WriteAsync(block.AsMemory(0, Math.Min(remaining, block.Length)));
        await Console.Error.WriteAsync("large-error\n");
        break;
    case "delayed-output":
        await Console.Out.WriteAsync("first\n");
        await Console.Out.FlushAsync();
        await Task.Delay(count);
        await Console.Out.WriteAsync("second\n");
        break;
    case "environment-key":
        // Test presence without ever exposing the credential in test output.
        await Console.Out.WriteAsync(Environment.GetEnvironmentVariable("EXECSERVER_API_KEY") is null ? "absent" : "present");
        break;
    case "tail-before-exit":
        await Console.Out.WriteAsync("tail\n");
        await Console.Out.FlushAsync();
        // Remain alive until the harness has read the output and then closes stdin.
        await Console.OpenStandardInput().CopyToAsync(Stream.Null);
        break;
    case "closed-stdout-delayed-stderr":
        // Console.Out duplicates fd1 on Unix. Write natively before initializing
        // that managed writer, so closing the native handle really delivers EOF.
        NativeFixture.WriteStdout(Encoding.UTF8.GetBytes("ready\n"));
        NativeFixture.CloseStdout();
        await Console.Error.WriteAsync("ready\n");
        await Console.Error.FlushAsync();
        await Console.OpenStandardInput().CopyToAsync(Stream.Null);
        await Task.Delay(count);
        await Console.Error.WriteAsync("later\n");
        break;
    case "marker":
        if (args.Length != 2) return 2;
        await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), args[1]), "started");
        break;
    case "probe-marker":
        if (args.Length != 2) return 2;
        var marker = Path.Combine(Path.GetTempPath(), args[1]);
        var exists = File.Exists(marker);
        await Console.Out.WriteAsync(exists ? "present" : "absent");
        if (exists) File.Delete(marker);
        break;
    case "exit":
        return count;
    default:
        await Console.Error.WriteLineAsync($"Unknown fixture mode: {mode}");
        return 2;
}
return 0;

static class NativeFixture
{
    private const int StandardOutputHandle = -11;
    private const int StandardOutputDescriptor = 1;
    [DllImport("libc", EntryPoint = "write", SetLastError = true)]
    private static extern nint Write(int descriptor, byte[] buffer, nuint count);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int descriptor);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int kind);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(IntPtr handle, byte[] bytes, uint count, out uint written, IntPtr overlapped);

    public static void WriteStdout(byte[] bytes)
    {
        bool written = OperatingSystem.IsWindows()
            ? WriteFile(GetStdHandle(StandardOutputHandle), bytes, (uint)bytes.Length, out uint count, IntPtr.Zero) && count == bytes.Length
            : Write(1, bytes, (nuint)bytes.Length) == bytes.Length;
        if (!written) throw new IOException("Fixture could not write its native stdout marker.");
    }

    public static void CloseStdout()
    {
        if (OperatingSystem.IsLinux())
        {
            // The dotnet host may retain duplicates before managed Main runs.
            // Close every alias of this fixture's own stdout pipe, not stderr.
            string? target = new FileInfo("/proc/self/fd/1").LinkTarget;
            var aliases = Directory.GetFileSystemEntries("/proc/self/fd")
                .Where(path => new FileInfo(path).LinkTarget == target)
                .Select(path => int.Parse(Path.GetFileName(path), CultureInfo.InvariantCulture)).ToArray();
            foreach (int descriptor in aliases) Close(descriptor);
            return;
        }
        bool closed = OperatingSystem.IsWindows() ? CloseHandle(GetStdHandle(StandardOutputHandle)) : Close(StandardOutputDescriptor) == 0;
        if (!closed) throw new IOException("Fixture could not close its native stdout handle.");
    }
}
