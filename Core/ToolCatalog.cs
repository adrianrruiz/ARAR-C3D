using System.Reflection;
using ARAR.Civil3D.Services;

namespace ARAR.Civil3D.Core;

/// <summary>Descubre implementaciones concretas de ITool en este ensamblado.</summary>
public static class ToolCatalog
{
    public static IReadOnlyList<ITool> Discover(Assembly? assembly = null)
    {
        assembly ??= Assembly.GetExecutingAssembly();
        var tools = new List<ITool>();

        foreach (Type type in assembly.GetTypes()
                     .Where(t => !t.IsAbstract && !t.IsInterface && typeof(ITool).IsAssignableFrom(t)))
        {
            try
            {
                if (Activator.CreateInstance(type) is ITool tool)
                    tools.Add(tool);
            }
            catch (System.Exception ex)
            {
                AddinLogger.Error($"No se pudo crear la herramienta {type.FullName}.", ex);
            }
        }

        return tools.OrderBy(t => t.Order).ThenBy(t => t.Title, StringComparer.CurrentCulture).ToArray();
    }
}
