# WASAPI Fix Notes

## Problem

The ShaderToy plugin had a raw `WasapiLoopback` implementation, but `AudioCapture` was avoiding it and using WinMM because the WASAPI path was observed to produce empty or null audio frames.

The normal Artemis audio plugin works with NAudio on the same machine, so this pointed to an implementation issue in ShaderToy rather than a driver problem.

## Root Cause

The previous ShaderToy WASAPI code created COM and WASAPI objects on the caller thread inside `WasapiLoopback.Start()`, then used `IAudioCaptureClient` from a separate worker thread in `CaptureLoop()`.

That meant:

- `IMMDeviceEnumerator`, `IMMDevice`, `IAudioClient`, and `IAudioCaptureClient` were created on one thread
- polling and buffer reads happened on another thread
- COM was not explicitly initialized on the worker thread

For raw COM interop this is unsafe and is the most likely reason the capture loop ended up with empty packets or non-functional reads.

## Changes Made

### Debug logging added

- `WasapiLoopback` now logs:
  - selected render endpoint
  - WASAPI mix format tag/subformat/rate/channels/bits/block align
- `AudioCapture` now logs:
  - requested source/backend/gate level on start
  - selected render endpoint ID used by WASAPI

Hot-path packet, buffer, texture-fill, and per-callback audio logs were removed after the WASAPI format bug was confirmed because they can create very large logs and slow the preview over time.

### Preset UI fixes

- The preset dropdown now re-selects the saved preset after `Save`.
- Initial settings load now infers the selected preset from the current shader definition title when possible.
- Added a `New` preset button that clears the current preset selection/name without clearing the shader.
- The preset name text box now updates the view model while typing, so clicking `Save` uses the visible name immediately.

### Stereo audio texture mode

- Added a `Stereo texture` checkbox to the settings UI.
- Default is off for compatibility with existing mono shaders.
- Mono/default mode writes:
  - `R = mono`
  - `G = 0`
  - `B = mono`
  - `A = 255`
- Stereo mode writes:
  - `R = left`
  - `G = right`
  - `B = mono average`
  - `A = 255`
- Both WASAPI render capture and Stereo Mix / WinMM now pass left/right sample buffers into `AudioCapture`; mono average is produced at the texture stage.

### Manual source selection

- Added an explicit audio source setting in the ShaderToy properties/UI:
  - `Render loopback`
  - `Stereo Mix / WinMM`
- Changed the default to `Render loopback`.
- Added an explicit render-device picker for render loopback mode:
  - `Default render device`
  - every active render endpoint returned by WASAPI device enumeration
- Removed implicit Stereo Mix selection from the WASAPI loopback path so render loopback now stays on the actual playback endpoint.
- Updated WinMM Stereo Mix mode so it throws if no Stereo Mix device exists, instead of silently falling back to the default recording device.
- There is no automatic fallback between these modes anymore. The selected source is the source used.

### `WasapiLoopback.cs`

- Fixed the root WASAPI decode bug found from the workshop `shader_debug.log`:
  - `WAVEFORMATEX` / `WAVEFORMATEXTENSIBLE` were using default .NET struct packing
  - default packing padded `WAVEFORMATEX` to 20 bytes, but the Windows layout is 18 bytes
  - that shifted `WAVEFORMATEXTENSIBLE.SubFormat`, so the plugin read garbage GUIDs such as `00100000-0080-aa00-...`
  - the endpoint's 32-bit float mix format was therefore misclassified as 32-bit integer PCM
  - float sample bytes interpreted as int32 produced large fake peaks/RMS, which made silence look like real audio
- Added `Pack = 2` to both structures so `SubFormat` is read from the correct offset and IEEE float playback audio is decoded as float.
- Reworked startup so the dedicated WASAPI thread is responsible for:
  - calling `CoInitializeEx(..., COINIT_MULTITHREADED)`
  - enumerating devices
  - activating `IAudioClient`
  - obtaining `IAudioCaptureClient`
  - starting the audio client
  - polling packets and reading buffers
  - releasing COM objects during teardown
