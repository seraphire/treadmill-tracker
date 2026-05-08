using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;

namespace TreadmillApp;

public partial class LogWindow : Window
{
    private readonly ObservableCollection<string> _entries;

    public LogWindow(ObservableCollection<string> entries)
    {
        InitializeComponent();
        _entries = entries;
        LogList.ItemsSource = _entries;

        // Auto-scroll to the newest entry as items arrive while the window is open.
        _entries.CollectionChanged += OnEntriesChanged;
        Closed += (_, _) => _entries.CollectionChanged -= OnEntriesChanged;

        Loaded += (_, _) => ScrollToEnd();
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
            ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        if (_entries.Count == 0) return;
        LogList.ScrollIntoView(_entries[_entries.Count - 1]);
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        try { System.Windows.Clipboard.SetText(string.Join("\r\n", _entries)); }
        catch { /* clipboard occasionally throws on contention; best-effort */ }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _entries.Clear();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
