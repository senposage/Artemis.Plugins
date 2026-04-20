namespace Artemis.Plugins.LayerBrushes.Shadertoy;

/// <summary>
/// Generates deterministic noise textures compatible with Shadertoy's built-in
/// noise assets (RGBA noise 256×256 and grey noise 256×256).
///
/// Uses a seeded integer hash so the pixel data is always identical across runs.
/// The exact values differ from Shadertoy's CDN assets, but for visual effects
/// on keyboard lighting the difference is imperceptible.
/// </summary>
internal static class NoiseTextureCache
{
    /// <summary>Generates RGBA noise — each channel is independent uniform random [0,255].</summary>
    public static byte[] GenerateRgba(int width, int height, uint seed = 0)
    {
        var data = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            uint h = Hash((uint)i + seed * 1_000_003u);
            data[i * 4 + 0] = (byte)(h         & 0xFF);
            data[i * 4 + 1] = (byte)((h >>  8) & 0xFF);
            data[i * 4 + 2] = (byte)((h >> 16) & 0xFF);
            data[i * 4 + 3] = (byte)((h >> 24) & 0xFF);
        }
        return data;
    }

    /// <summary>Generates greyscale noise — value replicated to all 4 channels, alpha=255.</summary>
    public static byte[] GenerateGrey(int width, int height, uint seed = 0)
    {
        var data = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            byte v = (byte)(Hash((uint)i + seed * 1_000_003u) & 0xFF);
            data[i * 4 + 0] = v;
            data[i * 4 + 1] = v;
            data[i * 4 + 2] = v;
            data[i * 4 + 3] = 255;
        }
        return data;
    }

    // Integer hash — finalizer from MurmurHash3, good avalanche properties.
    private static uint Hash(uint x)
    {
        x = ((x >> 16) ^ x) * 0x45d9f3bu;
        x = ((x >> 16) ^ x) * 0x45d9f3bu;
        x ^= x >> 16;
        return x;
    }
}
