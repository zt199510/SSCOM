using System.Collections.ObjectModel;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Threading.Channels;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sscom.Services;

namespace Sscom.ViewModels;

// Immutable rows own the received bytes. Both strings are projections of that same array.
public sealed class ReceiveRow
{
    private readonly byte[] _bytes;
    public long Offset { get; }
    public string OffsetText => Offset.ToString("X8");
    public int Count => _bytes.Length;
    public string Hex { get; }
    public string Ascii { get; }
    public ReceiveRow(long offset, ReadOnlySpan<byte> bytes)
    {
        Offset = offset;
        _bytes = bytes.ToArray();
        Hex = string.Join(" ", _bytes.Select(b => b.ToString("X2")));
        Ascii = new string(_bytes.Select(b => b is >= 0x20 and <= 0x7e ? (char)b : '.').ToArray());
    }
}

public sealed record ReceiveUpdate(bool ReplaceTail, ReceiveRow[] Rows, long Total);

// Used only by one consumer, including across sequential sessions. No reformatting of history.
public sealed class ReceiveBuffer
{
    public const int RowSize = 16;
    private readonly List<byte> _tail = new(RowSize);
    private long _total;

    public ReceiveUpdate Append(ReadOnlySpan<byte> bytes)
    {
        var replace = _tail.Count > 0;
        var offset = _total - _tail.Count;
        var rows = new List<ReceiveRow>();
        foreach (var value in bytes)
        {
            _tail.Add(value);
            _total++;
            if (_tail.Count != RowSize) continue;
            rows.Add(new ReceiveRow(offset, _tail.ToArray()));
            offset += RowSize;
            _tail.Clear();
        }
        if (_tail.Count > 0) rows.Add(new ReceiveRow(offset, _tail.ToArray()));
        return new ReceiveUpdate(replace, rows.ToArray(), _total);
    }
}

public partial class MainViewModel : ObservableObject
{
    private readonly ISerialConnection _serial;
    private readonly Dispatcher _dispatcher;
    private readonly ReceiveBuffer _buffer = new();
    private Task _receiveTask = Task.CompletedTask;
    private bool _shuttingDown;

    public ObservableCollection<string> Ports { get; } = new();
    public ObservableCollection<ReceiveRow> Rows { get; } = new();
    public int[] BaudRates { get; } = { 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600 };
    public int[] DataBitOptions { get; } = { 5, 6, 7, 8 };
    public string[] StopBitOptions { get; } = { "1 位", "1.5 位", "2 位" };
    public string[] ParityOptions { get; } = { "无", "奇校验", "偶校验", "标记", "空格" };
    public string[] HandshakeOptions { get; } = { "无", "XON / XOFF", "RTS / CTS", "RTS / CTS + XON / XOFF" };
    public string[] DisplayModes { get; } = { "双视图", "仅 HEX", "仅 ASCII" };
    public string[] SendModes { get; } = { "ASCII", "HEX" };

    [ObservableProperty] private string? selectedPort;
    [ObservableProperty] private string baudRate = "115200";
    [ObservableProperty] private int dataBits = 8;
    [ObservableProperty] private int stopBitIndex;
    [ObservableProperty] private int parityIndex;
    [ObservableProperty] private int handshakeIndex;
    [ObservableProperty] private int displayMode;
    [ObservableProperty] private int sendMode;
    [ObservableProperty] private string sendText = "Hello";
    [ObservableProperty] private bool autoScroll = true;
    [ObservableProperty] private bool isOpen;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private long receivedBytes;
    [ObservableProperty] private long sentBytes;
    [ObservableProperty] private string status = "请选择串口和参数，然后打开。";

    public bool CanConfigure => !IsOpen && !IsBusy && !_shuttingDown;
    public string OpenButtonText => IsOpen ? "关闭串口" : "打开串口";
    public double HexColumnWidth => DisplayMode == 2 ? 0 : 430;
    public double AsciiColumnWidth => DisplayMode == 1 ? 0 : 190;

    public MainViewModel(ISerialConnection serial, Dispatcher dispatcher, string? quickSendPath = null)
    {
        _serial = serial;
        _dispatcher = dispatcher;
        RefreshPorts();
        InitializeSendFeatures(quickSendPath ?? QuickSendStore.DefaultPath);
    }

