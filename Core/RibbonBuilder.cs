using System.Windows.Controls;
using System.Windows.Media;
using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.Windows;
using ARAR.Civil3D.Services;

namespace ARAR.Civil3D.Core;

/// <summary>Crea o restaura la pestaña ARAR-C3D cuando la cinta está disponible.</summary>
public static class RibbonBuilder
{
    private const string TabId = "ARAR_C3D_TAB";
    private static readonly string[] PanelOrder =
    {
        "INFORMACIÓN",
        "GENERACIÓN DE MOVIMIENTO DE TIERRAS",
        "SEÑALIZACIÓN VIAL"
    };
    private const string LegacyEarthworkPanelTitle = "GENERACION DE MOVIMIENTOS DE TIERRA - ELEMENTO";

    private static bool _idleSubscribed;
    private static bool _building;

    public static void Initialize()
    {
        if (!_idleSubscribed)
        {
            Application.Idle += OnIdle;
            _idleSubscribed = true;
        }

        TryBuildRibbon();
    }

    public static void Shutdown()
    {
        if (_idleSubscribed)
        {
            Application.Idle -= OnIdle;
            _idleSubscribed = false;
        }
    }

    private static void OnIdle(object? sender, EventArgs e) => TryBuildRibbon();

    private static void TryBuildRibbon()
    {
        if (_building)
            return;

        RibbonControl? ribbon = ComponentManager.Ribbon;
        if (ribbon is null || ribbon.Tabs.Any(t => string.Equals(t.Id, TabId, StringComparison.Ordinal)))
            return;

        _building = true;
        try
        {
            CreateTab(ribbon);
        }
        catch (System.Exception ex)
        {
            AddinLogger.Error("No se pudo construir la pestaña de cinta ARAR-C3D.", ex);
        }
        finally
        {
            _building = false;
        }
    }

    private static void CreateTab(RibbonControl ribbon)
    {
        IReadOnlyList<ITool> tools = ToolCatalog.Discover();
        var tab = new RibbonTab { Id = TabId, Title = "ARAR-C3D" };
        var panelNames = PanelOrder.Concat(tools.Select(t => NormalizePanelTitle(t.Panel)))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (string panelName in panelNames)
        {
            var source = new RibbonPanelSource { Title = panelName };
            var panel = new RibbonPanel { Source = source };

            foreach (ITool tool in tools.Where(t => string.Equals(NormalizePanelTitle(t.Panel), panelName, StringComparison.OrdinalIgnoreCase)))
                source.Items.Add(CreateButton(tool));

            tab.Panels.Add(panel);
        }

        ribbon.Tabs.Add(tab);
        AddinLogger.Info("Pestaña ARAR-C3D creada o restaurada.");
    }

    private static string NormalizePanelTitle(string panelName) =>
        string.Equals(panelName, LegacyEarthworkPanelTitle, StringComparison.OrdinalIgnoreCase)
            ? "GENERACIÓN DE MOVIMIENTO DE TIERRAS"
            : panelName;

    private static RibbonButton CreateButton(ITool tool)
    {
        ImageSource? smallImage = IconResourceProvider.GetImage(tool.IconResourceKey, 16);
        ImageSource? largeImage = IconResourceProvider.GetImage(tool.IconResourceKey, 32);
        return new RibbonButton
        {
            Text = tool.Title,
            ShowText = true,
            ShowImage = true,
            Size = tool.Size == ToolSize.Large ? RibbonItemSize.Large : RibbonItemSize.Standard,
            Orientation = Orientation.Vertical,
            ToolTip = tool.Tooltip,
            CommandHandler = new RibbonCommandHandler(tool.CommandName),
            Image = smallImage,
            LargeImage = largeImage
        };
    }
}
