using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Artemis.Plugins.LayerBrushes.Shadertoy;

/// <summary>
/// Scans GLSL source for comment-based hints about what channel inputs a shader expects.
///
/// Matches patterns such as:
///   // iChannel0: rgba noise medium
///   // iChannel1: audio
///   // iChannel2: keyboard
///   // iChannel3: buffer a
///
/// Results are non-destructive suggestions — the caller decides whether to apply them.
/// Only channels currently set to <see cref="ChannelInputType.None"/> are overwritten
/// when wired into <see cref="Screens.PassEditorViewModel.SuggestChannels"/>.
/// </summary>
internal static partial class ShaderChannelHints
{
    // Matches any comment line that also mentions iChannelN
    [GeneratedRegex(@"//[^\n]*iChannel(\d)[^\n]*", RegexOptions.IgnoreCase)]
    private static partial Regex CommentWithChannel();

    public static Dictionary<int, ChannelInputType> Detect(string? source)
    {
        var result = new Dictionary<int, ChannelInputType>();
        if (string.IsNullOrWhiteSpace(source)) return result;

        foreach (System.Text.RegularExpressions.Match m in CommentWithChannel().Matches(source))
        {
            if (!int.TryParse(m.Groups[1].Value, out int ch) || ch < 0 || ch > 3) continue;
            if (result.ContainsKey(ch)) continue;   // first match per channel wins

            string line = m.Value.ToLowerInvariant();

            ChannelInputType? type = null;

            if      (HasAny(line, "noise"))                          type = ChannelInputType.Noise2D;
            else if (HasAny(line, "audio", "music", "sound", "mic")) type = ChannelInputType.Audio;
            else if (HasAny(line, "keyboard"))                       type = ChannelInputType.Keyboard;
            else if (HasAny(line, "buffer a", "buffera"))            type = ChannelInputType.BufferA;
            else if (HasAny(line, "buffer b", "bufferb"))            type = ChannelInputType.BufferB;
            else if (HasAny(line, "buffer c", "bufferc"))            type = ChannelInputType.BufferC;
            else if (HasAny(line, "buffer d", "bufferd"))            type = ChannelInputType.BufferD;

            if (type.HasValue) result[ch] = type.Value;
        }
        return result;
    }

    private static bool HasAny(string haystack, params string[] needles)
    {
        foreach (string n in needles)
            if (haystack.Contains(n, System.StringComparison.Ordinal)) return true;
        return false;
    }
}
