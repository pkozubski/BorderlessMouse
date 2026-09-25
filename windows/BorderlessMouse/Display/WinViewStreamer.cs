using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using BorderlessMouse.Protocol;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static BorderlessMouse.Localization.L10n;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;

namespace BorderlessMouse.Display;

/// <summary>
/// Nagrywa wirtualny monitor Windows (DXGI Desktop Duplication), zamienia obraz na NV12
/// (procesor wideo D3D11, a gdy go brak – procesor CPU), koduje H.264 i wysyła do Maca
/// zaszyfrowanym kanałem (ten sam format rekordów co strumień ekranu Maca, kind 1).
/// Duplication oddaje klatkę tylko po zmianie obrazu, więc nieruchomy pulpit nic nie kosztuje.
/// Działa na własnym wątku.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WinViewStreamer : IDisposable
{
    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _keyframeRequested;
    private TcpClient? _tcp;
    private long _framesSent;
    private long _bytesSent;

    /// <summary>Strumień przerwany (wątek strumienia).</summary>
    public event Action<string>? Failed;

    public long FramesSent => Interlocked.Read(ref _framesSent);
    public long BytesSent => Interlocked.Read(ref _bytesSent);
    /// <summary>true = konwersja kolorów na GPU.</summary>
    public bool UsesGpu { get; private set; }

    /// <summary>Lista okien z chwili nagrania (wątek strumienia); null = klatki bez listy.</summary>
    public Func<byte[]>? WindowsSnapshot { get; set; }

    public void Start(string gdiDeviceName, IPAddress mac, int port, byte[] key, byte[] token)
    {
        Stop();
        if (key.Length != VideoStream.KeyBytes || token.Length != VideoStream.TokenBytes)
            throw new ArgumentException(T("Nieprawidłowe klucze strumienia okien.", "Invalid window stream keys."));
        var keyCopy = key.ToArray();
        var tokenCopy = token.ToArray();
        Interlocked.Exchange(ref _framesSent, 0);
        Interlocked.Exchange(ref _bytesSent, 0);
        _running = true;
        _keyframeRequested = true;
        _thread = new Thread(() => Run(gdiDeviceName, mac, port, keyCopy, tokenCopy))
        {
            IsBackground = true,
            Name = "blm-winview-tx",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    public void RequestKeyframe() => _keyframeRequested = true;

    public void Stop()
    {
        _running = false;
        try { _tcp?.Close(); } catch { /* ignore */ }
        var thread = _thread;
        if (thread is not null && thread != Thread.CurrentThread) thread.Join(TimeSpan.FromSeconds(3));
        _thread = null;
    }

    public void Dispose() => Stop();

    private void Run(string deviceName, IPAddress mac, int port, byte[] key, byte[] token)
    {
        string? failure = null;
        Capture? capture = null;
        H264Encoder? encoder = null;
        try
        {
            using var tcp = new TcpClient(mac.AddressFamily) { NoDelay = true, SendBufferSize = 4 * 1024 * 1024 };
            _tcp = tcp;
            if (!tcp.ConnectAsync(mac, port).Wait(TimeSpan.FromSeconds(4)))
                throw new IOException(T("Mac nie przyjął połączenia strumienia okien.", "The Mac did not accept the window stream connection."));
            tcp.Client.SendTimeout = 5000;
            using var stream = tcp.GetStream();
            stream.Write(token);
            using var sealer = new VideoStream.Sealer(key);
            capture = new Capture(deviceName);
            UsesGpu = capture.UsesGpu;
            var clock = Stopwatch.StartNew();
            while (_running)
            {
                var fresh = capture.Next(100);
                if (!_running) break;
                // Lista okien z tej samej chwili co obraz (zaraz po klatce z duplication).
                var windows = fresh || _keyframeRequested ? WindowsSnapshot?.Invoke() : null;
                if (!fresh && !(_keyframeRequested && capture.HasFrame)) continue;
                if (encoder is null || encoder.Width != capture.Width || encoder.Height != capture.Height)
                {
                    encoder?.Dispose();
                    encoder = new H264Encoder(capture.Width, capture.Height);
                    _keyframeRequested = true;
                }
                var now = clock.Elapsed;
                var force = _keyframeRequested;
                _keyframeRequested = false;
                if (encoder.Encode(capture.Nv12, now.Ticks, force) is not { } encoded) continue;
                var clear = VideoStream.EncodeFrame(encoded.Keyframe, capture.Width, capture.Height, (ulong)(now.Ticks / 10), encoded.AnnexB, windows);
                var record = sealer.Seal(clear);
                stream.Write(record);
                Interlocked.Increment(ref _framesSent);
                Interlocked.Add(ref _bytesSent, record.Length);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or AggregateException
                                       or SharpGenException or InvalidOperationException or COMException)
        {
            if (_running) failure = T("Strumień okien: ", "Window stream: ") + (ex.InnerException ?? ex).Message;
        }
        catch (Exception ex)
        {
            // Nieoczekiwany błąd kończy strumień (z opisem w Dzienniku), a nie całą aplikację.
            CrashLog.Write(ex);
            if (_running) failure = T("Strumień okien – nieoczekiwany błąd: ", "Window stream — unexpected error: ") + ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            encoder?.Dispose();
            capture?.Dispose();
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(token);
            _tcp = null;
        }
        if (_running)
        {
            _running = false;
            Failed?.Invoke(failure ?? T("Strumień okien zakończony.", "The window stream ended."));
        }
    }

    /// <summary>DXGI Desktop Duplication jednego monitora + konwersja do NV12 w pamięci.</summary>
    private sealed class Capture : IDisposable
    {
        private readonly string _deviceName;
        private ID3D11Device? _device;
        private ID3D11DeviceContext? _context;
        private IDXGIOutput1? _output;
        private IDXGIOutputDuplication? _duplication;
        private int _sourceWidth, _sourceHeight;
        private ID3D11Texture2D? _copy;      // kopia klatki (BGRA)
        private ID3D11Texture2D? _staging;   // NV12 albo BGRA do odczytu przez CPU
        private ID3D11Texture2D? _nv12;
        private ID3D11VideoDevice? _videoDevice;
        private ID3D11VideoContext? _videoContext;
        private ID3D11VideoProcessorEnumerator? _enumerator;
        private ID3D11VideoProcessor? _processor;
        private ID3D11VideoProcessorInputView? _inputView;
        private ID3D11VideoProcessorOutputView? _outputView;
        private bool _gpuFailed;
        private DateTime _lostSince = DateTime.MinValue;

        public int Width { get; private set; }
        public int Height { get; private set; }
        public byte[] Nv12 { get; private set; } = [];
        public bool HasFrame { get; private set; }
        public bool UsesGpu => !_gpuFailed && _videoDevice is not null;

        public Capture(string deviceName)
        {
            _deviceName = deviceName;
            Open();
        }

        private void Open()
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
            {
                using (adapter)
                {
                    for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                    {
                        using (output)
                        {
                            if (!string.Equals(output.Description.DeviceName, _deviceName, StringComparison.OrdinalIgnoreCase)) continue;
                            var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 };
                            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                                levels, out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
                            _device = device;
                            _context = context;
                            try
                            {
                                _videoDevice = _device.QueryInterface<ID3D11VideoDevice>();
                                _videoContext = _context.QueryInterface<ID3D11VideoContext>();
                            }
                            catch (SharpGenException)
                            {
                                _gpuFailed = true;
                            }
                            _output = output.QueryInterface<IDXGIOutput1>();
                            _duplication = _output.DuplicateOutput(_device);
                            return;
                        }
                    }
                }
            }
            throw new InvalidOperationException(T("Nie znaleziono monitora wirtualnego w DXGI.", "The virtual monitor was not found in DXGI."));
        }

        /// <summary>Czeka na nową klatkę; true = <see cref="Nv12"/> ma nowy obraz.</summary>
        public bool Next(int timeoutMs)
        {
            if (_duplication is null)
            {
                Thread.Sleep(timeoutMs);
                Reopen();
                return false;
            }
            var result = _duplication.AcquireNextFrame((uint)timeoutMs, out var info, out var resource);
            if (result == Vortice.DXGI.ResultCode.WaitTimeout) return false;
            if (result == Vortice.DXGI.ResultCode.AccessLost || result.Failure)
            {
                // Zmiana trybu, ekran blokady (UAC) albo odłączenie – próbujemy od nowa.
                resource?.Dispose();
                if (_lostSince == DateTime.MinValue) _lostSince = DateTime.UtcNow;
                if (DateTime.UtcNow - _lostSince > TimeSpan.FromSeconds(10))
                    throw new InvalidOperationException(T("Utracono dostęp do obrazu monitora wirtualnego.", "Lost access to the virtual monitor image."));
                _duplication.Dispose();
                _duplication = null;
                return false;
            }
            _lostSince = DateTime.MinValue;
            try
            {
                // Sam ruch kursora (LastPresentTime == 0) nie zmienia obrazu.
                if (info.LastPresentTime == 0 && HasFrame) return false;
                using var texture = resource!.QueryInterface<ID3D11Texture2D>();
                var description = texture.Description;
                EnsureResources((int)description.Width, (int)description.Height, description.Format);
                _context!.CopyResource(_copy!, texture);
            }
            finally
            {
                resource?.Dispose();
                _duplication.ReleaseFrame();
            }
            Convert();
            HasFrame = true;
            return true;
        }

        private void Reopen()
        {
            try
            {
                _duplication = _output!.DuplicateOutput(_device!);
            }
            catch (SharpGenException)
            {
                if (_lostSince != DateTime.MinValue && DateTime.UtcNow - _lostSince > TimeSpan.FromSeconds(10))
                    throw new InvalidOperationException(T("Utracono dostęp do obrazu monitora wirtualnego.", "Lost access to the virtual monitor image."));
            }
        }

        private void EnsureResources(int width, int height, Format format)
        {
            if (_copy is not null && _sourceWidth == width && _sourceHeight == height) return;
            DisposeResources();
            _sourceWidth = width;
            _sourceHeight = height;
            // H.264 wymaga parzystych wymiarów – ostatni nieparzysty wiersz/kolumnę pomijamy.
            Width = Math.Max(16, width & ~1);
            Height = Math.Max(16, height & ~1);
            Nv12 = new byte[Width * Height * 3 / 2];
            HasFrame = false;
            _copy = _device!.CreateTexture2D(new Texture2DDescription(format, (uint)width, (uint)height, 1, 1,
                BindFlags.RenderTarget | BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None));
            if (!_gpuFailed)
            {
                try
                {
                    CreateProcessor(width, height);
                    return;
                }
                catch (Exception ex) when (ex is SharpGenException or InvalidOperationException)
                {
                    DisposeProcessor();
                    _gpuFailed = true;
                }
            }
            _staging = _device.CreateTexture2D(new Texture2DDescription(format, (uint)width, (uint)height, 1, 1,
                BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read, 1, 0, ResourceOptionFlags.None));
        }

        private void CreateProcessor(int width, int height)
        {
            var content = new VideoProcessorContentDescription
            {
                InputFrameFormat = VideoFrameFormat.Progressive,
                InputWidth = (uint)width,
                InputHeight = (uint)height,
                OutputWidth = (uint)Width,
                OutputHeight = (uint)Height,
                InputFrameRate = new Rational(60, 1),
                OutputFrameRate = new Rational(60, 1),
                Usage = VideoUsage.PlaybackNormal,
            };
            _enumerator = _videoDevice!.CreateVideoProcessorEnumerator(content);
            if ((_enumerator.CheckVideoProcessorFormat(Format.NV12) & VideoProcessorFormatSupport.Output) == 0)
                throw new InvalidOperationException("NV12 output is not supported by the video processor.");
            _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);
            // Wejście: pełny zakres RGB; wyjście: BT.709, zakres wideo 16–235 (tak jak czyta Mac).
            _videoContext!.VideoProcessorSetStreamColorSpace(_processor, 0, new VideoProcessorColorSpace { RGB_Range = 0 });
            _videoContext.VideoProcessorSetOutputColorSpace(_processor, new VideoProcessorColorSpace
            {
                YCbCr_Matrix = 1,
                Nominal_Range = (uint)VideoProcessorNominalRange.Range_16_235,
            });
            _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
            _videoContext.VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false);
            _nv12 = _device!.CreateTexture2D(new Texture2DDescription(Format.NV12, (uint)Width, (uint)Height, 1, 1,
                BindFlags.RenderTarget, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None));
            _staging = _device.CreateTexture2D(new Texture2DDescription(Format.NV12, (uint)Width, (uint)Height, 1, 1,
                BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read, 1, 0, ResourceOptionFlags.None));
            _inputView = _videoDevice.CreateVideoProcessorInputView(_copy!, _enumerator, new VideoProcessorInputViewDescription
            {
                FourCC = 0,
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
            });
            _outputView = _videoDevice.CreateVideoProcessorOutputView(_nv12, _enumerator, new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
            });
            _videoContext.VideoProcessorSetStreamSourceRect(_processor, 0, true, new Vortice.RawRect(0, 0, Width, Height));
            _videoContext.VideoProcessorSetStreamDestRect(_processor, 0, true, new Vortice.RawRect(0, 0, Width, Height));
            _videoContext.VideoProcessorSetOutputTargetRect(_processor, true, new Vortice.RawRect(0, 0, Width, Height));
        }

        private void Convert()
        {
            if (_processor is not null)
            {
                var streams = new[] { new VideoProcessorStream { Enable = true, InputSurface = _inputView! } };
                _videoContext!.VideoProcessorBlt(_processor, _outputView!, 0, 1, streams).CheckError();
                _context!.CopyResource(_staging!, _nv12!);
                var mapped = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    unsafe
                    {
                        var source = (byte*)mapped.DataPointer;
                        var pitch = (int)mapped.RowPitch;
                        fixed (byte* target = Nv12)
                        {
                            for (var y = 0; y < Height; y++)
                                Buffer.MemoryCopy(source + (long)y * pitch, target + (long)y * Width, Width, Width);
                            var chroma = source + (long)pitch * Height;
                            var chromaTarget = target + (long)Width * Height;
                            for (var y = 0; y < Height / 2; y++)
                                Buffer.MemoryCopy(chroma + (long)y * pitch, chromaTarget + (long)y * Width, Width, Width);
                        }
                    }
                }
                finally
                {
                    _context.Unmap(_staging!, 0);
                }
                return;
            }
            _context!.CopyResource(_staging!, _copy!);
            var bgra = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                unsafe { Nv12Converter.FromBgra((byte*)bgra.DataPointer, (int)bgra.RowPitch, Width, Height, Nv12); }
            }
            finally
            {
                _context.Unmap(_staging!, 0);
            }
        }

        private void DisposeProcessor()
        {
            _inputView?.Dispose(); _inputView = null;
            _outputView?.Dispose(); _outputView = null;
            _processor?.Dispose(); _processor = null;
            _enumerator?.Dispose(); _enumerator = null;
            _nv12?.Dispose(); _nv12 = null;
        }

        private void DisposeResources()
        {
            DisposeProcessor();
            _staging?.Dispose(); _staging = null;
            _copy?.Dispose(); _copy = null;
        }

        public void Dispose()
        {
            DisposeResources();
            _duplication?.Dispose();
            _output?.Dispose();
            _videoContext?.Dispose();
            _videoDevice?.Dispose();
            _context?.Dispose();
            _device?.Dispose();
        }
    }
}

