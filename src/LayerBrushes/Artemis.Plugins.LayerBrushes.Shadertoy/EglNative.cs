using System.Runtime.InteropServices;

namespace Artemis.Plugins.LayerBrushes.Shadertoy;

/// <summary>
/// P/Invoke bindings for ANGLE's libEGL.dll.
/// Display attributes force the D3D11+WARP backend so all shader
/// execution is CPU-only — the GPU driver is never touched.
/// </summary>
internal static class EglNative
{
    private const string LIB = "libEGL";

    // Core EGL attribute tokens
    public const int EGL_NONE              = 0x3038;
    public const int EGL_OPENGL_ES3_BIT    = 0x0040;
    public const int EGL_SURFACE_TYPE      = 0x3033;
    public const int EGL_PBUFFER_BIT       = 0x0001;
    public const int EGL_RENDERABLE_TYPE   = 0x3040;
    public const int EGL_RED_SIZE          = 0x3024;
    public const int EGL_GREEN_SIZE        = 0x3023;
    public const int EGL_BLUE_SIZE         = 0x3022;
    public const int EGL_ALPHA_SIZE        = 0x3021;
    public const int EGL_CONTEXT_MAJOR_VERSION = 0x3098;
    public const int EGL_CONTEXT_MINOR_VERSION = 0x30FB;
    public const int EGL_WIDTH             = 0x3057;
    public const int EGL_HEIGHT            = 0x3056;

    // ANGLE extensions — selects D3D11 WARP (CPU-only) device
    public const int EGL_PLATFORM_ANGLE_ANGLE                  = 0x3202;
    public const int EGL_PLATFORM_ANGLE_TYPE_ANGLE             = 0x3203;
    public const int EGL_PLATFORM_ANGLE_TYPE_D3D11_ANGLE       = 0x3208;
    public const int EGL_PLATFORM_ANGLE_DEVICE_TYPE_ANGLE      = 0x3209;
    public const int EGL_PLATFORM_ANGLE_DEVICE_TYPE_WARP_ANGLE = 0x320B;

    public static readonly nint EGL_NO_DISPLAY = 0;
    public static readonly nint EGL_NO_CONTEXT = 0;
    public static readonly nint EGL_NO_SURFACE = 0;

    // Standard EGL display — used on Linux with Mesa
    [DllImport(LIB, CallingConvention = CallingConvention.Winapi)]
    public static extern nint eglGetDisplay(nint displayId);

    // ANGLE extension — used on Windows to select D3D11 backend
    [DllImport(LIB, CallingConvention = CallingConvention.Winapi)]
    public static extern nint eglGetPlatformDisplayEXT(int platform, nint nativeDisplay, [In] int[] attribs);

    [DllImport(LIB, CallingConvention = CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool eglInitialize(nint display, out int major, out int minor);

    [DllImport(LIB, CallingConvention = CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool eglChooseConfig(nint display, [In] int[] attribs, [Out] nint[] configs, int configSize, out int numConfigs);

    [DllImport(LIB, CallingConvention = CallingConvention.Winapi)]
    public static extern nint eglCreateContext(nint display, nint config, nint shareCtx, [In] int[] attribs);

    [DllImport(LIB, CallingConvention = CallingConvention.Winapi)]
    public static extern nint eglCreatePbufferSurface(nint display, nint config, [In] int[] attribs);

    [DllImport(LIB, CallingConvention = CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool eglMakeCurrent(nint display, nint draw, nint read, nint ctx);

    [DllImport(LIB, CallingConvention = CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool eglDestroyContext(nint display, nint ctx);

    [DllImport(LIB, CallingConvention = CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool eglDestroySurface(nint display, nint surface);

    [DllImport(LIB, CallingConvention = CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool eglTerminate(nint display);

    [DllImport(LIB, CallingConvention = CallingConvention.Winapi)]
    public static extern int eglGetError();

    [DllImport(LIB, CallingConvention = CallingConvention.Winapi)]
    public static extern nint eglGetProcAddress([MarshalAs(UnmanagedType.LPStr)] string procname);
}
