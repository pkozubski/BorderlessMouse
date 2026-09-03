using System.Security.Cryptography;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using BorderlessMouse.Input;
using BorderlessMouse.Protocol;
using static BorderlessMouse.Localization.L10n;

namespace BorderlessMouse.Net;

/// <summary>Synchronizuje tekst i obrazy. Dostęp do schowka odbywa się na wątku UI.</summary>
public sealed class ClipboardSync
{
    private static readonly DataFormat<byte[]> PngFormat = DataFormat.CreateBytesPlatformFormat("PNG");
    private readonly IClipboard _clipboard;
    private readonly DispatcherTimer _timer;
    private readonly SemaphoreSlim _access = new(1, 1);
    private readonly Func<uint>? _getSequence;
    private uint _lastSequence;
    private string? _lastFingerprint;
    // OLE może poprosić o bitmapę dopiero przy wklejaniu. Musi żyć do zmiany schowka.
    private Bitmap? _ownedBitmap;
    private IAsyncDataTransfer? _ownedData;

    public event Action<ClipboardContent>? LocalChanged;
    public event Action<string>? Error;
    public bool Enabled { get; set; } = true;

    public ClipboardSync(IClipboard clipboard, Func<uint>? getSequence = null)
    {
        _clipboard = clipboard;
        _getSequence = getSequence ?? (OperatingSystem.IsWindows() ? NativeMethods.GetClipboardSequenceNumber : null);
        _lastSequence = _getSequence?.Invoke() ?? 0;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => _ = PollAsync());
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    internal async Task PollAsync()
    {
        if (!Enabled || !_access.Wait(0)) return;
        try
        {
            var sequence = _getSequence?.Invoke();
            if (sequence.HasValue && sequence.Value == _lastSequence) return;
            // OLE może wyrenderować PNG dopiero teraz i zmienić jego bajty oraz
            // sekwencję. To nadal nasz schowek, więc nie wolno go odsyłać.
            if (_ownedData is not null && ReferenceEquals(await _clipboard.TryGetInProcessDataAsync(), _ownedData))
            {
                _lastSequence = _getSequence?.Invoke() ?? 0;
                return;
            }
            var content = await ReadContentAsync();
            // Nie oznaczaj nowej zmiany jako obsłużonej, jeśli powstała podczas odczytu.
            if (!Enabled || (sequence.HasValue && _getSequence?.Invoke() != sequence)) return;
            if (sequence.HasValue)
            {
                _lastSequence = sequence.Value;
            }
            var fingerprint = content is null ? null : Fingerprint(content);
            var previous = _lastFingerprint;
            _lastFingerprint = fingerprint;
            if (content is not null && fingerprint != previous) LocalChanged?.Invoke(content);
        }
        catch (Exception)
        {
            // Nie zmieniamy numeru sekwencji: zajęty schowek odczytamy przy kolejnym ticku.
        }
        finally { _access.Release(); }
    }

    private async Task<ClipboardContent?> ReadContentAsync()
    {
        using var data = await _clipboard.TryGetDataAsync();
        if (data is null) return null;
        // Zachowaj oryginalne PNG (w tym przezroczystość), jeśli aplikacja je udostępnia.
        if (data.Formats.Contains(PngFormat) && await data.TryGetValueAsync(PngFormat) is { } png)
        {
            var content = ClipboardContent.Create(ClipboardFormat.Png, png);
            if (content is null) Error?.Invoke(T("Nieobsługiwany obraz lub przekroczony limit 32 MiB / 64 megapikseli.", "Unsupported image or the 32 MiB / 64 megapixel limit was exceeded."));
            return content;
        }
        var bitmap = await data.TryGetBitmapAsync();
        if (bitmap is not null)
        {
            try
            {
                if ((long)bitmap.PixelSize.Width * bitmap.PixelSize.Height > ProtocolConstants.MaxClipboardImagePixels)
                {
                    Error?.Invoke("Obraz przekracza limit 64 megapikseli.");
                    return null;
                }
                var content = await Task.Run(() =>
                {
                    using var stream = new MemoryStream();
                    bitmap.Save(stream); // Avalonia zapisuje bitmapę jako PNG.
                    return ClipboardContent.Create(ClipboardFormat.Png, stream.GetBuffer().AsSpan(0, (int)stream.Length));
                });
                if (content is null) Error?.Invoke("Obraz przekracza limit 32 MiB.");
                return content;
            }
            finally
            {
                if (!ReferenceEquals(bitmap, _ownedBitmap)) bitmap.Dispose();
            }
        }
        // Obraz ma pierwszeństwo przed tekstem/URL-em kopiowanym wraz z nim.
        var text = await data.TryGetTextAsync();
        return text is null ? null : ClipboardContent.FromText(text);
    }

    /// <summary>Zwraca sukces dopiero po umieszczeniu danych w schowku.</summary>
    public async Task<bool> ApplyAsync(ClipboardContent content)
    {
        await _access.WaitAsync();
        Bitmap? bitmap = null;
        try
        {
            if (!Enabled) return false;
            var item = new DataTransferItem();
            if (content.Format == ClipboardFormat.Png)
            {
                var png = content.Data.ToArray();
                bitmap = await Task.Run(() =>
                {
                    using var stream = new MemoryStream(png, writable: false);
                    return new Bitmap(stream);
                });
                if (!Enabled) return false;
                item.Set(PngFormat, png);
                item.SetBitmap(bitmap); // Natywny format bitmapy dla Painta i innych aplikacji Windows.
            }
            else
            {
                item.SetText(content.Text);
            }
            var transfer = new DataTransfer();
            transfer.Add(item);
            await _clipboard.SetDataAsync(transfer);
            _ownedData = transfer;
            _ownedBitmap?.Dispose();
            _ownedBitmap = bitmap;
            bitmap = null;
            _lastFingerprint = Fingerprint(content);
            _lastSequence = _getSequence?.Invoke() ?? 0;
            return true;
        }
        catch (Exception)
        {
            Error?.Invoke(T("Nie udało się zapisać schowka z Maca. Spróbuj skopiować ponownie.", "Could not write the Mac clipboard content. Copy it again."));
            return false;
        }
        finally
        {
            bitmap?.Dispose();
            _access.Release();
        }
    }

    private static string Fingerprint(ClipboardContent content) =>
        $"{(byte)content.Format}:{Convert.ToHexString(SHA256.HashData(content.Data.Span))}";
}
