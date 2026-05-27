using System;
using System.Windows;

namespace ExecutePrograms;

public partial class SqlLogWindow : Window
{
    public SqlLogWindow()
    {
        InitializeComponent();
    }

    public void AppendLog(string message)
    {
        Dispatcher.Invoke(() =>
        {
            LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}]  {message}{Environment.NewLine}");
            LogScroll.ScrollToBottom();
        });
    }

    private void LogClear_Click(object sender, RoutedEventArgs e) => LogBox.Clear();
}
