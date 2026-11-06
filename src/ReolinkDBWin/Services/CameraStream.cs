using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using ReolinkDBWin.Core.Settings;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace ReolinkDBWin.Services;

public enum StreamState
{
    Stopped,
    Connecting,
    Live,
    Error,
}

/// <summary>
/// Un flux vidéo décodé par VLC directement dans une <see cref="WriteableBitmap"/> WPF (rappels vidéo « vmem »).
/// Contrairement à une fenêtre native, l'image peut être superposée, animée (fondu, glissement) et zoomée,
/// et un flux peut tourner en arrière-plan, déjà prêt, avant d'être affiché.
/// </summary>
public sealed class CameraStream : IDisposable
{
    private const int BufferCount = 3;
    private enum Slot { Free, Writing, Ready, Reading }

    private readonly Dispatcher _dispatcher;
    private readonly StreamSettings _options;
    private readonly uint _maxWidth;
    private readonly object _gate = new();
    private readonly IntPtr[] _buffers = new IntPtr[BufferCount];
    private readonly Slot[] _slots = new Slot[BufferCount];
    private readonly DispatcherTimer _watchdog;
    private readonly DispatcherTimer _reconnect;

    // Les délégués passés à VLC doivent rester référencés.
    private readonly MediaPlayer.LibVLCVideoFormatCb _formatCb;
    private readonly MediaPlayer.LibVLCVideoCleanupCb _cleanupCb;
    private readonly MediaPlayer.LibVLCVideoLockCb _lockCb;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _displayCb;

    private MediaPlayer? _player;
    // Toutes les opérations sur le lecteur natif (Stop/Play/Dispose) passent par cette file, l'une après
    // l'autre : un Dispose ne peut plus libérer le lecteur pendant qu'un Play l'utilise (crash natif).
    private readonly object _opsGate = new();
    private Task _ops = Task.CompletedTask;
    private static int _alive;

    /// <summary>Nombre de flux dont le lecteur natif n'est pas encore libéré (diagnostic).</summary>
    public static int AliveCount => Volatile.Read(ref _alive);
    private int _width, _height, _pitch;
    private int _renderPending;
    private bool _wanted;
    private bool _disposed;
    private bool _mute = true;
    private int _failures;
    private DateTime _lastFrameAt;

