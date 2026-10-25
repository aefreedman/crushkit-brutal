using System.Buffers.Binary;
using CrushKit.Brutal.Audio;
using Xunit;

namespace CrushKit.Tests.Audio;

public sealed class AudioTests
{
    private sealed class Wave : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".wav");
        public Wave()
        {
            byte[] data = new byte[46];
            "RIFF"u8.CopyTo(data); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 38);
            "WAVEfmt "u8.CopyTo(data.AsSpan(8)); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 16);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(20), 1); BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(22), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), 8000); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(28), 16000);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(32), 2); BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(34), 16);
            "data"u8.CopyTo(data.AsSpan(36)); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(40), 2);
            File.WriteAllBytes(Path, data);
        }
        public AudioSettings<string> Settings(bool enabled = true, double gain = .5, int voices = 2) =>
            new(enabled, gain, voices, new Dictionary<string, string> { ["ping"] = Path });
        public void Dispose() => File.Delete(Path);
    }
    private sealed class Fake : IAudioOutput<string>
    {
        public readonly HashSet<int> Busy = [];
        public readonly List<(int Voice, string Clip, float Gain)> Played = [];
        public int Stops, Disposals;
        public bool FailDispose;
        public bool Playing(int voice) => Busy.Contains(voice);
        public void Play(int voice, string clip, float gain) { Busy.Add(voice); Played.Add((voice, clip, gain)); }
        public void StopAll() { Busy.Clear(); Stops++; }
        public void Dispose() { Disposals++; StopAll(); if (FailDispose) throw new IOException("dispose"); }
    }
    [Fact]
    public void FirstIdleDropNewThenReuseAndNoDeferredReplay()
    {
        using var wave = new Wave(); var output = new Fake(); var settings = wave.Settings();
        using var player = new VoicePlayer<string>(settings, output);
        Assert.Same(settings, player.Settings);
        Assert.True(player.TryPlay("ping")); Assert.True(player.TryPlay("ping")); Assert.False(player.TryPlay("ping"));
        Assert.Equal(new[] { 0, 1 }, output.Played.Select(p => p.Voice));
        output.Busy.Remove(0); Assert.True(player.TryPlay("ping")); Assert.Equal(0, output.Played[^1].Voice);
        player.SetMuted(true); Assert.Empty(output.Busy); Assert.False(player.TryPlay("ping"));
        player.SetMuted(false); Assert.Equal(3, output.Played.Count);
        Assert.True(player.TryPlay("ping")); Assert.Equal(.5f, output.Played[^1].Gain);
        player.StopAll(); Assert.Empty(output.Busy);
    }
    [Theory]
    [InlineData(false, .5)] [InlineData(true, 0)]
    public void DisabledOrZeroGainDoesNotPlay(bool enabled, double gain)
    {
        using var wave = new Wave(); var output = new Fake();
        using var player = new VoicePlayer<string>(wave.Settings(enabled, gain), output);
        Assert.False(player.TryPlay("ping")); Assert.Empty(output.Played);
    }
    [Fact]
    public void DisabledFactoryNeverOpensDevice()
    {
        using var wave = new Wave();
        using var output = SdlMixerOutput<string>.Create(wave.Settings(false), out var diagnostic);
        Assert.IsType<NullAudioOutput<string>>(output); Assert.Null(diagnostic);
    }
    [Fact]
    public void UnavailableLibraryIsNonfatalWithOneBoundedDiagnostic()
    {
        int attempts = 0;
        using var output = SdlMixerOutput<string>.WithAvailabilityFallback(() =>
        { attempts++; throw new DllNotFoundException(new string('x', 1000)); }, out var diagnostic);
        Assert.IsType<NullAudioOutput<string>>(output); Assert.Equal(1, attempts);
        Assert.NotNull(diagnostic); Assert.InRange(diagnostic.Length, 1, 280);
        output.Play(0, "anything", .5f); Assert.False(output.Playing(0));
    }
    [Fact]
    public void ProgrammingErrorsAreNotSilenced()
    {
        Assert.Throws<ArgumentException>(() => SdlMixerOutput<string>.WithAvailabilityFallback(
            () => throw new ArgumentException("bad"), out _));
    }
    [Fact]
    public void SettingsCopyAssetsAndRejectMissingOrCorruptWave()
    {
        using var wave = new Wave();
        var map = new Dictionary<string, string> { ["ping"] = wave.Path };
        var settings = new AudioSettings<string>(true, .5, 2, map);
        map.Clear(); Assert.Single(settings.Assets);
        Assert.Throws<ArgumentException>(() => new AudioSettings<string>(true, .5, 2, map));
        File.WriteAllBytes(wave.Path, new byte[44]);
        Assert.Throws<InvalidDataException>(() => wave.Settings());
    }
    [Theory]
    [InlineData(-1, 2)] [InlineData(2, 2)] [InlineData(double.NaN, 2)] [InlineData(.5, 0)] [InlineData(.5, 257)]
    public void InvalidTuningRejected(double gain, int voices)
    {
        using var wave = new Wave();
        Assert.Throws<ArgumentOutOfRangeException>(() => wave.Settings(gain: gain, voices: voices));
    }
    [Fact]
    public void UnknownClipWrongThreadAndDisposalAreGuarded()
    {
        using var wave = new Wave(); var output = new Fake();
        var player = new VoicePlayer<string>(wave.Settings(), output);
        Assert.Throws<ArgumentException>(() => player.TryPlay("unknown"));
        Exception? play = null, dispose = null;
        var thread = new Thread(() => { play = Record.Exception(() => player.TryPlay("ping")); dispose = Record.Exception(player.Dispose); });
        thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.IsType<InvalidOperationException>(play); Assert.IsType<InvalidOperationException>(dispose);
        Assert.True(player.TryPlay("ping")); player.Dispose(); player.Dispose();
        Assert.Equal(1, output.Disposals); Assert.Throws<ObjectDisposedException>(() => player.TryPlay("ping"));
    }
    [Fact]
    public void FailedDisposalIsNotRetried()
    {
        using var wave = new Wave(); var output = new Fake { FailDispose = true };
        var player = new VoicePlayer<string>(wave.Settings(), output);
        Assert.Throws<IOException>(player.Dispose); player.Dispose(); Assert.Equal(1, output.Disposals);
    }
    [Fact]
    public void AudioHasNoGameInputOrGraphicsReferences() =>
        Assert.DoesNotContain(typeof(VoicePlayer<>).Assembly.GetReferencedAssemblies(), a =>
            a.Name!.Contains("Breakout") || a.Name.Contains("Vulkan") || a.Name.Contains("ImGui") || a.Name.Contains("Glfw") || a.Name.Contains("CrushKit.Brutal.Input"));
}
