using System.Text;
using System.Text.Json;

namespace ExecServer.Diagnostics;

/// <summary>Bounded rotated JSON diagnostics; stream bodies and auth headers never enter log state.</summary>
public sealed class JsonFileLoggerProvider : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly ServerSettings _settings;
    private readonly string _path;
    private FileStream? _file;

    public JsonFileLoggerProvider(ServerSettings settings)
    {
        _settings = settings;
        Directory.CreateDirectory(settings.LogDirectory);
        if ((File.GetAttributes(settings.LogDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Log directory must not be a symbolic link.");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(settings.LogDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _path = Path.Combine(settings.LogDirectory, "server.jsonl");
        _file = Open();
    }

    private FileStream Open()
    {
        if (File.Exists(_path) && (File.GetAttributes(_path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Log file must not be a symbolic link.");
        var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(_path, options);
    }

    // Single identity seam for future owner-defined masking, with no plugin framework.
    private static object Filter(object value) => value;

    private void Write<TState>(string category, LogLevel level, EventId eventId, TState state, Exception? exception)
    {
        var fields = state is IEnumerable<KeyValuePair<string, object?>> values
            ? values.ToDictionary(pair => pair.Key, pair => pair.Value) : new Dictionary<string, object?> { ["message"] = state };
        byte[] line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Filter(new
        {
            timestamp = DateTimeOffset.UtcNow, level = level.ToString(), category,
            eventId = eventId.Id, fields, error = exception?.GetType().Name
        })) + "\n");
        lock (_gate)
        {
            if (_file is null) return;
            try
            {
                if (_file.Length > 0 && _file.Length + line.Length > _settings.LogFileBytes)
                {
                    _file.Dispose();
                    for (int index = _settings.LogFileCount - 1; index > 0; index--)
                    {
                        string source = index == 1 ? _path : _path + "." + (index - 1);
                        if (File.Exists(source)) File.Move(source, _path + "." + index, true);
                    }
                    if (_settings.LogFileCount == 1) File.WriteAllBytes(_path, []);
                    _file = Open();
                }
                _file.Write(line);
                _file.Flush();
            }
            catch (IOException)
            {
                Console.Error.WriteLine("{\"event\":\"diagnostic_storage_error\"}");
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new JsonLogger(this, categoryName);
    public void Dispose() { lock (_gate) { _file?.Dispose(); _file = null; } }

    private sealed class JsonLogger(JsonFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => provider.Write(category, logLevel, eventId, state, exception);
    }
}
