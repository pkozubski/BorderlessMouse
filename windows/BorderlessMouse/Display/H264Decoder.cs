using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;
using ResultCode = Vortice.MediaFoundation.ResultCode;

namespace BorderlessMouse.Display;

/// <summary>
/// Dekoder H.264 Microsoft Media Foundation (synchroniczny MFT). Z urządzeniem
/// D3D11 dekoduje sprzętowo (DXVA) do tekstur NV12 na GPU; bez niego – albo gdy
/// sterownik odmówi – dekoduje programowo do pamięci (NV12). Nie jest bezpieczny
/// wątkowo: używać z jednego wątku.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class H264Decoder : IDisposable
{
    /// <summary>MF_LOW_LATENCY – dekoder oddaje klatkę od razu, bez kolejki.</summary>
    private static readonly Guid LowLatencyKey = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private const uint InterlaceProgressive = 2;
    private const long FrameDuration = 166_667; // 1/60 s w jednostkach 100 ns

    private readonly IMFTransform _transform;
    private readonly IMFDXGIDeviceManager? _manager;
    private bool _providesSamples;
    private int _outputBufferSize;
    private long _time;
    private bool _disposed;

    /// <summary>true = wyjście to tekstury D3D11 (IMFDXGIBuffer).</summary>
    public bool UsesGpu { get; }
    /// <summary>Rozmiar bufora wyjściowego (może być wyrównany, np. 1088 zamiast 1080).</summary>
    public int OutputWidth { get; private set; }
    public int OutputHeight { get; private set; }
    /// <summary>Odstęp wierszy NV12 w trybie programowym.</summary>
    public int OutputStride { get; private set; }

    public H264Decoder(ID3D11Device? device, int width, int height)
    {
        MediaFactory.MFStartup(true).CheckError();
        try
        {
            _transform = CreateTransform();
            var attributes = _transform.Attributes;
            attributes.Set(LowLatencyKey, 1u);
            if (device is not null && MediaFactory.MFGetAttributeUInt32(attributes, TransformAttributeKeys.D3D11Aware, 0) != 0)
            {
                try
                {
                    _manager = MediaFactory.MFCreateDXGIDeviceManager();
                    _manager.ResetDevice(device).CheckError();
                    _transform.ProcessMessage(TMessageType.MessageSetD3DManager, (UIntPtr)(nuint)_manager.NativePointer);
                    UsesGpu = true;
                }
                catch (SharpGenException)
                {
                    // np. WARP albo sterownik bez DXVA – zostajemy przy dekodowaniu programowym
                    try { _transform.ProcessMessage(TMessageType.MessageSetD3DManager, UIntPtr.Zero); } catch (SharpGenException) { }
                    _manager?.Dispose();
                    _manager = null;
                    UsesGpu = false;
                }
            }

            using var input = MediaFactory.MFCreateMediaType();
            input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            MediaFactory.MFSetAttributeSize(input, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height);
            MediaFactory.MFSetAttributeRatio(input, MediaTypeAttributeKeys.FrameRate, 60, 1);
            input.Set(MediaTypeAttributeKeys.InterlaceMode, InterlaceProgressive);
            _transform.SetInputType(0, input, 0);
            SelectOutputType();
            _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
        }
        catch
        {
            _transform?.Dispose();
            _manager?.Dispose();
            MediaFactory.MFShutdown();
            throw;
        }
    }

    private static IMFTransform CreateTransform()
    {
        var flags = (uint)(EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagLocalmft | EnumFlag.EnumFlagSortandfilter);
        var input = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 };
        using var activates = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoDecoder, flags, input, null);
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
        throw new InvalidOperationException("Brak dekodera H.264 Media Foundation (Windows N bez Media Feature Pack?).");
    }

    private void SelectOutputType()
    {
        for (var index = 0; ; index++)
        {
            IMFMediaType type;
            try
            {
                type = _transform.GetOutputAvailableType(0, index);
            }
            catch (SharpGenException ex) when (ex.ResultCode == ResultCode.NoMoreTypes)
            {
                throw new InvalidOperationException("Dekoder H.264 nie oferuje wyjścia NV12.");
            }
            using (type)
            {
                if (type.GetGUID(MediaTypeAttributeKeys.Subtype) != VideoFormatGuids.NV12) continue;
                _transform.SetOutputType(0, type, 0);
                MediaFactory.MFGetAttributeSize(type, MediaTypeAttributeKeys.FrameSize, out var w, out var h).CheckError();
                OutputWidth = (int)w;
                OutputHeight = (int)h;
                var stride = (int)MediaFactory.MFGetAttributeUInt32(type, MediaTypeAttributeKeys.DefaultStride, w);
                OutputStride = Math.Abs(stride) >= OutputWidth ? Math.Abs(stride) : OutputWidth;
                break;
            }
        }
        var info = _transform.GetOutputStreamInfo(0);
        const int provides = (int)(OutputStreamInfoFlags.OutputStreamProvidesSamples | OutputStreamInfoFlags.OutputStreamCanProvideSamples);
        _providesSamples = (info.Flags & provides) != 0;
        _outputBufferSize = Math.Max(info.Size, OutputStride * OutputHeight * 3 / 2);
    }

    /// <summary>
    /// Dekoduje jedną jednostkę dostępu Annex B. Każda zdekodowana klatka trafia do
    /// <paramref name="onOutput"/>; próbka jest ważna tylko w trakcie wywołania.
    /// </summary>
    public void Decode(ReadOnlySpan<byte> annexB, Action<IMFSample> onOutput)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var buffer = MediaFactory.MFCreateMemoryBuffer(annexB.Length);
        buffer.Lock(out var pointer, out _, out _);
        try
        {
            unsafe { annexB.CopyTo(new Span<byte>((void*)pointer, annexB.Length)); }
        }
        finally
        {
            buffer.Unlock();
        }
        buffer.CurrentLength = annexB.Length;
        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = _time;
        sample.SampleDuration = FrameDuration;
        _time += FrameDuration;
        try
        {
            _transform.ProcessInput(0, sample, 0);
        }
        catch (SharpGenException ex) when (ex.ResultCode == ResultCode.Notaccepting)
        {
            Drain(onOutput);
            _transform.ProcessInput(0, sample, 0);
        }
        Drain(onOutput);
    }

    private void Drain(Action<IMFSample> onOutput)
    {
        for (var guard = 0; guard < 16; guard++)
        {
            IMFSample? own = null;
            if (!_providesSamples)
            {
                own = MediaFactory.MFCreateSample();
                using var outBuffer = MediaFactory.MFCreateMemoryBuffer(_outputBufferSize);
                own.AddBuffer(outBuffer);
            }
            var output = new OutputDataBuffer { StreamID = 0, Sample = own! };
            var result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref output, out _);
            output.Events?.Dispose();
            // Wrapper próbki podanej przez nas nie ma własnej referencji – zwalniamy tylko `own`.
            var produced = own is null ? output.Sample : own;
            try
            {
                if (result == ResultCode.TransformNeedMoreInput) return;
                if (result == ResultCode.TransformStreamChange)
                {
                    SelectOutputType();
                    continue;
                }
                result.CheckError();
                if (produced is not null) onOutput(produced);
            }
            finally
            {
                produced?.Dispose();
            }
        }
    }

    /// <summary>Kopiuje zdekodowane NV12 z pamięci (tryb programowy) do bufora ciągłego.</summary>
    public byte[] CopyNv12(IMFSample sample)
    {
        using var buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out var pointer, out _, out var length);
        try
        {
            var bytes = new byte[length];
            Marshal.Copy(pointer, bytes, 0, length);
            return bytes;
        }
        finally
        {
            buffer.Unlock();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero); } catch (SharpGenException) { }
        try { _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero); } catch (SharpGenException) { }
        if (_manager is not null)
        {
            try { _transform.ProcessMessage(TMessageType.MessageSetD3DManager, UIntPtr.Zero); } catch (SharpGenException) { }
        }
        _transform.Dispose();
        _manager?.Dispose();
        MediaFactory.MFShutdown();
    }
}
