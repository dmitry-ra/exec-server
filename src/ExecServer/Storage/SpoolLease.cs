namespace ExecServer.Storage;

/// <summary>One owner of the dedicated ephemeral output directory.</summary>
public sealed class SpoolLease : IDisposable
{
    private readonly FileStream _lease;

    public SpoolLease(string directory)
    {
        Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Spool directory must not be a symbolic link.");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string leasePath = Path.Combine(directory, ".lock");
        if (File.Exists(leasePath) && (File.GetAttributes(leasePath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Spool lease must not be a symbolic link.");
        _lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            foreach (string path in Directory.EnumerateFiles(directory, "*.blob"))
            {
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _))
                    throw new IOException("Unexpected file in dedicated spool directory.");
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Spool output must not be a symbolic link.");
                File.Delete(path);
            }
        }
        catch { _lease.Dispose(); throw; }
    }

    public void Dispose() => _lease.Dispose();
}
