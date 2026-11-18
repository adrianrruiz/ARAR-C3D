using Autodesk.AutoCAD.Runtime;
using ARAR.Civil3D.Services;
using ARAR.Civil3D.Tools;

namespace ARAR.Civil3D.Core;

public sealed class ArarExtensionApplication : IExtensionApplication
{
    public void Initialize()
    {
        AddinLogger.Info("ARAR-C3D cargado.");
        RibbonBuilder.Initialize();
        CorridorSectionViewerTool.RegisterDocumentEvents();
    }

    public void Terminate()
    {
        CorridorSectionViewerTool.UnregisterDocumentEvents();
        RibbonBuilder.Shutdown();
        AddinLogger.Info("ARAR-C3D descargado.");
    }
}
