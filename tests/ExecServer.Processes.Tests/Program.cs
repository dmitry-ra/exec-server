using System.Text;
using ExecServer;
using ExecServer.Contracts;
using ExecServer.Processes;
using Microsoft.Extensions.Logging.Abstractions;

const string expectedStdout = "OUT";
const string expectedStderr = "ERR";
if (args.FirstOrDefault() == "--exec-child") return UnixLauncher.Run(args[1..]);
if (args.FirstOrDefault() == "--fixture")
{
    await Console.Out.WriteAsync(expectedStdout);
    await Console.Error.WriteAsync(expectedStderr);
    return 0;
}

const int completionTimeoutSeconds = 10;
var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "exec-process-tests-" + Guid.NewGuid().ToString("N")));
try
{
    string self = Environment.ProcessPath ?? throw new InvalidOperationException("Test executable is unknown.");
    var childArgs = new List<string>();
    if (Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        childArgs.Add(Path.GetFullPath(Environment.GetCommandLineArgs()[0]));
    childArgs.Add("--fixture");
    var manager = new ExecutionManager(new ServerSettings { SpoolDirectory = directory.FullName }, NullLogger<ExecutionManager>.Instance);
    var record = manager.Start(new StartExecutionRequest { Executable = self, Args = childArgs, Durable = true });
    await Task.WhenAll(record.Completion, record.CaptureCompletion).WaitAsync(TimeSpan.FromSeconds(completionTimeoutSeconds));
    Task<bool> deletion;
    using (record.AcquireOutputRead())
    {
        deletion = manager.DeleteAsync(record.Id);
        Check(!deletion.IsCompleted, "deletion overtook an admitted output read");
        Check(manager.Find(record.Id) == record, "metadata was removed before resource cleanup");
        try
        {
            using var refused = record.AcquireOutputRead();
            throw new InvalidOperationException("new output read entered after deletion began");
        }
        catch (ApiFault error) when (error.Status == 404) { }
        Check(Encoding.UTF8.GetString(await record.Stdout.ReadAsync(0, expectedStdout.Length)) == expectedStdout,
            "stdout was disposed during admitted read");
        Check(Encoding.UTF8.GetString(await record.Stderr.ReadAsync(0, expectedStderr.Length)) == expectedStderr,
            "stderr was disposed between stream reads");
        manager.BeginDrain();
        Check(manager.Drained.IsCompletedSuccessfully, "output reader extended managed-root draining");
    }
    Check(await deletion.WaitAsync(TimeSpan.FromSeconds(completionTimeoutSeconds)), "deletion did not finish after read released");
    Check(manager.Find(record.Id) is null, "cleaned record remained published");
    Console.WriteLine("PASS: admitted output read survives deletion; new reads refused; root drain independent");
    return 0;
}
finally { directory.Delete(true); }

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
