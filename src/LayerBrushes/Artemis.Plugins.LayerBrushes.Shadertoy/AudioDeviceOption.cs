namespace Artemis.Plugins.LayerBrushes.Shadertoy;

public sealed record AudioDeviceOption(string Id, string Name)
{
    public override string ToString() => Name;
}
