using System.Text;
using ExecServer.Contracts;
using ExecServer.Processes;
using ExecServer.Storage;
using Microsoft.AspNetCore.Mvc;

namespace ExecServer.Controllers;

[ApiController]
public sealed class ExecutionsController(ExecutionManager manager, ServerSettings settings,
    IHostApplicationLifetime lifetime, ILogger<ExecutionsController> logger) : ExecServerApiControllerBase
{
    private ExecutionRecord Record(string id) => manager.Find(id) ?? throw new ApiFault(404, "not_found", "Execution not found.");

    public override async Task<ActionResult<ExecutionInfo>> StartExecution(StartExecutionRequest body, CancellationToken cancellationToken = default)
    {
        if (body.WaitMilliseconds < 0) throw new ApiFault(400, "invalid_wait", "Wait must not be negative.");
        var record = manager.Start(body);
        int wait = Math.Min(body.WaitMilliseconds ?? settings.DefaultWaitMilliseconds, settings.MaxWaitMilliseconds);
        if (wait > 0)
        {
            Task complete = Task.WhenAll(record.Completion, record.CaptureCompletion);
            await Task.WhenAny(complete, Task.Delay(wait, cancellationToken));
        }
        return StatusCode(record.HasExited ? 201 : 202, record.Snapshot());
    }

    public override Task<ActionResult<ICollection<ExecutionInfo>>> ListExecutions(CancellationToken cancellationToken = default)
        => Task.FromResult<ActionResult<ICollection<ExecutionInfo>>>(manager.Records.Select(record => record.Snapshot()).ToList());

    public override Task<ActionResult<ExecutionInfo>> GetExecution(string id, CancellationToken cancellationToken = default)
        => Task.FromResult<ActionResult<ExecutionInfo>>(Record(id).Snapshot());

    public override async Task<IActionResult> DeleteExecution(string id, CancellationToken cancellationToken = default)
        => await manager.DeleteAsync(id) ? NoContent() : NotFound(new ApiError { Code = "not_found", Message = "Execution not found." });

    public override async Task<ActionResult<OutputResponse>> ReadOutput(string id, long? stdoutOffset = null,
        long? stderrOffset = null, int? limit = null, string? format = "text", int? waitMilliseconds = null,
        CancellationToken cancellationToken = default)
    {
        var record = Record(id);
        using var read = record.AcquireOutputRead();
        long outOffset = stdoutOffset ?? 0, errOffset = stderrOffset ?? 0;
        ValidateRead(outOffset, limit, record.Stdout.Length);
        ValidateRead(errOffset, limit, record.Stderr.Length);
        if (format is not ("text" or "base64")) throw new ApiFault(400, "invalid_format", "Format must be text or base64.");
        if (waitMilliseconds < 0) throw new ApiFault(400, "invalid_wait", "Wait must not be negative.");
        int wait = Math.Min(waitMilliseconds ?? 0, settings.MaxWaitMilliseconds);
        if (wait > 0 && outOffset == record.Stdout.Length && errOffset == record.Stderr.Length
            && !record.CaptureCompletion.IsCompleted)
        {
            using var wake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var waiters = new List<Task>();
            if (!record.Stdout.IsComplete) waiters.Add(record.Stdout.WaitForChangeAsync(outOffset, TimeSpan.FromMilliseconds(wait), wake.Token));
            if (!record.Stderr.IsComplete) waiters.Add(record.Stderr.WaitForChangeAsync(errOffset, TimeSpan.FromMilliseconds(wait), wake.Token));
            if (waiters.Count > 0) await Task.WhenAny(waiters);
            await wake.CancelAsync();
            cancellationToken.ThrowIfCancellationRequested();
        }
        OutputChunk stdout = await ChunkAsync(record.Stdout, outOffset, limit, format, cancellationToken);
        OutputChunk stderr = await ChunkAsync(record.Stderr, errOffset, limit, format, cancellationToken);
        // Consumption is committed only once the full response has been written successfully server-side.
        Response.OnCompleted(() => ConsumeAsync(record,
            stdout.BlobId is null ? stdout.NextOffset : null,
            stderr.BlobId is null ? stderr.NextOffset : null));
        logger.LogInformation("Output read {ExecutionId} {StdoutOffset} {StderrOffset} {Limit} {Format} {StdoutBytes} {StderrBytes}",
            id, outOffset, errOffset, limit, format, stdout.ReturnedBytes, stderr.ReturnedBytes);
        return new OutputResponse { Execution = record.Snapshot(), Stdout = stdout, Stderr = stderr };
    }

    private void ValidateRead(long offset, int? limit, long length)
    {
        if (offset < 0 || offset > length || limit <= 0)
            throw new ApiFault(400, "invalid_range", "Offset must be within captured data and limit must be positive.", length);
    }

    private async Task<OutputChunk> ChunkAsync(OutputBuffer buffer, long offset, int? limit, string format,
        CancellationToken cancellationToken)
    {
        // A completed buffer cannot grow. Observe completion before length so a
        // final append cannot turn an older length into a false EOF indication.
        bool complete = buffer.IsComplete;
        long total = buffer.Length;
        int count = (int)Math.Min(total - offset, Math.Min(limit ?? settings.MaxReadBytes, settings.MaxReadBytes));
        var chunk = new OutputChunk
        {
            Offset = offset, RequestedBytes = limit, TotalBytes = total,
            ReturnedBytes = count, NextOffset = offset + count, RemainingBytes = total - offset - count,
            Eof = complete && offset + count == total
        };
        if (count > settings.InlineBytes)
        {
            chunk.BlobId = buffer.BlobId.ToString("N");
            chunk.ReturnedBytes = 0;
            chunk.NextOffset = offset;
            chunk.RemainingBytes = total - offset;
            chunk.Eof = false;
            return chunk;
        }
        byte[] bytes = await buffer.ReadAsync(offset, count, cancellationToken);
        if (format == "base64") chunk.Binary = bytes;
        else
        {
            try { chunk.Text = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException)
            {
                throw new ApiFault(400, "invalid_utf8", "Requested bytes are not a complete valid UTF-8 sequence; retry as base64 or read the raw blob.", total);
            }
        }
        return chunk;
    }

    public override async Task<IActionResult> ReadBlob(string blobId, long? offset = null, int? limit = null,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(blobId, "N", out var guid)) throw new ApiFault(404, "not_found", "Blob not found.");
        var record = manager.Records.FirstOrDefault(value => value.Stdout.BlobId == guid || value.Stderr.BlobId == guid)
            ?? throw new ApiFault(404, "not_found", "Blob not found.");
        using var read = record.AcquireOutputRead();
        bool stdout = record.Stdout.BlobId == guid;
        OutputBuffer buffer = stdout ? record.Stdout : record.Stderr;
        long start = offset ?? 0, length = buffer.Length;
        ValidateRead(start, limit, length);
        int count = (int)Math.Min(length - start, Math.Min(limit ?? settings.MaxReadBytes, settings.MaxReadBytes));
        byte[] bytes = await buffer.ReadAsync(start, count, cancellationToken);
        Response.Headers["X-Output-Offset"] = start.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Response.Headers["X-Output-Next-Offset"] = (start + bytes.Length).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Response.Headers["X-Output-Remaining-Bytes"] = (length - start - bytes.Length).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Response.Headers["X-Output-Total-Bytes"] = length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Response.OnCompleted(() => ConsumeAsync(record, stdout ? start + bytes.Length : null, stdout ? null : start + bytes.Length));
        logger.LogInformation("Blob read {ExecutionId} {BlobId} {Offset} {Bytes}", record.Id, blobId, start, bytes.Length);
        return File(bytes, "application/octet-stream");
    }

    private async Task ConsumeAsync(ExecutionRecord record, long? stdoutEnd, long? stderrEnd)
    {
        if (HttpContext.RequestAborted.IsCancellationRequested) return;
        if (stdoutEnd.HasValue) record.ConsumeTail(true, stdoutEnd.Value);
        if (stderrEnd.HasValue) record.ConsumeTail(false, stderrEnd.Value);
        if (record.CanAutoDelete) await manager.DeleteAsync(record.Id);
    }

    public override async Task<ActionResult<StdinResult>> WriteStdin(WriteStdinRequest body, string id,
        CancellationToken cancellationToken = default)
    {
        var record = Record(id);
        byte[] data = ExecutionRecord.InputBytes(body.Text, body.Binary);
        long count = await record.WriteInputAsync(data, body.Close ?? false, cancellationToken);
        logger.LogInformation("stdin write {ExecutionId} {Bytes} {Closed}", id, count, record.InputClosed);
        return new StdinResult { BytesWritten = count, Closed = record.InputClosed };
    }

    public override Task<ActionResult<StdinResult>> CloseStdin(string id, CancellationToken cancellationToken = default)
        => WriteStdin(new WriteStdinRequest { Close = true }, id, cancellationToken);

    public override Task<ActionResult<ICollection<ProcessInfo>>> GetProcesses(string id, CancellationToken cancellationToken = default)
        => Task.FromResult<ActionResult<ICollection<ProcessInfo>>>(ProcessControl.Tree(Record(id).Pid));

    public override Task<IActionResult> SendSignal(SignalRequest body, CancellationToken cancellationToken = default)
    {
        ProcessControl.Signal(body.Pid, body.Signal);
        logger.LogInformation("Signal sent {Pid} {Signal}", body.Pid, body.Signal);
        return Task.FromResult<IActionResult>(NoContent());
    }

    private ServiceStatus Status() => new()
    {
        Mode = manager.IsDraining ? "draining" : "running",
        OperatingSystem = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux",
        Version = "0.1.0", ActiveExecutions = manager.Records.Count(record => !record.HasExited)
    };

    public override Task<ActionResult<ServiceStatus>> GetStatus(CancellationToken cancellationToken = default)
        => Task.FromResult<ActionResult<ServiceStatus>>(Status());

    public override Task<ActionResult<ServiceStatus>> Shutdown(CancellationToken cancellationToken = default)
    {
        manager.BeginDrain();
        Response.OnCompleted(() => { _ = StopWhenDrainedAsync(); return Task.CompletedTask; });
        return Task.FromResult<ActionResult<ServiceStatus>>(Accepted(Status()));
    }

    private async Task StopWhenDrainedAsync() { await manager.Drained; lifetime.StopApplication(); }
}
