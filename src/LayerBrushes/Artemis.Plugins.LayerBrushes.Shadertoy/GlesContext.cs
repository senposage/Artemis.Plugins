using System;
using static Artemis.Plugins.LayerBrushes.Shadertoy.EglNative;

namespace Artemis.Plugins.LayerBrushes.Shadertoy;

/// <summary>
/// Creates an ANGLE EGL display/context backed by D3D11 WARP.
/// WARP is Windows' built-in CPU-only software rasterizer — the GPU driver
/// is never involved, so this context cannot conflict with games or Artemis.
///
/// All initialization and teardown must run on <paramref name="thread"/> since
/// EGL contexts are thread-bound.
/// </summary>
internal sealed class GlesContext : IDisposable
{
    private readonly GlesRenderThread _thread;
    private nint _display;
    private nint _context;
    private nint _surface;

    public GlesContext(GlesRenderThread thread)
    {
        _thread = thread;
        _thread.Invoke(Initialize);
    }

    private void Initialize()
    {
        ShaderLogger.Log("EGL: Initialize start");

        int major = 0, minor = 0;

        if (OperatingSystem.IsLinux())
        {
            // Mesa native EGL — EGL_DEFAULT_DISPLAY lets the driver pick the best available
            _display = eglGetDisplay(0);
            ShaderLogger.Log($"EGL: Mesa display=0x{_display:X} err=0x{eglGetError():X4}");
            EglCheck(_display == EGL_NO_DISPLAY, "eglGetDisplay (Mesa)");
            EglCheck(!eglInitialize(_display, out major, out minor), "eglInitialize (Mesa)");
            ShaderLogger.Log($"EGL: initialized v{major}.{minor} backend=Mesa");
        }
        else
        {
            // ANGLE on Windows — try GPU D3D11 first, fall back to WARP (CPU)
            int[] dispAttribsGpu =
            [
                EGL_PLATFORM_ANGLE_TYPE_ANGLE, EGL_PLATFORM_ANGLE_TYPE_D3D11_ANGLE,
                EGL_NONE
            ];
            int[] dispAttribsWarp =
            [
                EGL_PLATFORM_ANGLE_TYPE_ANGLE,        EGL_PLATFORM_ANGLE_TYPE_D3D11_ANGLE,
                EGL_PLATFORM_ANGLE_DEVICE_TYPE_ANGLE, EGL_PLATFORM_ANGLE_DEVICE_TYPE_WARP_ANGLE,
                EGL_NONE
            ];

            _display = eglGetPlatformDisplayEXT(EGL_PLATFORM_ANGLE_ANGLE, 0, dispAttribsGpu);
            bool useWarp = _display == EGL_NO_DISPLAY || !eglInitialize(_display, out major, out minor);
            if (useWarp)
            {
                ShaderLogger.Log("EGL: GPU D3D11 unavailable, falling back to WARP");
                _display = eglGetPlatformDisplayEXT(EGL_PLATFORM_ANGLE_ANGLE, 0, dispAttribsWarp);
                ShaderLogger.Log($"EGL: WARP display=0x{_display:X} err=0x{eglGetError():X4}");
                EglCheck(_display == EGL_NO_DISPLAY, "eglGetPlatformDisplayEXT (WARP)");
                EglCheck(!eglInitialize(_display, out major, out minor), "eglInitialize (WARP)");
            }
            else
            {
                ShaderLogger.Log($"EGL: GPU D3D11 display=0x{_display:X}");
            }
            ShaderLogger.Log($"EGL: initialized v{major}.{minor} backend={(useWarp ? "WARP" : "GPU D3D11")}");
        }

        int[] cfgAttribs =
        [
            EGL_SURFACE_TYPE,    EGL_PBUFFER_BIT,
            EGL_RENDERABLE_TYPE, EGL_OPENGL_ES3_BIT,
            EGL_RED_SIZE,   8, EGL_GREEN_SIZE, 8,
            EGL_BLUE_SIZE,  8, EGL_ALPHA_SIZE, 8,
            EGL_NONE
        ];
        var cfgBuf = new nint[1];
        EglCheck(!eglChooseConfig(_display, cfgAttribs, cfgBuf, 1, out int n) || n == 0, "eglChooseConfig");
        nint cfg = cfgBuf[0];
        ShaderLogger.Log($"EGL: numConfigs={n}, cfg=0x{cfg:X}");

        int[] ctxAttribs = [EGL_CONTEXT_MAJOR_VERSION, 3, EGL_CONTEXT_MINOR_VERSION, 0, EGL_NONE];
        _context = eglCreateContext(_display, cfg, EGL_NO_CONTEXT, ctxAttribs);
        ShaderLogger.Log($"EGL: context=0x{_context:X} err=0x{eglGetError():X4}");
        EglCheck(_context == EGL_NO_CONTEXT, "eglCreateContext");

        // Tiny dummy PBuffer surface — real rendering goes to per-shader FBOs.
        int[] surfAttribs = [EGL_WIDTH, 1, EGL_HEIGHT, 1, EGL_NONE];
        _surface = eglCreatePbufferSurface(_display, cfg, surfAttribs);
        ShaderLogger.Log($"EGL: surface=0x{_surface:X} err=0x{eglGetError():X4}");
        EglCheck(_surface == EGL_NO_SURFACE, "eglCreatePbufferSurface");

        bool ok = eglMakeCurrent(_display, _surface, _surface, _context);
        ShaderLogger.Log($"EGL: makeCurrent={ok} err=0x{eglGetError():X4}");
        EglCheck(!ok, "eglMakeCurrent");

        ShaderLogger.Log("EGL: Initialize complete");
    }

    private static void EglCheck(bool failed, string call)
    {
        if (failed) throw new Exception($"ANGLE EGL: {call} failed (0x{eglGetError():X4})");
    }

    public void Dispose()
    {
        _thread.Invoke(() =>
        {
            eglMakeCurrent(_display, EGL_NO_SURFACE, EGL_NO_SURFACE, EGL_NO_CONTEXT);
            if (_surface != EGL_NO_SURFACE) eglDestroySurface(_display, _surface);
            if (_context != EGL_NO_CONTEXT) eglDestroyContext(_display, _context);
            if (_display != EGL_NO_DISPLAY) eglTerminate(_display);
        });
    }
}
