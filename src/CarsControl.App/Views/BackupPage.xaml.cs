using System.Windows;
using System.Windows.Controls;
using CarsControl.App.ViewModels;

namespace CarsControl.App.Views;

public partial class BackupPage : Page
{
    private readonly BackupsViewModel _backups;

    public BackupPage(BackupsViewModel backups, SetupSyncViewModel setup)
    {
        InitializeComponent();
        _backups = backups;
        DataContext = backups;
        // Two tabs read the same sync view model.
        SetupScroll.DataContext = setup;
        PcsScroll.DataContext = setup;
        Loaded += async (_, _) =>
        {
            backups.Refresh();
            await setup.RefreshAsync();
        };
    }

    /// <summary>0 = Backups, 1 = Your setup, 2 = Your PCs.</summary>
    public void ShowTab(int index) => Tabs.SelectedIndex = Math.Clamp(index, 0, Tabs.Items.Count - 1);

    private void OnFolderChosen(object sender, RoutedEventArgs e) => _backups.EditToUsb = false;
}
