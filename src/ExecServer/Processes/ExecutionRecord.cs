using System.Diagnostics;
using System.Text;
using ExecServer.Contracts;
using ExecServer.Storage;

namespace ExecServer.Processes;

public sealed class ExecutionRecord : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _inputGate = new(1, 1);
    private readonly CancellationTokenSource _inputLifetime = new();
    private readonly CancellationTokenSource _captureLifetime = new();
    private StreamWriter? _stdin;
    private StreamReader? _stdoutReader;
    private StreamReader? _stderrReader;
    private readonly Lazy<Task> _closeInput;
    private readonly Lazy<Task> _disposal;
    private readonly TaskCompletionSource _outputReadersDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _outputReaders;
    private bool _outputClosing;
    private readonly List<ApiError> _errors = [];
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private volatile bool _inputClosed;
    private DateTimeOffset? _endedAt;
    private int? _exitCode;
    private bool _captureComplete;
    private long _stdoutConsumedEnd = -1;
    private long _stderrConsumedEnd = -1;
    private Task _initialInput = Task.CompletedTask;

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public Process Process { get; }
    public StartExecutionRequest Request { get; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public OutputBuffer Stdout { get; }
    public OutputBuffer Stderr { get; }
    public Task Completion { get; private set; } = Task.CompletedTask;
    public Task CaptureCompletion { get; private set; } = Task.CompletedTask;
    public int Pid { get; private set; }
    public bool HasExited { get { lock (_sync) return _endedAt.HasValue; } }

    public ExecutionRecord(Process process, StartExecutionRequest request, OutputBuffer stdout, OutputBuffer stderr)
    {
        Process = process;
        Request = request;
        Stdout = stdout;
        Stderr = stderr;
        _closeInput = new Lazy<Task>(CloseInputCoreAsync);
        _disposal = new Lazy<Task>(DisposeCoreAsync);
    }

    internal void AttachStartedProcess()
    {
        Pid = Process.Id;
        try
        {
            _stdin = Process.StandardInput;
            _stdoutReader = Process.StandardOutput;
            _stderrReader = Process.StandardError;
            Task stdoutPump = PumpAsync(_stdoutReader.BaseStream, Stdout, "stdout");
            Task stderrPump = PumpAsync(_stderrReader.BaseStream, Stderr, "stderr");
            CaptureCompletion = ObserveCaptureAsync(stdoutPump, stderrPump);
            // Initiate before publishing so later writes cannot overtake initial input.
            _initialInput = WriteInitialInputAsync();
        }
        finally { Completion = ObserveExitAsync(); }
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
            while ((count = await stream.ReadAsync(buffer, _captureLifetime.Token).ConfigureAwait(false)) != 0)
                await destination.AppendAsync(buffer.AsMemory(0, count)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_captureLifetime.IsCancellationRequested) { }
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
            _inputClosed = true;
            _exitCode = Process.ExitCode;
            _endedAt = DateTimeOffset.UtcNow;
            _elapsed.Stop();
        }
        _ = CloseInputAfterExitAsync();
    }

    private async Task CloseInputAfterExitAsync()
    {
        try { await _closeInput.Value.ConfigureAwait(false); }
        catch (IOException error) { AddError("stdin_close", error.Message); }
    }

    private async Task CloseInputCoreAsync()
    {
        _inputClosed = true;
        await _inputLifetime.CancelAsync().ConfigureAwait(false);
        await _inputGate.WaitAsync().ConfigureAwait(false);
        try { _stdin?.Dispose(); }
        finally
        {
            _inputLifetime.Dispose();
            _inputGate.Release();
        }
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

    public IDisposable AcquireOutputRead()
    {
        lock (_sync)
        {
            if (_outputClosing) throw new ApiFault(404, "not_found", "Execution output is being deleted.");
            _outputReaders++;
            return new OutputReadLease(this);
        }
    }

    private void ReleaseOutputRead()
    {
        lock (_sync)
        {
            _outputReaders--;
            if (_outputClosing && _outputReaders == 0) _outputReadersDrained.TrySetResult();
        }
    }

    private Task StopOutputAdmission()
    {
        lock (_sync)
        {
            _outputClosing = true;
            if (_outputReaders == 0) _outputReadersDrained.TrySetResult();
            return _outputReadersDrained.Task;
        }
    }

    private sealed class OutputReadLease(ExecutionRecord owner) : IDisposable
    {
        private ExecutionRecord? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseOutputRead();
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
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
        {
            AddError("stdin", error.Message);
        }
    }

    public async Task<long> WriteInputAsync(byte[] data, bool close, CancellationToken cancellationToken)
    {
        if (_inputClosed) return ClosedInputResult(data, close);
        await _inputGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_inputClosed) return ClosedInputResult(data, close);
            if (_stdin is null) throw new InvalidOperationException("stdin is unavailable after failed launch setup.");
            using var write = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _inputLifetime.Token);
            if (data.Length > 0)
            {
                await _stdin.BaseStream.WriteAsync(data, write.Token).ConfigureAwait(false);
                await _stdin.BaseStream.FlushAsync(write.Token).ConfigureAwait(false);
            }
            if (close)
            {
                _stdin.Close();
                _inputClosed = true;
            }
            return data.LongLength;
        }
        catch (OperationCanceledException) when (_inputClosed)
        {
            throw new InvalidOperationException("stdin closed because its managed root exited.");
        }
        finally { _inputGate.Release(); }
    }

    private static long ClosedInputResult(byte[] data, bool close)
    {
        if (close && data.Length == 0) return 0;
        throw new InvalidOperationException("stdin is already closed.");
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

    internal async Task AbortAsync()
    {
        await _captureLifetime.CancelAsync().ConfigureAwait(false);
        await Completion.ConfigureAwait(false);
        await DisposeAsync().ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => new(_disposal.Value);

    private async Task DisposeCoreAsync()
    {
        // Caller permits deletion only once the root and both capture pumps have ended.
        await StopOutputAdmission().ConfigureAwait(false);
        using var ownedProcess = Process;
        using var captureLifetime = _captureLifetime;
        using var stdoutReader = _stdoutReader;
        using var stderrReader = _stderrReader;
        await using var stdout = Stdout.ConfigureAwait(false);
        await using var stderr = Stderr.ConfigureAwait(false);
        await _closeInput.Value.ConfigureAwait(false);
        await CaptureCompletion.ConfigureAwait(false);
        await _initialInput.ConfigureAwait(false);
        // Stale record references can still enter WriteInputAsync after deletion.
        // This managed-only gate has no wait handle and remains safe for those callers.
    }
}
