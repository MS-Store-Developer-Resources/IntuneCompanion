using System.Windows;
using IntuneCompanion.ViewModels;

namespace IntuneCompanion;

public partial class LogViewerWindow : Window
{
    private readonly LogViewerViewModel _vm;

    public LogViewerWindow()
    {
        InitializeComponent();
        _vm = (LogViewerViewModel)DataContext;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        var selectedLine = LogDataGrid.SelectedItem as LogLine;
        var previousIndex = LogDataGrid.SelectedIndex;

        await _vm.LoadFileCommand.ExecuteAsync(_vm.SelectedFile);

        if (selectedLine is not null && previousIndex >= 0)
        {
            // Try to find the same row by matching content
            var match = _vm.LogLines
                .Select((line, idx) => (line, idx))
                .FirstOrDefault(x => x.line.DateText == selectedLine.DateText && x.line.Message == selectedLine.Message);

            if (match.line is not null)
            {
                LogDataGrid.SelectedIndex = match.idx;
                LogDataGrid.ScrollIntoView(LogDataGrid.SelectedItem);
            }
            else if (previousIndex < _vm.LogLines.Count)
            {
                LogDataGrid.SelectedIndex = previousIndex;
                LogDataGrid.ScrollIntoView(LogDataGrid.SelectedItem);
            }
        }
    }

    private void SearchUp_Click(object sender, RoutedEventArgs e)
    {
        Search(forward: false);
    }

    private void SearchDown_Click(object sender, RoutedEventArgs e)
    {
        Search(forward: true);
    }

    private void Search(bool forward)
    {
        var term = SearchTextBox.Text;
        if (string.IsNullOrWhiteSpace(term))
            return;

        int startIndex = LogDataGrid.SelectedIndex;
        int count = _vm.LogLines.Count;
        if (count == 0)
            return;

        int step = forward ? 1 : -1;
        int start = startIndex + step;

        for (int i = 0; i < count - 1; i++)
        {
            int idx = ((start + i * step) % count + count) % count;
            var line = _vm.LogLines[idx];

            if (line.Message?.Contains(term, StringComparison.OrdinalIgnoreCase) == true ||
                line.DateText?.Contains(term, StringComparison.OrdinalIgnoreCase) == true)
            {
                LogDataGrid.SelectedIndex = idx;
                LogDataGrid.ScrollIntoView(LogDataGrid.SelectedItem);
                return;
            }
        }
    }

    private void CopyRow_Click(object sender, RoutedEventArgs e)
    {
        if (LogDataGrid.SelectedItem is LogLine line)
        {
            Clipboard.SetText($"{line.DateText}\t{line.Message}");
        }
    }

    private void LogDataGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (LogDataGrid.SelectedItem is LogLine line)
            DetailTextBox.Text = line.Message;
        else
            DetailTextBox.Text = string.Empty;
    }
}
