using System.Windows;
using System.Windows.Controls;
using ARAR.Civil3D.Models;

namespace ARAR.Civil3D.UI;

/// <summary>Ventana modal para elegir una superficie TIN de la lista del dibujo o pasar a selección manual.</summary>
public sealed class SelectSurfaceWindow : Window
{
    private readonly ComboBox _surfaceCombo = new()
    {
        DisplayMemberPath = nameof(RoadSurfaceChoice.Name),
        Height = 26,
        VerticalContentAlignment = VerticalAlignment.Center
    };

    public RoadSurfaceChoice? SelectedSurface { get; private set; }
    public bool SelectManually { get; private set; }

    public SelectSurfaceWindow(IReadOnlyList<RoadSurfaceChoice> surfaces)
    {
        Title = "Subir sólidos a superficie";
        Width = 430;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = System.Windows.Media.Brushes.White;

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = "Seleccione la superficie TIN de destino:",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        });

        _surfaceCombo.ItemsSource = surfaces;
        if (surfaces.Count > 0)
            _surfaceCombo.SelectedIndex = 0;
        else
            _surfaceCombo.IsEnabled = false;
        panel.Children.Add(_surfaceCombo);

        if (surfaces.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "No se encontraron superficies TIN en el dibujo actual.",
                Foreground = System.Windows.Media.Brushes.DarkRed,
                Margin = new Thickness(0, 6, 0, 0)
            });
        }

        var buttons = new DockPanel { Margin = new Thickness(0, 16, 0, 0), LastChildFill = false };

        var manualButton = new Button
        {
            Content = "Seleccionar manualmente",
            Padding = new Thickness(10, 5, 10, 5)
        };
        manualButton.Click += (_, _) =>
        {
            SelectManually = true;
            DialogResult = true;
        };
        DockPanel.SetDock(manualButton, Dock.Left);
        buttons.Children.Add(manualButton);

        var rightButtons = new StackPanel { Orientation = Orientation.Horizontal };
        var okButton = new Button
        {
            Content = "Aceptar",
            Padding = new Thickness(14, 5, 14, 5),
            Margin = new Thickness(0, 0, 6, 0),
            IsDefault = true,
            IsEnabled = surfaces.Count > 0
        };
        okButton.Click += (_, _) =>
        {
            if (_surfaceCombo.SelectedItem is not RoadSurfaceChoice choice)
                return;
            SelectedSurface = choice;
            DialogResult = true;
        };

        var cancelButton = new Button
        {
            Content = "Cancelar",
            Padding = new Thickness(12, 5, 12, 5),
            IsCancel = true
        };

        rightButtons.Children.Add(okButton);
        rightButtons.Children.Add(cancelButton);
        DockPanel.SetDock(rightButtons, Dock.Right);
        buttons.Children.Add(rightButtons);

        panel.Children.Add(buttons);
        Content = panel;
    }
}
