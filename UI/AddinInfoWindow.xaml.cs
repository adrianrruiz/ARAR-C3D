using System.Windows;

namespace ARAR.Civil3D.UI;

public partial class AddinInfoWindow : Window
{
    public AddinInfoWindow() => InitializeComponent();

    private void AcceptButton_Click(object sender, RoutedEventArgs e) => Close();
}
