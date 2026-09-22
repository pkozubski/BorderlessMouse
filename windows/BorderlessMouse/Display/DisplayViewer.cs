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
/// Pełnoekranowe okno z obrazem ekranu wirtualnego Maca. Dekodowanie (Media
/// Foundation), konwersja NV12 → RGB (procesor wideo D3D11) i wyświetlanie
/// (łańcuch wymiany DXGI) działają na jednym, własnym wątku z pętlą komunikatów.
/// Okno nigdy nie przejmuje fokusu ani kursora – wejście i tak idzie do Maca.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DisplayViewer : IDisposable
{
    private const string ClassName = "BorderlessMouseDisplay";
    /// <summary>Tyle klatek w kolejce oznacza, że dekoder nie nadąża – odrzucamy i prosimy o klatkę kluczową.</summary>
    private const int MaxQueuedFrames = 8;

    private readonly ConcurrentQueue<VideoStream.VideoFrame> _frames = new();
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly WndProcDelegate _wndProc;
    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _awaitingKeyframe = true;
    private long _framesPresented;
    private long _framesDecoded;
    private long _lastKeyframeRequestTicks;

    // tylko wątek wyświetlania
    private IntPtr _hwnd;
    private RECT _monitor;
    private bool _wantVisible;
    private bool _visible;
    private bool _hasPicture;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11VideoDevice? _videoDevice;
    private ID3D11VideoContext? _videoContext;
    private IDXGISwapChain1? _swapChain;
    private H264Decoder? _decoder;
    private int _decoderWidth, _decoderHeight;
    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11VideoProcessor? _processor;
    private ID3D11VideoProcessorOutputView? _outputView;
    private int _processorWidth, _processorHeight;
    private readonly Dictionary<(IntPtr, uint), ID3D11VideoProcessorInputView> _inputViews = new();
    private ID3D11Texture2D? _uploadStaging;
    private ID3D11Texture2D? _uploadTexture;

    /// <summary>Błąd, po którym podgląd nie działa (wątek wyświetlania).</summary>
    public event Action<string>? Failed;
    /// <summary>Dekoder potrzebuje klatki kluczowej (wątek wyświetlania lub odbiornika).</summary>
    public event Action? KeyframeNeeded;

    public long FramesPresented => Interlocked.Read(ref _framesPresented);
    public long FramesDecoded => Interlocked.Read(ref _framesDecoded);
    public bool UsesGpuDecoding { get; private set; }

    public DisplayViewer()
    {
        _wndProc = WndProc;
    }

    public void Start(RECT monitor)
    {
        Stop();
        _monitor = monitor;
        _running = true;
        _awaitingKeyframe = true;
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
    }

    /// <summary>Wątek odbiornika. Klatki P bez poprzedzającej klatki kluczowej są pomijane.</summary>
    public void Enqueue(VideoStream.VideoFrame frame)
    {
        if (!_running) return;
        if (_awaitingKeyframe)
        {
            if (!frame.IsKeyframe) return;
            _awaitingKeyframe = false;
        }
        if (_frames.Count >= MaxQueuedFrames)
        {
            _frames.Clear();
            RequestKeyframe();
            if (!frame.IsKeyframe) return;
            _awaitingKeyframe = false;
        }
        _frames.Enqueue(frame);
        _wake.Set();
    }

    public void SetVisible(bool visible)
    {
        _commands.Enqueue(() =>
        {
            _wantVisible = visible;
            ApplyVisibility();
        });
        _wake.Set();
    }

    // ---------------- wątek wyświetlania ----------------

    private void Run()
    {
        try
        {
            CreateWindow();
            CreateDevice();
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
        while (_frames.TryDequeue(out var frame))
        {
            var isLast = _frames.IsEmpty;
            try
            {
                EnsureDecoder(frame);
                if (_decoder is null) continue;
                var decoder = _decoder;
                decoder.Decode(frame.Payload, sample =>
                {
                    Interlocked.Increment(ref _framesDecoded);
                    // Przy zaległościach wyświetlamy tylko najnowszą klatkę.
                    if (isLast || _frames.IsEmpty) Present(decoder, sample, frame.Width, frame.Height);
                });
            }
            catch (SharpGenException ex)
            {
                // Uszkodzony strumień albo reset sterownika: nowy dekoder od następnej klatki kluczowej.
                DisposeDecoder();
                _frames.Clear();
                _awaitingKeyframe = true;
                RequestKeyframe();
                if (ex.ResultCode == Vortice.DXGI.ResultCode.DeviceRemoved || ex.ResultCode == Vortice.DXGI.ResultCode.DeviceReset)
                {
                    DisposeGraphics();
                    CreateDevice();
                }
            }
        }
    }

    private void EnsureDecoder(VideoStream.VideoFrame frame)
    {
        if (_decoder is not null && _decoderWidth == frame.Width && _decoderHeight == frame.Height) return;
        // Nowy rozmiar (zmiana trybu na Macu) zawsze zaczyna się od klatki kluczowej.
        if (!frame.IsKeyframe)
        {
            _awaitingKeyframe = true;
            RequestKeyframe();
            return;
        }
        DisposeDecoder();
        _decoder = new H264Decoder(_device, frame.Width, frame.Height);
        _decoderWidth = frame.Width;
        _decoderHeight = frame.Height;
        UsesGpuDecoding = _decoder.UsesGpu;
    }

    private void Present(H264Decoder decoder, IMFSample sample, int frameWidth, int frameHeight)
    {
        if (_videoDevice is null || _videoContext is null || _swapChain is null) return;
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
            texture = Upload(decoder, sample);
            slice = 0;
        }
        using (texture)
        {
            var description = texture.Description;
            EnsureProcessor((int)description.Width, (int)description.Height);
            var key = (texture.NativePointer, slice);
            if (!_inputViews.TryGetValue(key, out var inputView))
            {
                inputView = _videoDevice.CreateVideoProcessorInputView(texture, _enumerator!, new VideoProcessorInputViewDescription
                {
                    FourCC = 0,
                    ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                    Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = slice },
                });
                _inputViews[key] = inputView;
            }
            var width = _monitor.Width;
            var height = _monitor.Height;
            var visibleWidth = Math.Min(frameWidth, (int)description.Width);
            var visibleHeight = Math.Min(frameHeight, (int)description.Height);
            _videoContext.VideoProcessorSetStreamSourceRect(_processor!, 0, true, new RawRect(0, 0, visibleWidth, visibleHeight));
            _videoContext.VideoProcessorSetStreamDestRect(_processor!, 0, true, Letterbox(visibleWidth, visibleHeight, width, height));
            _videoContext.VideoProcessorSetOutputTargetRect(_processor!, true, new RawRect(0, 0, width, height));
            var streams = new[] { new VideoProcessorStream { Enable = true, InputSurface = inputView } };
            _videoContext.VideoProcessorBlt(_processor!, _outputView!, 0, 1, streams).CheckError();
        }
        _swapChain.Present(0, PresentFlags.None).CheckError();
        Interlocked.Increment(ref _framesPresented);
        if (!_hasPicture)
        {
            _hasPicture = true;
            ApplyVisibility();
        }
    }

    /// <summary>Tryb programowy: NV12 z pamięci → tekstura GPU (przez teksturę staging).</summary>
    private ID3D11Texture2D Upload(H264Decoder decoder, IMFSample sample)
    {
        var width = (uint)decoder.OutputWidth;
        var height = (uint)decoder.OutputHeight;
        if (_uploadTexture is null || _uploadTexture.Description.Width != width || _uploadTexture.Description.Height != height)
        {
            _uploadStaging?.Dispose();
            _uploadTexture?.Dispose();
            foreach (var view in _inputViews.Values) view.Dispose();
            _inputViews.Clear();
            var staging = new Texture2DDescription(Format.NV12, width, height, 1, 1, BindFlags.None,
                ResourceUsage.Staging, CpuAccessFlags.Write, 1, 0, ResourceOptionFlags.None);
            _uploadStaging = _device!.CreateTexture2D(in staging);
            var target = new Texture2DDescription(Format.NV12, width, height, 1, 1, BindFlags.ShaderResource,
                ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None);
            _uploadTexture = _device.CreateTexture2D(in target);
        }
        var nv12 = decoder.CopyNv12(sample);
        var mapped = _context!.Map(_uploadStaging!, 0, MapMode.Write, Vortice.Direct3D11.MapFlags.None);
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
            _context.Unmap(_uploadStaging!, 0);
        }
        _context.CopyResource(_uploadTexture!, _uploadStaging!);
        // Osobny wrapper z własną referencją: wywołujący zwalnia go jak teksturę dekodera.
        _uploadTexture!.AddRef();
        return new ID3D11Texture2D(_uploadTexture.NativePointer);
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

    private void EnsureProcessor(int inputWidth, int inputHeight)
    {
        if (_processor is not null && _processorWidth == inputWidth && _processorHeight == inputHeight) return;
        DisposeProcessor();
        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)inputWidth,
            InputHeight = (uint)inputHeight,
            OutputWidth = (uint)_monitor.Width,
            OutputHeight = (uint)_monitor.Height,
            InputFrameRate = new Rational(60, 1),
            OutputFrameRate = new Rational(60, 1),
            Usage = VideoUsage.PlaybackNormal,
        };
        _enumerator = _videoDevice!.CreateVideoProcessorEnumerator(content);
        _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);
        // Koder Maca: BT.709, zakres wideo 16–235; wyjście: pełny zakres RGB.
        var input = new VideoProcessorColorSpace { YCbCr_Matrix = 1, Nominal_Range = (uint)VideoProcessorNominalRange.Range_16_235 };
        _videoContext!.VideoProcessorSetStreamColorSpace(_processor, 0, input);
        _videoContext.VideoProcessorSetOutputColorSpace(_processor, new VideoProcessorColorSpace { RGB_Range = 0 });
        _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
        _videoContext.VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false);
        using var backBuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
        _outputView = _videoDevice.CreateVideoProcessorOutputView(backBuffer, _enumerator, new VideoProcessorOutputViewDescription
        {
            ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
        });
        _processorWidth = inputWidth;
        _processorHeight = inputHeight;
    }

    private void CreateWindow()
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
        _hwnd = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE, ClassName, "BorderlessMouse – Mac", WS_POPUP,
            _monitor.Left, _monitor.Top, _monitor.Width, _monitor.Height, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
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
        using var factory = adapter.GetParent<IDXGIFactory2>();
        var description = new SwapChainDescription1((uint)_monitor.Width, (uint)_monitor.Height, Format.B8G8R8A8_UNorm, false,
            Usage.RenderTargetOutput, 2, Scaling.Stretch, SwapEffect.FlipDiscard, AlphaMode.Ignore, SwapChainFlags.None);
        _swapChain = factory.CreateSwapChainForHwnd(_device, _hwnd, description, null, null);
        factory.MakeWindowAssociation(_hwnd, WindowAssociationFlags.IgnoreAll);
    }

    private void ApplyVisibility()
    {
        if (_hwnd == IntPtr.Zero) return;
        // Bez pierwszej klatki okno pokazałoby czarny ekran zamiast pulpitu Windows.
        var show = _wantVisible && _hasPicture;
        if (show == _visible) return;
        _visible = show;
        if (show)
        {
            SetWindowPos(_hwnd, HWND_TOPMOST, _monitor.Left, _monitor.Top, _monitor.Width, _monitor.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
            // Ukryte okno mogło nie zachować obrazu, a statyczny ekran Maca nie wyśle nowej
            // klatki sam z siebie – Mac powtórzy ostatnią klatkę jako kluczową.
            RequestKeyframe();
        }
        else
        {
            ShowWindow(_hwnd, SW_HIDE);
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_SETCURSOR:
                SetCursor(IntPtr.Zero); // kursor Maca jest już w obrazie
                return 1;
            case WM_MOUSEACTIVATE:
                return MA_NOACTIVATE;
            case WM_CLOSE:
                return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void RequestKeyframe()
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastKeyframeRequestTicks);
        if (now - last < 300) return;
        Interlocked.Exchange(ref _lastKeyframeRequestTicks, now);
        KeyframeNeeded?.Invoke();
    }

    private void DisposeDecoder()
    {
        DisposeProcessor();
        _decoder?.Dispose();
        _decoder = null;
        _decoderWidth = _decoderHeight = 0;
    }

    private void DisposeProcessor()
    {
        foreach (var view in _inputViews.Values) view.Dispose();
        _inputViews.Clear();
        _outputView?.Dispose();
        _outputView = null;
        _processor?.Dispose();
        _processor = null;
        _enumerator?.Dispose();
        _enumerator = null;
        _processorWidth = _processorHeight = 0;
    }

    private void DisposeGraphics()
    {
        DisposeDecoder();
        _uploadStaging?.Dispose();
        _uploadStaging = null;
        _uploadTexture?.Dispose();
        _uploadTexture = null;
        _swapChain?.Dispose();
        _swapChain = null;
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
        DisposeGraphics();
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        // Klasa wskazuje na WndProc tej instancji – nie może przeżyć okna.
        UnregisterClassW(ClassName, GetModuleHandle(null));
        _visible = _hasPicture = _wantVisible = false;
    }

    public void Dispose()
    {
        Stop();
        _wake.Dispose();
    }
}
