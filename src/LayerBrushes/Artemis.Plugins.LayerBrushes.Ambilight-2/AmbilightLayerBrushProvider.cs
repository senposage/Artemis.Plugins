using Artemis.Core.LayerBrushes;

namespace Artemis.Plugins.LayerBrushes.Ambilight
{
    public class AmbilightLayerBrushProvider : LayerBrushProvider
    {
        #region Methods

        public override void Enable()
        {
            RegisterLayerBrushDescriptor<AmbilightLayerBrush>("Ambilight v2", "A brush that shows the current display-image with stable monitor identification.", "MonitorMultiple");
        }

        public override void Disable()
        { }

        #endregion
    }
}
