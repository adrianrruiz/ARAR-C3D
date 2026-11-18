using System.Windows.Input;
using Autodesk.AutoCAD.ApplicationServices.Core;
using ARAR.Civil3D.Services;

namespace ARAR.Civil3D.Core;

internal sealed class RibbonCommandHandler : ICommand
{
    private readonly string _commandName;

    public RibbonCommandHandler(string commandName) => _commandName = commandName;

    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => Application.DocumentManager.MdiActiveDocument is not null;

    public void Execute(object? parameter)
    {
        try
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document is null)
                return;

            document.SendStringToExecute(_commandName + "\n", true, false, false);
        }
        catch (System.Exception ex)
        {
            AddinLogger.Error($"No se pudo iniciar el comando {_commandName} desde la cinta.", ex);
            Application.DocumentManager.MdiActiveDocument?.Editor.WriteMessage(
                $"\nNo se pudo iniciar el comando: {ex.Message}");
        }
    }
}
