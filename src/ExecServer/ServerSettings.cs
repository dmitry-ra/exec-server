namespace ExecServer;

public sealed class ServerSettings
{
    public long MemoryThresholdBytes { get; set; } = 1_048_576;
    public int MaxReadBytes { get; set; } = 65_536;
    public int InlineBytes { get; set; } = 16_384;
    public int DefaultWaitMilliseconds { get; set; } = 1_000;
    public int MaxWaitMilliseconds { get; set; } = 30_000;
    public string LogDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExecServer", "logs");
    public long LogFileBytes { get; set; } = 10_485_760;
    public int LogFileCount { get; set; } = 5;
    public string SpoolDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExecServer", "spool");

    public void Validate()
    {
        if (MemoryThresholdBytes < 0 || MaxReadBytes <= 0 || InlineBytes < 0 || InlineBytes > MaxReadBytes
            || DefaultWaitMilliseconds < 0 || MaxWaitMilliseconds < DefaultWaitMilliseconds
            || LogFileBytes <= 0 || LogFileCount < 1)
            throw new InvalidOperationException("Invalid ExecServer settings.");
        SpoolDirectory = Path.GetFullPath(SpoolDirectory);
        LogDirectory = Path.GetFullPath(LogDirectory);
    }
}
