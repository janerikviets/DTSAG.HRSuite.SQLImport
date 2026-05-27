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
}
