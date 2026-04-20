# Shadertoy Linux Port

## Goal
Run the Shadertoy plugin on Linux with hardware acceleration using native Mesa EGL/GLES instead of ANGLE/D3D11.

## Rendering

### What changed
- `csproj`: TFM `net10.0-windows` → `net10.0`
- `ShaderToyLayerBrushProvider.Enable()`: On Linux, registers a `NativeLibrary.SetDllImportResolver` that maps `libEGL` → `libEGL.so.1` and `libGLESv2` → `libGLESv2.so.2` (system Mesa). Skips ANGLE DLL finding entirely.
- `EglNative.cs`: Added `eglGetDisplay` P/Invoke for the standard EGL display path.
- `GlesContext.Initialize()`: On Linux, uses `eglGetDisplay(EGL_DEFAULT_DISPLAY)` instead of ANGLE's `eglGetPlatformDisplayEXT`. The rest of the init (config, context, pbuffer surface) is identical.

### Linux requirements
- Mesa with EGL and GLES3 support (standard on all modern distros)
- `libEGL.so.1` and `libGLESv2.so.2` present (provided by `libegl1` and `libgles2`)
- On Ubuntu/Mint: `sudo apt-get install libegl1 libgles2` (usually already installed)

### Windows unchanged
- ANGLE DLL finding (Edge/Chrome) and D3D11/WARP context unchanged
- All `#if WINDOWS` guards in EGL init preserve the existing path exactly

---

## Audio

### What changed
- `WasapiLoopback.cs`: Entire file wrapped in `#if WINDOWS`
- `WinmmCapture.cs`: Entire file wrapped in `#if WINDOWS`
- `AudioCapture.CreateCaptureBackend()`: `#if WINDOWS` guard around WASAPI/WinMM, `#else` calls `LinuxPipeWireAudioCapture`
- New `LinuxPipeWireAudioCapture.cs`: Spawns `parecord` subprocess to capture default audio monitor as raw F32LE stereo PCM

### Linux requirements
- PipeWire running (standard on Wayland desktops)
- `parecord` available: `sudo apt-get install pulseaudio-utils` OR `pipewire-pulse` (the latter provides PulseAudio compatibility)
- If `parecord` unavailable, audio will silently fail (plugin still renders, just no audio reactivity)

---

## TODO / Known Issues

- [ ] Test EGL context creation on Mesa (AMD/Intel/Nvidia)
- [ ] Verify pbuffer surface works for offscreen rendering on Mesa
- [ ] Test `parecord` loopback capture on Cinnamon Wayland
- [ ] UI audio device selector (currently shows WASAPI devices) — on Linux should hide or show PipeWire sources
- [ ] Check if `AudioInputSource.StereoMix` option should be hidden on Linux in the settings UI
- [ ] Cinnamon Wayland: no ScreenCast portal, no wlroots — Shadertoy rendering works independently of screen capture so this is fine
- [ ] Consider `pw-cat` as fallback if `parecord` not available
