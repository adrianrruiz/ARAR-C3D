namespace ARAR.Civil3D.Core;

/// <summary>Metadatos y contrato de ejecución de una herramienta de cinta.</summary>
public interface ITool
{
    string Id { get; }
    string Title { get; }
    string Panel { get; }
    string Description { get; }
    string Tooltip { get; }
    string CommandName { get; }
    string IconResourceKey { get; }
    ToolSize Size { get; }
    int Order { get; }
    void Execute();
}

public enum ToolSize
{
    Small,
    Large
}
