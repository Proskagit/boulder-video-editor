using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AiVideoEditor.Audio;

/// <summary>
/// The two things <see cref="WasapiAudioOutput"/> needs from the system's audio devices (D024 Step 9.3): which render
/// device is the default now, and an output opened on a given device. Devices are identified by their endpoint id,
/// never by name. Not a device-management API: no enumeration, no notifications.
/// </summary>
internal interface IAudioEndpoints
{
    /// <summary>The endpoint id of the current default render device (multimedia role), or null when there is none.</summary>
    string? DefaultRenderDeviceId();

    /// <summary>A shared-mode, event-driven output on the device with <paramref name="deviceId"/>, not initialised
    /// yet. Throws if the device can't be opened. The returned player also implements <see cref="IWavePosition"/>.</summary>
    IWavePlayer Open(string deviceId, int latencyMilliseconds);
}

/// <summary>The real devices, through WASAPI (NAudio). Called on the thread that controls the output (the UI thread).</summary>
internal sealed class WasapiEndpoints : IAudioEndpoints
{
    public string? DefaultRenderDeviceId()
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
            return null;
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return device.ID;
    }

    public IWavePlayer Open(string deviceId, int latencyMilliseconds)
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDevice(deviceId); // owned by the WasapiOut from here on
        return new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: latencyMilliseconds);
    }
}
