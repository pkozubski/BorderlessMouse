using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using BorderlessMouse.Net;
using BorderlessMouse.Protocol;

internal static class Program
{
    private static readonly DataFormat<byte[]> PngFormat = DataFormat.CreateBytesPlatformFormat("PNG");

    [STAThread]
    private static void Main()
    {
        AppBuilder.Configure<Application>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia().SetupWithoutStarting();
        using var done = new CancellationTokenSource();
        Dispatcher.UIThread.Post(async () =>
        {
            try { await RunChecks(); }
            catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
            finally { done.Cancel(); }
        });
        Dispatcher.UIThread.MainLoop(done.Token);
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static DataTransfer TextData(string text)
    {
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText(text));
        return transfer;
    }

    private static DataTransfer ImageData(byte[] png, bool includeText = false)
    {
        var item = new DataTransferItem();
        item.Set(PngFormat, png);
        if (includeText) item.SetText("https://example.com/photo");
        var transfer = new DataTransfer();
        transfer.Add(item);
        return transfer;
    }

    private static async Task RunChecks()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "clipboard.json")));
        string Field(string name) => fixture.RootElement.GetProperty(name).GetString()!;
        var png = Convert.FromBase64String(Field("pngBase64"));
        var image = ClipboardContent.Create(ClipboardFormat.Png, png)!;
        var text = ClipboardContent.FromText(Field("text"))!;
        Expect(Convert.ToHexString(Frame.Clipboard(image)).Equals(Field("pngFrameHex"), StringComparison.OrdinalIgnoreCase), "PNG wire compatibility");
        Expect(Convert.ToHexString(Frame.Clipboard(text)).Equals(Field("textFrameHex"), StringComparison.OrdinalIgnoreCase), "UTF-8 wire compatibility");
        Expect(Frame.ParseClipboard(Frame.Clipboard(image).AsSpan(2))!.Data.Span.SequenceEqual(png), "PNG parse");
        Expect(Frame.ParseClipboard([]) is null, "empty payload");
        Expect(Frame.ParseClipboard([2, 1]) is null, "unknown format");
        Expect(Frame.ParseClipboard([0, 0xFF]) is null, "invalid UTF-8");
        Expect(Frame.ParseClipboard([1, 1, 2, 3]) is null, "invalid PNG");
        var maxText = ClipboardContent.FromText(new string('A', ProtocolConstants.MaxClipboardBytes))!;
        Expect(Frame.Clipboard(maxText)[1] == 0xFF, "long text frame");
        Expect(ClipboardContent.FromText(maxText.Text + "A") is null, "text limit");
        var maxPNG = new byte[ProtocolConstants.MaxClipboardImageBytes];
        png.CopyTo(maxPNG, 0);
        var frame = Frame.Clipboard(ClipboardContent.Create(ClipboardFormat.Png, maxPNG)!);
        Expect(frame.Length == ProtocolConstants.MaxLongFrame + 6, "maximum PNG frame");
        Expect(BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(2)) == 32 * 1024 * 1024 + 1, "long frame length includes format byte");
        Expect(ClipboardContent.Create(ClipboardFormat.Png, new byte[ProtocolConstants.MaxClipboardImageBytes + 1]) is null, "PNG byte limit");
        var oversized = (byte[])png.Clone();
        oversized.AsSpan(16, 8).Fill(0xFF);
        Expect(ClipboardContent.Create(ClipboardFormat.Png, oversized) is null, "PNG pixel limit before decode");

        var clipboard = FakeClipboard.Create();
        var sync = new ClipboardSync(clipboard.Clipboard, () => clipboard.Sequence);
        var sent = new List<ClipboardContent>();
        sync.LocalChanged += sent.Add;
        await clipboard.SetDataAsync(TextData(text.Text!));
        await sync.PollAsync();
        Expect(sent.Count == 1 && sent[0].Text == text.Text, "local text");
        await clipboard.SetDataAsync(ImageData(png, includeText: true));
        await sync.PollAsync();
        Expect(sent.Count == 2 && sent[^1].Format == ClipboardFormat.Png, "image takes priority over URL");
        await sync.PollAsync();
        Expect(sent.Count == 2, "unchanged clipboard is not sent twice");
        Expect(await sync.ApplyAsync(image), "apply PNG");
        Expect((await clipboard.Clipboard.TryGetValueAsync(PngFormat))!.AsSpan().SequenceEqual(png), "original PNG bytes are available");
        var received = (await clipboard.Clipboard.TryGetBitmapAsync())!;
        Expect(received.PixelSize == new PixelSize(2, 2), "bitmap preserves pixel dimensions");
        using (var rgba = new WriteableBitmap(new PixelSize(2, 2), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul))
        {
            using var target = rgba.Lock();
            received.CopyPixels(target, AlphaFormat.Unpremul);
            var pixels = new byte[target.RowBytes * 2];
            Marshal.Copy(target.Address, pixels, 0, pixels.Length);
            Expect(pixels[7] == 128 && pixels[target.RowBytes + 7] == 0, "bitmap preserves transparency");
        }
        await sync.PollAsync();
        Expect(sent.Count == 2, "received image is not echoed");
        // Delayed OLE rendering may advance the sequence without changing ownership.
        var readsBeforeRender = clipboard.ReadCount;
        clipboard.Sequence++;
        await sync.PollAsync();
        Expect(clipboard.ReadCount == readsBeforeRender, "our clipboard is not read back after delayed rendering");
        using (var stream = new MemoryStream()) received.Save(stream);
        Expect(sent.Count == 2, "delayed rendering keeps bitmap alive without echo");
        await clipboard.SetDataAsync(TextData(text.Text!));
        await sync.PollAsync();
        Expect(sent.Count == 3 && sent[^1].Text == text.Text, "text-image-text changes are not suppressed");
        await clipboard.ClearAsync();
        await sync.PollAsync();
        await clipboard.SetDataAsync(TextData(text.Text!));
        await sync.PollAsync();
        Expect(sent.Count == 4, "text after an empty clipboard is resent");

        var bitmapData = new DataTransfer();
        bitmapData.Add(DataTransferItem.Create(DataFormat.Bitmap, new Bitmap(new MemoryStream(png))));
        await clipboard.SetDataAsync(bitmapData);
        await sync.PollAsync();
        Expect(sent.Count == 5 && sent[^1].Format == ClipboardFormat.Png, "bitmap-only screenshot converts to PNG");
        Expect(await sync.ApplyAsync(text), "apply text");
        await sync.PollAsync();
        Expect(sent.Count == 5, "received text is not echoed");
        Expect(!await sync.ApplyAsync(ClipboardContent.Create(ClipboardFormat.Png, png.AsSpan(0, 33))!), "truncated PNG is rejected by decoder");
        Expect(await clipboard.Clipboard.TryGetTextAsync() == text.Text, "invalid image leaves clipboard intact");

        await clipboard.SetDataAsync(TextData("retry after lock"));
        clipboard.FailReads = 1;
        await sync.PollAsync();
        Expect(sent.Count == 5, "locked clipboard is not marked as read");
        await sync.PollAsync();
        Expect(sent.Count == 6 && sent[^1].Text == "retry after lock", "retry succeeds without another sequence change");

        await clipboard.SetDataAsync(TextData("old snapshot"));
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clipboard.WaitBeforeRead = releaseRead.Task;
        var poll = sync.PollAsync();
        await clipboard.SetDataAsync(TextData("new snapshot"));
        releaseRead.SetResult();
        await poll;
        Expect(sent.Count == 6, "stale snapshot is discarded");
        await sync.PollAsync();
        Expect(sent.Count == 7 && sent[^1].Text == "new snapshot", "latest clipboard change is not lost");

        await clipboard.SetDataAsync(TextData("local while receiving"));
        releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clipboard.WaitBeforeRead = releaseRead.Task;
        poll = sync.PollAsync();
        var apply = sync.ApplyAsync(image);
        Expect(!apply.IsCompleted, "receiving waits for an active poll");
        releaseRead.SetResult();
        await poll;
        Expect(await apply, "queued image is applied");
        var count = sent.Count;
        await sync.PollAsync();
        Expect(sent.Count == count, "queued receive is not echoed");
        sync.Enabled = false;
        Expect(!await sync.ApplyAsync(text), "disabled synchronization does not modify the clipboard");
        await clipboard.SetDataAsync(TextData("disabled"));
        await sync.PollAsync();
        Expect(sent.Count == count, "disabled synchronization does not send");
        sync.Enabled = true;
        clipboard.FailWrites = 1;
        Expect(!await sync.ApplyAsync(image), "clipboard write failure is reported");
        Expect(await clipboard.Clipboard.TryGetTextAsync() == "disabled", "failed write preserves previous clipboard");
        Expect(await sync.ApplyAsync(image), "retry after write failure");
        Console.WriteLine("✓ Clipboard: shared wire fixtures, size limits, PNG/bitmap, transparency, no echo, locked clipboard and concurrent changes");
    }
}

