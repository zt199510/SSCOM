using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Sscom.Services;
using Sscom.ViewModels;

namespace Sscom;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _closeReady;
    private bool _closing;
    private bool _scrollPending;

    public MainWindow() : this(new MainViewModel(new SerialService(), Dispatcher.CurrentDispatcher)) { }

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.Rows.CollectionChanged += Rows_CollectionChanged;
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
            _closeReady = true;
            Close();
        }
    }
}
