namespace ExecServer;

public sealed class ApiFault(int status, string code, string message, long? accumulatedBytes = null) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public long? AccumulatedBytes { get; } = accumulatedBytes;
}