    public CameraStream(string id, string name, string url, StreamSettings options, Dispatcher dispatcher, int maxWidth = 1920)
    {
        Id = id;
        Name = name;
        Url = url;
        _options = options;
        _dispatcher = dispatcher;
        _maxWidth = (uint)Math.Max(320, maxWidth);
        Interlocked.Increment(ref _alive);
        _formatCb = OnFormat;
        _cleanupCb = OnCleanup;
        _lockCb = OnLock;
        _displayCb = OnDisplay;

        _watchdog = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(2) };
        _watchdog.Tick += (_, _) => CheckStalled();
        _reconnect = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
        _reconnect.Tick += (_, _) =>
        {
            _reconnect.Stop();
            if (_wanted && !_disposed) Play();
        };
    }

    public string Id { get; }
    public string Name { get; }
    public string Url { get; }
    /// <summary>Qualité demandée à la création (sous-flux / largeur de décodage).</summary>
    public StreamQuality Quality { get; init; } = StreamQuality.Full;
    public StreamState State { get; private set; } = StreamState.Stopped;
    public string Message { get; private set; } = "";
    /// <summary>Image courante (remplacée si la résolution change : voir <see cref="FrameSourceChanged"/>).</summary>
    public WriteableBitmap? Frame { get; private set; }
    /// <summary>Prête à être affichée sans écran de chargement.</summary>
    public bool IsReady => State == StreamState.Live && Frame is not null;

    public event Action? FrameSourceChanged;
    public event Action? StateChanged;

    public bool Mute
    {
        get => _mute;
        set
        {
            _mute = value;
            if (_player is { } p) p.Mute = value;
        }
    }

    public void Start()
    {
        if (_disposed || _wanted) return;
        _wanted = true;
        _failures = 0;
        _watchdog.Start();
        Play();
    }

    /// <summary>Relance immédiate (bouton « reconnecter »).</summary>
    public void Restart()
    {
        if (_disposed) return;
        _wanted = true;
        _failures = 0;
        _reconnect.Stop();
        Play();
    }

    private void Play()
    {
        EnsurePlayer();
        SetState(StreamState.Connecting, _failures > 0 ? "Reconnexion…" : "Connexion…");
        _lastFrameAt = DateTime.UtcNow;
        var media = new Media(VlcService.Instance, new Uri(Url));
        media.AddOption($":network-caching={Math.Clamp(_options.NetworkCachingMs, 0, 5000)}");
        media.AddOption(":clock-jitter=0");
        media.AddOption(":clock-synchro=0");
        if (_options.UseTcp && Url.StartsWith("rtsp", StringComparison.OrdinalIgnoreCase)) media.AddOption(":rtsp-tcp");
        Log.Info($"Flux « {Name} » : démarrage ({StreamUrlBuilder.Redact(Url)})");
        Enqueue("lecture", player =>
        {
            try
            {
                player.Stop();
                if (!_disposed && _wanted) player.Play(media);
            }
            finally
            {
                media.Dispose();
            }
        });
    }

    /// <summary>Exécute une opération sur le lecteur natif, hors du thread de l'interface, en série.</summary>
    private void Enqueue(string what, Action<MediaPlayer> op)
    {
        var player = _player;
        if (player is null) return;
        lock (_opsGate)
        {
            _ops = _ops.ContinueWith(_ =>
            {
                try { op(player); }
                catch (Exception ex) { Log.Error($"Flux « {Name} » : {what}", ex); }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    private void EnsurePlayer()
    {
        if (_player is not null) return;
        var p = new MediaPlayer(VlcService.Instance) { EnableHardwareDecoding = true, Mute = _mute };
        p.SetVideoFormatCallbacks(_formatCb, _cleanupCb);
        p.SetVideoCallbacks(_lockCb, null, _displayCb);
        p.EncounteredError += (_, _) => _dispatcher.BeginInvoke(() => OnLost("Erreur de lecture du flux."));
        p.EndReached += (_, _) => _dispatcher.BeginInvoke(() => OnLost("Le flux s'est interrompu."));
        _player = p;
    }

    private void OnLost(string message)
    {
        if (!_wanted || _disposed) return;
        _failures++;
        var hint = _failures >= 3 ? " Vérifiez l'adresse, les identifiants et que RTSP est activé." : "";
        SetState(StreamState.Error, message + hint);
        Log.Info($"Flux « {Name} » : {message} (échec n° {_failures}, nouvelle tentative)");
        _reconnect.Interval = TimeSpan.FromSeconds(Math.Min(30, 2 * _failures));
        _reconnect.Start();
    }

    private void CheckStalled()
    {
        if (!_wanted || _disposed) return;
        var silent = DateTime.UtcNow - _lastFrameAt;
        if (State == StreamState.Live && silent > TimeSpan.FromSeconds(8))
            OnLost("Image figée.");
        else if (State == StreamState.Connecting && silent > TimeSpan.FromSeconds(20) && !_reconnect.IsEnabled)
            OnLost("Délai de connexion dépassé.");
    }

    private void SetState(StreamState state, string message)
    {
        if (State == state && Message == message) return;
        State = state;
        Message = message;
        StateChanged?.Invoke();
    }

    // ------------------------------------------------------------------------------------------
    // Rappels VLC (thread de décodage)
    // ------------------------------------------------------------------------------------------

    // Important : aucune exception ne doit sortir d'un rappel VLC (elle terminerait le processus).
    private uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        try
        {
            return Format(chroma, ref width, ref height, ref pitches, ref lines);
        }
        catch (Exception ex)
        {
            Log.Error($"Flux « {Name} » : format vidéo", ex);
            return 0; // VLC abandonne l'affichage proprement
        }
    }

    private uint Format(IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        uint w = width, h = height;
        if (w < 16 || h < 16) return 0;
        if (w > _maxWidth)
        {
            h = Math.Max(16u, (uint)((ulong)h * _maxWidth / w) & ~1u);
            w = _maxWidth;
        }
        Marshal.Copy("RV32"u8.ToArray(), 0, chroma, 4);
        width = w;
        height = h;
        pitches = Align(w * 4, 32);
        lines = Align(h, 32);
        lock (_gate)
        {
            WaitWhileReading();
            FreeBuffers();
            var size = (int)(pitches * lines);
            for (var i = 0; i < BufferCount; i++)
            {
                _buffers[i] = Marshal.AllocHGlobal(size);
                _slots[i] = Slot.Free;
            }
            _width = (int)w;
            _height = (int)h;
            _pitch = (int)pitches;
        }
        return 1;
    }

    private void OnCleanup(ref IntPtr opaque)
    {
        try
        {
            lock (_gate)
            {
                WaitWhileReading();
                FreeBuffers();
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Flux « {Name} » : libération vidéo", ex);
        }
    }

    private IntPtr OnLock(IntPtr opaque, IntPtr planes)
    {
        try
        {
            return Lock(planes);
        }
        catch (Exception ex)
        {
            Log.Error($"Flux « {Name} » : verrou d'image", ex);
            return IntPtr.Zero;
        }
    }

    private IntPtr Lock(IntPtr planes)
    {
        int index;
        lock (_gate)
        {
            index = Array.IndexOf(_slots, Slot.Free);
            if (index < 0) index = Array.IndexOf(_slots, Slot.Ready); // on sacrifie l'image en attente
            if (index < 0) index = Array.FindIndex(_slots, s => s != Slot.Reading);
            _slots[index] = Slot.Writing;
            Marshal.WriteIntPtr(planes, _buffers[index]);
        }
        return new IntPtr(index + 1); // identifiant de l'image
    }

    private void OnDisplay(IntPtr opaque, IntPtr picture)
    {
        try
        {
            Display(picture);
        }
        catch (Exception ex)
        {
            Log.Error($"Flux « {Name} » : affichage d'image", ex);
        }
    }

    private void Display(IntPtr picture)
    {
        var index = picture.ToInt32() - 1;
        if (index is < 0 or >= BufferCount) return;
        lock (_gate)
        {
            for (var i = 0; i < BufferCount; i++)
                if (_slots[i] == Slot.Ready) _slots[i] = Slot.Free;
            if (_slots[index] == Slot.Writing) _slots[index] = Slot.Ready;
        }
        if (Interlocked.Exchange(ref _renderPending, 1) == 0)
            _dispatcher.BeginInvoke(Render, DispatcherPriority.Render);
    }

    private void WaitWhileReading()
    {
        while (Array.IndexOf(_slots, Slot.Reading) >= 0) Monitor.Wait(_gate, 100);
    }

    private void FreeBuffers()
    {
        for (var i = 0; i < BufferCount; i++)
        {
            if (_buffers[i] != IntPtr.Zero) Marshal.FreeHGlobal(_buffers[i]);
            _buffers[i] = IntPtr.Zero;
            _slots[i] = Slot.Free;
        }
    }

    private static uint Align(uint v, uint a) => (v + a - 1) / a * a;

    // ------------------------------------------------------------------------------------------
    // Copie vers l'image WPF (thread de l'interface)
    // ------------------------------------------------------------------------------------------

    private void Render()
    {
        Interlocked.Exchange(ref _renderPending, 0);
        if (_disposed) return;
        int index, w, h, pitch;
        lock (_gate)
        {
            if (_disposed) return;
            index = Array.IndexOf(_slots, Slot.Ready);
            if (index < 0 || _buffers[index] == IntPtr.Zero) return;
            _slots[index] = Slot.Reading;
            w = _width;
            h = _height;
            pitch = _pitch;
        }
        var sizeChanged = false;
        try
        {
            if (Frame is null || Frame.PixelWidth != w || Frame.PixelHeight != h)
            {
                Frame = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
                sizeChanged = true;
            }
            Frame.WritePixels(new Int32Rect(0, 0, w, h), _buffers[index], pitch * h, pitch);
        }
        catch (Exception ex)
        {
            Log.Error($"Rendu « {Name} »", ex);
        }
        finally
        {
            lock (_gate)
            {
                if (_slots[index] == Slot.Reading) _slots[index] = Slot.Free;
                Monitor.PulseAll(_gate);
            }
        }
        _lastFrameAt = DateTime.UtcNow;
        if (sizeChanged) FrameSourceChanged?.Invoke();
        if (State != StreamState.Live)
        {
            _failures = 0;
            SetState(StreamState.Live, "En direct");
        }
    }

    /// <summary>Enregistre l'image courante en JPEG.</summary>
    public bool SaveSnapshot(string path)
    {
        if (Frame is null) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
        encoder.Frames.Add(BitmapFrame.Create(Frame));
        using var fs = File.Create(path);
        encoder.Save(fs);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _wanted = false;
        _watchdog.Stop();
        _reconnect.Stop();
        Log.Info($"Flux « {Name} » : fermeture");
        // Mis en file derrière un éventuel Play en cours : jamais de libération concurrente.
        Enqueue("fermeture", player =>
        {
            try
            {
                player.Stop();
                player.Dispose();
            }
            finally
            {
                lock (_gate)
                {
                    WaitWhileReading(); // le thread de l'interface peut être en train de copier l'image
                    FreeBuffers();
                }
                Interlocked.Decrement(ref _alive);
            }
        });
        if (_player is null)
        {
            lock (_gate) FreeBuffers();
            Interlocked.Decrement(ref _alive);
        }
        _player = null;
        SetState(StreamState.Stopped, "Arrêté");
    }

    /// <summary>Attend la fermeture des lecteurs natifs (à la sortie de l'application).</summary>
    public static async Task WaitForAllAsync(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (AliveCount > 0 && DateTime.UtcNow < until) await Task.Delay(50);
    }
}
