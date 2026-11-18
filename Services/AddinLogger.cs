using System;
using System.IO;

namespace ARAR.Civil3D.Services;

/// <summary>Registro sencillo de eventos en la carpeta AppData del usuario actual.</summary>
public static class AddinLogger
{
    private static readonly object SyncRoot = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ARAR-C3D", "logs");

    public static void Info(string message) => Write("INFO", message, null);
    public static void Error(string message, System.Exception exception) => Write("ERROR", message, exception);

    private static void Write(string level, string message, System.Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            string path = Path.Combine(LogDirectory, $"ARAR-C3D-{DateTime.Now:yyyyMMdd}.log");
            string details = exception is null ? string.Empty : Environment.NewLine + exception;
            lock (SyncRoot)
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{details}{Environment.NewLine}");
        }
        catch
        {
            // El registro no debe impedir que se complete un comando de Civil 3D.
        }
    }
}
