using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using BorderlessMouse.Protocol;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;
using static BorderlessMouse.Display.DisplayNative;
using static BorderlessMouse.Input.NativeMethods;
using static BorderlessMouse.Localization.L10n;

namespace BorderlessMouse.Display;

/// <summary>
/// Obraz z Maca na Windowsie. Dwa tryby:
/// <list type="bullet">
/// <item>pełny pulpit – jedno okno na cały monitor ze strumieniem całego ekranu wirtualnego;</item>
/// <item>tryb okien – każde okno Maca jest zwykłym oknem Windows: pasek tytułu z przyciskami
/// minimalizacji, maksymalizacji i zamknięcia, menu aplikacji Maca w nagłówku, zmiana rozmiaru
/// ramką, pasek zadań i Alt+Tab. Każde ma osobny strumień i dekoder.</item>
/// </list>
/// Dekodowanie (Media Foundation), konwersja NV12 → RGB (procesor wideo D3D11)
/// i wyświetlanie (łańcuchy wymiany DXGI) działają na jednym wątku z pętlą komunikatów.
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
    /// obszar klienta na ekranie Windows i odpowiadające mu okno na ekranie wirtualnym.
    /// </summary>
    public readonly record struct ProxyInfo(uint Id, int Pid, bool Popup, RECT Client, RECT MacPixels, int DisplayWidth, int DisplayHeight);

    private const string ClassName = "BorderlessMouseDisplay";
    private const uint FullscreenId = 0;
    /// <summary>Tyle klatek w kolejce oznacza, że dekoder nie nadąża – odrzucamy i prosimy o klatki kluczowe.</summary>
    private const int MaxQueuedFrames = 48;
    private const uint WM_MOVE = 0x0003;
    private const uint WM_SIZE = 0x0005;
    private const uint WM_ACTIVATE = 0x0006;
    private const uint WM_ERASEBKGND = 0x0014;
    private const uint WM_SETICON = 0x0080;
    private const uint WM_COMMAND = 0x0111;
    private const uint WM_SYSCOMMAND = 0x0112;
    private const uint WM_INITMENU = 0x0116;
    private const uint WM_ENTERSIZEMOVE = 0x0231;
    private const uint WM_EXITSIZEMOVE = 0x0232;
    private const long SC_KEYMENU = 0xF100;
    private const long SIZE_RESTORED = 0;
    private const long SIZE_MINIMIZED = 1;
    private const long SIZE_MAXIMIZED = 2;
    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const uint WS_EX_APPWINDOW = 0x00040000;
    private const uint SWP_NOZORDER = 0x0004;

    private readonly ConcurrentQueue<VideoStream.VideoFrame> _frames = new();
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly WndProcDelegate _wndProc;
    private Thread? _thread;
    private volatile bool _running;
    private long _framesPresented;
    private long _lastKeyframeRequestTicks;
    private volatile IReadOnlyDictionary<IntPtr, ProxyInfo> _proxies = new Dictionary<IntPtr, ProxyInfo>();

    // tylko wątek wyświetlania
    private RECT _monitor;
    private bool _windowMode;
    private bool _wantFullscreenVisible;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11VideoDevice? _videoDevice;
    private ID3D11VideoContext? _videoContext;
    private IDXGIFactory2? _factory;
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
    /// <summary>Alt+F4, przycisk zamknięcia, zamknięcie z paska zadań.</summary>
    public event Action<uint>? CloseRequested;
    /// <summary>Użytkownik zmienił rozmiar ramką albo zmaksymalizował okno (rozmiar klienta w pikselach Windows).</summary>
    public event Action<uint, int, int>? ResizeRequested;
    /// <summary>Wybrano pozycję menu aplikacji (pid, numer pozycji).</summary>
    public event Action<int, int>? MenuInvoked;
    /// <summary>Menu okna jest otwierane – warto odświeżyć je z Maca (wyszarzenia, zaznaczenia).</summary>
    public event Action<int>? MenuOpening;

    public long FramesPresented => Interlocked.Read(ref _framesPresented);
    public bool UsesGpuDecoding { get; private set; }

    /// <summary>Okna Windows reprezentujące okna Maca – bezpieczne do odczytu z dowolnego wątku.</summary>
    public IReadOnlyDictionary<IntPtr, ProxyInfo> Proxies => _proxies;

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
        _thread.SetApartmentState(ApartmentState.MTA);
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

    /// <summary>Menu aplikacji Maca dla nagłówka jej okien.</summary>
    public void SetMenu(int pid, IReadOnlyList<MacMenuItem> items) => Post(() =>
    {
        _menus[pid] = items;
        foreach (var surface in _surfaces.Values.Where(s => s.Pid == pid && s.Kind == SurfaceKind.Window)) ApplyMenu(surface);
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
                while (_commands.TryDequeue(out var command)) command();
                DecodePending();
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
                if (frame.CornerRadius != surface.CornerRadius && surface.Kind == SurfaceKind.Popup)
                {
                    surface.CornerRadius = frame.CornerRadius;
                    ApplyShape(surface);
                }
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
            // Pełny pulpit: proporcje ekranu. Okno: obraz wypełnia obszar klienta (w trakcie
            // zmiany rozmiaru skaluje się, zanim Mac dopasuje okno).
            var destination = surface.Kind == SurfaceKind.Fullscreen
                ? Letterbox(visibleWidth, visibleHeight, width, height)
                : new RawRect(0, 0, width, height);
            _videoContext.VideoProcessorSetStreamSourceRect(surface.Processor!, 0, true, new RawRect(0, 0, visibleWidth, visibleHeight));
            _videoContext.VideoProcessorSetStreamDestRect(surface.Processor!, 0, true, destination);
            _videoContext.VideoProcessorSetOutputTargetRect(surface.Processor!, true, new RawRect(0, 0, width, height));
            var streams = new[] { new VideoProcessorStream { Enable = true, InputSurface = inputView } };
            _videoContext.VideoProcessorBlt(surface.Processor!, surface.OutputView!, 0, 1, streams).CheckError();
        }
        surface.SwapChain.Present(0, PresentFlags.None).CheckError();
        Interlocked.Increment(ref _framesPresented);
        if (!surface.HasPicture)
        {
            surface.HasPicture = true;
            ApplyVisibility(surface);
        }
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
        using var backBuffer = surface.SwapChain!.GetBuffer<ID3D11Texture2D>(0);
        surface.OutputView = _videoDevice.CreateVideoProcessorOutputView(backBuffer, surface.Enumerator, new VideoProcessorOutputViewDescription
        {
            ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
        });
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
        public int Pid;
        public string Title = "";
        /// <summary>Okno Maca przeliczone na ekran Windows (z ostatniej listy).</summary>
        public RECT MacScreen;
        public RECT MacPixels;
        public int DisplayWidth, DisplayHeight;
        /// <summary>Obszar klienta na ekranie Windows – tam jest obraz okna Maca.</summary>
        public RECT Client;
        /// <summary>Rozmiar łańcucha wymiany = rozmiar obszaru klienta.</summary>
        public int Width, Height;
        public bool InSizeMove;
        public bool Maximized;
        public long LastResizeRequest;
        public IntPtr Menu;
        public ushort CornerRadius;
        public int ShapeWidth, ShapeHeight, ShapeRadius = -1;
        public bool AwaitingKeyframe = true;
        public bool HasPicture;
        public bool Visible;
        public IDXGISwapChain1? SwapChain;
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
        surface.SwapChain = CreateSwapChain(handle, surface.Width, surface.Height);
        Register(surface);
    }

    private static uint StyleOf(SurfaceKind kind) => kind == SurfaceKind.Window ? WS_OVERLAPPEDWINDOW : WS_POPUP;
    private static uint ExStyleOf(SurfaceKind kind) =>
        kind == SurfaceKind.Window ? WS_EX_APPWINDOW : WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE;

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
        surface.Client = kind == SurfaceKind.Popup ? PopupClient(surface) : window.MacScreen;
        var frame = FrameFor(surface.Client, kind, hasMenu: false);
        surface.Handle = CreateWindowExW(ExStyleOf(kind), ClassName, window.Title, StyleOf(kind), frame.Left, frame.Top,
            Math.Max(1, frame.Width), Math.Max(1, frame.Height), IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (surface.Handle == IntPtr.Zero) return;
        surface.Width = Math.Max(1, surface.Client.Width);
        surface.Height = Math.Max(1, surface.Client.Height);
        surface.SwapChain = CreateSwapChain(surface.Handle, surface.Width, surface.Height);
        Register(surface);
        ApplyIcon(surface);
        ApplyMenu(surface);
        // Klatki mogły przyjść przed listą okien – prosimy o pełny obraz.
        RequestKeyframe(surface.Id);
    }

    private void UpdateSurface(Surface surface, ProxyWindow window)
    {
        if (surface.Title != window.Title)
        {
            surface.Title = window.Title;
            SetWindowTextW(surface.Handle, window.Title);
        }
        var previous = surface.MacScreen;
        surface.MacScreen = window.MacScreen;
        surface.MacPixels = window.MacPixels;
        surface.DisplayWidth = window.DisplayWidth;
        surface.DisplayHeight = window.DisplayHeight;
        if (surface.Kind == SurfaceKind.Popup)
        {
            SetClient(surface, PopupClient(surface));
            return;
        }
        // Okno na Windowsie żyje własnym położeniem (można je przenieść nawet na inny monitor).
        // Przesunięcie okna na Macu (np. za jego pasek tytułu) przesuwa je o tyle samo, a rozmiar
        // idzie za oknem Maca – chyba że użytkownik właśnie je rozciąga albo jest zmaksymalizowane.
        if (surface.InSizeMove || surface.Maximized || IsIconic(surface.Handle)) return;
        var dx = window.MacScreen.Left - previous.Left;
        var dy = window.MacScreen.Top - previous.Top;
        var client = new RECT
        {
            Left = surface.Client.Left + dx, Top = surface.Client.Top + dy,
            Right = surface.Client.Left + dx + window.MacScreen.Width, Bottom = surface.Client.Top + dy + window.MacScreen.Height,
        };
        SetClient(surface, client);
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

    /// <summary>Ramka okna tak, żeby obszar klienta wypadł dokładnie w <paramref name="client"/>.</summary>
    private static RECT FrameFor(RECT client, SurfaceKind kind, bool hasMenu)
    {
        var frame = client;
        if (kind == SurfaceKind.Window) AdjustWindowRectEx(ref frame, StyleOf(kind), hasMenu, ExStyleOf(kind));
        return frame;
    }

    private void SetClient(Surface surface, RECT client)
    {
        if (client.Width <= 0 || client.Height <= 0) return;
        if (client.Left == surface.Client.Left && client.Top == surface.Client.Top
            && client.Width == surface.Client.Width && client.Height == surface.Client.Height) return;
        var frame = FrameFor(client, surface.Kind, surface.Menu != IntPtr.Zero);
        SetWindowPos(surface.Handle, IntPtr.Zero, frame.Left, frame.Top, frame.Width, frame.Height, SWP_NOZORDER | SWP_NOACTIVATE);
        SyncClient(surface);
    }

    /// <summary>Po każdej zmianie ramki: faktyczny obszar klienta i rozmiar łańcucha wymiany.</summary>
    private void SyncClient(Surface surface)
    {
        if (surface.Kind == SurfaceKind.Fullscreen || IsIconic(surface.Handle)) return;
        if (!GetClientRect(surface.Handle, out var local)) return;
        var origin = new POINT { X = 0, Y = 0 };
        ClientToScreen(surface.Handle, ref origin);
        surface.Client = new RECT { Left = origin.X, Top = origin.Y, Right = origin.X + local.Width, Bottom = origin.Y + local.Height };
        var width = Math.Max(1, local.Width);
        var height = Math.Max(1, local.Height);
        if (width != surface.Width || height != surface.Height)
        {
            surface.Width = width;
            surface.Height = height;
            DisposeProcessor(surface); // widok wyjścia trzyma bufor łańcucha wymiany
            surface.SwapChain?.ResizeBuffers(2, (uint)width, (uint)height, Format.B8G8R8A8_UNorm).CheckError();
            ApplyShape(surface);
            RequestKeyframe(surface.Id);
        }
        PublishProxies();
    }

    /// <summary>Menu i podpowiedzi Maca mają zaokrąglone narożniki; zwykłe okna mają ramkę Windows.</summary>
    private static void ApplyShape(Surface surface)
    {
        if (surface.Kind != SurfaceKind.Popup) return;
        var radius = surface.CornerRadius;
        if (surface.ShapeRadius == radius && surface.ShapeWidth == surface.Width && surface.ShapeHeight == surface.Height) return;
        surface.ShapeRadius = radius;
        surface.ShapeWidth = surface.Width;
        surface.ShapeHeight = surface.Height;
        if (radius == 0)
        {
            SetWindowRgn(surface.Handle, IntPtr.Zero, true);
            return;
        }
        var region = CreateRoundRectRgn(0, 0, surface.Width + 1, surface.Height + 1, radius * 2, radius * 2);
        if (SetWindowRgn(surface.Handle, region, true) == 0) DeleteObject(region);
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
            SetWindowPos(surface.Handle, HWND_TOPMOST, surface.Client.Left, surface.Client.Top, surface.Width, surface.Height,
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

    /// <summary>Menu aplikacji Maca w nagłówku okna. Obszar klienta zostaje w tym samym miejscu.</summary>
    private void ApplyMenu(Surface surface)
    {
        if (surface.Kind != SurfaceKind.Window || !_menus.TryGetValue(surface.Pid, out var items)) return;
        var client = surface.Client;
        var menu = CreateMenu();
        AppendItems(menu, items);
        var old = surface.Menu;
        SetWindowMenu(surface.Handle, menu);
        surface.Menu = menu;
        if (old != IntPtr.Zero) DestroyMenu(old);
        DrawMenuBar(surface.Handle);
        // Pasek menu zabiera miejsce z obszaru klienta – powiększamy ramkę zamiast zmniejszać obraz.
        if (!surface.Maximized && !IsIconic(surface.Handle))
        {
            var frame = FrameFor(client, surface.Kind, hasMenu: true);
            SetWindowPos(surface.Handle, IntPtr.Zero, frame.Left, frame.Top, frame.Width, frame.Height, SWP_NOZORDER | SWP_NOACTIVATE);
        }
        SyncClient(surface);
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
        DestroyWindow(surface.Handle); // niszczy też przypisane menu
    }

    private void PublishProxies()
    {
        _proxies = _surfaces.Values.Where(s => s.Kind != SurfaceKind.Fullscreen)
            .ToDictionary(s => s.Handle, s => new ProxyInfo(s.Id, s.Pid, s.Kind == SurfaceKind.Popup, s.Client, s.MacPixels,
                s.DisplayWidth, s.DisplayHeight));
    }

    // ---------------- okno i urządzenie ----------------

    private void RegisterWindowClass()
    {
        var classNamePtr = Marshal.StringToHGlobalUni(ClassName);
        try
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = GetModuleHandle(null),
                hCursor = IntPtr.Zero,
                lpszClassName = classNamePtr,
            };
            if (RegisterClassExW(ref wc) == 0)
            {
                var error = Marshal.GetLastWin32Error();
                if (error != 1410) throw new InvalidOperationException($"RegisterClassEx failed: {error}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(classNamePtr);
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
        using var adapter = dxgiDevice.GetAdapter();
        _factory = adapter.GetParent<IDXGIFactory2>();
    }

    private IDXGISwapChain1 CreateSwapChain(IntPtr handle, int width, int height)
    {
        var description = new SwapChainDescription1((uint)Math.Max(1, width), (uint)Math.Max(1, height), Format.B8G8R8A8_UNorm, false,
            Usage.RenderTargetOutput, 2, Scaling.Stretch, SwapEffect.FlipDiscard, AlphaMode.Ignore, SwapChainFlags.None);
        var swapChain = _factory!.CreateSwapChainForHwnd(_device!, handle, description, null, null);
        _factory.MakeWindowAssociation(handle, WindowAssociationFlags.IgnoreAll);
        return swapChain;
    }

    /// <summary>Utrata urządzenia (np. aktualizacja sterownika): nowe urządzenie i łańcuchy wymiany.</summary>
    private void RecreateDevice()
    {
        foreach (var surface in _surfaces.Values) DisposeGpu(surface);
        DisposeDevice();
        CreateDevice();
        foreach (var surface in _surfaces.Values)
        {
            surface.SwapChain = CreateSwapChain(surface.Handle, surface.Width, surface.Height);
            surface.AwaitingKeyframe = true;
        }
        _frames.Clear();
        RequestKeyframe(null);
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        _byHandle.TryGetValue(hWnd, out var surface);
        switch (msg)
        {
            case WM_SETCURSOR when ((long)lParam & 0xFFFF) == 1: // HTCLIENT – ramkę obsługuje Windows
                // Pełny pulpit: kursor Maca jest w obrazie. Okna Maca: zwykły kursor Windows.
                SetCursor(surface?.Kind == SurfaceKind.Fullscreen ? IntPtr.Zero : LoadCursorW(IntPtr.Zero, IDC_ARROW));
                return 1;
            case WM_MOUSEACTIVATE when surface?.Kind != SurfaceKind.Window:
                return MA_NOACTIVATE;
            case WM_ACTIVATE when surface?.Kind == SurfaceKind.Window:
                WindowActivated?.Invoke(surface.Id, ((long)wParam & 0xFFFF) != 0);
                break;
            case WM_ENTERSIZEMOVE when surface is not null:
                surface.InSizeMove = true;
                break;
            case WM_EXITSIZEMOVE when surface is not null:
                surface.InSizeMove = false;
                SyncClient(surface);
                RequestResize(surface, force: true);
                break;
            case WM_MOVE when surface?.Kind == SurfaceKind.Window:
                SyncClient(surface);
                break;
            case WM_SIZE when surface?.Kind == SurfaceKind.Window:
            {
                var kind = (long)wParam;
                if (kind == SIZE_MINIMIZED) break;
                var wasMaximized = surface.Maximized;
                surface.Maximized = kind == SIZE_MAXIMIZED;
                SyncClient(surface);
                // Maksymalizacja, przywrócenie albo rozciąganie ramką: Mac dopasowuje okno do obszaru klienta.
                if (surface.Maximized != wasMaximized) RequestResize(surface, force: true);
                else if (surface.InSizeMove) RequestResize(surface, force: false);
                else if (kind == SIZE_RESTORED) RequestKeyframe(surface.Id); // po przywróceniu z paska zadań
                break;
            }
            case WM_COMMAND when surface?.Kind == SurfaceKind.Window && ((long)wParam >> 16 & 0xFFFF) == 0:
            {
                var id = (int)((long)wParam & 0xFFFF);
                if (id > 0) MenuInvoked?.Invoke(surface.Pid, id - 1);
                return IntPtr.Zero;
            }
            case WM_INITMENU when surface?.Kind == SurfaceKind.Window:
                MenuOpening?.Invoke(surface.Pid);
                break;
            case WM_ERASEBKGND:
                return 1;
            case WM_SYSCOMMAND when ((long)wParam & 0xFFF0) == SC_KEYMENU && lParam == IntPtr.Zero:
                // Samo Alt nie może wprowadzać okna w tryb menu – Alt idzie do Maca jako Option.
                // Kliknięcie w menu myszą działa normalnie.
                return IntPtr.Zero;
            case WM_CLOSE:
                if (surface?.Kind == SurfaceKind.Window) CloseRequested?.Invoke(surface.Id);
                return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    /// <summary>W trakcie rozciągania najwyżej 10 razy na sekundę – Mac przerysowuje okno.</summary>
    private void RequestResize(Surface surface, bool force)
    {
        var now = Environment.TickCount64;
        if (!force && now - surface.LastResizeRequest < 100) return;
        surface.LastResizeRequest = now;
        ResizeRequested?.Invoke(surface.Id, surface.Width, surface.Height);
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
        surface.ProcessorInputWidth = surface.ProcessorInputHeight = surface.ProcessorOutputWidth = surface.ProcessorOutputHeight = 0;
    }

    private static void DisposeGpu(Surface surface)
    {
        DisposeDecoder(surface);
        surface.UploadStaging?.Dispose();
        surface.UploadStaging = null;
        surface.UploadTexture?.Dispose();
        surface.UploadTexture = null;
        surface.SwapChain?.Dispose();
        surface.SwapChain = null;
    }

    private void DisposeDevice()
    {
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
        foreach (var icon in _icons.Values) DestroyIcon(icon);
        _icons.Clear();
        _menus.Clear();
        _wantFullscreenVisible = false;
        // Klasa wskazuje na WndProc tej instancji – nie może przeżyć okien.
        UnregisterClassW(ClassName, GetModuleHandle(null));
    }

    public void Dispose()
    {
        Stop();
        _wake.Dispose();
    }
}
