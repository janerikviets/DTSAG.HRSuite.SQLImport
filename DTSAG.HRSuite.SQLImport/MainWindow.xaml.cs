using System.Windows;
using DTSAG.HRSuite.SQLImport.ViewModels;

namespace DTSAG.HRSuite.SQLImport;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        PwdBox.PasswordChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
                vm.Password = PwdBox.Password;
        };
    }

    private void BtnNavImport_Click(object sender, RoutedEventArgs e) => Navigate("Import");
    private void BtnNavSettings_Click(object sender, RoutedEventArgs e) => Navigate("Settings");

    private void Navigate(string view)
    {
        var isImport = view == "Import";
        ViewImport.Visibility = isImport ? Visibility.Visible : Visibility.Collapsed;
        ViewSettings.Visibility = isImport ? Visibility.Collapsed : Visibility.Visible;
        BtnNavImport.Style = (Style)FindResource(isImport ? "NavButtonActiveStyle" : "NavButtonStyle");
        BtnNavSettings.Style = (Style)FindResource(isImport ? "NavButtonStyle" : "NavButtonActiveStyle");
        PageTitle.Text = isImport ? "Import" : "Einstellungen";
    }
}