// Avalonia marks IClipboard as not directly implementable; a proxy supplies test responses.
public class FakeClipboard : DispatchProxy
{
    public IClipboard Clipboard { get; private set; } = null!;
    public static FakeClipboard Create()
    {
        var clipboard = Create<IClipboard, FakeClipboard>();
        var proxy = (FakeClipboard)(object)clipboard;
        proxy.Clipboard = clipboard;
        return proxy;
    }
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    {
        "TryGetDataAsync" => TryGetDataAsync(),
        "SetDataAsync" => SetDataAsync((IAsyncDataTransfer?)args![0]),
        "SetTextAsync" => SetTextAsync((string?)args![0]),
        "ClearAsync" => ClearAsync(),
        "FlushAsync" => Task.CompletedTask,
        "TryGetInProcessDataAsync" => Task.FromResult(_data),
        _ => throw new NotSupportedException(method?.Name),
    };
    private IAsyncDataTransfer? _data;
    public uint Sequence { get; set; }
    public int FailReads { get; set; }
    public int FailWrites { get; set; }
    public int ReadCount { get; private set; }
    public Task? WaitBeforeRead { get; set; }

    public async Task<IAsyncDataTransfer?> TryGetDataAsync()
    {
        ReadCount++;
        if (FailReads > 0) { FailReads--; throw new IOException("Clipboard locked"); }
        var snapshot = _data;
        if (WaitBeforeRead is { } wait) { WaitBeforeRead = null; await wait; }
        return snapshot;
    }
    public Task SetDataAsync(IAsyncDataTransfer? data)
    {
        if (FailWrites > 0) { FailWrites--; throw new IOException("Clipboard locked"); }
        _data = data;
        Sequence++;
        return Task.CompletedTask;
    }
    public Task SetTextAsync(string? text)
    {
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText(text));
        return SetDataAsync(data);
    }
    public Task ClearAsync() => SetDataAsync(null);
}
