using Artemis.Core;

namespace Artemis.Plugins.LayerBrushes.Shadertoy.LayerBrushes.PropertyGroups;

public class ShaderToyPropertyGroup : LayerPropertyGroup
{
    public ShaderToyShaderProperties Shader { get; set; }

    protected override void PopulateDefaults() { }

    protected override void EnableProperties()
    {
        Shader.IsHidden = true;
    }

    protected override void DisableProperties() { }
}
