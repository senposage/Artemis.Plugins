using System.Runtime.InteropServices;
using System.Text;

namespace Artemis.Plugins.LayerBrushes.Shadertoy;

/// <summary>
/// P/Invoke bindings for ANGLE's libGLESv2.dll (OpenGL ES 3.0).
/// All calls must be made on the GlesRenderThread that made the EGL context current.
/// </summary>
internal static unsafe class GlesNative
{
    private const string LIB = "libGLESv2";

    // --- Enum constants ---
    public const uint GL_RGBA               = 0x1908;
    public const uint GL_UNSIGNED_BYTE      = 0x1401;
    public const uint GL_FLOAT              = 0x1406;
    public const uint GL_COLOR_BUFFER_BIT   = 0x00004000;
    public const uint GL_TEXTURE_2D         = 0x0DE1;
    public const uint GL_RGBA8              = 0x8058;
    public const uint GL_TEXTURE_MIN_FILTER = 0x2801;
    public const uint GL_TEXTURE_MAG_FILTER = 0x2800;
    public const uint GL_NEAREST            = 0x2600;
    public const uint GL_TEXTURE_WRAP_S     = 0x2802;
    public const uint GL_TEXTURE_WRAP_T     = 0x2803;
    public const uint GL_CLAMP_TO_EDGE      = 0x812F;
    public const uint GL_REPEAT             = 0x2901;
    public const uint GL_TEXTURE0           = 0x84C0;
    public const uint GL_FRAMEBUFFER        = 0x8D40;
    public const uint GL_COLOR_ATTACHMENT0  = 0x8CE0;
    public const uint GL_FRAMEBUFFER_COMPLETE = 0x8CD5;
    public const uint GL_ARRAY_BUFFER       = 0x8892;
    public const uint GL_PIXEL_PACK_BUFFER  = 0x88EB;
    public const uint GL_STATIC_DRAW        = 0x88B4;
    public const uint GL_STREAM_READ        = 0x88E1;
    public const uint GL_MAP_READ_BIT       = 0x0001;
    public const uint GL_TRIANGLES          = 0x0004;
    public const uint GL_VERTEX_SHADER      = 0x8B31;
    public const uint GL_FRAGMENT_SHADER    = 0x8B30;
    public const uint GL_COMPILE_STATUS     = 0x8B81;
    public const uint GL_LINK_STATUS        = 0x8B82;
    public const uint GL_INFO_LOG_LENGTH    = 0x8B84;

    // --- Framebuffer ---
    [DllImport(LIB)] public static extern void glGenFramebuffers(int n, uint* fbs);
    [DllImport(LIB)] public static extern void glDeleteFramebuffers(int n, uint* fbs);
    [DllImport(LIB)] public static extern void glBindFramebuffer(uint target, uint fb);
    [DllImport(LIB)] public static extern void glFramebufferTexture2D(uint target, uint attachment, uint texTarget, uint tex, int level);
    [DllImport(LIB)] public static extern uint glCheckFramebufferStatus(uint target);

    // --- Texture ---
    [DllImport(LIB)] public static extern void glGenTextures(int n, uint* texs);
    [DllImport(LIB)] public static extern void glDeleteTextures(int n, uint* texs);
    [DllImport(LIB)] public static extern void glBindTexture(uint target, uint tex);
    [DllImport(LIB)] public static extern void glTexImage2D(uint target, int level, int internalFmt, int w, int h, int border, uint fmt, uint type, void* data);
    [DllImport(LIB)] public static extern void glTexSubImage2D(uint target, int level, int xoffset, int yoffset, int w, int h, uint fmt, uint type, void* data);
    [DllImport(LIB)] public static extern void glTexParameteri(uint target, uint pname, int param);

