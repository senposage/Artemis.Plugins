using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using static Artemis.Plugins.LayerBrushes.Shadertoy.GlesNative;

namespace Artemis.Plugins.LayerBrushes.Shadertoy;

internal static unsafe class GlesProgramBinaryCache
{
    private const int CacheVersion = 1;
    private static bool? _isSupported;

    public static bool IsSupported()
    {
        if (_isSupported.HasValue) return _isSupported.Value;

        int formats;
        glGetIntegerv(GL_NUM_PROGRAM_BINARY_FORMATS, &formats);
        _isSupported = formats > 0;
        ShaderLogger.Log($"ProgramBinaryCache: supported={_isSupported.Value}, formats={formats}");
        return _isSupported.Value;
    }

    public static string BuildKey(string passName, string vertexSource, string fragmentSource)
    {
        string renderer = GetString(GL_RENDERER);
        string version = GetString(GL_VERSION);
        string input = $"{CacheVersion}\n{renderer}\n{version}\n{passName}\n{vertexSource}\n{fragmentSource}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash);
    }

    public static bool TryLoadProgram(string key, out uint program)
    {
        program = 0;
        if (!IsSupported()) return false;

        string path = GetPath(key);
        if (!File.Exists(path)) return false;

        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            int version = reader.ReadInt32();
            if (version != CacheVersion) return false;

            uint format = reader.ReadUInt32();
            int length = reader.ReadInt32();
            if (length <= 0 || length > 64 * 1024 * 1024) return false;
            byte[] data = reader.ReadBytes(length);
            if (data.Length != length) return false;

            uint candidate = glCreateProgram();
            fixed (byte* p = data)
                glProgramBinary(candidate, format, p, data.Length);

            int linked;
            glGetProgramiv(candidate, GL_LINK_STATUS, &linked);
            if (linked == 0)
            {
                ShaderLogger.Log($"ProgramBinaryCache: rejected cached binary {key}: {GetProgramInfoLog(candidate)}");
                glDeleteProgram(candidate);
                return false;
            }

            program = candidate;
            ShaderLogger.Log($"ProgramBinaryCache: hit {key}, bytes={length}, format=0x{format:X}");
            return true;
        }
        catch (Exception ex)
        {
            ShaderLogger.Log($"ProgramBinaryCache: load failed {key}: {ex.Message}");
            return false;
        }
    }

    public static void SaveProgram(string key, uint program)
    {
        if (!IsSupported()) return;

        try
        {
            int length;
            glGetProgramiv(program, GL_PROGRAM_BINARY_LENGTH, &length);
            if (length <= 0) return;

            byte[] data = new byte[length];
            uint format;
            int written;
            fixed (byte* p = data)
                glGetProgramBinary(program, length, &written, &format, p);
            if (written <= 0) return;

            Directory.CreateDirectory(CacheDirectory);
            using var stream = File.Create(GetPath(key));
            using var writer = new BinaryWriter(stream);
            writer.Write(CacheVersion);
            writer.Write(format);
            writer.Write(written);
            writer.Write(data, 0, written);
            ShaderLogger.Log($"ProgramBinaryCache: saved {key}, bytes={written}, format=0x{format:X}");
        }
        catch (Exception ex)
        {
            ShaderLogger.Log($"ProgramBinaryCache: save failed {key}: {ex.Message}");
        }
    }

    public static void Clear()
    {
        try
        {
            if (!Directory.Exists(CacheDirectory)) return;
            Directory.Delete(CacheDirectory, recursive: true);
            ShaderLogger.Log("ProgramBinaryCache: cleared");
        }
        catch (Exception ex)
        {
            ShaderLogger.Log($"ProgramBinaryCache: clear failed: {ex.Message}");
        }
    }

    private static string CacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Artemis",
        "ShaderToy-ProgramCache");

    private static string GetPath(string key) => Path.Combine(CacheDirectory, key + ".bin");
}