    partial void OnSelectedPortChanged(string? value) => TogglePortCommand.NotifyCanExecuteChanged();
    partial void OnSendTextChanged(string value) => SendCommand.NotifyCanExecuteChanged();
    partial void OnDisplayModeChanged(int value)
    {
        OnPropertyChanged(nameof(HexColumnWidth));
        OnPropertyChanged(nameof(AsciiColumnWidth));
    }
    partial void OnIsOpenChanged(bool value)
    {
        // Every way the port closes (button, unplug, write failure, shutdown) ends here.
        if (!value) StopTimedSending("串口已关闭");
        NotifyState();
    }
    partial void OnIsBusyChanged(bool value) => NotifyState();
    private void NotifyState()
    {
        OnPropertyChanged(nameof(CanConfigure));
        OnPropertyChanged(nameof(OpenButtonText));
        TogglePortCommand.NotifyCanExecuteChanged();
        RefreshPortsCommand.NotifyCanExecuteChanged();
        SendCommand.NotifyCanExecuteChanged();
        NotifySendFeatureState();
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private void RefreshPorts()
    {
        try
        {
            var previous = SelectedPort;
            var names = _serial.GetPortNames();
            Ports.Clear();
            foreach (var name in names) Ports.Add(name);
            SelectedPort = names.Contains(previous) ? previous : names.FirstOrDefault();
            Status = names.Length == 0 ? "没有可用串口，请连接设备后点击刷新。" : "请选择串口和参数，然后打开。";
        }
        catch (Exception ex) { Status = $"刷新串口失败：{Explain(ex)}"; }
    }
    private bool CanRefresh() => CanConfigure;
    private bool CanToggle() => !IsBusy && !_shuttingDown && (IsOpen || SelectedPort != null);
    private bool CanSend() => IsOpen && !IsBusy && !_shuttingDown && SendText.Length > 0;

    [RelayCommand(CanExecute = nameof(CanToggle))]
    private async Task TogglePortAsync()
    {
        IsBusy = true;
        try
        {
            if (IsOpen)
            {
                await _serial.CloseAsync();
                await _receiveTask;
                IsOpen = false;
            }
            else
            {
                if (!int.TryParse(BaudRate, out var baud) || baud is < 1200 or > 921600)
                    throw new ArgumentException("波特率请输入 1200 到 921600 之间的整数。");
                if (!DataBitOptions.Contains(DataBits) || StopBitIndex is < 0 or > 2 ||
                    ParityIndex is < 0 or > 4 || HandshakeIndex is < 0 or > 3)
                    throw new ArgumentException("请选择有效的串口参数。");
                var stops = new[] { StopBits.One, StopBits.OnePointFive, StopBits.Two };
                var settings = new SerialSettings(SelectedPort!, baud, DataBits, stops[StopBitIndex],
                    (Parity)ParityIndex, (Handshake)HandshakeIndex);
                var reader = await _serial.OpenAsync(settings);
                IsOpen = true;
                Status = $"已打开 {SelectedPort} · {baud} 波特";
                _receiveTask = Task.Run(() => ConsumeAsync(reader));
            }
        }
        catch (Exception ex)
        {
            await CloseAfterErrorAsync();
            Status = $"操作失败：{Explain(ex)}";
        }
        finally { IsBusy = false; }
    }

    private async Task ConsumeAsync(ChannelReader<byte[]> reader)
    {
        Exception? failure = null;
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                await Task.Delay(40).ConfigureAwait(false);
                var batch = new List<byte>(12288);
                while (batch.Count < 8192 && reader.TryRead(out var bytes)) batch.AddRange(bytes);
                if (batch.Count == 0) continue;
                var update = _buffer.Append(batch.ToArray());
                await _dispatcher.InvokeAsync(() =>
                {
                    var start = 0;
                    if (update.ReplaceTail) { Rows[^1] = update.Rows[0]; start = 1; }
                    for (var i = start; i < update.Rows.Length; i++) Rows.Add(update.Rows[i]);
                    ReceivedBytes = update.Total;
                }, DispatcherPriority.Background);
            }
        }
        catch (Exception ex) { failure = ex; }
        // All successfully queued bytes have been consumed before surfacing a channel fault.
        try { await _serial.CloseAsync().ConfigureAwait(false); }
        catch (Exception ex) { failure ??= ex; }
        await _dispatcher.InvokeAsync(() =>
        {
            IsOpen = false;
            Status = failure == null ? "已关闭串口，接收数据已保留。" : $"连接已中断：{Explain(failure)} 已接收数据已保留，可重新打开。";
        });
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private Task SendAsync() => SendThroughPortAsync(SendText, SendMode, SendSource.Manual);

    // The single send path shared by manual, timed and quick-group sends.
    private async Task<bool> SendCoreAsync(string text, int mode, SendSource source)
    {
        if (!IsOpen || IsBusy || _shuttingDown) return false;
        byte[] bytes;
        try { bytes = ParseSendBytes(text, mode); }
        catch (Exception ex) { Status = $"发送内容无效：{ex.Message}"; return false; }
        IsBusy = true;
        try
        {
            await _serial.WriteAsync(bytes);
            SentBytes += bytes.Length;
            RecordSend(source, text, mode, bytes.Length);
            Status = $"已向串口写入 {bytes.Length} 字节。";
            return true;
        }
        catch (Exception ex)
        {
            await CloseAfterErrorAsync();
            Status = $"发送失败：{Explain(ex)} 本次可能部分写入，发送计数仅含完整成功的写入。";
            return false;
        }
        finally { IsBusy = false; }
    }

    public static byte[] ParseSendBytes(string text, int mode)
    {
        if (mode == 0)
        {
            if (text.Any(c => c > 127)) throw new ArgumentException("ASCII 仅支持 00–7F，请使用 HEX 发送其他字节。");
            if (text.Length == 0) throw new ArgumentException("请输入发送内容。");
            return Encoding.ASCII.GetBytes(text);
        }
        var compact = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        if (compact.Length == 0 || compact.Length % 2 != 0 || compact.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("HEX 请输入完整的两位十六进制字节，例如 48 65 6C 6C 6F。");
        return Convert.FromHexString(compact);
    }

    private async Task CloseAfterErrorAsync()
    {
        try { await _serial.CloseAsync(); }
        catch { /* Preserve the original actionable error. */ }
        await _receiveTask;
        IsOpen = false;
    }

    public async Task ShutdownAsync()
    {
        _shuttingDown = true;
        StopTimedSending("程序退出");
        NotifyState();
        if (TogglePortCommand.ExecutionTask is { } toggle) await toggle;
        if (SendCommand.ExecutionTask is { } send) await send;
        await _sendTask;
        await CloseAfterErrorAsync();
    }

    private static string Explain(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "串口被占用或没有访问权限，请关闭其他使用该串口的程序。",
        IOException => $"设备已拔出、不可用或发生串口通信错误。{ex.Message}",
        TimeoutException => "串口读写超时，请检查连接和流控参数。",
        ArgumentException => $"串口不存在或参数无效：{ex.Message}",
        _ => ex.GetBaseException().Message
    };
}
