using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using BorderlessMouse.Protocol;
using SharpGen.Runtime;
using Vortice;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.MediaFoundation;
using static BorderlessMouse.Display.DisplayNative;
using static BorderlessMouse.Input.NativeMethods;
using static BorderlessMouse.Localization.L10n;
using AlphaMode = Vortice.DXGI.AlphaMode;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;
using FactoryType = Vortice.Direct2D1.FactoryType;
using PixelFormat = Vortice.DCommon.PixelFormat;
using BitmapInterpolationMode = Vortice.Direct2D1.BitmapInterpolationMode;

namespace BorderlessMouse.Display;

/// <summary>
/// Obraz z Maca na Windowsie. Dwa tryby:
/// <list type="bullet">
/// <item>pełny pulpit – jedno okno na cały monitor ze strumieniem całego ekranu wirtualnego;</item>
/// <item>tryb okien – każde okno Maca jest oknem Windows w stylu macOS: wygładzone zaokrąglone
/// rogi, pasek menu aplikacji z przyciskami okna (zamknij, minimalizuj, maksymalizuj), pod nim
/// obraz okna Maca z jego własnym paskiem tytułu. Okno jest na pasku zadań i w Alt+Tab.</item>
/// </list>
/// Dekodowanie (Media Foundation) i konwersja NV12 → RGB (procesor wideo D3D11) są wspólne.
/// Okna Maca składa DirectComposition z przezroczystością, a pasek i maskę rogów rysuje
/// Direct2D/DirectWrite – dzięki temu krawędzie są wygładzone. Wszystko działa na jednym wątku.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DisplayViewer : IDisposable
{
    /// <summary>
    /// Okno Maca: <paramref name="MacScreen"/> – jego miejsce przeliczone na ekran Windows
    /// (tam powstaje nowe okno), <paramref name="MacPixels"/> – położenie na ekranie wirtualnym.
    /// </summary>
    public sealed record ProxyWindow(uint Id, int Pid, RECT MacScreen, RECT MacPixels, int DisplayWidth, int DisplayHeight,
        bool Popup, string Title);

    /// <summary>
    /// Okno Windows reprezentujące okno Maca (do przeliczania kursora w hookach):
    /// obszar obrazu na ekranie Windows i odpowiadające mu okno na ekranie wirtualnym.
    /// </summary>
    public readonly record struct ProxyInfo(uint Id, int Pid, bool Popup, RECT Client, RECT MacPixels, int DisplayWidth, int DisplayHeight);

    private const string ClassName = "BorderlessMouseDisplay";
    private const uint FullscreenId = 0;
    /// <summary>Tyle klatek w kolejce oznacza, że dekoder nie nadąża – odrzucamy i prosimy o klatki kluczowe.</summary>
    private const int MaxQueuedFrames = 48;
    /// <summary>Wysokość paska menu przy 100% (jak pasek menu macOS).</summary>
    private const float HeaderHeight96 = 30;
    private const uint WM_MOVE = 0x0003;
    private const uint WM_SIZE = 0x0005;
    private const uint WM_ACTIVATE = 0x0006;
    private const uint WM_ERASEBKGND = 0x0014;
    private const uint WM_SETICON = 0x0080;
    private const uint WM_NCHITTEST = 0x0084;
    private const uint WM_NCLBUTTONDBLCLK = 0x00A3;
    private const uint WM_SYSCOMMAND = 0x0112;
    private const uint WM_TIMER = 0x0113;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_MOUSELEAVE = 0x02A3;
    private const uint WM_CANCELMODE = 0x001F;
    private const uint WM_ENTERSIZEMOVE = 0x0231;
    private const uint WM_EXITSIZEMOVE = 0x0232;
    private const long SC_KEYMENU = 0xF100;
    private const long SIZE_MINIMIZED = 1;
    private const int HTCLIENT = 1;
    private const int HTCAPTION = 2;
    private const int SW_MINIMIZE = 6;
    private const uint WS_SYSMENU = 0x00080000;
    private const uint WS_MINIMIZEBOX = 0x00020000;
    private const uint WS_EX_APPWINDOW = 0x00040000;
    private const uint WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    private const uint SWP_NOZORDER = 0x0004;

    // Kolory jak w ciemnym motywie macOS; tło paska = kolor, którym Mac wypełnia narożniki okien.
    private static readonly Color4 HeaderBackground = new(41 / 255f, 41 / 255f, 43 / 255f, 1);
    private static readonly Color4 HeaderHover = new(1, 1, 1, 0.12f);
    private static readonly Color4 HeaderText = new(0.93f, 0.93f, 0.93f, 1);
    private static readonly Color4 HeaderDisabled = new(0.55f, 0.55f, 0.56f, 1);
    private static readonly Color4[] LightColors =
    {
        new(1f, 95 / 255f, 87 / 255f, 1), new(254 / 255f, 188 / 255f, 46 / 255f, 1), new(40 / 255f, 200 / 255f, 64 / 255f, 1),
    };
    private static readonly Color4[] LightGlyphColors =
    {
        new(0.45f, 0.05f, 0.03f, 1), new(0.55f, 0.33f, 0.02f, 1), new(0.02f, 0.35f, 0.08f, 1),
    };
    private static readonly string[] LightGlyphs = { "×", "−", "+" };
    private const int LightNone = -1;

    /// <summary>Kształty kursora z Maca (CursorTracker.Shape) → kursory systemowe Windows.</summary>
    private static readonly int[] CursorIds = { 32512, 32513, 32649, 32644, 32645, 32515, 32648, 32649, 32646, 32642, 32643, 32514 };

    private readonly ConcurrentQueue<VideoStream.VideoFrame> _frames = new();
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly WndProcDelegate _wndProc;
    private Thread? _thread;
    private volatile bool _running;
    private long _framesPresented;
    private long _lastKeyframeRequestTicks;
    private volatile uint _movingWindowId;
    private IntPtr _movingHandle;
    private volatile IReadOnlyDictionary<IntPtr, ProxyInfo> _proxies = new Dictionary<IntPtr, ProxyInfo>();

    // tylko wątek wyświetlania
    private RECT _monitor;
    private bool _windowMode;
    private bool _wantFullscreenVisible;
    private int _cursorShape;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11VideoDevice? _videoDevice;
    private ID3D11VideoContext? _videoContext;
    private IDXGIFactory2? _factory;
    private ID2D1Factory1? _d2dFactory;
    private ID2D1Device? _d2dDevice;
    private ID2D1DeviceContext? _d2d;
    private IDWriteFactory? _dwrite;
    private IDCompositionDevice? _composition;
    private readonly Dictionary<uint, Surface> _surfaces = new();
    private readonly Dictionary<IntPtr, Surface> _byHandle = new();
    private readonly Dictionary<int, IntPtr> _icons = new();
    private readonly Dictionary<int, IReadOnlyList<MacMenuItem>> _menus = new();

    /// <summary>Błąd, po którym podgląd nie działa (wątek wyświetlania).</summary>
    public event Action<string>? Failed;
    /// <summary>Strumień potrzebuje klatki kluczowej; null = wszystkie.</summary>
    public event Action<uint?>? KeyframeNeeded;
    /// <summary>Okno Maca aktywowane (true) albo dezaktywowane na Windowsie.</summary>
    public event Action<uint, bool>? WindowActivated;
    /// <summary>Przycisk zamknięcia, Alt+F4, zamknięcie z paska zadań.</summary>
    public event Action<uint>? CloseRequested;
    /// <summary>Maksymalizacja lub przywrócenie – nowy rozmiar obrazu okna w pikselach Windows.</summary>
    public event Action<uint, int, int>? ResizeRequested;
    /// <summary>Wybrano pozycję menu aplikacji (pid, numer pozycji).</summary>
    public event Action<int, int>? MenuInvoked;
    /// <summary>Menu jest rozwijane – warto odświeżyć je z Maca (wyszarzenia, zaznaczenia).</summary>
    public event Action<int>? MenuOpening;

    public long FramesPresented => Interlocked.Read(ref _framesPresented);
    public bool UsesGpuDecoding { get; private set; }

    /// <summary>Okna Windows reprezentujące okna Maca – bezpieczne do odczytu z dowolnego wątku.</summary>
    public IReadOnlyDictionary<IntPtr, ProxyInfo> Proxies => _proxies;

    /// <summary>Okno Maca przeciągane teraz za pasek menu (0 = żadne).</summary>
    public uint MovingWindowId => _movingWindowId;

    /// <summary>Przerywa przeciąganie okna (np. gdy przechodzi na ekran Maca). Dowolny wątek.</summary>
    public void CancelMove()
    {
        var handle = _movingHandle;
        if (handle != IntPtr.Zero) PostMessageW(handle, WM_CANCELMODE, IntPtr.Zero, IntPtr.Zero);
    }

    public DisplayViewer()
    {
        _wndProc = WndProc;
    }

    public void Start(RECT monitor, bool windowMode)
    {
        Stop();
        _monitor = monitor;
        _windowMode = windowMode;
        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "blm-display", Priority = ThreadPriority.AboveNormal };
        _thread.SetApartmentState(ApartmentState.STA); // TrackPopupMenu i DirectComposition
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _wake.Set();
        var thread = _thread;
        if (thread is not null && thread != Thread.CurrentThread) thread.Join(TimeSpan.FromSeconds(3));
        _thread = null;
        _frames.Clear();
        _commands.Clear();
        _proxies = new Dictionary<IntPtr, ProxyInfo>();
    }

    /// <summary>Wątek odbiornika.</summary>
    public void Enqueue(VideoStream.VideoFrame frame)
    {
        if (!_running) return;
        if (_frames.Count >= MaxQueuedFrames)
        {
            _frames.Clear();
            RequestKeyframe(null);
        }
        _frames.Enqueue(frame);
        _wake.Set();
    }

    public void SetWindowMode(bool windowMode) => Post(() =>
    {
        if (_windowMode == windowMode) return;
        _windowMode = windowMode;
        foreach (var surface in _surfaces.Values.ToList()) DestroySurface(surface);
        if (!windowMode) CreateFullscreenSurface();
        PublishProxies();
    });

    /// <summary>Pełny pulpit: obraz Maca przykrywa monitor tylko podczas sterowania Makiem.</summary>
    public void SetVisible(bool visible) => Post(() =>
    {
        _wantFullscreenVisible = visible;
        if (_surfaces.TryGetValue(FullscreenId, out var surface)) ApplyVisibility(surface);
    });

    /// <summary>Tryb okien: aktualna lista okien Maca (od najwyższego).</summary>
    public void UpdateWindows(IReadOnlyList<ProxyWindow> windows) => Post(() =>
    {
        if (!_windowMode || _device is null) return;
        var wanted = windows.ToDictionary(w => w.Id);
        foreach (var surface in _surfaces.Values.Where(s => !wanted.ContainsKey(s.Id)).ToList()) DestroySurface(surface);
        // Najpierw zwykłe okna – menu i podpowiedzi są ustawiane względem nich.
        foreach (var window in windows.OrderBy(w => w.Popup))
        {
            if (_surfaces.TryGetValue(window.Id, out var surface)) UpdateSurface(surface, window);
            else CreateWindowSurface(window);
        }
        PublishProxies();
    });

    /// <summary>Ikona aplikacji Maca (PNG) dla okien tego procesu.</summary>
    public void SetIcon(int pid, byte[] png) => Post(() =>
    {
        var icon = CreateIconFromResourceEx(png, (uint)png.Length, true, 0x00030000, 0, 0, 0);
        if (icon == IntPtr.Zero) return;
        if (_icons.Remove(pid, out var old)) DestroyIcon(old);
        _icons[pid] = icon;
        foreach (var surface in _surfaces.Values.Where(s => s.Pid == pid)) ApplyIcon(surface);
    });

    /// <summary>Menu aplikacji Maca – pokazywane w pasku nad jej oknami.</summary>
    public void SetMenu(int pid, IReadOnlyList<MacMenuItem> items) => Post(() =>
    {
        _menus[pid] = items;
        foreach (var surface in _surfaces.Values.Where(s => s.Pid == pid && s.Kind == SurfaceKind.Window)) Compose(surface);
    });

    /// <summary>Kształt kursora Maca nad jego oknem (strzałka, kursor tekstowy, rączka…).</summary>
    public void SetCursorShape(int shape) => Post(() =>
    {
        _cursorShape = shape is >= 0 and < 12 ? shape : 0;
        // Kursor stojący nad oknem Maca zmienia się od razu, bez czekania na ruch myszy.
        GetCursorPos(out var point);
        var root = GetAncestor(WindowFromPoint(point), GA_ROOT);
        if (_byHandle.TryGetValue(root, out var surface) && surface.Kind != SurfaceKind.Fullscreen && Contains(surface.Client, point.X, point.Y))
            SetCursor(LoadCursorW(IntPtr.Zero, CursorIds[_cursorShape]));
    });

    private void Post(Action action)
    {
        _commands.Enqueue(action);
        _wake.Set();
    }

    // ---------------- wątek wyświetlania ----------------

    private void Run()
    {
        try
        {
            RegisterWindowClass();
            CreateDevice();
            if (!_windowMode) CreateFullscreenSurface();
            PublishProxies();
            var handles = new[] { _wake.SafeWaitHandle.DangerousGetHandle() };
            while (_running)
            {
                MsgWaitForMultipleObjectsEx(1, handles, 100, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
                while (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
                {
                    TranslateMessage(ref msg);
                    DispatchMessageW(ref msg);
                }
                Pump();
            }
        }
        catch (Exception ex)
        {
            if (_running) Failed?.Invoke(T("Podgląd ekranu Maca: ", "Mac display viewer: ") + ex.Message);
        }
        finally
        {
            Cleanup();
        }
    }

    /// <summary>Polecenia i dekodowanie – także z pętli modalnej (przeciąganie, menu), inaczej obraz by stał.</summary>
    private void Pump()
    {
        while (_commands.TryDequeue(out var command)) command();
        DecodePending();
    }

    private void DecodePending()
    {
        // Przy zaległościach każda powierzchnia pokazuje tylko swoją najnowszą klatkę.
        var batch = new List<VideoStream.VideoFrame>();
        while (_frames.TryDequeue(out var queued)) batch.Add(queued);
        var lastIndex = new Dictionary<uint, int>();
        for (var i = 0; i < batch.Count; i++) lastIndex[batch[i].StreamId] = i;

        for (var i = 0; i < batch.Count; i++)
        {
            var frame = batch[i];
            if (!_surfaces.TryGetValue(frame.StreamId, out var surface)) continue; // okno jeszcze bez listy
            if (surface.AwaitingKeyframe && !frame.IsKeyframe) continue;
            var present = lastIndex[frame.StreamId] == i;
            try
            {
                EnsureDecoder(surface, frame);
                if (surface.Decoder is null) continue;
                surface.AwaitingKeyframe = false;
                surface.CornerRadius = frame.CornerRadius;
                var decoder = surface.Decoder;
                decoder.Decode(frame.Payload, sample =>
                {
                    if (present) Present(surface, decoder, sample, frame.Width, frame.Height);
                });
            }
            catch (SharpGenException ex)
            {
                // Uszkodzony strumień albo reset sterownika: nowy dekoder od następnej klatki kluczowej.
                DisposeDecoder(surface);
                surface.AwaitingKeyframe = true;
                RequestKeyframe(surface.Id);
                if (ex.ResultCode == Vortice.DXGI.ResultCode.DeviceRemoved || ex.ResultCode == Vortice.DXGI.ResultCode.DeviceReset)
                {
                    RecreateDevice();
                    return;
                }
            }
        }
    }

    private void EnsureDecoder(Surface surface, VideoStream.VideoFrame frame)
    {
        // Rozmiar strumienia zmienia się tylko razem z klatką kluczową.
        if (surface.Decoder is not null && surface.DecoderWidth == frame.Width && surface.DecoderHeight == frame.Height) return;
        if (!frame.IsKeyframe)
        {
            surface.AwaitingKeyframe = true;
            RequestKeyframe(surface.Id);
            return;
        }
        DisposeDecoder(surface);
        // Mac koduje wymiary parzyste, najmniej 16 px.
        surface.Decoder = new H264Decoder(_device, Math.Max(16, (frame.Width + 1) & ~1), Math.Max(16, (frame.Height + 1) & ~1));
        surface.DecoderWidth = frame.Width;
        surface.DecoderHeight = frame.Height;
        UsesGpuDecoding = surface.Decoder.UsesGpu;
    }

    /// <summary>Zdekodowana klatka → obraz okna (NV12 → RGB) → złożenie z paskiem i maską rogów.</summary>
    private void Present(Surface surface, H264Decoder decoder, IMFSample sample, int frameWidth, int frameHeight)
    {
        if (_videoDevice is null || _videoContext is null || surface.SwapChain is null || surface.Width <= 0 || surface.Height <= 0) return;
        ID3D11Texture2D texture;
        uint slice;
        if (decoder.UsesGpu)
        {
            using var buffer = sample.GetBufferByIndex(0);
            using var dxgiBuffer = buffer.QueryInterface<IMFDXGIBuffer>();
            texture = new ID3D11Texture2D(dxgiBuffer.GetResource(typeof(ID3D11Texture2D).GUID));
            slice = dxgiBuffer.SubresourceIndex;
        }
        else
        {
            texture = Upload(surface, decoder, sample);
            slice = 0;
        }
        using (texture)
        {
            var description = texture.Description;
            EnsureProcessor(surface, (int)description.Width, (int)description.Height);
            var key = (texture.NativePointer, slice);
            if (!surface.InputViews.TryGetValue(key, out var inputView))
            {
                inputView = _videoDevice.CreateVideoProcessorInputView(texture, surface.Enumerator!, new VideoProcessorInputViewDescription
                {
                    FourCC = 0,
                    ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                    Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = slice },
                });
                surface.InputViews[key] = inputView;
            }
            var width = surface.Width;
            var height = surface.Height;
            var visibleWidth = Math.Min(frameWidth, (int)description.Width);
            var visibleHeight = Math.Min(frameHeight, (int)description.Height);
            var destination = surface.Kind == SurfaceKind.Fullscreen
                ? Letterbox(visibleWidth, visibleHeight, width, height)
                : new RawRect(0, 0, width, height);
            _videoContext.VideoProcessorSetStreamSourceRect(surface.Processor!, 0, true, new RawRect(0, 0, visibleWidth, visibleHeight));
            _videoContext.VideoProcessorSetStreamDestRect(surface.Processor!, 0, true, destination);
            _videoContext.VideoProcessorSetOutputTargetRect(surface.Processor!, true, new RawRect(0, 0, width, height));
            var streams = new[] { new VideoProcessorStream { Enable = true, InputSurface = inputView } };
            _videoContext.VideoProcessorBlt(surface.Processor!, surface.OutputView!, 0, 1, streams).CheckError();
        }
        surface.HasVideo = true;
        if (surface.Kind == SurfaceKind.Fullscreen) surface.SwapChain.Present(0, PresentFlags.None).CheckError();
        else Compose(surface);
        Interlocked.Increment(ref _framesPresented);
        if (!surface.HasPicture)
        {
            surface.HasPicture = true;
            ApplyVisibility(surface);
        }
    }

    /// <summary>
    /// Okno Maca: pasek menu, obraz okna i maska zaokrąglonych rogów (wygładzona) w jednym
    /// przebiegu Direct2D. Wywoływane po nowej klatce i po zmianie paska (najechanie, menu).
    /// </summary>
    private void Compose(Surface surface)
    {
        if (surface.Kind == SurfaceKind.Fullscreen || _d2d is null || surface.SwapChain is null || !surface.HasVideo) return;
        surface.Target ??= CreateTargetBitmap(surface.SwapChain);
        var total = new Rect(0, 0, surface.Width, surface.Height + surface.HeaderHeight);
        var radius = Math.Max(0, (float)surface.CornerRadius);
        _d2d.Target = surface.Target;
        _d2d.BeginDraw();
        _d2d.Clear(new Color4(0, 0, 0, 0));
        using var mask = _d2dFactory!.CreateRoundedRectangleGeometry(new RoundedRectangle(
            new System.Drawing.RectangleF(0, 0, total.Width, total.Height), radius, radius));
        _d2d.PushLayer(new LayerParameters1
        {
            ContentBounds = new RawRectF(0, 0, total.Width, total.Height),
            GeometricMask = mask,
            MaskAntialiasMode = AntialiasMode.PerPrimitive,
            MaskTransform = Matrix3x2.Identity,
            Opacity = 1,
        }, null!);
        if (surface.HeaderHeight > 0) DrawHeader(surface);
        if (surface.VideoBitmap is not null)
        {
            var destination = new Rect(0, surface.HeaderHeight, surface.Width, surface.Height);
            _d2d.DrawBitmap(surface.VideoBitmap, destination, 1, BitmapInterpolationMode.Linear, new Rect(0, 0, surface.Width, surface.Height));
        }
        _d2d.PopLayer();
        var result = _d2d.EndDraw();
        _d2d.Target = null;
        if (result.Failure) return;
        surface.SwapChain.Present(0, PresentFlags.None);
    }

    private void DrawHeader(Surface surface)
    {
        var d2d = _d2d!;
        var scale = surface.HeaderHeight / HeaderHeight96;
        using var background = d2d.CreateSolidColorBrush(HeaderBackground);
        d2d.FillRectangle(new Rect(0, 0, surface.Width, surface.HeaderHeight), background);

        // Przyciski okna jak w macOS: 12 pt średnicy, 8 pt odstępu, 20 pt od lewej krawędzi środka pierwszego.
        surface.LightBounds.Clear();
        var diameter = 13 * scale;
        var gap = 8 * scale;
        var x = 12 * scale;
        var centerY = surface.HeaderHeight / 2f;
        using var glyphFormat = _dwrite!.CreateTextFormat("Segoe UI", FontWeight.Bold, FontStyle.Normal, 10.5f * scale);
        glyphFormat.TextAlignment = TextAlignment.Center;
        glyphFormat.ParagraphAlignment = ParagraphAlignment.Center;
        for (var i = 0; i < 3; i++)
        {
            using var brush = d2d.CreateSolidColorBrush(LightColors[i]);
            d2d.FillEllipse(new Ellipse(new Vector2(x + diameter / 2, centerY), diameter / 2, diameter / 2), brush);
            if (surface.HoverLight != LightNone)
            {
                using var glyphBrush = d2d.CreateSolidColorBrush(LightGlyphColors[i]);
                d2d.DrawText(LightGlyphs[i], glyphFormat, new Rect(x, centerY - diameter / 2 - scale, diameter, diameter), glyphBrush);
            }
            surface.LightBounds.Add(((int)(x - gap / 2), (int)(x + diameter + gap / 2)));
            x += diameter + gap;
        }

        // Nazwa aplikacji (pogrubiona) i jej menu, jak pasek menu macOS.
        surface.ItemBounds.Clear();
        x += 10 * scale;
        var padding = 7 * scale;
        var items = HeaderItems(surface);
        var labels = items.Count > 0 ? items.Select(i => (i.Title, i.Enabled)).ToList() : new List<(string, bool)> { (surface.Title, true) };
        using var regular = _dwrite.CreateTextFormat("Segoe UI", FontWeight.Normal, FontStyle.Normal, 13 * scale);
        using var bold = _dwrite.CreateTextFormat("Segoe UI", FontWeight.Bold, FontStyle.Normal, 13 * scale);
        using var text = d2d.CreateSolidColorBrush(HeaderText);
        using var disabled = d2d.CreateSolidColorBrush(HeaderDisabled);
        using var hover = d2d.CreateSolidColorBrush(HeaderHover);
        for (var i = 0; i < labels.Count; i++)
        {
            var (label, enabled) = labels[i];
            var format = i == 0 ? bold : regular;
            format.ParagraphAlignment = ParagraphAlignment.Center;
            using var layout = _dwrite.CreateTextLayout(label, format, 1000, surface.HeaderHeight);
            var width = layout.Metrics.Width;
            if (x + width > surface.Width - padding) break; // wąskie okno – reszta menu się nie mieści
            if (i == surface.HoverItem && items.Count > 0)
            {
                var inset = 4 * scale;
                d2d.FillRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(x - padding, inset, width + 2 * padding,
                    surface.HeaderHeight - 2 * inset), 5 * scale, 5 * scale), hover);
            }
            d2d.DrawTextLayout(new Vector2(x, 0), layout, enabled ? text : disabled);
            surface.ItemBounds.Add(((int)(x - padding), (int)(x + width + padding)));
            x += width + 2 * padding;
        }
    }

    private ID2D1Bitmap1 CreateTargetBitmap(IDXGISwapChain1 swapChain)
    {
        using var surface = swapChain.GetBuffer<IDXGISurface>(0);
        return _d2d!.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
            new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96,
            BitmapOptions.Target | BitmapOptions.CannotDraw));
    }

    /// <summary>Tryb programowy: NV12 z pamięci → tekstura GPU (przez teksturę staging).</summary>
    private ID3D11Texture2D Upload(Surface surface, H264Decoder decoder, IMFSample sample)
    {
        var width = (uint)decoder.OutputWidth;
        var height = (uint)decoder.OutputHeight;
        if (surface.UploadTexture is null || surface.UploadTexture.Description.Width != width || surface.UploadTexture.Description.Height != height)
        {
            surface.UploadStaging?.Dispose();
            surface.UploadTexture?.Dispose();
            foreach (var view in surface.InputViews.Values) view.Dispose();
            surface.InputViews.Clear();
            var staging = new Texture2DDescription(Format.NV12, width, height, 1, 1, BindFlags.None,
                ResourceUsage.Staging, CpuAccessFlags.Write, 1, 0, ResourceOptionFlags.None);
            surface.UploadStaging = _device!.CreateTexture2D(in staging);
            var target = new Texture2DDescription(Format.NV12, width, height, 1, 1, BindFlags.ShaderResource,
                ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None);
            surface.UploadTexture = _device.CreateTexture2D(in target);
        }
        var nv12 = decoder.CopyNv12(sample);
        var mapped = _context!.Map(surface.UploadStaging!, 0, MapMode.Write, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var stride = decoder.OutputStride;
            var rows = (int)height * 3 / 2;
            for (var row = 0; row < rows && (row + 1) * stride <= nv12.Length; row++)
            {
                Marshal.Copy(nv12, row * stride, mapped.DataPointer + row * (int)mapped.RowPitch, (int)width);
            }
        }
        finally
        {
            _context.Unmap(surface.UploadStaging!, 0);
        }
        _context.CopyResource(surface.UploadTexture!, surface.UploadStaging!);
        // Osobny wrapper z własną referencją: wywołujący zwalnia go jak teksturę dekodera.
        surface.UploadTexture!.AddRef();
        return new ID3D11Texture2D(surface.UploadTexture.NativePointer);
    }

    private static RawRect Letterbox(int sourceWidth, int sourceHeight, int width, int height)
    {
        var scale = Math.Min(width / (double)sourceWidth, height / (double)sourceHeight);
        var w = (int)Math.Round(sourceWidth * scale);
        var h = (int)Math.Round(sourceHeight * scale);
        var x = (width - w) / 2;
        var y = (height - h) / 2;
        return new RawRect(x, y, x + w, y + h);
    }

    /// <summary>
    /// Procesor wideo pisze do łańcucha wymiany (pełny pulpit) albo do tekstury obrazu okna,
    /// z której Direct2D składa okno z paskiem i zaokrąglonymi rogami.
    /// </summary>
    private void EnsureProcessor(Surface surface, int inputWidth, int inputHeight)
    {
        if (surface.Processor is not null && surface.ProcessorInputWidth == inputWidth && surface.ProcessorInputHeight == inputHeight
            && surface.ProcessorOutputWidth == surface.Width && surface.ProcessorOutputHeight == surface.Height) return;
        DisposeProcessor(surface);
        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)inputWidth,
            InputHeight = (uint)inputHeight,
            OutputWidth = (uint)surface.Width,
            OutputHeight = (uint)surface.Height,
            InputFrameRate = new Rational(60, 1),
            OutputFrameRate = new Rational(60, 1),
            Usage = VideoUsage.PlaybackNormal,
        };
        surface.Enumerator = _videoDevice!.CreateVideoProcessorEnumerator(content);
        surface.Processor = _videoDevice.CreateVideoProcessor(surface.Enumerator, 0);
        // Koder Maca: BT.709, zakres wideo 16–235; wyjście: pełny zakres RGB.
        var input = new VideoProcessorColorSpace { YCbCr_Matrix = 1, Nominal_Range = (uint)VideoProcessorNominalRange.Range_16_235 };
        _videoContext!.VideoProcessorSetStreamColorSpace(surface.Processor, 0, input);
        _videoContext.VideoProcessorSetOutputColorSpace(surface.Processor, new VideoProcessorColorSpace { RGB_Range = 0 });
        _videoContext.VideoProcessorSetStreamFrameFormat(surface.Processor, 0, VideoFrameFormat.Progressive);
        _videoContext.VideoProcessorSetStreamAutoProcessingMode(surface.Processor, 0, false);
        ID3D11Texture2D output;
        if (surface.Kind == SurfaceKind.Fullscreen)
        {
            output = surface.SwapChain!.GetBuffer<ID3D11Texture2D>(0);
        }
        else
        {
            var description = new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)surface.Width, (uint)surface.Height, 1, 1,
                BindFlags.RenderTarget | BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None);
            surface.VideoTexture = _device!.CreateTexture2D(in description);
            using var dxgiSurface = surface.VideoTexture.QueryInterface<IDXGISurface>();
            surface.VideoBitmap = _d2d!.CreateBitmapFromDxgiSurface(dxgiSurface, new BitmapProperties1(
                new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore), 96, 96, BitmapOptions.None));
            output = surface.VideoTexture;
            output.AddRef();
        }
        using (output)
        {
            surface.OutputView = _videoDevice.CreateVideoProcessorOutputView(output, surface.Enumerator, new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
            });
        }
        surface.ProcessorInputWidth = inputWidth;
        surface.ProcessorInputHeight = inputHeight;
        surface.ProcessorOutputWidth = surface.Width;
        surface.ProcessorOutputHeight = surface.Height;
    }

    // ---------------- powierzchnie ----------------

    private enum SurfaceKind { Fullscreen, Window, Popup }

    private sealed class Surface
    {
        public uint Id;
        public SurfaceKind Kind;
        public IntPtr Handle;
        public int HeaderHeight;
        public int Pid;
        public string Title = "";
        /// <summary>Okno Maca przeliczone na ekran Windows (z ostatniej listy).</summary>
        public RECT MacScreen;
        public RECT MacPixels;
        public int DisplayWidth, DisplayHeight;
        /// <summary>Obszar obrazu okna Maca na ekranie Windows (pod paskiem menu).</summary>
        public RECT Client;
        /// <summary>Rozmiar obrazu okna (bez paska) w pikselach.</summary>
        public int Width, Height;
        public bool InSizeMove;
        public bool Maximized;
        public RECT RestoreClient;
        public int HoverItem = -1;
        public int HoverLight = LightNone;
        public readonly List<(int left, int right)> ItemBounds = new();
        public readonly List<(int left, int right)> LightBounds = new();
        public ushort CornerRadius;
        public bool AwaitingKeyframe = true;
        public bool HasPicture;
        public bool HasVideo;
        public bool Visible;
        public IDXGISwapChain1? SwapChain;
        public IDCompositionTarget? CompositionTarget;
        public IDCompositionVisual? Visual;
        public ID2D1Bitmap1? Target;
        public ID3D11Texture2D? VideoTexture;
        public ID2D1Bitmap1? VideoBitmap;
        public H264Decoder? Decoder;
        public int DecoderWidth, DecoderHeight;
        public ID3D11VideoProcessorEnumerator? Enumerator;
        public ID3D11VideoProcessor? Processor;
        public ID3D11VideoProcessorOutputView? OutputView;
        public int ProcessorInputWidth, ProcessorInputHeight, ProcessorOutputWidth, ProcessorOutputHeight;
        public readonly Dictionary<(IntPtr, uint), ID3D11VideoProcessorInputView> InputViews = new();
        public ID3D11Texture2D? UploadStaging;
        public ID3D11Texture2D? UploadTexture;
    }

    private void CreateFullscreenSurface()
    {
        var handle = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE, ClassName, "BorderlessMouse – Mac", WS_POPUP,
            _monitor.Left, _monitor.Top, _monitor.Width, _monitor.Height, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (handle == IntPtr.Zero) throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
        var surface = new Surface
        {
            Id = FullscreenId, Kind = SurfaceKind.Fullscreen, Handle = handle, Client = _monitor,
            Width = _monitor.Width, Height = _monitor.Height,
        };
        var description = new SwapChainDescription1((uint)surface.Width, (uint)surface.Height, Format.B8G8R8A8_UNorm, false,
            Usage.RenderTargetOutput, 2, Scaling.Stretch, SwapEffect.FlipDiscard, AlphaMode.Ignore, SwapChainFlags.None);
        surface.SwapChain = _factory!.CreateSwapChainForHwnd(_device!, handle, description, null, null);
        _factory.MakeWindowAssociation(handle, WindowAssociationFlags.IgnoreAll);
        Register(surface);
    }

    private void CreateWindowSurface(ProxyWindow window)
    {
        var kind = window.Popup ? SurfaceKind.Popup : SurfaceKind.Window;
        var surface = new Surface
        {
            Id = window.Id, Kind = kind, Pid = window.Pid, Title = window.Title,
            MacScreen = window.MacScreen, MacPixels = window.MacPixels,
            DisplayWidth = window.DisplayWidth, DisplayHeight = window.DisplayHeight,
        };
        // Nowe okno powstaje tam, gdzie leży okno Maca; menu i podpowiedzi – przy swoim oknie.
        var client = kind == SurfaceKind.Popup ? PopupClient(surface) : window.MacScreen;
        // Bez ramki i bez bitmapy przekierowania: obraz (z przezroczystymi rogami) daje DirectComposition.
        var style = kind == SurfaceKind.Window ? WS_POPUP | WS_SYSMENU | WS_MINIMIZEBOX : WS_POPUP;
        var exStyle = WS_EX_NOREDIRECTIONBITMAP | (kind == SurfaceKind.Window ? WS_EX_APPWINDOW : WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE);
        surface.Handle = CreateWindowExW(exStyle, ClassName, window.Title, style, client.Left, client.Top,
            Math.Max(1, client.Width), Math.Max(1, client.Height), IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (surface.Handle == IntPtr.Zero) return;
        if (kind == SurfaceKind.Window)
            surface.HeaderHeight = (int)Math.Round(HeaderHeight96 * Math.Max(96, GetDpiForWindow(surface.Handle)) / 96);
        Register(surface);
        Place(surface, client);
        CreateComposition(surface);
        ApplyIcon(surface);
        // Klatki mogły przyjść przed listą okien – prosimy o pełny obraz.
        RequestKeyframe(surface.Id);
    }

    /// <summary>Łańcuch wymiany z przezroczystością, podpięty do okna przez DirectComposition.</summary>
    private void CreateComposition(Surface surface)
    {
        var description = new SwapChainDescription1((uint)surface.Width, (uint)(surface.Height + surface.HeaderHeight), Format.B8G8R8A8_UNorm,
            false, Usage.RenderTargetOutput, 2, Scaling.Stretch, SwapEffect.FlipSequential, AlphaMode.Premultiplied, SwapChainFlags.None);
        surface.SwapChain = _factory!.CreateSwapChainForComposition(_device!, description, null);
        _composition!.CreateTargetForHwnd(surface.Handle, true, out surface.CompositionTarget).CheckError();
        surface.Visual = _composition.CreateVisual();
        surface.Visual.SetContent(surface.SwapChain).CheckError();
        surface.CompositionTarget!.SetRoot(surface.Visual).CheckError();
        _composition.Commit().CheckError();
    }

    private void UpdateSurface(Surface surface, ProxyWindow window)
    {
        if (surface.Title != window.Title)
        {
            surface.Title = window.Title;
            SetWindowTextW(surface.Handle, window.Title);
            if (!_menus.ContainsKey(surface.Pid)) Compose(surface);
        }
        var previous = surface.MacScreen;
        surface.MacScreen = window.MacScreen;
        surface.MacPixels = window.MacPixels;
        surface.DisplayWidth = window.DisplayWidth;
        surface.DisplayHeight = window.DisplayHeight;
        if (surface.Kind == SurfaceKind.Popup)
        {
            Place(surface, PopupClient(surface));
            return;
        }
        // Okno na Windowsie żyje własnym położeniem (można je przenieść nawet na inny monitor).
        // Przesunięcie okna na Macu (za jego pasek tytułu) przesuwa je o tyle samo, a rozmiar
        // idzie za oknem Maca – chyba że użytkownik właśnie je przesuwa albo jest zmaksymalizowane.
        if (surface.InSizeMove || surface.Maximized || IsIconic(surface.Handle))
        {
            PublishProxies();
            return;
        }
        var dx = window.MacScreen.Left - previous.Left;
        var dy = window.MacScreen.Top - previous.Top;
        Place(surface, new RECT
        {
            Left = surface.Client.Left + dx, Top = surface.Client.Top + dy,
            Right = surface.Client.Left + dx + window.MacScreen.Width, Bottom = surface.Client.Top + dy + window.MacScreen.Height,
        });
    }

    /// <summary>Menu i podpowiedzi: to samo przesunięcie względem okna aplikacji co na Macu.</summary>
    private RECT PopupClient(Surface popup)
    {
        var owner = _surfaces.Values
            .Where(s => s.Kind == SurfaceKind.Window && s.Pid == popup.Pid)
            .OrderByDescending(s => Contains(s.MacScreen, popup.MacScreen.Left, popup.MacScreen.Top))
            .FirstOrDefault();
        var dx = owner is null ? 0 : owner.Client.Left - owner.MacScreen.Left;
        var dy = owner is null ? 0 : owner.Client.Top - owner.MacScreen.Top;
        var m = popup.MacScreen;
        return new RECT { Left = m.Left + dx, Top = m.Top + dy, Right = m.Right + dx, Bottom = m.Bottom + dy };
    }

    private static bool Contains(RECT r, int x, int y) => x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;

    /// <summary>Ustawia okno tak, żeby obraz wypadł w <paramref name="client"/> (pasek menu nad nim).</summary>
    private void Place(Surface surface, RECT client)
    {
        if (client.Width <= 0 || client.Height <= 0) return;
        if (client.Left != surface.Client.Left || client.Top != surface.Client.Top || client.Width != surface.Width
            || client.Height != surface.Height || surface.Width == 0)
        {
            SetWindowPos(surface.Handle, IntPtr.Zero, client.Left, client.Top - surface.HeaderHeight, client.Width,
                client.Height + surface.HeaderHeight, SWP_NOZORDER | SWP_NOACTIVATE);
        }
        SyncClient(surface);
    }

    /// <summary>Faktyczne położenie okna, rozmiar łańcucha wymiany i tekstury obrazu.</summary>
    private void SyncClient(Surface surface)
    {
        if (surface.Kind == SurfaceKind.Fullscreen || IsIconic(surface.Handle)) return;
        if (!GetWindowRect(surface.Handle, out var frame)) return;
        surface.Client = new RECT { Left = frame.Left, Top = frame.Top + surface.HeaderHeight, Right = frame.Right, Bottom = frame.Bottom };
        var width = Math.Max(1, frame.Width);
        var height = Math.Max(1, frame.Height - surface.HeaderHeight);
        if (width != surface.Width || height != surface.Height)
        {
            surface.Width = width;
            surface.Height = height;
            if (surface.SwapChain is not null)
            {
                DisposeProcessor(surface); // widok wyjścia i tekstura obrazu mają stary rozmiar
                surface.Target?.Dispose();
                surface.Target = null;
                surface.SwapChain.ResizeBuffers(2, (uint)width, (uint)(height + surface.HeaderHeight), Format.B8G8R8A8_UNorm).CheckError();
                surface.HasVideo = false;
                RequestKeyframe(surface.Id);
            }
        }
        PublishProxies();
    }

    private void ApplyVisibility(Surface surface)
    {
        var show = surface.HasPicture && (surface.Kind != SurfaceKind.Fullscreen || _wantFullscreenVisible);
        if (show == surface.Visible) return;
        surface.Visible = show;
        if (!show)
        {
            ShowWindow(surface.Handle, SW_HIDE);
            return;
        }
        if (surface.Kind == SurfaceKind.Window)
        {
            ShowWindow(surface.Handle, SW_SHOWNOACTIVATE);
            SyncClient(surface);
        }
        else
        {
            var top = surface.Kind == SurfaceKind.Fullscreen ? surface.Client.Top : surface.Client.Top;
            SetWindowPos(surface.Handle, HWND_TOPMOST, surface.Client.Left, top, surface.Width, surface.Height,
                SWP_NOACTIVATE | SWP_SHOWWINDOW);
            ShowWindow(surface.Handle, SW_SHOWNOACTIVATE);
        }
        // Ukryte okno mogło nie zachować obrazu, a statyczny obraz nie przyjdzie sam z siebie.
        RequestKeyframe(surface.Id);
    }

    private void ApplyIcon(Surface surface)
    {
        if (surface.Kind != SurfaceKind.Window || !_icons.TryGetValue(surface.Pid, out var icon)) return;
        SendMessageW(surface.Handle, WM_SETICON, 0, icon);
        SendMessageW(surface.Handle, WM_SETICON, 1, icon);
    }

    /// <summary>Zielony przycisk / podwójne kliknięcie paska: cały obszar roboczy monitora albo poprzedni rozmiar.</summary>
    private void ToggleMaximize(Surface surface)
    {
        RECT target;
        if (surface.Maximized)
        {
            surface.Maximized = false;
            target = surface.RestoreClient;
        }
        else
        {
            var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(MonitorFromWindow(surface.Handle, MONITOR_DEFAULTTONEAREST), ref info)) return;
            surface.RestoreClient = surface.Client;
            surface.Maximized = true;
            var work = info.rcWork;
            target = new RECT { Left = work.Left, Top = work.Top + surface.HeaderHeight, Right = work.Right, Bottom = work.Bottom };
        }
        Place(surface, target);
        ResizeRequested?.Invoke(surface.Id, surface.Width, surface.Height);
    }

    private void Register(Surface surface)
    {
        _surfaces[surface.Id] = surface;
        _byHandle[surface.Handle] = surface;
    }

    private void DestroySurface(Surface surface)
    {
        DisposeGpu(surface);
        _surfaces.Remove(surface.Id);
        _byHandle.Remove(surface.Handle);
        DestroyWindow(surface.Handle);
    }

    private void PublishProxies()
    {
        _proxies = _surfaces.Values.Where(s => s.Kind != SurfaceKind.Fullscreen)
            .ToDictionary(s => s.Handle, s => new ProxyInfo(s.Id, s.Pid, s.Kind == SurfaceKind.Popup, s.Client, s.MacPixels,
                s.DisplayWidth, s.DisplayHeight));
    }

    // ---------------- pasek menu: interakcja ----------------

    private IReadOnlyList<MacMenuItem> HeaderItems(Surface surface) =>
        _menus.TryGetValue(surface.Pid, out var items) ? items : Array.Empty<MacMenuItem>();

    private static int LightAt(Surface surface, int x)
    {
        for (var i = 0; i < surface.LightBounds.Count; i++)
            if (x >= surface.LightBounds[i].left && x < surface.LightBounds[i].right) return i;
        return LightNone;
    }

    private static int HeaderItemAt(Surface surface, int x)
    {
        for (var i = 0; i < surface.ItemBounds.Count; i++)
            if (x >= surface.ItemBounds[i].left && x < surface.ItemBounds[i].right) return i;
        return -1;
    }

    private void UpdateHover(Surface surface, int x, int y)
    {
        var inHeader = y >= 0 && y < surface.HeaderHeight;
        var item = inHeader ? HeaderItemAt(surface, x) : -1;
        var light = inHeader ? LightAt(surface, x) : LightNone;
        // macOS pokazuje symbole na wszystkich trzech przyciskach po najechaniu na którykolwiek.
        var anyLight = light != LightNone ? 0 : LightNone;
        if (item == surface.HoverItem && anyLight == surface.HoverLight) return;
        surface.HoverItem = item;
        surface.HoverLight = anyLight;
        Compose(surface);
    }

    private void ShowHeaderMenu(Surface surface, int index)
    {
        var items = HeaderItems(surface);
        if (index >= items.Count || items[index].Children.Count == 0) return;
        MenuOpening?.Invoke(surface.Pid);
        var menu = CreatePopupMenu();
        AppendItems(menu, items[index].Children);
        var origin = new POINT { X = surface.ItemBounds[index].left, Y = surface.HeaderHeight };
        ClientToScreen(surface.Handle, ref origin);
        SetForegroundWindow(surface.Handle);
        var command = TrackPopupMenuEx(menu, 0x0100 | 0x0002, origin.X, origin.Y, surface.Handle, IntPtr.Zero); // TPM_RETURNCMD | TPM_RIGHTBUTTON
        DestroyMenu(menu);
        surface.HoverItem = -1;
        Compose(surface);
        if (command > 0) MenuInvoked?.Invoke(surface.Pid, command - 1);
    }

    private static void AppendItems(IntPtr menu, IReadOnlyList<MacMenuItem> items)
    {
        foreach (var item in items)
        {
            if (item.Separator)
            {
                AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                continue;
            }
            var text = item.Title.Replace("&", "&&") + (item.Shortcut.Length > 0 ? "\t" + item.Shortcut : "");
            var flags = (item.Enabled ? 0u : MF_GRAYED) | (item.Checked ? MF_CHECKED : 0u);
            if (item.Children.Count > 0)
            {
                var sub = CreatePopupMenu();
                AppendItems(sub, item.Children);
                AppendMenuW(menu, MF_STRING | MF_POPUP | flags, (UIntPtr)(nuint)sub, text);
            }
            else
            {
                AppendMenuW(menu, MF_STRING | flags, (UIntPtr)(uint)(item.Index + 1), text);
            }
        }
    }

    // ---------------- okno i urządzenie ----------------

    private void RegisterWindowClass()
    {
        var namePtr = Marshal.StringToHGlobalUni(ClassName);
        try
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = GetModuleHandle(null),
                hCursor = IntPtr.Zero,
                lpszClassName = namePtr,
            };
            if (RegisterClassExW(ref wc) == 0)
            {
                var error = Marshal.GetLastWin32Error();
                if (error != 1410) throw new InvalidOperationException($"RegisterClassEx failed: {error}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(namePtr);
        }
    }

    private void CreateDevice()
    {
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 };
        const DeviceCreationFlags flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
        var result = D3D11.D3D11CreateDevice(null, DriverType.Hardware, flags, levels, out ID3D11Device? device, out ID3D11DeviceContext? context);
        if (result.Failure || device is null)
            result = D3D11.D3D11CreateDevice(null, DriverType.Warp, flags, levels, out device, out context);
        result.CheckError();
        _device = device!;
        _context = context!;
        using (var multithread = _device.QueryInterface<ID3D11Multithread>())
        {
            multithread.SetMultithreadProtected(true);
        }
        _videoDevice = _device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = _context.QueryInterface<ID3D11VideoContext>();
        using var dxgiDevice = _device.QueryInterface<IDXGIDevice1>();
        dxgiDevice.MaximumFrameLatency = 1;
        using (var adapter = dxgiDevice.GetAdapter())
        {
            _factory = adapter.GetParent<IDXGIFactory2>();
        }
        // Okna Maca: Direct2D (pasek, maska rogów) i DirectComposition (przezroczystość).
        _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.SingleThreaded, DebugLevel.None);
        _d2dDevice = _d2dFactory.CreateDevice(dxgiDevice);
        _d2d = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
        _d2d.AntialiasMode = AntialiasMode.PerPrimitive;
        _d2d.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
        _dwrite ??= DWrite.DWriteCreateFactory<IDWriteFactory>(Vortice.DirectWrite.FactoryType.Shared);
        _composition = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
    }

    /// <summary>Utrata urządzenia (np. aktualizacja sterownika): nowe urządzenie i łańcuchy wymiany.</summary>
    private void RecreateDevice()
    {
        var surfaces = _surfaces.Values.ToList();
        foreach (var surface in surfaces) DisposeGpu(surface);
        DisposeDevice();
        CreateDevice();
        foreach (var surface in surfaces)
        {
            if (surface.Kind == SurfaceKind.Fullscreen)
            {
                DestroySurface(surface);
                CreateFullscreenSurface();
                continue;
            }
            CreateComposition(surface);
            surface.AwaitingKeyframe = true;
        }
        _frames.Clear();
        RequestKeyframe(null);
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        _byHandle.TryGetValue(hWnd, out var surface);
        var window = surface is { Kind: SurfaceKind.Window };
        switch (msg)
        {
            case WM_NCHITTEST when window:
            {
                // Pusty pasek menu działa jak pasek tytułu (przeciąganie okna).
                var point = new POINT { X = (short)((long)lParam & 0xFFFF), Y = (short)(((long)lParam >> 16) & 0xFFFF) };
                ScreenToClient(hWnd, ref point);
                var header = surface!;
                var inHeader = point.Y >= 0 && point.Y < header.HeaderHeight;
                return inHeader && HeaderItemAt(header, point.X) < 0 && LightAt(header, point.X) == LightNone ? HTCAPTION : HTCLIENT;
            }
            case WM_NCLBUTTONDBLCLK when window && (long)wParam == HTCAPTION:
                ToggleMaximize(surface!);
                return IntPtr.Zero;
            case WM_SETCURSOR:
            {
                if (surface?.Kind == SurfaceKind.Fullscreen)
                {
                    SetCursor(IntPtr.Zero); // kursor Maca jest w obrazie
                    return 1;
                }
                // Nad obrazem okna Maca – kształt kursora z Maca; nad paskiem – strzałka.
                GetCursorPos(out var cursor);
                var overImage = surface is not null && Contains(surface.Client, cursor.X, cursor.Y);
                SetCursor(LoadCursorW(IntPtr.Zero, overImage ? CursorIds[_cursorShape] : CursorIds[0]));
                return 1;
            }
            case WM_MOUSEMOVE when window:
            {
                UpdateHover(surface!, (short)((long)lParam & 0xFFFF), (short)(((long)lParam >> 16) & 0xFFFF));
                var track = new TRACKMOUSEEVENT { cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = TME_LEAVE, hwndTrack = hWnd };
                TrackMouseEvent(ref track);
                break;
            }
            case WM_MOUSELEAVE when window:
                UpdateHover(surface!, -1, -1);
                break;
            case WM_LBUTTONDOWN when window:
            {
                var x = (short)((long)lParam & 0xFFFF);
                var y = (short)(((long)lParam >> 16) & 0xFFFF);
                if (y >= surface!.HeaderHeight) break; // obraz okna – kliknięcie idzie do Maca przez hook
                var light = LightAt(surface, x);
                if (light == 0) CloseRequested?.Invoke(surface.Id);
                else if (light == 1) ShowWindow(surface.Handle, SW_MINIMIZE);
                else if (light == 2) ToggleMaximize(surface);
                else if (HeaderItemAt(surface, x) is var item and >= 0) ShowHeaderMenu(surface, item);
                return IntPtr.Zero;
            }
            case WM_MOUSEACTIVATE when surface is not null && surface.Kind != SurfaceKind.Window:
                return MA_NOACTIVATE;
            case WM_ACTIVATE when window:
                WindowActivated?.Invoke(surface!.Id, ((long)wParam & 0xFFFF) != 0);
                break;
            case WM_ENTERSIZEMOVE when window:
                surface!.InSizeMove = true;
                _movingHandle = hWnd;
                _movingWindowId = surface.Id;
                // Pętla przeciągania jest modalna – licznik czasu pozwala dalej dekodować obraz.
                SetTimer(hWnd, (UIntPtr)1, 15, IntPtr.Zero);
                break;
            case WM_EXITSIZEMOVE when window:
                surface!.InSizeMove = false;
                KillTimer(hWnd, (UIntPtr)1);
                _movingWindowId = 0;
                _movingHandle = IntPtr.Zero;
                SyncClient(surface);
                break;
            case WM_TIMER when window && surface!.InSizeMove:
                Pump();
                return IntPtr.Zero;
            case WM_MOVE when window:
                SyncClient(surface!);
                break;
            case WM_SIZE when window && (long)wParam != SIZE_MINIMIZED:
                // Po przywróceniu z paska zadań: bieżące położenie i świeży obraz.
                SyncClient(surface!);
                RequestKeyframe(surface!.Id);
                break;
            case WM_ERASEBKGND:
                return 1;
            case WM_SYSCOMMAND when ((long)wParam & 0xFFF0) == SC_KEYMENU:
                // Alt nie otwiera menu systemowego – idzie do Maca jako Option.
                return IntPtr.Zero;
            case WM_CLOSE:
                if (window) CloseRequested?.Invoke(surface!.Id);
                return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void RequestKeyframe(uint? streamId)
    {
        if (streamId is null)
        {
            var now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _lastKeyframeRequestTicks) < 300) return;
            Interlocked.Exchange(ref _lastKeyframeRequestTicks, now);
        }
        KeyframeNeeded?.Invoke(streamId);
    }

    private static void DisposeDecoder(Surface surface)
    {
        DisposeProcessor(surface);
        surface.Decoder?.Dispose();
        surface.Decoder = null;
        surface.DecoderWidth = surface.DecoderHeight = 0;
    }

    private static void DisposeProcessor(Surface surface)
    {
        foreach (var view in surface.InputViews.Values) view.Dispose();
        surface.InputViews.Clear();
        surface.OutputView?.Dispose();
        surface.OutputView = null;
        surface.Processor?.Dispose();
        surface.Processor = null;
        surface.Enumerator?.Dispose();
        surface.Enumerator = null;
        surface.VideoBitmap?.Dispose();
        surface.VideoBitmap = null;
        surface.VideoTexture?.Dispose();
        surface.VideoTexture = null;
        surface.ProcessorInputWidth = surface.ProcessorInputHeight = surface.ProcessorOutputWidth = surface.ProcessorOutputHeight = 0;
    }

    private static void DisposeGpu(Surface surface)
    {
        DisposeDecoder(surface);
        surface.UploadStaging?.Dispose();
        surface.UploadStaging = null;
        surface.UploadTexture?.Dispose();
        surface.UploadTexture = null;
        surface.Target?.Dispose();
        surface.Target = null;
        surface.Visual?.Dispose();
        surface.Visual = null;
        surface.CompositionTarget?.Dispose();
        surface.CompositionTarget = null;
        surface.SwapChain?.Dispose();
        surface.SwapChain = null;
        surface.HasVideo = false;
    }

    private void DisposeDevice()
    {
        _composition?.Dispose();
        _composition = null;
        _d2d?.Dispose();
        _d2d = null;
        _d2dDevice?.Dispose();
        _d2dDevice = null;
        _d2dFactory?.Dispose();
        _d2dFactory = null;
        _factory?.Dispose();
        _factory = null;
        _videoContext?.Dispose();
        _videoContext = null;
        _videoDevice?.Dispose();
        _videoDevice = null;
        _context?.Dispose();
        _context = null;
        _device?.Dispose();
        _device = null;
    }

    private void Cleanup()
    {
        foreach (var surface in _surfaces.Values.ToList()) DestroySurface(surface);
        _proxies = new Dictionary<IntPtr, ProxyInfo>();
        DisposeDevice();
        _dwrite?.Dispose();
        _dwrite = null;
        foreach (var icon in _icons.Values) DestroyIcon(icon);
        _icons.Clear();
        _menus.Clear();
        _wantFullscreenVisible = false;
        // Klasa wskazuje na procedurę tej instancji – nie może przeżyć okien.
        UnregisterClassW(ClassName, GetModuleHandle(null));
    }

    public void Dispose()
    {
        Stop();
        _wake.Dispose();
    }
}
