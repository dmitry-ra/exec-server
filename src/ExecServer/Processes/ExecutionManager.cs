using System.Collections.Concurrent;
using System.Diagnostics;
using ExecServer.Contracts;
using ExecServer.Storage;

namespace ExecServer.Processes;

public sealed class ExecutionManager(ServerSettings settings, ILogger<ExecutionManager> logger)
{
    private const int FailedLaunchExitTimeoutMilliseconds = 5000;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, ExecutionRecord> _records = new();
    private bool _draining;
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool IsDraining { get { lock (_gate) return _draining; } }
    public Task Drained => _drained.Task;
    public IEnumerable<ExecutionRecord> Records => _records.Values;
    public ExecutionRecord? Find(string id) => _records.GetValueOrDefault(id);

    public ExecutionRecord Start(StartExecutionRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Executable);
        _ = ExecutionRecord.InputBytes(request.Stdin?.Text, request.Stdin?.Binary);
        lock (_gate)
        {
            if (_draining) throw new InvalidOperationException("Service is draining; new launches are refused.");
            var start = UnixLauncher.StartInfo(request.Executable, request.Args ?? []);
            start.UseShellExecute = false;
            start.RedirectStandardInput = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.CreateNoWindow = true;
            // Do not accidentally expose the service's authentication secret to child diagnostics.
            start.Environment.Remove("EXECSERVER_API_KEY");
            if (request.Cwd is not null) start.WorkingDirectory = request.Cwd;
            foreach (var variable in request.Environment ?? new Dictionary<string, string>())
                start.Environment[variable.Key] = variable.Value;
            var process = new Process { StartInfo = start };
            var stdout = new OutputBuffer(settings.SpoolDirectory, settings.MemoryThresholdBytes);
            var stderr = new OutputBuffer(settings.SpoolDirectory, settings.MemoryThresholdBytes);
            bool started = false;
            bool published = false;
            var record = new ExecutionRecord(process, request, stdout, stderr);
            try
            {
                started = process.Start();
                if (!started) throw new IOException("The operating system refused process creation.");
                record.AttachStartedProcess();
                if (!_records.TryAdd(record.Id, record)) throw new InvalidOperationException("Execution ID collision.");
                published = true;
                logger.LogInformation("Process started {ExecutionId} {Pid} {Executable} {Arguments} {Cwd} {Environment}",
                    record.Id, record.Pid, request.Executable, request.Args, request.Cwd, request.Environment);
                _ = ObserveAsync(record);
                return record;
            }
            catch (Exception setupError)
            {
                record.AddError("launch_setup", setupError.Message);
                Exception? cleanupFailure = null;
                try
                {
                    if (started)
                    {
                        if (!process.HasExited) process.Kill();
                        if (!process.WaitForExit(FailedLaunchExitTimeoutMilliseconds))
                            throw new IOException("Started process did not exit during failed-launch cleanup.");
                    }
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
                {
                    if (!process.HasExited) cleanupFailure = error;
                }
                if (cleanupFailure is not null)
                {
                    record.AddError("launch_cleanup", cleanupFailure.Message);
                    if (!published && !_records.TryAdd(record.Id, record))
                        throw new IOException("Failed launch could not retain its execution record.", cleanupFailure);
                    _ = ObserveAsync(record);
                    throw new IOException("Failed launch remains owned as execution " + record.Id + ".", cleanupFailure);
                }
                record.AbortAsync().GetAwaiter().GetResult();
                if (published) _records.TryRemove(record.Id, out _);
                throw;
            }
        }
    }

    private async Task ObserveAsync(ExecutionRecord record)
    {
        await record.Completion.ConfigureAwait(false);
        try
        {
            logger.LogInformation("Process exited {ExecutionId} {Pid} {ExitCode}", record.Id, record.Pid, record.Snapshot().ExitCode);
        }
        finally
        {
            lock (_gate) CheckDrained();
            await record.CaptureCompletion.ConfigureAwait(false);
            if (record.CanAutoDelete) await DeleteAsync(record.Id).ConfigureAwait(false);
        }
    }

    public void BeginDrain()
    {
        lock (_gate) { _draining = true; CheckDrained(); }
    }

    private void CheckDrained()
    {
        if (_draining && _records.Values.All(record => record.HasExited)) _drained.TrySetResult();
    }

    public async Task<bool> DeleteAsync(string id)
    {
        if (!_records.TryGetValue(id, out var record)) return false;
        if (!record.HasExited || !record.CaptureCompletion.IsCompleted)
            throw new InvalidOperationException("Execution or output capture is still active.");
        try { await record.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            record.AddError("cleanup", error.Message);
            throw;
        }
        if (!_records.TryRemove(id, out _)) return false;
        logger.LogInformation("Execution deleted {ExecutionId}", id);
        return true;
    }
}