    // --- Buffer / VAO ---
    [DllImport(LIB)] public static extern void glGenBuffers(int n, uint* bufs);
    [DllImport(LIB)] public static extern void glDeleteBuffers(int n, uint* bufs);
    [DllImport(LIB)] public static extern void glBindBuffer(uint target, uint buf);
    [DllImport(LIB)] public static extern void glBufferData(uint target, nint size, void* data, uint usage);
    [DllImport(LIB)] public static extern void glGenVertexArrays(int n, uint* vaos);
    [DllImport(LIB)] public static extern void glDeleteVertexArrays(int n, uint* vaos);
    [DllImport(LIB)] public static extern void glBindVertexArray(uint vao);
    [DllImport(LIB)] public static extern void glVertexAttribPointer(uint idx, int size, uint type, byte normalized, int stride, void* offset);
    [DllImport(LIB)] public static extern void glEnableVertexAttribArray(uint idx);

    // --- Shader / Program ---
    [DllImport(LIB)] public static extern uint glCreateShader(uint type);
    [DllImport(LIB)] public static extern void glDeleteShader(uint shader);
    [DllImport(LIB)] public static extern void glShaderSource(uint shader, int count, string[] src, int[]? lengths);
    [DllImport(LIB)] public static extern void glCompileShader(uint shader);
    [DllImport(LIB)] public static extern void glGetShaderiv(uint shader, uint pname, int* param);
    [DllImport(LIB)] public static extern void glGetShaderInfoLog(uint shader, int bufSize, int* length, byte* log);
    [DllImport(LIB)] public static extern uint glCreateProgram();
    [DllImport(LIB)] public static extern void glDeleteProgram(uint prog);
    [DllImport(LIB)] public static extern void glAttachShader(uint prog, uint shader);
    [DllImport(LIB)] public static extern void glLinkProgram(uint prog);
    [DllImport(LIB)] public static extern void glGetProgramiv(uint prog, uint pname, int* param);
    [DllImport(LIB)] public static extern void glGetProgramInfoLog(uint prog, int bufSize, int* length, byte* log);
    [DllImport(LIB)] public static extern void glUseProgram(uint prog);

    // --- Uniforms ---
    [DllImport(LIB)] public static extern int  glGetUniformLocation(uint prog, [MarshalAs(UnmanagedType.LPStr)] string name);
    [DllImport(LIB)] public static extern void glUniform1i(int loc, int v);
    [DllImport(LIB)] public static extern void glUniform1f(int loc, float v);
    [DllImport(LIB)] public static extern void glUniform1fv(int loc, int count, float* v);
    [DllImport(LIB)] public static extern void glUniform3f(int loc, float x, float y, float z);
    [DllImport(LIB)] public static extern void glUniform3fv(int loc, int count, float* v);
    [DllImport(LIB)] public static extern void glUniform4f(int loc, float x, float y, float z, float w);

    // --- Texture unit binding ---
    [DllImport(LIB)] public static extern void glActiveTexture(uint texture);

    // --- Render / Readback ---
    [DllImport(LIB)] public static extern void glViewport(int x, int y, int w, int h);
    [DllImport(LIB)] public static extern void glClearColor(float r, float g, float b, float a);
    [DllImport(LIB)] public static extern void glClear(uint mask);
    [DllImport(LIB)] public static extern void glDrawArrays(uint mode, int first, int count);
    [DllImport(LIB)] public static extern void glReadPixels(int x, int y, int w, int h, uint fmt, uint type, void* pixels);
    [DllImport(LIB)] public static extern void* glMapBufferRange(uint target, nint offset, nint length, uint access);
    [DllImport(LIB)] public static extern byte glUnmapBuffer(uint target);
    [DllImport(LIB)] public static extern void glFinish();
    [DllImport(LIB)] public static extern uint glGetError();

    // --- Helpers ---
    public static string GetShaderInfoLog(uint shader)
    {
        int len;
        glGetShaderiv(shader, GL_INFO_LOG_LENGTH, &len);
        if (len <= 1) return string.Empty;
        var buf = new byte[len];
        fixed (byte* p = buf) glGetShaderInfoLog(shader, len, null, p);
        return Encoding.UTF8.GetString(buf, 0, len - 1);
    }

    public static string GetProgramInfoLog(uint prog)
    {
        int len;
        glGetProgramiv(prog, GL_INFO_LOG_LENGTH, &len);
        if (len <= 1) return string.Empty;
        var buf = new byte[len];
        fixed (byte* p = buf) glGetProgramInfoLog(prog, len, null, p);
        return Encoding.UTF8.GetString(buf, 0, len - 1);
    }
}
