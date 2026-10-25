namespace CrushKit.Brutal.Audio;

/// <summary>Fixed voice backend. Caller owns policy; no callbacks or native handles cross this boundary.</summary>
public interface IAudioOutput<TClip> : IDisposable where TClip : notnull
{
    bool Playing(int voice);
    void Play(int voice, TClip clip, float gain);
    void StopAll();
}

/// <summary>Silent backend; never loads native audio or opens a device.</summary>
public sealed class NullAudioOutput<TClip> : IAudioOutput<TClip> where TClip : notnull
{
    public bool Playing(int voice) => false;
    public void Play(int voice, TClip clip, float gain) { }
    public void StopAll() { }
    public void Dispose() { }
}
