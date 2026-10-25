# Input and audio

These independent modules do not require Draw2D, Vulkan, ImGui, game simulation, JSON schemas or game assets. Both use the existing published BRUTAL bindings; they do not implement another GLFW binding, image/audio decoder, mixer or OS input backend.

## Input

`GlfwInputPoller(window, keys, mouseButtons)` samples only application-selected keyboard/mouse controls plus one standardized gamepad. The host initializes/terminates GLFW, owns the window, pumps events and decides focus/capture policy. Poll on the creating window/event thread. Samples own immutable copies and may be retained. A selected pad stays selected until unavailable, then IDs 0..15 are scanned. Unfocused/minimized samples are inactive and contain no held controls.

`DeviceSnapshot.Down` reports raw controls; gamepad axes keep GLFW orientation (+Y down), with no implicit bindings, deadzone or Y inversion. Nonfinite raw axes remain raw; `InputMath.TryDirection` safely rejects them. Polling can miss complete press/release or disconnect/reconnect transitions between frames. Raw joystick mappings and hardware acceptance are not promised.

`IInputSource` and `InputPoller` support fake sources without GLFW initialization. Source implementations must provide copied `GamepadSnapshot` data, not mutable native buffers. The poller does not dispose or own the source.

`ButtonEdges.Update(active, sources)` tracks aggregate held state by stable source IDs. Startup, Reset, inactive return and newly appearing held devices baseline rather than firing. Multiple held sources do not double-fire. Applications choose bindings/source IDs and reset on focus/capture transitions; no aim or restart precedence is implied. `InputMath.ValidateDeadzone` rejects nonfinite/out-of-range or float-unrepresentable values; `TryDirection` returns a normalized direction strictly outside that radial threshold without rescaling movement magnitude.

Breakout keeps its existing input schema/router, WASD/arrows, primary/restart/F1 bindings, Y-up aim conversion, source priorities and pointer behavior. It delegates device sampling, edges and normalization to the kit.

## Audio

`AudioSettings<TClip>` takes explicit enabled state, gain, maximum voices and a clip-to-path map. Keys are application-defined (for example strings or an enum); there are no engine cue names. Settings copy the map and validate every file before allocation, even for disabled output. The current intentionally narrow asset contract is canonical mono PCM16 WAV; this is not a claim that SDL.Mixer only supports that format. No runtime synthesis/alternate decoder is introduced.

`SdlMixerOutput<TClip>.Create(settings, out diagnostic)` owns one SDL_mixer session, predecoded clips and fixed tracks. Disabled settings return `NullAudioOutput<TClip>` without initializing SDL or opening a device. Native library/device unavailability returns silent output plus one bounded diagnostic; invalid application configuration is not swallowed. A muted application should choose the null output before calling the native factory. SDL window/global SDL Quit ownership stays outside the module.

`VoicePlayer<TClip>(settings, output)` owns its output and retains the same immutable settings instance. It uses the first idle voice, otherwise drops the new request; it never queues, steals or replays suppressed requests. `SetMuted(true)` stops current feedback; `StopAll` is explicit. The application maps focus, pause, restart and gameplay facts to these operations. Creating-thread ownership and idempotent disposal are enforced by the player/native backend. Do not dispose an owned backend independently or share it across players.

Breakout retains its AudioCue enum, six required assets, terminal cue priority, per-fixed-tick event mapping and focus/mute/restart decisions. The same kit settings object flows from game validation to backend and player.

## Verified published reuse

Exact resolved versions remain in project/lock files, restored from https://api.nuget.org/v3/index.json. A disposable net10.0 compile probe (same SDK pin) restored and built successfully before extraction, without executing native code. It exercised these expressions against published packages:

| Capability | Published package/version | Compile evidence / reuse | Gap or limit |
| --- | --- | --- | --- |
| Keyboard, mouse, focus and standardized gamepad state | Ahwoo.BRUTAL.Glfw 2026.10.5 | `GlfwWindow.GetKey(GlfwKey)`, `GetMouseButton(GlfwMouseButton)`, `CursorPos`, `IsFocused`, `IsIconified`; `Glfw.JoystickIsGamepad(int)`, `Glfw.GetGamepadState(int, out GlfwGamepadState)`, copied Axes/Buttons | Thin ownership/sampling and pure application-facing edge helpers only; no broader BRUTAL action framework absence is asserted |
| Device, clip and track lifecycle | Ahwoo.BRUTAL.Sdl.Mixer 2026.10.3 | `SdlMixer.Init()`, `CreateMixerDevice(uint.MaxValue, null)`; `MixMixer.LoadAudio(path, true)`, `CreateTrack()`; `MixTrack.SetAudio`, `SetGain`, `Play(0)`, `Playing()`, `Stop(0)`, Dispose and `SdlMixer.Quit()` | Reuse published mixer/decoding; kit adds bounded first-idle/drop-new policy and generic clip identity, not another mixer |

The probe restore/build exited 0 with no warnings. This proves the exercised public API shape, not physical device behavior or a complete published-package API inventory. Transitive native/Core/Logging/Numerics and notice dependencies are locked and bundled through package metadata/notice targets. Headless tests use fakes and generated silent WAV fixtures; they never open an audio device or poll physical controllers.

Run `dotnet run --project samples/InputAudio` for an independent headless example and `tools/verify-packages.ps1` for fresh package-only renderer and mechanics consumers. Real audible output/latency, controller hardware and non-Windows runtime acceptance remain unverified.
