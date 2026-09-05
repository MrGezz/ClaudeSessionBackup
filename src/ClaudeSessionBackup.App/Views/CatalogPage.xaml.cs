using System.Windows.Controls;
using System.Windows.Input;
using ClaudeSessionBackup.App.ViewModels;
using ClaudeSessionBackup.Core.Model;

namespace ClaudeSessionBackup.App.Views;

public partial class CatalogPage : UserControl
{
    public CatalogPage()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Double-click on a catalog row opens the transcript viewer,
    /// same as the explicit "Open" button.
    /// </summary>
    private void CatalogGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CatalogGrid.SelectedItem is SessionEntry entry &&
            entry.TranscriptRel is not null &&
            DataContext is MainViewModel vm)
        {
            vm.OpenTranscriptCommand.Execute(entry);
        }
    }
}
