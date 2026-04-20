using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Artemis.Plugins.LayerBrushes.Shadertoy;

public sealed class ShaderLibraryEntry
{
    public string Name { get; set; } = "";
    public ShaderDefinition Shader { get; set; } = new();
}

/// <summary>
/// Persists named shader presets to
/// %Documents%\Artemis\Plugins\Data\ShaderToy\presets.json
/// so they survive plugin upgrades.
/// </summary>
internal sealed class ShaderLibrary
{
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    private readonly string _path;
    private List<ShaderLibraryEntry> _entries = [];

    internal static ShaderLibrary? Instance { get; private set; }

    internal static void Initialize(string pluginDir)
    {
        Instance = new ShaderLibrary(pluginDir);
    }

    private ShaderLibrary(string pluginDir)
    {
        // Windows: %Documents%\Artemis\Plugins\Data\ShaderToy
        // Linux:   ~/.local/share/Artemis/Plugins/Data/ShaderToy  (XDG_DATA_HOME)
        Environment.SpecialFolder dataFolder = OperatingSystem.IsLinux()
            ? Environment.SpecialFolder.LocalApplicationData
            : Environment.SpecialFolder.MyDocuments;

        string dataDir = Path.Combine(
            Environment.GetFolderPath(dataFolder),
            "Artemis", "Plugins", "Data", "ShaderToy");

        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "presets.json");

        // One-time migration from old plugin-dir location
        string legacy = Path.Combine(pluginDir, "presets.json");
        if (File.Exists(legacy) && !File.Exists(_path))
        {
            try
            {
                File.Copy(legacy, _path);
                ShaderLogger.Log($"ShaderLibrary: migrated presets from {legacy} → {_path}");
            }
            catch (Exception ex)
            {
                ShaderLogger.Log($"ShaderLibrary: migration failed: {ex.Message}");
            }
        }

        Load();
    }

    public IReadOnlyList<ShaderLibraryEntry> Entries => _entries;

    public void Save(string name, ShaderDefinition shader)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var idx = _entries.FindIndex(e => e.Name == name);
        var entry = new ShaderLibraryEntry { Name = name, Shader = shader };
        if (idx >= 0) _entries[idx] = entry;
        else _entries.Add(entry);
        Persist();
    }

    public void Delete(string name)
    {
        _entries.RemoveAll(e => e.Name == name);
        Persist();
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            _entries = JsonSerializer.Deserialize<List<ShaderLibraryEntry>>(
                File.ReadAllText(_path), _json) ?? [];
        }
        catch (Exception ex)
        {
            ShaderLogger.Log($"ShaderLibrary: load failed: {ex.Message}");
            _entries = [];
        }
    }

    private void Persist()
    {
        try   { File.WriteAllText(_path, JsonSerializer.Serialize(_entries, _json)); }
        catch (Exception ex) { ShaderLogger.Log($"ShaderLibrary: persist failed: {ex.Message}"); }
    }
}
