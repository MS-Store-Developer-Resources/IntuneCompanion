using System.Collections.Specialized;
using System.Windows;

namespace IntuneCompanion
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            Loaded += (_, _) =>
            {
                if (LogListBox.ItemsSource is INotifyCollectionChanged col)
                {
                    col.CollectionChanged += (_, _) =>
                    {
                        if (LogListBox.Items.Count > 0)
                            LogListBox.ScrollIntoView(LogListBox.Items[^1]);
                    };
                }
            };
        }

        private void OpenLogViewer_Click(object sender, RoutedEventArgs e)
        {
            var window = new LogViewerWindow { Owner = this };
            window.Show();
        }
    }
}