- Added synchronous startup coordination with `ManualResetEventSlim` so `Start()` now waits for initialization to either succeed or fail.
- Added startup exception propagation so callers can log the exact WASAPI initialization failure.
- Changed default endpoint selection to use the multimedia render role to match the known-good NAudio plugin behavior, instead of binding the console-role render endpoint.
- Expanded sample decoding so the raw WASAPI path now handles more shared-mode PCM formats instead of only accepting float32 and int16.
- Render loopback mode now stays on the multimedia render endpoint instead of auto-switching to capture devices.
- Added explicit endpoint selection by persisted WASAPI endpoint ID.
- Added a clearer unsupported-format log when the mix format is not one of the formats we currently decode.

### `AudioCapture.cs`

- Snapshot the requested source/device before starting the background capture thread.
  - This prevents rapid settings changes from starting one backend while logging or marking another source as active.
  - It also removes confusing lines like `started StereoMix capture for source=RenderLoopback`.
- Changed capture ownership from `WinmmCapture` only to a generic `IDisposable` backend.
- Added backend selection that now:
  - uses `WasapiLoopback` for render loopback mode
  - uses `WinmmCapture` for Stereo Mix / WinMM mode
- Removed the WinMM `WAVE_MAPPER` escape hatch entirely, so Stereo Mix / WinMM mode cannot accidentally open the default recording device.
- render loopback no longer silently falls back to WinMM, so it cannot accidentally capture some other recording source
- capture restarts when either the source mode or selected render endpoint changes
- Added backend name tracking so logs show whether WASAPI or WinMM is active.
- Track the active backend sample rate and use it for FFT bin mapping instead of assuming `44100 Hz`.
  - The workshop log showed WASAPI using `48000 Hz`, so the old fixed bin width made the spectrum mapping wrong even after samples were decoded correctly.
- Added a simple time-domain noise gate derived from the configured dB floor so low-level capture noise is written as silence instead of constantly energizing the FFT.
- Kept the preview restart/watchdog behavior that was already added:
  - clears stale preview audio state after inactivity
  - restarts the current backend if frames stop arriving for too long
- Extended the same `EnsureActive(...)` watchdog to the normal render path, not just the preview path, so stale audio data is cleared/restarted consistently when playback stops.

## Current Behavior

- Preview and renderer now default to render loopback.
- Stereo Mix is only used when explicitly selected.
- If render loopback WASAPI cannot initialize, ShaderToy now fails closed instead of silently switching to another capture source.
- The log file should now make it much easier to tell which backend actually started.
- The render-device setting is only used by WASAPI render loopback. Stereo Mix / WinMM mode does not use it.

## Files Changed

- `AudioCapture.cs`
- `AudioDeviceOption.cs`
- `WasapiLoopback.cs`
- `LayerBrushes/ShaderToyLayerBrush.cs`
- `LayerBrushes/PropertyGroups/ShaderToyShaderProperties.cs`
- `Screens/ShaderPropertiesViewModel.cs`
- `Screens/ShaderPropertiesView.axaml`

## Recommended Verification

1. Open ShaderToy plugin settings with a shader that uses the audio channel.
2. Confirm the preview reacts to active system audio.
3. Stop and resume audio playback and confirm the preview resumes updating.
4. Check `shader_debug.log` to confirm whether `WASAPI` or `WinMM` was used.
5. Confirm the log line `WasapiLoopback: render endpoint = ... explicitDevice=...` matches the selected render device.
6. If silence still produces input, inspect `WasapiLoopback.Packet`, `WasapiLoopback.Deliver`, and `AudioCapture.OnData` lines to see whether Windows is delivering non-silent loopback packets or whether the plugin is energizing the texture after gating.
