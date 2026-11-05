using ReolinkDBWin.Core.Models;

namespace ReolinkDBWin.Core.Events;

/// <summary>Base des surveillances d'événements : boucle d'arrière-plan avec reconnexion progressive.</summary>
public abstract class EventMonitor : IAsyncDisposable
{
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public event EventHandler<DetectionEvent>? Detected;
    public event EventHandler<MonitorStatus>? StatusChanged;
    /// <summary>Caméras ayant une détection en cours (Frigate : événements non terminés).</summary>
    public event EventHandler<IReadOnlyCollection<string>>? ActiveCamerasChanged;

    public abstract EventSource Source { get; }
    public MonitorStatus Status { get; private set; }

    protected EventMonitor()
    {
        Status = new MonitorStatus(EventSource.Test, MonitorState.Stopped, "Arrêté");
    }

    public void Start()
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(() => LoopAsync(ct), ct);
    }

    public async Task StopAsync()
    {
        if (_cts is null || _loop is null) return;
        _cts.Cancel();
        try { await _loop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch { /* déjà signalé */ }
        try { await OnStoppedAsync().ConfigureAwait(false); } catch { /* ignoré */ }
        _cts.Dispose();
        _cts = null;
        _loop = null;
        SetStatus(MonitorState.Stopped, "Arrêté");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                SetStatus(MonitorState.Connecting, "Connexion…");
                await RunAsync(ct).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(2);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                SetStatus(MonitorState.Error, ex.Message);
            }

            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
        }
    }

    /// <summary>Boucle de surveillance. Retourner ou lever une exception provoque une reconnexion.</summary>
    protected abstract Task RunAsync(CancellationToken ct);

    protected virtual Task OnStoppedAsync() => Task.CompletedTask;

    protected void SetStatus(MonitorState state, string message)
    {
        var status = new MonitorStatus(Source, state, message);
        if (status == Status) return;
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    protected void Raise(DetectionEvent e) => Detected?.Invoke(this, e);

    protected void RaiseActiveCameras(IReadOnlyCollection<string> cameras) => ActiveCamerasChanged?.Invoke(this, cameras);
}
