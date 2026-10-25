namespace CrushKit.Brutal.Audio;

/// <summary>Main-thread, first-idle/drop-new voice policy. Owns output; never queues or steals voices.
/// The application decides when focus/pause should call SetMuted or StopAll.</summary>
public sealed class VoicePlayer<TClip> : IDisposable where TClip : notnull
{
    public AudioSettings<TClip> Settings { get; }
    private readonly IAudioOutput<TClip> output;
    private readonly int owner = Environment.CurrentManagedThreadId;
    private bool muted, disposed;

    public VoicePlayer(AudioSettings<TClip> settings, IAudioOutput<TClip> output)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.output = output ?? throw new ArgumentNullException(nameof(output));
    }
    private void Guard()
    {
        if (Environment.CurrentManagedThreadId != owner) throw new InvalidOperationException("Audio belongs to its creating thread.");
        ObjectDisposedException.ThrowIf(disposed, this);
    }
    public void SetMuted(bool value)
    {
        Guard(); muted = value;
        if (muted) output.StopAll();
    }
    public void StopAll() { Guard(); output.StopAll(); }
    public bool TryPlay(TClip clip)
    {
        Guard();
        if (!Settings.Assets.ContainsKey(clip)) throw new ArgumentException("Unknown clip.", nameof(clip));
        if (!Settings.Enabled || muted || Settings.MasterGain == 0) return false;
        for (int i = 0; i < Settings.MaxVoices; i++)
            if (!output.Playing(i)) { output.Play(i, clip, Settings.MasterGain); return true; }
        return false;
    }
    public void Dispose()
    {
        if (Environment.CurrentManagedThreadId != owner) throw new InvalidOperationException("Audio belongs to its creating thread.");
        if (disposed) return;
        try { output.Dispose(); } finally { disposed = true; }
    }
}
