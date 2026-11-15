using System.Runtime.InteropServices;
using ExecServer.Processes;

namespace ExecServer;

/// <summary>Intercept normal service signals without imposing a forced shutdown deadline.</summary>
public sealed class DrainLifetime(ExecutionManager manager, IHostApplicationLifetime lifetime) : IHostLifetime, IDisposable
{
    private PosixSignalRegistration? _interrupt;
    private PosixSignalRegistration? _terminate;

    public Task WaitForStartAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows()) Console.CancelKeyPress += OnCancel;
        else
        {
            _interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);
            _terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);
        }
        return Task.CompletedTask;
    }

    private void OnCancel(object? sender, ConsoleCancelEventArgs args) { args.Cancel = true; Drain(); }
    private void OnSignal(PosixSignalContext context) { context.Cancel = true; Drain(); }
    private void Drain()
    {
        manager.BeginDrain();
        _ = CompleteAsync();
    }
    private async Task CompleteAsync() { await manager.Drained; lifetime.StopApplication(); }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public void Dispose()
    {
        Console.CancelKeyPress -= OnCancel;
        _interrupt?.Dispose();
        _terminate?.Dispose();
    }
}
