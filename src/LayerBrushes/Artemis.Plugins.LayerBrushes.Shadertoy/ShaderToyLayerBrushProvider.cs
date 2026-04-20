using System;
using System.IO;
using System.Linq;
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

        if (!_resolverRegistered)
        {
            if (OperatingSystem.IsLinux())
            {
                NativeLibrary.SetDllImportResolver(typeof(EglNative).Assembly, (name, _, _) =>
                {
                    string? soName = name switch
                    {
                        "libEGL"    => "libEGL.so.1",
                        "libGLESv2" => "libGLESv2.so.2",
                        _           => null
                    };
                    if (soName != null)
                    {
                        if (NativeLibrary.TryLoad(soName, out nint h))
                            return h;
                        ShaderLogger.Log($"DllResolver: FAILED to load {soName}");
                    }
                    return 0;
                });
                ShaderLogger.Log("Enable: registered Mesa EGL/GLES resolver");
            }
            else
            {
                _angleRuntimeDir = EnsureAngleRuntime(dir);
                ShaderLogger.Log($"Enable: ANGLE runtime dir = {_angleRuntimeDir}");

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
            }
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
    // Locates ANGLE DLLs (libEGL.dll + libGLESv2.dll) from the best available
    // source, then copies them to a stable runtime folder so they are never
    // locked by the process (allowing Artemis to overwrite the plugin dir on
    // updates, and Edge/Chrome to update their own dirs freely).
    //
    // Source priority:
    //   1. Plugin directory  — bundled copies, present in dev/manual builds
    //   2. Microsoft Edge    — ships with all modern Windows; always up-to-date
    //   3. Google Chrome     — widespread fallback on machines without Edge
    //
    // On the next restart a newer source (e.g. Edge auto-updated) is detected
    // via file-size change and automatically re-promoted to the runtime folder.
    // -------------------------------------------------------------------------
    private static string EnsureAngleRuntime(string pluginDir)
    {
        string runtimeDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Artemis", "ShaderToy-ANGLE");

        string? sourceDir = FindAngleSourceDir(pluginDir);
        if (sourceDir == null)
        {
            // No source found — try to use whatever is already in the runtime dir.
            ShaderLogger.Log("ANGLE: no DLL source found (plugin dir, Edge, or Chrome) — trying runtime dir as-is");
            return runtimeDir;
        }

        ShaderLogger.Log($"ANGLE: source dir = {sourceDir}");

        string[] dlls = ["libEGL.dll", "libGLESv2.dll"];

        bool needsCopy = dlls.Any(dll =>
        {
            string src = Path.Combine(sourceDir, dll);
            string dst = Path.Combine(runtimeDir, dll);
            if (!File.Exists(src)) return false;
            if (!File.Exists(dst)) return true;
            return new FileInfo(src).Length != new FileInfo(dst).Length;
        });

        if (needsCopy)
        {
            try
            {
                Directory.CreateDirectory(runtimeDir);
                foreach (string dll in dlls)
                {
                    string src = Path.Combine(sourceDir, dll);
                    if (!File.Exists(src)) continue;
                    File.Copy(src, Path.Combine(runtimeDir, dll), overwrite: true);
                    ShaderLogger.Log($"ANGLE: promoted {dll} from {sourceDir} → {runtimeDir}");
                }
            }
            catch (Exception ex)
            {
                // Fallback: load directly from source (DLLs won't be locked since
                // Edge/Chrome keep their versioned dirs intact across updates).
                ShaderLogger.Log($"ANGLE: runtime copy failed ({ex.Message}), loading directly from source dir");
                return sourceDir;
            }
        }

        return runtimeDir;
    }

    /// <summary>
    /// Returns the directory containing ANGLE's libEGL.dll + libGLESv2.dll,
    /// preferring bundled plugin copies, then Edge, then Chrome.
    /// </summary>
    private static string? FindAngleSourceDir(string pluginDir)
    {
        // 1. Bundled in the plugin directory (dev builds / manual install)
        if (AngleDllsExist(pluginDir))
            return pluginDir;

        // 2. Microsoft Edge — present on all modern Windows by default
        string[] edgeBases = [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),    "Microsoft", "Edge", "Application"),
        ];
        string? edge = FindAngleInChromiumAppDir(edgeBases);
        if (edge != null)
        {
            ShaderLogger.Log($"ANGLE: found Edge ANGLE at {edge}");
            return edge;
        }

        // 3. Google Chrome — widespread fallback
        string[] chromeBases = [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),    "Google", "Chrome", "Application"),
        ];
        string? chrome = FindAngleInChromiumAppDir(chromeBases);
        if (chrome != null)
            ShaderLogger.Log($"ANGLE: found Chrome ANGLE at {chrome}");
        return chrome;
    }

    /// <summary>
    /// Scans each base path for a versioned Chromium application subdirectory
    /// (e.g. "131.0.2903.112") that contains both ANGLE DLLs.
    /// Returns the newest matching directory, or null if none found.
    /// </summary>
    private static string? FindAngleInChromiumAppDir(string[] basePaths)
    {
        foreach (string basePath in basePaths)
        {
            if (!Directory.Exists(basePath)) continue;
            try
            {
                string? dir = Directory.GetDirectories(basePath)
                    .Where(d => AngleDllsExist(d))
                    .OrderByDescending(d => Path.GetFileName(d))
                    .FirstOrDefault();
                if (dir != null) return dir;
            }
            catch { }
        }
        return null;
    }

    private static bool AngleDllsExist(string dir) =>
        File.Exists(Path.Combine(dir, "libEGL.dll")) &&
        File.Exists(Path.Combine(dir, "libGLESv2.dll"));
}
