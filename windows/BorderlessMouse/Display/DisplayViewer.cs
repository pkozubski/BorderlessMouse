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
/// <item>tryb okien – każde okno Maca jest oknem Windows w stylu macOS: zaokrąglone rogi,
/// własny pasek tytułu Maca (w obrazie) i nad nim ciemny pasek menu aplikacji. Okno jest na
/// pasku zadań i w Alt+Tab, da się je zminimalizować, przeciągnąć za pasek menu
/// i zmaksymalizować podwójnym kliknięciem. Każde ma osobny strumień i dekoder.</item>
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
    /// obszar obrazu na ekranie Windows i odpowiadające mu okno na ekranie wirtualnym.
    /// </summary>
    public readonly record struct ProxyInfo(uint Id, int Pid, bool Popup, RECT Client, RECT MacPixels, int DisplayWidth, int DisplayHeight);

    private const string ClassName = "BorderlessMouseDisplay";
    private const string HeaderClassName = "BorderlessMouseMenuBar";
    private const uint FullscreenId = 0;
    /// <summary>Tyle klatek w kolejce oznacza, że dekoder nie nadąża – odrzucamy i prosimy o klatki kluczowe.</summary>
    private const int MaxQueuedFrames = 48;
    /// <summary>Wysokość paska menu przy 100% (jak pasek menu macOS).</summary>
    private const int HeaderHeight96 = 28;
    private const uint WM_MOVE = 0x0003;
    private const uint WM_SIZE = 0x0005;
    private const uint WM_ACTIVATE = 0x0006;
    private const uint WM_PAINT = 0x000F;
    private const uint WM_ERASEBKGND = 0x0014;
    private const uint WM_SETICON = 0x0080;
    private const uint WM_NCLBUTTONDOWN = 0x00A1;
    private const uint WM_SYSCOMMAND = 0x0112;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_MOUSELEAVE = 0x02A3;
    private const uint WM_TIMER = 0x0113;
    private const uint WM_CANCELMODE = 0x001F;
    private const int SW_MINIMIZE = 6;
    private const uint WM_ENTERSIZEMOVE = 0x0231;
    private const uint WM_EXITSIZEMOVE = 0x0232;
    private const long SC_KEYMENU = 0xF100;
    private const long SIZE_MINIMIZED = 1;
    private const int HTCAPTION = 2;
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_CLIPCHILDREN = 0x02000000;
    private const uint WS_SYSMENU = 0x00080000;
    private const uint WS_MINIMIZEBOX = 0x00020000;
    private const uint WS_EX_APPWINDOW = 0x00040000;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint CS_DBLCLKS = 0x0008;

    private static readonly uint HeaderBackground = Rgb(41, 41, 43);   // jak kolor narożników z Maca
    private static readonly uint HeaderHover = Rgb(72, 72, 76);
    private static readonly uint HeaderText = Rgb(236, 236, 236);
    private static readonly uint HeaderDisabled = Rgb(130, 130, 134);
    // Przyciski okna jak w macOS: zamknij, minimalizuj, maksymalizuj.
    private static readonly uint[] LightColors = { Rgb(255, 95, 87), Rgb(254, 188, 46), Rgb(40, 200, 64) };
    private static readonly uint[] LightGlyphColors = { Rgb(120, 20, 15), Rgb(140, 90, 10), Rgb(10, 90, 25) };
    private static readonly string[] LightGlyphs = { "×", "–", "+" };
    private const int LightNone = -1;

    private readonly ConcurrentQueue<VideoStream.VideoFrame> _frames = new();
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly WndProcDelegate _wndProc;
    private readonly WndProcDelegate _headerProc;
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
    /// <summary>Alt+F4, zamknięcie z paska zadań.</summary>
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
        _headerProc = HeaderProc;
    }

    public void Start(RECT monitor, bool windowMode)
    {
        Stop();
        _monitor = monitor;
        _windowMode = windowMode;
        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "blm-display", Priority = ThreadPriority.AboveNormal };
        _thread.SetApartmentState(ApartmentState.STA); // TrackPopupMenu i GDI
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
        foreach (var surface in _surfaces.Values.Where(s => s.Pid == pid && s.Header != IntPtr.Zero))
            InvalidateRect(surface.Header, IntPtr.Zero, true);
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
            RegisterWindowClasses();
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
                if (frame.CornerRadius != surface.CornerRadius && surface.Kind != SurfaceKind.Fullscreen)
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
            // Pełny pulpit: proporcje ekranu. Okno: obraz wypełnia obszar (przy zmianie
            // rozmiaru skaluje się, zanim Mac dopasuje okno).
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
        /// <summary>Okno najwyższego poziomu (pasek zadań, Alt+Tab).</summary>
        public IntPtr Handle;
        /// <summary>Okno potomne z obrazem (łańcuch wymiany). Dla menu i pełnego pulpitu = Handle.</summary>
        public IntPtr Video;
        /// <summary>Pasek menu w stylu macOS nad obrazem (tylko zwykłe okna).</summary>
        public IntPtr Header;
        public int HeaderHeight;
        public int Pid;
        public string Title = "";
        /// <summary>Okno Maca przeliczone na ekran Windows (z ostatniej listy).</summary>
        public RECT MacScreen;
        public RECT MacPixels;
        public int DisplayWidth, DisplayHeight;
        /// <summary>Obszar obrazu na ekranie Windows.</summary>
        public RECT Client;
        /// <summary>Rozmiar łańcucha wymiany = rozmiar obszaru obrazu.</summary>
        public int Width, Height;
        public bool InSizeMove;
        public bool Maximized;
        public RECT RestoreClient;
        public int HoverItem = -1;
        public int HoverLight = LightNone;
        public List<(int left, int right)> LightBounds = new();
        public List<(int left, int right)> ItemBounds = new();
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
            Id = FullscreenId, Kind = SurfaceKind.Fullscreen, Handle = handle, Video = handle, Client = _monitor,
            Width = _monitor.Width, Height = _monitor.Height,
        };
        surface.SwapChain = CreateSwapChain(handle, surface.Width, surface.Height);
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
        var instance = GetModuleHandle(null);
        if (kind == SurfaceKind.Window)
        {
            // Bez ramki Windows: pasek tytułu jest w obrazie okna Maca, a nad nim pasek menu.
            surface.Handle = CreateWindowExW(WS_EX_APPWINDOW, ClassName, window.Title, WS_POPUP | WS_SYSMENU | WS_MINIMIZEBOX | WS_CLIPCHILDREN,
                client.Left, client.Top, Math.Max(1, client.Width), Math.Max(1, client.Height), IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (surface.Handle == IntPtr.Zero) return;
            surface.HeaderHeight = HeaderHeight96 * (int)Math.Max(96, GetDpiForWindow(surface.Handle)) / 96;
            surface.Header = CreateWindowExW(0, HeaderClassName, "", WS_CHILD | WS_VISIBLE, 0, 0, 1, surface.HeaderHeight,
                surface.Handle, IntPtr.Zero, instance, IntPtr.Zero);
            surface.Video = CreateWindowExW(0, ClassName, "", WS_CHILD | WS_VISIBLE, 0, surface.HeaderHeight, 1, 1,
                surface.Handle, IntPtr.Zero, instance, IntPtr.Zero);
        }
        else
        {
            surface.Handle = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE, ClassName, window.Title, WS_POPUP,
                client.Left, client.Top, Math.Max(1, client.Width), Math.Max(1, client.Height), IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (surface.Handle == IntPtr.Zero) return;
            surface.Video = surface.Handle;
        }
        Register(surface);
        Place(surface, client);
        surface.SwapChain = CreateSwapChain(surface.Video, surface.Width, surface.Height);
        ApplyIcon(surface);
        // Klatki mogły przyjść przed listą okien – prosimy o pełny obraz.
        RequestKeyframe(surface.Id);
    }

    private void UpdateSurface(Surface surface, ProxyWindow window)
    {
        if (surface.Title != window.Title)
        {
            surface.Title = window.Title;
            SetWindowTextW(surface.Handle, window.Title);
            if (surface.Header != IntPtr.Zero) InvalidateRect(surface.Header, IntPtr.Zero, true);
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
        var header = surface.HeaderHeight;
        var width = client.Width;
        var height = client.Height;
        if (client.Left != surface.Client.Left || client.Top != surface.Client.Top || width != surface.Client.Width
            || height != surface.Client.Height || surface.Width == 0)
        {
            SetWindowPos(surface.Handle, IntPtr.Zero, client.Left, client.Top - header, width, height + header, SWP_NOZORDER | SWP_NOACTIVATE);
            if (surface.Header != IntPtr.Zero)
            {
                MoveWindow(surface.Header, 0, 0, width, header, true);
                MoveWindow(surface.Video, 0, header, width, height, true);
            }
        }
        SyncClient(surface);
    }

    /// <summary>Faktyczny obszar obrazu i rozmiar łańcucha wymiany po każdej zmianie okna.</summary>
    private void SyncClient(Surface surface)
    {
        if (surface.Kind == SurfaceKind.Fullscreen || IsIconic(surface.Handle)) return;
        if (!GetClientRect(surface.Video, out var local)) return;
        var origin = new POINT { X = 0, Y = 0 };
        ClientToScreen(surface.Video, ref origin);
        surface.Client = new RECT { Left = origin.X, Top = origin.Y, Right = origin.X + local.Width, Bottom = origin.Y + local.Height };
        var width = Math.Max(1, local.Width);
        var height = Math.Max(1, local.Height);
        if (width != surface.Width || height != surface.Height)
        {
            surface.Width = width;
            surface.Height = height;
            if (surface.SwapChain is not null)
            {
                DisposeProcessor(surface); // widok wyjścia trzyma bufor łańcucha wymiany
                surface.SwapChain.ResizeBuffers(2, (uint)width, (uint)height, Format.B8G8R8A8_UNorm).CheckError();
                RequestKeyframe(surface.Id);
            }
            ApplyShape(surface);
        }
        PublishProxies();
    }

    /// <summary>Zaokrąglone rogi całego okna (pasek menu + obraz) z promieniem okna Maca.</summary>
    private static void ApplyShape(Surface surface)
    {
        if (surface.Kind == SurfaceKind.Fullscreen) return;
        var radius = surface.CornerRadius;
        var width = surface.Width;
        var height = surface.Height + surface.HeaderHeight;
        if (surface.ShapeRadius == radius && surface.ShapeWidth == width && surface.ShapeHeight == height) return;
        surface.ShapeRadius = radius;
        surface.ShapeWidth = width;
        surface.ShapeHeight = height;
        if (radius == 0)
        {
            SetWindowRgn(surface.Handle, IntPtr.Zero, true);
            return;
        }
        var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2);
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

    /// <summary>Podwójne kliknięcie paska menu: cały obszar roboczy monitora albo poprzedni rozmiar.</summary>
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
        if (surface.Video != surface.Handle) _byHandle[surface.Video] = surface;
        if (surface.Header != IntPtr.Zero) _byHandle[surface.Header] = surface;
    }

    private void DestroySurface(Surface surface)
    {
        DisposeGpu(surface);
        _surfaces.Remove(surface.Id);
        _byHandle.Remove(surface.Handle);
        _byHandle.Remove(surface.Video);
        _byHandle.Remove(surface.Header);
        DestroyWindow(surface.Handle); // niszczy też okna potomne
    }

    private void PublishProxies()
    {
        _proxies = _surfaces.Values.Where(s => s.Kind != SurfaceKind.Fullscreen)
            .ToDictionary(s => s.Handle, s => new ProxyInfo(s.Id, s.Pid, s.Kind == SurfaceKind.Popup, s.Client, s.MacPixels,
                s.DisplayWidth, s.DisplayHeight));
    }

    // ---------------- pasek menu w stylu macOS ----------------

    private IntPtr HeaderProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (!_byHandle.TryGetValue(hWnd, out var surface)) return DefWindowProcW(hWnd, msg, wParam, lParam);
        switch (msg)
        {
            case WM_PAINT:
                PaintHeader(surface);
                return IntPtr.Zero;
            case WM_ERASEBKGND:
                return 1;
            case WM_SETCURSOR:
                SetCursor(LoadCursorW(IntPtr.Zero, IDC_ARROW));
                return 1;
            case WM_MOUSEMOVE:
            {
                var x = (short)((long)lParam & 0xFFFF);
                var hover = HeaderItemAt(surface, x);
                var light = LightAt(surface, x);
                if (hover != surface.HoverItem || light != surface.HoverLight)
                {
                    surface.HoverItem = hover;
                    surface.HoverLight = light;
                    InvalidateRect(hWnd, IntPtr.Zero, false);
                }
                var track = new TRACKMOUSEEVENT { cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = TME_LEAVE, hwndTrack = hWnd };
                TrackMouseEvent(ref track);
                return IntPtr.Zero;
            }
            case WM_MOUSELEAVE:
                surface.HoverItem = -1;
                surface.HoverLight = LightNone;
                InvalidateRect(hWnd, IntPtr.Zero, false);
                return IntPtr.Zero;
            case WM_LBUTTONDOWN:
            {
                var x = (short)((long)lParam & 0xFFFF);
                var item = HeaderItemAt(surface, x);
                var light = LightAt(surface, x);
                if (light == 0) CloseRequested?.Invoke(surface.Id);
                else if (light == 1) ShowWindow(surface.Handle, SW_MINIMIZE);
                else if (light == 2) ToggleMaximize(surface);
                else if (item >= 0) ShowHeaderMenu(surface, item);
                else
                {
                    // Puste miejsce paska: przeciąganie okna jak za pasek tytułu.
                    ReleaseCapture();
                    SendMessageW(surface.Handle, WM_NCLBUTTONDOWN, HTCAPTION, IntPtr.Zero);
                }
                return IntPtr.Zero;
            }
            case WM_LBUTTONDBLCLK:
                var clicked = (short)((long)lParam & 0xFFFF);
                if (HeaderItemAt(surface, clicked) < 0 && LightAt(surface, clicked) == LightNone) ToggleMaximize(surface);
                return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    /// <summary>Pozycje paska: nazwa aplikacji (pogrubiona, jak na Macu), potem jej menu.</summary>
    private IReadOnlyList<MacMenuItem> HeaderItems(Surface surface) =>
        _menus.TryGetValue(surface.Pid, out var items) ? items : Array.Empty<MacMenuItem>();

    private void PaintHeader(Surface surface)
    {
        var dc = BeginPaint(surface.Header, out var paint);
        try
        {
            GetClientRect(surface.Header, out var bounds);
            var background = CreateSolidBrush(HeaderBackground);
            FillRect(dc, ref bounds, background);
            DeleteObject(background);
            var scale = surface.HeaderHeight / (double)HeaderHeight96;
            var regular = CreateFontW(-(int)Math.Round(13 * scale), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
            var bold = CreateFontW(-(int)Math.Round(13 * scale), 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
            SetBkMode(dc, 1); // TRANSPARENT
            var items = HeaderItems(surface);
            var labels = items.Count > 0 ? items.Select(i => (i.Title, i.Enabled)).ToList() : new List<(string, bool)> { (surface.Title, true) };
            // Przyciski okna jak w macOS; po najechaniu pokazują symbole.
            surface.LightBounds.Clear();
            var diameter = (int)Math.Round(12 * scale);
            var lightX = (int)Math.Round(12 * scale);
            var lightY = (surface.HeaderHeight - diameter) / 2;
            var nullPen = SelectObject(dc, GetStockObject(8)); // NULL_PEN
            var glyphFont = CreateFontW(-(int)Math.Round(11 * scale), 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
            for (var i = 0; i < 3; i++)
            {
                var brush = CreateSolidBrush(LightColors[i]);
                var previousBrush = SelectObject(dc, brush);
                Ellipse(dc, lightX, lightY, lightX + diameter, lightY + diameter);
                SelectObject(dc, previousBrush);
                DeleteObject(brush);
                if (surface.HoverLight != LightNone)
                {
                    SelectObject(dc, glyphFont);
                    SetTextColor(dc, LightGlyphColors[i]);
                    var glyph = new RECT { Left = lightX, Top = lightY - 1, Right = lightX + diameter, Bottom = lightY + diameter };
                    DrawTextW(dc, LightGlyphs[i], LightGlyphs[i].Length, ref glyph, 0x1 | 0x4 | 0x20); // DT_CENTER | DT_VCENTER | DT_SINGLELINE
                }
                surface.LightBounds.Add((lightX - 3, lightX + diameter + 3));
                lightX += diameter + (int)Math.Round(8 * scale);
            }
            SelectObject(dc, nullPen);
            DeleteObject(glyphFont);

            surface.ItemBounds.Clear();
            var x = lightX + (int)Math.Round(12 * scale);
            var padding = (int)Math.Round(8 * scale);
            for (var i = 0; i < labels.Count; i++)
            {
                var (text, enabled) = labels[i];
                SelectObject(dc, i == 0 ? bold : regular);
                GetTextExtentPoint32W(dc, text, text.Length, out var size);
                var left = x - padding;
                var right = x + size.cx + padding;
                if (i == surface.HoverItem && items.Count > 0)
                {
                    var highlight = CreateSolidBrush(HeaderHover);
                    var old = SelectObject(dc, highlight);
                    var pen = SelectObject(dc, GetStockObject(8)); // NULL_PEN
                    var inset = (int)Math.Round(3 * scale);
                    RoundRect(dc, left, inset, right, surface.HeaderHeight - inset, (int)(8 * scale), (int)(8 * scale));
                    SelectObject(dc, pen);
                    SelectObject(dc, old);
                    DeleteObject(highlight);
                }
                SetTextColor(dc, enabled ? HeaderText : HeaderDisabled);
                var rect = new RECT { Left = x, Top = 0, Right = x + size.cx + 1, Bottom = surface.HeaderHeight };
                DrawTextW(dc, text, text.Length, ref rect, 0x24 | 0x800); // DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX
                surface.ItemBounds.Add((left, right));
                x = right + padding;
            }
            DeleteObject(regular);
            DeleteObject(bold);
        }
        finally
        {
            EndPaint(surface.Header, ref paint);
        }
    }

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

    private void ShowHeaderMenu(Surface surface, int index)
    {
        var items = HeaderItems(surface);
        if (index >= items.Count || items[index].Children.Count == 0) return;
        MenuOpening?.Invoke(surface.Pid);
        var menu = CreatePopupMenu();
        AppendItems(menu, items[index].Children);
        var origin = new POINT { X = surface.ItemBounds[index].left, Y = surface.HeaderHeight };
        ClientToScreen(surface.Header, ref origin);
        SetForegroundWindow(surface.Handle);
        var command = TrackPopupMenuEx(menu, 0x0100 | 0x0002, origin.X, origin.Y, surface.Handle, IntPtr.Zero); // TPM_RETURNCMD | TPM_RIGHTBUTTON
        DestroyMenu(menu);
        surface.HoverItem = -1;
        InvalidateRect(surface.Header, IntPtr.Zero, false);
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

    private static uint Rgb(byte r, byte g, byte b) => (uint)(r | (g << 8) | (b << 16));

    // ---------------- okno i urządzenie ----------------

    private void RegisterWindowClasses()
    {
        Register(ClassName, _wndProc, 0);
        Register(HeaderClassName, _headerProc, CS_DBLCLKS);

        static void Register(string name, WndProcDelegate proc, uint style)
        {
            var namePtr = Marshal.StringToHGlobalUni(name);
            try
            {
                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                    style = style,
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
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
            surface.SwapChain = CreateSwapChain(surface.Video, surface.Width, surface.Height);
            surface.AwaitingKeyframe = true;
        }
        _frames.Clear();
        RequestKeyframe(null);
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        _byHandle.TryGetValue(hWnd, out var surface);
        var topLevel = surface is not null && hWnd == surface.Handle;
        switch (msg)
        {
            case WM_SETCURSOR:
                // Pełny pulpit: kursor Maca jest w obrazie. Okna Maca: zwykły kursor Windows.
                SetCursor(surface?.Kind == SurfaceKind.Fullscreen ? IntPtr.Zero : LoadCursorW(IntPtr.Zero, IDC_ARROW));
                return 1;
            case WM_MOUSEACTIVATE when surface is not null && surface.Kind != SurfaceKind.Window:
                return MA_NOACTIVATE;
            case WM_ACTIVATE when topLevel && surface!.Kind == SurfaceKind.Window:
                WindowActivated?.Invoke(surface.Id, ((long)wParam & 0xFFFF) != 0);
                break;
            case WM_ENTERSIZEMOVE when topLevel:
                surface!.InSizeMove = true;
                _movingHandle = hWnd;
                _movingWindowId = surface.Id;
                // Pętla przeciągania jest modalna – licznik czasu pozwala dalej dekodować obraz.
                SetTimer(hWnd, (UIntPtr)1, 15, IntPtr.Zero);
                break;
            case WM_EXITSIZEMOVE when topLevel:
                surface!.InSizeMove = false;
                KillTimer(hWnd, (UIntPtr)1);
                _movingWindowId = 0;
                _movingHandle = IntPtr.Zero;
                SyncClient(surface);
                break;
            case WM_TIMER when topLevel && surface!.InSizeMove:
                Pump();
                return IntPtr.Zero;
            case WM_MOVE when topLevel && surface!.Kind == SurfaceKind.Window:
                SyncClient(surface);
                break;
            case WM_SIZE when topLevel && surface!.Kind == SurfaceKind.Window && (long)wParam != SIZE_MINIMIZED:
                // Po przywróceniu z paska zadań: bieżące położenie i świeży obraz.
                SyncClient(surface);
                RequestKeyframe(surface.Id);
                break;
            case WM_ERASEBKGND:
                return 1;
            case WM_SYSCOMMAND when ((long)wParam & 0xFFF0) == SC_KEYMENU:
                // Alt nie otwiera menu systemowego – idzie do Maca jako Option.
                return IntPtr.Zero;
            case WM_CLOSE:
                if (topLevel && surface!.Kind == SurfaceKind.Window) CloseRequested?.Invoke(surface.Id);
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
        // Klasy wskazują na procedury tej instancji – nie mogą przeżyć okien.
        UnregisterClassW(ClassName, GetModuleHandle(null));
        UnregisterClassW(HeaderClassName, GetModuleHandle(null));
    }

    public void Dispose()
    {
        Stop();
        _wake.Dispose();
    }
}
