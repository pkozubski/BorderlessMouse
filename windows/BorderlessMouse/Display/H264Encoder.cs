using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharpGen.Runtime;
using Vortice.MediaFoundation;
using ResultCode = Vortice.MediaFoundation.ResultCode;

namespace BorderlessMouse.Display;

/// <summary>
/// Koder H.264 Media Foundation (synchroniczny MFT – programowy koder Microsoftu) dla
/// strumienia okien Windows na Macu. Wejście NV12 w pamięci (BT.709, zakres wideo),
/// wyjście Annex B bez klatek B, z SPS/PPS przed każdą klatką kluczową. Jeden wątek.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class H264Encoder : IDisposable
{
    private const uint InterlaceProgressive = 2;
    private const uint ProfileBaseline = 66;
    private const uint MatrixBt709 = 1, PrimariesBt709 = 2, TransferBt709 = 5, NominalRange16To235 = 2;

    // ICodecAPI (codecapi.h)
    private static readonly Guid LowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private static readonly Guid RateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    private static readonly Guid Quality = new("fcbf57a3-7ea5-4b0c-9644-69b40c39c391");
    private static readonly Guid GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    private static readonly Guid BPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    private static readonly Guid ForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");
    private const uint RateControlQuality = 3;

    private readonly IMFTransform _transform;
    private readonly ICodecAPI? _codec;
    private readonly int _frameBytes;
    private readonly long _frameDuration;
    private int _outputBufferSize;
    private byte[]? _sequenceHeader;
    private bool _disposed;

    public int Width { get; }
    public int Height { get; }

    /// <param name="width">Parzysta szerokość.</param>
    /// <param name="height">Parzysta wysokość.</param>
    public H264Encoder(int width, int height, int framesPerSecond = 60, int bitrateKbps = 12000)
    {
        if (width < 16 || height < 16 || width % 2 != 0 || height % 2 != 0)
            throw new ArgumentException("H.264 needs even frame dimensions of at least 16×16.");
        Width = width;
        Height = height;
        _frameBytes = width * height * 3 / 2;
        _frameDuration = 10_000_000L / framesPerSecond;
        MediaFactory.MFStartup(true).CheckError();
        try
        {
            _transform = CreateTransform();
            try
            {
                _codec = (ICodecAPI)Marshal.GetObjectForIUnknown(_transform.NativePointer);
            }
            catch (InvalidCastException)
            {
                _codec = null;
            }
            // Przed typami: tryb sterowania bitrate'em i brak klatek B.
            SetCodec(RateControlMode, RateControlQuality);
            SetCodec(Quality, 80u);
            SetCodec(BPictureCount, 0u);
            SetCodec(GopSize, (uint)(framesPerSecond * 30));
            SetCodec(LowLatencyMode, true);

            using (var output = MediaFactory.MFCreateMediaType())
            {
                output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                output.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
                output.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrateKbps * 1000);
                output.Set(MediaTypeAttributeKeys.InterlaceMode, InterlaceProgressive);
                output.Set(MediaTypeAttributeKeys.Mpeg2Profile, ProfileBaseline);
                SetColor(output);
                MediaFactory.MFSetAttributeSize(output, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height);
                MediaFactory.MFSetAttributeRatio(output, MediaTypeAttributeKeys.FrameRate, (uint)framesPerSecond, 1);
                MediaFactory.MFSetAttributeRatio(output, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);
                _transform.SetOutputType(0, output, 0);
            }
            using (var input = MediaFactory.MFCreateMediaType())
            {
                input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
                input.Set(MediaTypeAttributeKeys.InterlaceMode, InterlaceProgressive);
                SetColor(input);
                MediaFactory.MFSetAttributeSize(input, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height);
                MediaFactory.MFSetAttributeRatio(input, MediaTypeAttributeKeys.FrameRate, (uint)framesPerSecond, 1);
                MediaFactory.MFSetAttributeRatio(input, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);
                _transform.SetInputType(0, input, 0);
            }
            var info = _transform.GetOutputStreamInfo(0);
            _outputBufferSize = Math.Max(info.Size, _frameBytes);
            _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
        }
        catch
        {
            if (_codec is not null) Marshal.ReleaseComObject(_codec);
            _transform?.Dispose();
            MediaFactory.MFShutdown();
            throw;
        }
    }

    private static void SetColor(IMFMediaType type)
    {
        type.Set(MediaTypeAttributeKeys.YuvMatrix, MatrixBt709);
        type.Set(MediaTypeAttributeKeys.VideoPrimaries, PrimariesBt709);
        type.Set(MediaTypeAttributeKeys.TransferFunction, TransferBt709);
        type.Set(MediaTypeAttributeKeys.VideoNominalRange, NominalRange16To235);
    }

    private static IMFTransform CreateTransform()
    {
        var flags = (uint)(EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagLocalmft | EnumFlag.EnumFlagSortandfilter);
        var input = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 };
        var output = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 };
        using var activates = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, flags, input, output);
        foreach (var activate in activates)
        {
            try
            {
                return activate.ActivateObject<IMFTransform>();
            }
            catch (SharpGenException)
            {
                // następny kandydat
            }
        }
        throw new InvalidOperationException("Brak kodera H.264 Media Foundation (Windows N bez Media Feature Pack?).");
    }

    private void SetCodec(Guid api, object value)
    {
        if (_codec is null) return;
        var key = api;
        _codec.SetValue(ref key, ref value); // nieobsługiwane właściwości koder po prostu odrzuca
    }

    /// <summary>
    /// Koduje jedną klatkę NV12 (<c>Width</c>×<c>Height</c>, wiersze bez odstępów).
    /// Zwraca Annex B i informację, czy to klatka kluczowa, albo null, gdy koder czeka.
    /// </summary>
    public (byte[] AnnexB, bool Keyframe)? Encode(ReadOnlySpan<byte> nv12, long timestamp100ns, bool forceKeyframe)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (nv12.Length < _frameBytes) throw new ArgumentException("NV12 frame too small.", nameof(nv12));
        if (forceKeyframe) SetCodec(ForceKeyFrame, 1u);
        using var buffer = MediaFactory.MFCreateMemoryBuffer(_frameBytes);
        buffer.Lock(out var pointer, out _, out _);
        try
        {
            unsafe { nv12[.._frameBytes].CopyTo(new Span<byte>((void*)pointer, _frameBytes)); }
        }
        finally
        {
            buffer.Unlock();
        }
        buffer.CurrentLength = _frameBytes;
        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = timestamp100ns;
        sample.SampleDuration = _frameDuration;
        _transform.ProcessInput(0, sample, 0);
        return Drain();
    }

    private (byte[] AnnexB, bool Keyframe)? Drain()
    {
        (byte[], bool)? result = null;
        for (var guard = 0; guard < 8; guard++)
        {
            using var own = MediaFactory.MFCreateSample();
            using (var outBuffer = MediaFactory.MFCreateMemoryBuffer(_outputBufferSize)) own.AddBuffer(outBuffer);
            var output = new OutputDataBuffer { StreamID = 0, Sample = own };
            var status = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref output, out _);
            output.Events?.Dispose();
            if (status == ResultCode.TransformNeedMoreInput) break;
            if (status == ResultCode.TransformStreamChange)
            {
                // Koder zmienił typ wyjścia (np. dopiero teraz zna SPS/PPS) – przyjmujemy go.
                using var type = _transform.GetOutputAvailableType(0, 0);
                _transform.SetOutputType(0, type, 0);
                _outputBufferSize = Math.Max(_transform.GetOutputStreamInfo(0).Size, _frameBytes);
                continue;
            }
            status.CheckError();
            var keyframe = own.GetUInt32(SampleAttributeKeys.CleanPoint, out var clean).Success && clean != 0;
            using var contiguous = own.ConvertToContiguousBuffer();
            contiguous.Lock(out var pointer, out _, out var length);
            byte[] data;
            try
            {
                data = new byte[length];
                Marshal.Copy(pointer, data, 0, length);
            }
            finally
            {
                contiguous.Unlock();
            }
            if (keyframe) data = WithParameterSets(data);
            result = result is { } previous ? (Concat(previous.Item1, data), previous.Item2 || keyframe) : (data, keyframe);
        }
        return result;
    }

    /// <summary>Mac tworzy dekoder z SPS/PPS – dokładamy je przed klatką kluczową, jeśli koder ich nie wstawił.</summary>
    private byte[] WithParameterSets(byte[] accessUnit)
    {
        if (ContainsNal(accessUnit, 7)) return accessUnit;
        if (_sequenceHeader is null)
        {
            try
            {
                using var type = _transform.GetOutputCurrentType(0);
                _sequenceHeader = type.GetBlob(MediaTypeAttributeKeys.MpegSequenceHeader);
            }
            catch (SharpGenException)
            {
                return accessUnit;
            }
        }
        return _sequenceHeader is { Length: > 0 } header ? Concat(header, accessUnit) : accessUnit;
    }

    /// <summary>Czy strumień Annex B zawiera jednostkę NAL danego typu.</summary>
    public static bool ContainsNal(ReadOnlySpan<byte> annexB, int type)
    {
        for (var i = 0; i + 3 < annexB.Length; i++)
        {
            if (annexB[i] != 0 || annexB[i + 1] != 0) continue;
            var start = annexB[i + 2] == 1 ? i + 3 : annexB[i + 2] == 0 && annexB[i + 3] == 1 ? i + 4 : -1;
            if (start < 0 || start >= annexB.Length) continue;
            if ((annexB[start] & 0x1F) == type) return true;
            i = start;
        }
        return false;
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var result = new byte[a.Length + b.Length];
        a.CopyTo(result, 0);
        b.CopyTo(result, a.Length);
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero); } catch (SharpGenException) { }
        try { _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero); } catch (SharpGenException) { }
        if (_codec is not null) Marshal.ReleaseComObject(_codec);
        _transform.Dispose();
        MediaFactory.MFShutdown();
    }

    /// <summary>ICodecAPI (strmif.h) – tylko metody używane tutaj, w kolejności z vtable.</summary>
    [ComImport]
    [Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICodecAPI
    {
        [PreserveSig] int IsSupported(ref Guid api);
        [PreserveSig] int IsModifiable(ref Guid api);
        [PreserveSig] int GetParameterRange(ref Guid api, out object valueMin, out object valueMax, out object steppingDelta);
        [PreserveSig] int GetParameterValues(ref Guid api, out IntPtr values, out uint valuesCount);
        [PreserveSig] int GetDefaultValue(ref Guid api, out object value);
        [PreserveSig] int GetValue(ref Guid api, out object value);
        [PreserveSig] int SetValue(ref Guid api, [In] ref object value);
    }
}
