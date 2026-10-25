using Brutal.SdlApi.Mixer;
using Brutal.SdlApi;

namespace CrushKit.Brutal.Audio;

/// <summary>One main-thread SDL_mixer owner; no SDL window or global SDL Quit ownership.</summary>
public sealed class SdlMixerOutput<TClip> : IAudioOutput<TClip> where TClip : notnull
{
    private readonly int owner = Environment.CurrentManagedThreadId;
    private readonly List<MixTrack> tracks = [];
    private readonly Dictionary<TClip, MixAudio> clips = [];
    private MixMixer mixer;
    private bool initialized, mixerCreated, disposed;
    private SdlMixerOutput() { }

    /// <summary>Call only in interactive gameplay on the main thread. Native failure is nonfatal;
    /// returns a null output and at most one bounded diagnostic. Settings must already be validated.</summary>
    public static unsafe IAudioOutput<TClip> Create(AudioSettings<TClip> settings, out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(settings);
        diagnostic = null;
        if (!settings.Enabled) return new NullAudioOutput<TClip>();
        return WithAvailabilityFallback(() => CreateNative(settings), out diagnostic);
    }

    private static unsafe IAudioOutput<TClip> CreateNative(AudioSettings<TClip> settings)
    {
        var output = new SdlMixerOutput<TClip>();
        try
        {
            SdlMixer.Init(); output.initialized = true;
            // SDL3 ABI: SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK is ((SDL_AudioDeviceID) 0xFFFFFFFFu).
            output.mixer = SdlMixer.CreateMixerDevice(uint.MaxValue, null); output.mixerCreated = true;
            foreach (var pair in settings.Assets) output.clips.Add(pair.Key, output.mixer.LoadAudio(pair.Value, true));
            for (int i = 0; i < settings.MaxVoices; i++) output.tracks.Add(output.mixer.CreateTrack());
            return output;
        }
        catch { output.Dispose(); throw; }
    }

    internal static IAudioOutput<TClip> WithAvailabilityFallback(Func<IAudioOutput<TClip>> create, out string? diagnostic)
    {
        diagnostic = null;
        try { return create(); }
        catch (Exception error) when (error is SdlException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or TypeInitializationException)
        {
            string message = error.Message.Replace('\r', ' ').Replace('\n', ' ');
            diagnostic = "Audio unavailable; muted: " + message[..Math.Min(message.Length, 240)];
            return new NullAudioOutput<TClip>();
        }
    }
    private void Guard()
    {
        if (owner != Environment.CurrentManagedThreadId) throw new InvalidOperationException("SDL mixer must stay on its creating main thread.");
        ObjectDisposedException.ThrowIf(disposed, this);
    }
    public bool Playing(int voice) { Guard(); return tracks[voice].Playing(); }
    public void Play(int voice, TClip cue, float gain)
    {
        Guard(); var track = tracks[voice]; track.SetAudio(clips[cue]); track.SetGain(gain); track.Play(0);
    }
    public void StopAll() { Guard(); foreach (var track in tracks) track.Stop(0); }
    public void Dispose()
    {
        if (owner != Environment.CurrentManagedThreadId) throw new InvalidOperationException("SDL mixer must stay on its creating main thread.");
        if (disposed) return;
        // Nested finally preserves downstream cleanup if a binding reports a destruction failure.
        try { foreach (var track in tracks) track.Dispose(); }
        finally
        {
            try { foreach (var clip in clips.Values) clip.Dispose(); }
            finally
            {
                try { if (mixerCreated) mixer.Dispose(); }
                finally { if (initialized) SdlMixer.Quit(); disposed = true; }
            }
        }
    }
}
