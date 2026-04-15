using System;
using System.IO;
using System.Runtime.InteropServices;
using Artemis.Core.LayerBrushes;
using Artemis.Plugins.LayerBrushes.Shadertoy.LayerBrushes;

namespace Artemis.Plugins.LayerBrushes.Shadertoy;

public class ShaderToyLayerBrushProvider : LayerBrushProvider
{
    internal static GlesRenderThread? RenderThread    { get; private set; }
    internal static GlesContext?      GlesContext     { get; private set; }
    internal static string?           PluginDirectory { get; private set; }

    // Folder the ANGLE DLLs are actually loaded from.
    // Kept outside the plugin directory so Artemis plugin updates never touch
    // a loaded DLL — the plugin dir copies are only used as the update source.
    private static string? _angleRuntimeDir;
    private static bool    _resolverRegistered;

    public override void Enable()
    {
        var dir = Plugin.Directory.FullName;
        PluginDirectory = dir;
        ShaderLogger.Init(dir);
        ShaderLogger.Log($"Enable: plugin dir = {dir}");
        ShaderLibrary.Initialize(dir);

        _angleRuntimeDir = EnsureAngleRuntime(dir);
        ShaderLogger.Log($"Enable: ANGLE runtime dir = {_angleRuntimeDir}");

        if (!_resolverRegistered)
        {
            NativeLibrary.SetDllImportResolver(typeof(EglNative).Assembly, (name, _, _) =>
            {
                if (name is "libEGL" or "libGLESv2")
                {
                    string path = Path.Combine(_angleRuntimeDir ?? dir, name + ".dll");
                    if (NativeLibrary.TryLoad(path, out nint h))
                        return h;
                    ShaderLogger.Log($"DllResolver: FAILED to load {name}.dll from {path}");
                }
                return 0;
            });
            _resolverRegistered = true;
        }

        try
        {
            var thread = new GlesRenderThread();
            GlesContext  = new GlesContext(thread);
            RenderThread = thread;
            ShaderLogger.Log("Enable: GlesRenderThread and GlesContext created OK");
        }
        catch (Exception ex)
        {
            ShaderLogger.Log($"Enable: FAILED to create GL context: {ex}");
            throw;
        }

        RegisterLayerBrushDescriptor<ShaderToyLayerBrush>("Shader Toy", "Renders Shadertoy GLSL shaders", "React");
    }

    public override void Disable()
    {
        AudioCapture.Stop();  // no-op if never started
        GlesContext?.Dispose();
        RenderThread?.Dispose();
        GlesContext  = null;
        RenderThread = null;
    }

    // -------------------------------------------------------------------------
    // Copies libEGL.dll / libGLESv2.dll from the plugin directory to a stable
    // runtime folder outside the plugin directory the first time, and again
    // whenever a plugin update ships newer DLLs (detected by file size change).
    //
    // Loading from the runtime folder means the plugin-dir copies are never
    // locked by the process, so Artemis can overwrite them during updates.
    // On the next restart the updated DLLs are automatically promoted here.
    // -------------------------------------------------------------------------
    private static string EnsureAngleRuntime(string pluginDir)
    {
        string runtimeDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Artemis", "ShaderToy-ANGLE");

        string[] dlls = ["libEGL.dll", "libGLESv2.dll"];

        bool needsCopy = false;
        foreach (string dll in dlls)
        {
            string src = Path.Combine(pluginDir,   dll);
            string dst = Path.Combine(runtimeDir,  dll);
            if (!File.Exists(src)) continue;                          // not bundled — skip
            if (!File.Exists(dst) || new FileInfo(src).Length != new FileInfo(dst).Length)
                needsCopy = true;
        }

        if (needsCopy)
        {
            try
            {
                Directory.CreateDirectory(runtimeDir);
                foreach (string dll in dlls)
                {
                    string src = Path.Combine(pluginDir, dll);
                    if (!File.Exists(src)) continue;
                    File.Copy(src, Path.Combine(runtimeDir, dll), overwrite: true);
                    ShaderLogger.Log($"ANGLE: promoted {dll} → {runtimeDir}");
                }
            }
            catch (Exception ex)
            {
                // Fallback: load from plugin dir (old behaviour, DLLs will be locked)
                ShaderLogger.Log($"ANGLE: runtime copy failed ({ex.Message}), falling back to plugin dir");
                return pluginDir;
            }
        }

        return runtimeDir;
    }
}
