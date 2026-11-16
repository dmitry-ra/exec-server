using ExecServer.Storage;
using ExecServer;
using ExecServer.Diagnostics;

var root = Path.Combine(Path.GetTempPath(), "exec-server-storage-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    await BinarySpillAndOffsets();
    await WaitTransitions();
    await ConcurrentAppendRead();
    await FailedSpillWakesAndPreservesPrefix();
    await DisposeSerializesWithRead();
    LogRotationFailureAndRecovery();
    Console.WriteLine("PASS: storage binary spill, offsets, wakeups, concurrency, failure, disposal, log rotation recovery");
}
finally { Directory.Delete(root, true); }

async Task BinarySpillAndOffsets()
{
    var bytes = Enumerable.Range(0, 1024).Select(i => (byte)i).ToArray();
    var buffer = new OutputBuffer(root, 257);
    var id = buffer.BlobId;
    await buffer.AppendAsync(bytes.AsMemory(0, 256));
    Check(!buffer.IsFileBacked, "threshold should not spill early");
    await buffer.AppendAsync(bytes.AsMemory(256));
    Check(buffer.IsFileBacked && buffer.Length == bytes.Length && buffer.BlobId == id, "spill metadata");
    Check((await buffer.ReadAsync(0, 2048)).SequenceEqual(bytes), "whole binary prefix survived spill");
    Check((await buffer.ReadAsync(253, 10)).SequenceEqual(bytes.Skip(253).Take(10)), "raw byte slice");
    Check((await buffer.ReadAsync(1024, 20)).Length == 0, "tail read");
    await Throws<ArgumentOutOfRangeException>(() => buffer.ReadAsync(1025, 1));
    await buffer.DisposeAsync();
    Check(!Directory.EnumerateFiles(root).Any(), "own spill file deleted");
    await Throws<ObjectDisposedException>(() => buffer.ReadAsync(0, 1));
    await buffer.DisposeAsync();
}

async Task WaitTransitions()
{
    await using var buffer = new OutputBuffer(root, 100);
    var waiting = buffer.WaitForChangeAsync(0, TimeSpan.FromSeconds(5));
    Check(!waiting.IsCompleted, "wait subscribed");
    await buffer.AppendAsync(new byte[] { 42 });
    await waiting.WaitAsync(TimeSpan.FromSeconds(1));
    // Subscribe after the append: no lost wakeup from stale length.
    await buffer.WaitForChangeAsync(0, TimeSpan.FromSeconds(5)).WaitAsync(TimeSpan.FromSeconds(1));
    var completed = buffer.WaitForChangeAsync(1, TimeSpan.FromSeconds(5));
    buffer.MarkComplete();
    await completed.WaitAsync(TimeSpan.FromSeconds(1));
    Check(buffer.IsComplete, "completion visible");
    await Throws<InvalidOperationException>(() => buffer.AppendAsync(new byte[] { 1 }));
    await using var other = new OutputBuffer(root, 1);
    using var canceled = new CancellationTokenSource();
    var canceledWait = other.WaitForChangeAsync(0, TimeSpan.FromSeconds(5), canceled.Token);
    canceled.Cancel();
    await Throws<OperationCanceledException>(() => canceledWait);
    await other.WaitForChangeAsync(0, TimeSpan.Zero);
}

async Task ConcurrentAppendRead()
{
    await using var buffer = new OutputBuffer(root, 64);
    await ExerciseConcurrentBuffer(buffer);
}

static async Task ExerciseConcurrentBuffer(OutputBuffer buffer)
{
    var producer = Task.Run(async () =>
    {
        for (var i = 0; i < 200; i++)
            await buffer.AppendAsync(Enumerable.Repeat((byte)i, 32).ToArray());
        buffer.MarkComplete();
    });
    var readers = Enumerable.Range(0, 4).Select(async _ =>
    {
        long offset = 0;
        var collected = new List<byte>();
        while (offset < 6400)
        {
            var chunk = await buffer.ReadAsync(offset, 47);
            if (chunk.Length == 0) { await buffer.WaitForChangeAsync(offset, TimeSpan.FromSeconds(1)); continue; }
            collected.AddRange(chunk);
            offset += chunk.Length;
        }
        Check(collected.SequenceEqual(Enumerable.Range(0, 200).SelectMany(i => Enumerable.Repeat((byte)i, 32))), "concurrent raw output");
    }).ToArray();
    await Task.WhenAll(readers.Append(producer));
}

async Task FailedSpillWakesAndPreservesPrefix()
{
    var directory = Directory.CreateDirectory(Path.Combine(root, "failure")).FullName;
    await using var buffer = new OutputBuffer(directory, 2);
    await buffer.AppendAsync(new byte[] { 10, 20 });
    var waiting = buffer.WaitForChangeAsync(2, TimeSpan.FromSeconds(5));
    // A colliding directory makes CreateNew fail without modifying another file.
    var blocker = Path.Combine(directory, buffer.BlobId.ToString("N") + ".blob");
    Directory.CreateDirectory(blocker);
    try
    {
        try
        {
            await buffer.AppendAsync(new byte[] { 30 });
            throw new InvalidOperationException("Expected spill collision failure");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        await waiting.WaitAsync(TimeSpan.FromSeconds(1));
        Check(buffer.Error is not null && buffer.Length == 2, "storage error surfaced without fictitious capture");
        Check((await buffer.ReadAsync(0, 10)).SequenceEqual(new byte[] { 10, 20 }), "retained memory prefix on spill failure");
    }
    finally { Directory.Delete(blocker); }
}

async Task DisposeSerializesWithRead()
{
    var buffer = new OutputBuffer(root, 0);
    var data = new byte[1024 * 1024];
    Random.Shared.NextBytes(data);
    await buffer.AppendAsync(data);
    var read = buffer.ReadAsync(0, data.Length);
    var dispose = buffer.DisposeAsync();
    Check((await read).SequenceEqual(data), "active read completed before disposal");
    await dispose;
    Check(!Directory.EnumerateFiles(root).Any(), "disposal cleanup");
}

void LogRotationFailureAndRecovery()
{
    var directory = Path.Combine(root, "log-rotation");
    var path = Path.Combine(directory, "server.jsonl");
    ILogger logger;
    using (var provider = new JsonFileLoggerProvider(new ServerSettings
        { LogDirectory = directory, LogFileBytes = 1, LogFileCount = 2 }))
    {
        logger = provider.CreateLogger("rotation-test");
        logger.LogInformation("before");
        // An owned directory blocks rotation on every OS, without permission changes.
        Directory.CreateDirectory(path + ".1");
        logger.LogInformation("blocked once");
        logger.LogInformation("blocked twice");
        Directory.Delete(path + ".1");
        logger.LogInformation("recovered");
        Check(File.ReadAllText(path).Contains("recovered", StringComparison.Ordinal), "logger recovered after failed rotation");
    }
    var length = new FileInfo(path).Length;
    logger.LogInformation("after disposal");
    Check(new FileInfo(path).Length == length, "disposed logger reopened its file");
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAILED: " + message);
}

static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}