/// <summary>BGRA (pełny zakres) → NV12 BT.709, zakres wideo 16–235, na CPU.</summary>
public static class Nv12Converter
{
    public static unsafe void FromBgra(byte[] source, int pitch, int width, int height, byte[] nv12)
    {
        if (source.Length < pitch * height || nv12.Length < width * height * 3 / 2 || width % 2 != 0 || height % 2 != 0)
            throw new ArgumentException("Invalid BGRA or NV12 buffer size.");
        fixed (byte* pointer = source) FromBgra(pointer, pitch, width, height, nv12);
    }

    public static unsafe void FromBgra(byte* source, int pitch, int width, int height, byte[] nv12)
    {
        fixed (byte* target = nv12)
        {
            var chroma = target + (long)width * height;
            for (var y = 0; y < height; y++)
            {
                var row = source + (long)y * pitch;
                var luma = target + (long)y * width;
                for (var x = 0; x < width; x++)
                {
                    int b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2];
                    luma[x] = (byte)(16 + ((47 * r + 157 * g + 16 * b + 128) >> 8));
                }
                if ((y & 1) != 0) continue;
                var next = y + 1 < height ? row + pitch : row;
                var uv = chroma + (long)(y / 2) * width;
                for (var x = 0; x < width; x += 2)
                {
                    // Średnia z bloku 2×2.
                    int b = row[x * 4] + row[x * 4 + 4] + next[x * 4] + next[x * 4 + 4];
                    int g = row[x * 4 + 1] + row[x * 4 + 5] + next[x * 4 + 1] + next[x * 4 + 5];
                    int r = row[x * 4 + 2] + row[x * 4 + 6] + next[x * 4 + 2] + next[x * 4 + 6];
                    uv[x] = (byte)Math.Clamp(128 + ((-26 * r - 86 * g + 112 * b + 512) >> 10), 16, 240);
                    uv[x + 1] = (byte)Math.Clamp(128 + ((112 * r - 102 * g - 10 * b + 512) >> 10), 16, 240);
                }
            }
        }
    }
}
