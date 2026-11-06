using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ReolinkDBWin.Services;

namespace ReolinkDBWin.Controls;

/// <summary>Affiche un <see cref="CameraStream"/> : image, nom, voyant d'état, zoom molette et déplacement.</summary>
public partial class CameraTile : UserControl
{
    private CameraStream? _stream;
    private Point? _panStart;
    private Point _panOrigin;

    public CameraTile()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ClampPan();
        UpdateState();
    }

    public CameraStream? Stream => _stream;
    public bool EnableZoom { get; set; } = true;
    /// <summary>Afficher le message « Connexion… » tant qu'aucune image n'est disponible.</summary>
    public bool ShowStatus { get; set; } = true;
    public double MaxZoom { get; set; } = 4;
    public double Zoom => ZoomScale.ScaleX;
    public bool IsZoomed => Zoom > 1.001;

    public bool ShowName
    {
        get => NameBadge.Visibility == Visibility.Visible;
        set => NameBadge.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public bool ShowLed
    {
        get => Led.Visibility == Visibility.Visible;
        set => Led.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Le niveau de zoom a changé (la qualité du flux peut devoir augmenter).</summary>
    public event Action<CameraTile>? ZoomChanged;

    /// <summary>Change le flux affiché (null = vide). <paramref name="keepZoom"/> : remplacement de qualité du même flux.</summary>
    public void Attach(CameraStream? stream, bool keepZoom = false)
    {
        if (ReferenceEquals(stream, _stream)) return;
        if (_stream is not null)
        {
            _stream.FrameSourceChanged -= OnFrameSourceChanged;
            _stream.StateChanged -= UpdateState;
        }
        _stream = stream;
        if (stream is not null)
        {
            stream.FrameSourceChanged += OnFrameSourceChanged;
            stream.StateChanged += UpdateState;
        }
        NameText.Text = stream?.Name ?? "";
        VideoImage.Source = stream?.Frame;
        if (!keepZoom) ResetZoom();
        UpdateState();
    }

    private void OnFrameSourceChanged() => VideoImage.Source = _stream?.Frame;

    private void UpdateState()
    {
        var state = _stream?.State ?? StreamState.Stopped;
        var hasImage = _stream?.Frame is not null && VideoImage.Source is not null;
        StatusPanel.Visibility = ShowStatus && _stream is not null && (!hasImage || state == StreamState.Error)
            ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = _stream?.Message ?? "";
        var color = state switch
        {
            StreamState.Live => Color.FromRgb(0x2E, 0xB8, 0x72),
            StreamState.Connecting => Color.FromRgb(0xE8, 0xA3, 0x3D),
            StreamState.Error => Color.FromRgb(0xE5, 0x48, 0x4D),
            _ => Color.FromRgb(0x8A, 0x94, 0xA3),
        };
        Led.Fill = new SolidColorBrush(color);
        LedGlow.Color = color;
        Led.ToolTip = state switch
        {
            StreamState.Live => "En direct",
            StreamState.Connecting => "Connexion…",
            StreamState.Error => "Erreur : " + _stream?.Message,
            _ => "Arrêté",
        };
    }

    /// <summary>Fait briller le contour (détection sur cette caméra).</summary>
    public void Flash()
    {
        var anim = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(3) };
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.05)));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.7)));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        FlashBorder.BeginAnimation(OpacityProperty, anim);
    }

    // ------------------------------------------------------------------------------------------
    // Zoom (molette, centré sur le curseur) et déplacement (glisser)
    // ------------------------------------------------------------------------------------------

    public void ResetZoom()
    {
        var changed = IsZoomed;
        ZoomScale.ScaleX = ZoomScale.ScaleY = 1;
        ZoomPan.X = ZoomPan.Y = 0;
        UpdateZoomBadge();
        if (changed) ZoomChanged?.Invoke(this);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!EnableZoom || _stream is null) return;
        var old = ZoomScale.ScaleX;
        var zoom = Math.Clamp(old * Math.Pow(1.2, e.Delta / 120.0), 1, Math.Max(1, MaxZoom));
        if (Math.Abs(zoom - old) < 0.0001) return;
        var p = e.GetPosition(Root);
        // Le point sous le curseur reste fixe : t' = p − (p − t)·(s'/s)
        ZoomPan.X = p.X - (p.X - ZoomPan.X) * (zoom / old);
        ZoomPan.Y = p.Y - (p.Y - ZoomPan.Y) * (zoom / old);
        ZoomScale.ScaleX = ZoomScale.ScaleY = zoom;
        ClampPan();
        UpdateZoomBadge();
        ZoomChanged?.Invoke(this);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount == 2 && IsZoomed)
        {
            ResetZoom();
            e.Handled = true;
            return;
        }
        if (!IsZoomed) return; // laisser la fenêtre se déplacer
        _panStart = e.GetPosition(Root);
        _panOrigin = new Point(ZoomPan.X, ZoomPan.Y);
        CaptureMouse();
        Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_panStart is not { } start) return;
        var p = e.GetPosition(Root);
        ZoomPan.X = _panOrigin.X + (p.X - start.X);
        ZoomPan.Y = _panOrigin.Y + (p.Y - start.Y);
        ClampPan();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_panStart is null) return;
        _panStart = null;
        ReleaseMouseCapture();
        Cursor = null;
        e.Handled = true;
    }

    private void ClampPan()
    {
        var s = ZoomScale.ScaleX;
        var w = Root.ActualWidth;
        var h = Root.ActualHeight;
        ZoomPan.X = Math.Clamp(ZoomPan.X, w * (1 - s), 0);
        ZoomPan.Y = Math.Clamp(ZoomPan.Y, h * (1 - s), 0);
    }

    private void UpdateZoomBadge()
    {
        ZoomBadge.Visibility = IsZoomed ? Visibility.Visible : Visibility.Collapsed;
        ZoomText.Text = $"{Zoom:0.0}×";
    }
}
