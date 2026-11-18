using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

internal static class Bootstrapper
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        string bundleRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Autodesk", "ApplicationPlugins", "ARAR-C3D.bundle");
        string contentsRoot = Path.Combine(bundleRoot, "Contents");
        string win64Root = Path.Combine(contentsRoot, "Win64");

        DialogResult answer = MessageBox.Show(
            "Se instalarán las herramientas ARAR-C3D para Civil 3D 2026 en el perfil del usuario actual.\n\n" +
            "Cierre Civil 3D antes de continuar. ¿Desea instalar ahora?",
            "Instalador ARAR-C3D", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

        if (answer != DialogResult.Yes)
            return;

        try
        {
            Directory.CreateDirectory(win64Root);
            WriteResource("PackageContents.xml", Path.Combine(bundleRoot, "PackageContents.xml"));
            WriteResource("ARAR.Civil3D.dll", Path.Combine(win64Root, "ARAR.Civil3D.dll"));
            WriteResource("ARAR.Civil3D.deps.json", Path.Combine(win64Root, "ARAR.Civil3D.deps.json"));

            MessageBox.Show(
                "ARAR-C3D se instaló correctamente.\n\n" +
                "Abra o reinicie Civil 3D 2026 para cargar el menú.",
                "Instalación completada", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "No se pudo completar la instalación.\n\n" + ex.Message,
                "Error de instalación", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void WriteResource(string resourceName, string destinationPath)
    {
        using (Stream input = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
        {
            if (input == null)
                throw new InvalidOperationException("Falta el archivo incluido: " + resourceName);

            using (FileStream output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
                input.CopyTo(output);
        }
    }
}
