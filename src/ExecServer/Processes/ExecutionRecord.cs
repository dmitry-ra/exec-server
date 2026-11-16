using System.Diagnostics;
using System.Text;
using ExecServer.Contracts;
using ExecServer.Storage;

namespace ExecServer.Processes;

public sealed class ExecutionRecord : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _inputGate = new(1, 1);
    private readonly CancellationTokenSource _initialInputCancellation = new();
    private readonly List<ApiError> _errors = [];
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private bool _inputClosed;
    private DateTimeOffset? _endedAt;
    private int? _exitCode;
    private bool _captureComplete;
    private long _stdoutConsumedEnd = -1;
    private long _stderrConsumedEnd = -1;
    private readonly Task _initialInput;

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public Process Process { get; }
    public StartExecutionRequest Request { get; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public OutputBuffer Stdout { get; }
    public OutputBuffer Stderr { get; }
    public Task Completion { get; }
    public Task CaptureCompletion { get; }
    public int Pid { get; }
    public bool HasExited { get { lock (_sync) return _endedAt.HasValue; } }

    public ExecutionRecord(Process process, StartExecutionRequest request, OutputBuffer stdout, OutputBuffer stderr)
    {
        Process = process;
        Pid = process.Id;
        Request = request;
        Stdout = stdout;
        Stderr = stderr;
        Task stdoutPump = PumpAsync(process.StandardOutput.BaseStream, Stdout, "stdout");
        Task stderrPump = PumpAsync(process.StandardError.BaseStream, Stderr, "stderr");
        Completion = ObserveExitAsync();
        CaptureCompletion = ObserveCaptureAsync(stdoutPump, stderrPump);
        // Initiate before publishing this record so later writes cannot overtake initial input.
        _initialInput = WriteInitialInputAsync();
    }

    public ExecutionInfo Snapshot()
    {
        lock (_sync)
            return new ExecutionInfo
            {
                Id = Id, Pid = Pid, Executable = Request.Executable,
                Args = Request.Args ?? [], Cwd = Request.Cwd ?? Environment.CurrentDirectory,
                Environment = Request.Environment ?? new Dictionary<string, string>(),
                Durable = Request.Durable ?? false, State = _endedAt.HasValue ? "exited" : "running",
                StartedAt = StartedAt, EndedAt = _endedAt, ExitCode = _exitCode,
                DurationMilliseconds = _elapsed.Elapsed.TotalMilliseconds,
                StdoutBytes = Stdout.Length, StderrBytes = Stderr.Length,
                CaptureComplete = _captureComplete, Errors = _errors.ToList()
            };
    }

    private async Task PumpAsync(Stream stream, OutputBuffer destination, string name)
    {
        try
        {
            byte[] buffer = new byte[32_768];
            int count;
            while ((count = await stream.ReadAsync(buffer).ConfigureAwait(false)) != 0)
                await destination.AppendAsync(buffer.AsMemory(0, count)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            AddError("capture_" + name, error.Message, destination.Length);
        }
        finally { destination.MarkComplete(); }
    }

    private async Task ObserveExitAsync()
    {
        await Process.WaitForExitAsync().ConfigureAwait(false);
        lock (_sync)
        {
            _exitCode = Process.ExitCode;
            _endedAt = DateTimeOffset.UtcNow;
            _elapsed.Stop();
        }
        await _initialInputCancellation.CancelAsync().ConfigureAwait(false);
    }

    private async Task ObserveCaptureAsync(Task stdoutPump, Task stderrPump)
    {
        await Task.WhenAll(stdoutPump, stderrPump).ConfigureAwait(false);
        lock (_sync) _captureComplete = true;
    }

    public void AddError(string code, string message, long? accumulatedBytes = null)
    {
        lock (_sync) _errors.Add(new ApiError { Code = code, Message = message, AccumulatedBytes = accumulatedBytes });
    }

    public static byte[] InputBytes(string? text, byte[]? binary)
    {
        if (text is not null && binary is not null)
            throw new ArgumentException("Specify either text or binary, not both.");
        return binary ?? new UTF8Encoding(false, true).GetBytes(text ?? "");
    }

    private async Task WriteInitialInputAsync()
    {
        try
        {
            await WriteInputAsync(InputBytes(Request.Stdin?.Text, Request.Stdin?.Binary), Request.CloseStdin ?? false,
                _initialInputCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_initialInputCancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
        {
            AddError("stdin", error.Message);
        }
    }

    public async Task<long> WriteInputAsync(byte[] data, bool close, CancellationToken cancellationToken)
    {
        await _inputGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_inputClosed)
            {
                if (close && data.Length == 0) return 0;
                throw new InvalidOperationException("stdin is already closed.");
            }
            if (data.Length > 0)
            {
                await Process.StandardInput.BaseStream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                await Process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            if (close)
            {
                Process.StandardInput.Close();
                _inputClosed = true;
            }
            return data.LongLength;
        }
        finally { _inputGate.Release(); }
    }

    public bool InputClosed { get { return _inputClosed; } }

    public void ConsumeTail(bool stdout, long deliveredEnd)
    {
        lock (_sync)
        {
            if (stdout && deliveredEnd == Stdout.Length) _stdoutConsumedEnd = deliveredEnd;
            if (!stdout && deliveredEnd == Stderr.Length) _stderrConsumedEnd = deliveredEnd;
        }
    }

    public bool CanAutoDelete
    {
        get { lock (_sync) return !(Request.Durable ?? false) && _endedAt.HasValue && _captureComplete
            && _stdoutConsumedEnd == Stdout.Length && _stderrConsumedEnd == Stderr.Length; }
    }

    public async ValueTask DisposeAsync()
    {
        // Caller permits deletion only once the root and both capture pumps have ended.
        await CaptureCompletion.ConfigureAwait(false);
        await _initialInput.ConfigureAwait(false);
        await Stdout.DisposeAsync().ConfigureAwait(false);
        await Stderr.DisposeAsync().ConfigureAwait(false);
        Process.Dispose();
        _inputGate.Dispose();
        _initialInputCancellation.Dispose();
    }
}
