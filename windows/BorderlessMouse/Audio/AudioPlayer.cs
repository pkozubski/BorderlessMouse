using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using static BorderlessMouse.Localization.L10n;

namespace BorderlessMouse.Audio;

public sealed record AudioDeviceInfo(string? Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Odtwarzanie WASAPI (NAudio) z bufora jitter.</summary>
[SupportedOSPlatform("windows")]
public sealed class AudioPlayer : IDisposable
{
    private WasapiOut? _output;
    private MMDevice? _device;

    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;
    public string ActiveDescription { get; private set; } = string.Empty;

    public static IReadOnlyList<AudioDeviceInfo> EnumerateOutputDevices()
    {
        var list = new List<AudioDeviceInfo> { new(null, T("Domyślne urządzenie systemowe", "Default system device")) };
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                list.Add(new AudioDeviceInfo(d.ID, d.FriendlyName));
            }
        }
        catch
        {
            // brak urządzeń / brak COM – zostaje tylko domyślne
        }
        return list;
    }

    /// <summary>Uruchamia odtwarzanie. Zwraca opis użytego trybu.</summary>
    public string Start(string? deviceId, IWaveProvider provider, bool exclusive, int wasapiLatencyMs)
    {
        Stop();
        var enumerator = new MMDeviceEnumerator();
        MMDevice device;
        try
        {
            device = deviceId is null
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : enumerator.GetDevice(deviceId);
        }
        catch
        {
            device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        finally
        {
            enumerator.Dispose();
        }
        _device = device;

        var mode = exclusive ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared;
        try
        {
            _output = new WasapiOut(device, mode, true, wasapiLatencyMs);
            _output.Init(provider);
            _output.Play();
        }
        catch when (exclusive)
        {
            // urządzenie nie wspiera tego formatu w trybie exclusive – wracamy do shared
            _output?.Dispose();
            _output = new WasapiOut(device, AudioClientShareMode.Shared, true, Math.Max(wasapiLatencyMs, 10));
            _output.Init(provider);
            _output.Play();
            mode = AudioClientShareMode.Shared;
        }
        ActiveDescription = $"{device.FriendlyName} · WASAPI {(mode == AudioClientShareMode.Exclusive ? "exclusive" : "shared")} · {wasapiLatencyMs} ms";
        return ActiveDescription;
    }

    public void Stop()
    {
        try { _output?.Stop(); } catch { /* ignore */ }
        _output?.Dispose();
        _output = null;
        _device?.Dispose();
        _device = null;
        ActiveDescription = string.Empty;
    }

    public void Dispose() => Stop();
}
