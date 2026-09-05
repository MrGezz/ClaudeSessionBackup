using System.Collections.Specialized;
using System.Windows.Controls;

namespace ClaudeSessionBackup.App.Views;

public partial class DashboardPage : UserControl
{
    public DashboardPage()
    {
        InitializeComponent();

        // Auto-scroll the log pane to the newest line. The CollectionChanged
        // event fires on the dispatcher because LogLines is an
        // ObservableCollection updated via BeginInvoke, so this never races.
        Loaded += (_, _) =>
        {
            if (LogListBox.ItemsSource is INotifyCollectionChanged ncc)
            {
                ncc.CollectionChanged += (_, _) =>
                {
                    if (LogListBox.Items.Count > 0)
                    {
                        LogListBox.ScrollIntoView(LogListBox.Items[^1]);
                    }
                };
            }
        };
    }
}
