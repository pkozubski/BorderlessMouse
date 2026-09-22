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
/// <item>tryb okien – każde okno Maca ma własne, prawdziwe okno Windows (pasek zadań,
/// Alt+Tab, minimalizacja, kolejność okien) z osobnym strumieniem i dekoderem.</item>
/// </list>
/// Dekodowanie (Media Foundation), konwersja NV12 → RGB (procesor wideo D3D11)
/// i wyświetlanie (łańcuchy wymiany DXGI) działają na jednym wątku z pętlą komunikatów.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DisplayViewer : IDisposable
{
    /// <summary>Okno Maca do pokazania: prostokąt w pikselach ekranu Windows.</summary>
    public sealed record ProxyWindow(uint Id, int Pid, RECT Bounds, bool Popup, string Title);

    /// <summary>Okno Windows reprezentujące okno Maca (do rozpoznawania go w hookach).</summary>
    public readonly record struct ProxyInfo(uint Id, bool Popup);

    private const string ClassName = "BorderlessMouseDisplay";
    private const uint FullscreenId = 0;
    /// <summary>Tyle klatek w kolejce oznacza, że dekoder nie nadąża – odrzucamy i prosimy o klatki kluczowe.</summary>
    private const int MaxQueuedFrames = 48;
    private const uint WM_ACTIVATE = 0x0006;
    private const uint WM_SIZE = 0x0005;
    private const uint WM_ERASEBKGND = 0x0014;
    private const uint WM_SETICON = 0x0080;
    private const uint WM_SYSCOMMAND = 0x0112;
    private const long SC_KEYMENU = 0xF100;
    private const uint WS_SYSMENU = 0x00080000;
    private const uint WS_MINIMIZEBOX = 0x00020000;
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

    /// <summary>Błąd, po którym podgląd nie działa (wątek wyświetlania).</summary>
    public event Action<string>? Failed;
    /// <summary>Strumień potrzebuje klatki kluczowej; null = wszystkie.</summary>
    public event Action<uint?>? KeyframeNeeded;
    /// <summary>Okno Maca aktywowane (true) albo dezaktywowane na Windowsie.</summary>
    public event Action<uint, bool>? WindowActivated;
    /// <summary>Alt+F4, zamknięcie z paska zadań.</summary>
    public event Action<uint>? CloseRequested;

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
        foreach (var window in windows)
        {
            if (!_surfaces.TryGetValue(window.Id, out var surface))
            {
                surface = CreateWindowSurface(window);
                if (surface is null) continue;
            }
            UpdateSurface(surface, window);
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
        // Małe okna są dopełniane do 64 px po stronie Maca (minimum sprzętowego kodera).
        surface.Decoder = new H264Decoder(_device, Math.Max(64, (frame.Width + 1) & ~1), Math.Max(64, (frame.Height + 1) & ~1));
        surface.DecoderWidth = frame.Width;
        surface.DecoderHeight = frame.Height;
        UsesGpuDecoding = surface.Decoder.UsesGpu;
    }

    private void Present(Surface surface, H264Decoder decoder, IMFSample sample, int frameWidth, int frameHeight)
    {
        if (_videoDevice is null || _videoContext is null || surface.SwapChain is null) return;
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
        public RECT Bounds;
        public int Width, Height;
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
            Id = FullscreenId, Kind = SurfaceKind.Fullscreen, Handle = handle, Bounds = _monitor,
            Width = _monitor.Width, Height = _monitor.Height,
        };
        surface.SwapChain = CreateSwapChain(handle, surface.Width, surface.Height);
        Register(surface);
    }

    private Surface? CreateWindowSurface(ProxyWindow window)
    {
        var kind = window.Popup ? SurfaceKind.Popup : SurfaceKind.Window;
        // Zwykłe okno Maca: przycisk na pasku zadań, Alt+Tab, minimalizacja. Menu i podpowiedzi:
        // bez paska zadań, zawsze nad oknami i bez odbierania fokusu.
        var style = kind == SurfaceKind.Window ? WS_POPUP | WS_SYSMENU | WS_MINIMIZEBOX : WS_POPUP;
        var exStyle = kind == SurfaceKind.Window ? WS_EX_APPWINDOW : WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE;
        var b = window.Bounds;
        var handle = CreateWindowExW(exStyle, ClassName, window.Title, style, b.Left, b.Top, Math.Max(1, b.Width), Math.Max(1, b.Height),
            IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (handle == IntPtr.Zero) return null;
        var surface = new Surface
        {
            Id = window.Id, Kind = kind, Handle = handle, Pid = window.Pid, Title = window.Title, Bounds = b,
            Width = Math.Max(1, b.Width), Height = Math.Max(1, b.Height),
        };
        surface.SwapChain = CreateSwapChain(handle, surface.Width, surface.Height);
        Register(surface);
        ApplyIcon(surface);
        // Klatki mogły przyjść przed listą okien – prosimy o pełny obraz.
        RequestKeyframe(surface.Id);
        return surface;
    }

    private void UpdateSurface(Surface surface, ProxyWindow window)
    {
        if (surface.Title != window.Title)
        {
            surface.Title = window.Title;
            SetWindowTextW(surface.Handle, window.Title);
        }
        var b = window.Bounds;
        if (b.Left == surface.Bounds.Left && b.Top == surface.Bounds.Top && b.Width == surface.Bounds.Width && b.Height == surface.Bounds.Height) return;
        surface.Bounds = b;
        // Zminimalizowane okno zostaje na pasku zadań; położenie nadrobimy po przywróceniu.
        if (IsIconic(surface.Handle)) return;
        ApplyBounds(surface);
    }

    private void ApplyBounds(Surface surface)
    {
        var b = surface.Bounds;
        var width = Math.Max(1, b.Width);
        var height = Math.Max(1, b.Height);
        SetWindowPos(surface.Handle, IntPtr.Zero, b.Left, b.Top, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
        if (width == surface.Width && height == surface.Height) return;
        surface.Width = width;
        surface.Height = height;
        DisposeProcessor(surface); // widok wyjścia trzyma bufor łańcucha wymiany
        surface.SwapChain?.ResizeBuffers(2, (uint)width, (uint)height, Format.B8G8R8A8_UNorm).CheckError();
        ApplyShape(surface);
    }

    /// <summary>Zaokrąglone narożniki jak na Macu (promień z przezroczystości okna).</summary>
    private static void ApplyShape(Surface surface)
    {
        if (surface.Kind == SurfaceKind.Fullscreen) return;
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
        var b = surface.Bounds;
        var insertAfter = surface.Kind == SurfaceKind.Window ? IntPtr.Zero : HWND_TOPMOST;
        SetWindowPos(surface.Handle, insertAfter, b.Left, b.Top, surface.Width, surface.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        ShowWindow(surface.Handle, SW_SHOWNOACTIVATE);
        // Ukryte okno mogło nie zachować obrazu, a statyczny obraz nie przyjdzie sam z siebie.
        RequestKeyframe(surface.Id);
    }

    private void ApplyIcon(Surface surface)
    {
        if (surface.Kind != SurfaceKind.Window || !_icons.TryGetValue(surface.Pid, out var icon)) return;
        SendMessageW(surface.Handle, WM_SETICON, 0, icon);
        SendMessageW(surface.Handle, WM_SETICON, 1, icon);
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
            .ToDictionary(s => s.Handle, s => new ProxyInfo(s.Id, s.Kind == SurfaceKind.Popup));
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
        var description = new SwapChainDescription1((uint)width, (uint)height, Format.B8G8R8A8_UNorm, false,
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
            case WM_SETCURSOR:
                // Pełny pulpit: kursor Maca jest w obrazie. Okna Maca: zwykły kursor Windows.
                SetCursor(surface?.Kind == SurfaceKind.Fullscreen ? IntPtr.Zero : LoadCursorW(IntPtr.Zero, IDC_ARROW));
                return 1;
            case WM_MOUSEACTIVATE when surface?.Kind != SurfaceKind.Window:
                return MA_NOACTIVATE;
            case WM_ACTIVATE when surface?.Kind == SurfaceKind.Window:
                WindowActivated?.Invoke(surface.Id, ((long)wParam & 0xFFFF) != 0);
                break;
            case WM_SIZE when surface?.Kind == SurfaceKind.Window && (long)wParam == 0: // SIZE_RESTORED
                // Po przywróceniu z paska zadań: bieżące położenie z Maca i świeży obraz.
                ApplyBounds(surface);
                RequestKeyframe(surface.Id);
                break;
            case WM_ERASEBKGND:
                return 1;
            case WM_SYSCOMMAND when ((long)wParam & 0xFFF0) == SC_KEYMENU:
                // Samo Alt nie może wprowadzać okna w tryb menu – dalsze klawisze idą do Maca.
                return IntPtr.Zero;
            case WM_CLOSE:
                if (surface?.Kind == SurfaceKind.Window) CloseRequested?.Invoke(surface.Id);
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
