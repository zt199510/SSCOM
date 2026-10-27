using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Sscom.Services;
using Sscom.ViewModels;

namespace Sscom;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _closeReady;
    private bool _closing;
    private bool _scrollPending;
    private bool _historyScrollPending;

    public MainWindow() : this(new MainViewModel(new SerialService(), Dispatcher.CurrentDispatcher)) { }

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.Rows.CollectionChanged += Rows_CollectionChanged;
        viewModel.SendHistory.CollectionChanged += SendHistory_CollectionChanged;
    }

    private void SendHistory_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || _historyScrollPending) return;
        _historyScrollPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _historyScrollPending = false;
            if (_viewModel.SendHistory.Count > 0) HistoryList.ScrollIntoView(_viewModel.SendHistory[^1]);
        }));
    }

    private void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedQuickSendGroup is not { } group) return;
        var answer = MessageBox.Show(this, $"删除分组“{group.Name}”及其 {group.Entries.Count} 个条目？",
            "删除分组", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.OK) _viewModel.DeleteSelectedQuickSendGroup();
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "导入快捷发送", Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        var groups = _viewModel.QuickSendGroups;
        var answer = MessageBox.Show(this,
            $"导入将删除并替换当前全部 {groups.Count} 个分组（共 {groups.Sum(g => g.Entries.Count)} 个条目），不会合并。\n" +
            "此操作无法撤销，如需保留请先取消并导出备份。\n\n确定用导入文件替换全部分组吗？",
            "导入并替换全部分组", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.OK) _viewModel.ImportQuickSend(dialog.FileName);
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "导出快捷发送", Filter = "JSON 文件 (*.json)|*.json", FileName = "sscom-quick-send.json" };
        if (dialog.ShowDialog(this) == true) _viewModel.ExportQuickSend(dialog.FileName);
    }

    private void Rows_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_viewModel.AutoScroll || _scrollPending) return;
        _scrollPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _scrollPending = false;
            if (_viewModel.AutoScroll && _viewModel.Rows.Count > 0)
                ReceiveList.ScrollIntoView(_viewModel.Rows[^1]);
        }));
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeReady) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        IsEnabled = false;
        try
        {
            // Even an already-closed connection may clean up synchronously. Leave the
            // original Closing event before issuing the final Close call.
            await Dispatcher.Yield(DispatcherPriority.Background);
            await _viewModel.ShutdownAsync();
        }
        finally
        {
            _viewModel.Rows.CollectionChanged -= Rows_CollectionChanged;
            _viewModel.SendHistory.CollectionChanged -= SendHistory_CollectionChanged;
            _closeReady = true;
            Close();
        }
    }
}
