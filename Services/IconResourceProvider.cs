using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Reflection;
using System.IO;

namespace ARAR.Civil3D.Services;

/// <summary>Carga los iconos personalizados del ensamblado y usa los recursos WPF como respaldo.</summary>
public static class IconResourceProvider
{
    private static readonly Lazy<ResourceDictionary> Dictionary = new(() =>
        new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/ARAR.Civil3D;component/Resources/ToolIcons.xaml", UriKind.Absolute)
        });

    public static ImageSource? GetImage(string resourceKey, int pixelSize)
    {
        if (pixelSize is 16 or 32 && TryGetPng(resourceKey, pixelSize) is ImageSource png)
            return png;

        try
        {
            DrawingImage? source = (Dictionary.Value.Contains(resourceKey)
                ? Dictionary.Value[resourceKey]
                : Dictionary.Value["DefaultIcon"]) as DrawingImage;
            if (source is null)
                return null;

            var group = new DrawingGroup
            {
                Transform = new ScaleTransform(pixelSize / 32.0, pixelSize / 32.0)
            };
            group.Children.Add(source.Drawing);
            return new DrawingImage { Drawing = group };
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? TryGetPng(string resourceKey, int pixelSize)
    {
        string? iconName = resourceKey switch
        {
            "AboutIcon" => "01_acerca_de_ARAR-C3D",
            "DrawingInfoIcon" => "02_informacion_del_dibujo",
            "DrawRoadMarkingIcon" => "03_dibujar_marca_vial",
            "RoadMarking3DIcon" => "04_marca_vial_3D",
            "LiftSolidsIcon" => "05_subir_solidos_a_superficie",
            "CebreadoIcon" => "06_cebreado",
            "SectionViewerIcon" => "07_visor_de_secciones",
            "VolumeTableIcon" => "tabla_volumenes",
            "ExportTablesIcon" => "exportar_excel",
            _ => null
        };
        if (iconName is null) return null;
        string resourceName = $"ARAR.Civil3D.Resources.Icons.{iconName}.png";
        try
        {
            using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (stream is null) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = pixelSize;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            Services.AddinLogger.Error($"No se pudo cargar el icono provisional {resourceName}.", ex);
            return null;
        }
    }
}
