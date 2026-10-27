using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sscom.Services;

namespace Sscom.ViewModels;

public enum SendSource { Manual, Timed, Quick }

public sealed class SendHistoryEntry
{
    public DateTime Time { get; }
    public SendSource Source { get; }
    public int Mode { get; }
    public string Content { get; }
    public int ByteCount { get; }
    public string Summary => $"{Time:HH:mm:ss.fff} · {SourceText} · {(Mode == 1 ? "HEX" : "ASCII")} · {ByteCount} 字节";
    public string SourceText => Source switch
    {
        SendSource.Timed => "定时",
        SendSource.Quick => "快捷",
        _ => "手动"
    };
    public SendHistoryEntry(DateTime time, SendSource source, int mode, string content, int byteCount)
    {
        Time = time;
        Source = source;
        Mode = mode;
        Content = content;
        ByteCount = byteCount;
    }
}

public partial class QuickSendEntry : ObservableObject
{
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string content = "";
    [ObservableProperty] private bool isHex;
}

public partial class QuickSendGroup : ObservableObject
{
    [ObservableProperty] private string name = "";
    public ObservableCollection<QuickSendEntry> Entries { get; } = new();
}

// Timed sending, quick-send groups and send history. Every send goes through SendCoreAsync,
// the original Day 1–2 manual send path.
public partial class MainViewModel
{
    public const int MinTimedInterval = 10;
    public const int MaxTimedInterval = 3_600_000;
    // Deliberate exception to "record every send": bounds memory under short-interval timed sending.
    public const int HistoryLimit = 2000;

    private DispatcherTimer _sendTimer = null!;
    private Task _sendTask = Task.CompletedTask;
    private int _activeInterval;
    private string _timedStopReason = "";
    private string _quickSendPath = "";
    private bool _quickSendLoading;

    public ObservableCollection<SendHistoryEntry> SendHistory { get; } = new();
    public ObservableCollection<QuickSendGroup> QuickSendGroups { get; } = new();

    [ObservableProperty] private string timedIntervalText = "1000";
    [ObservableProperty] private bool isTimedSending;
    [ObservableProperty] private QuickSendGroup? selectedQuickSendGroup;

    public bool CanEditTimedInterval => !IsTimedSending;
    public string TimedSendStateText => IsTimedSending
        ? $"定时发送中 · 每 {_activeInterval} ms"
        : "定时发送已停止" + (_timedStopReason.Length > 0 ? $"（{_timedStopReason}）" : "");

    private void InitializeSendFeatures(string quickSendPath)
    {
        // One timer for the lifetime of the view model, so reopening can never add a second one.
        _sendTimer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher);
        _sendTimer.Tick += SendTimer_Tick;
        _quickSendPath = quickSendPath;
        QuickSendGroups.CollectionChanged += QuickSendGroups_CollectionChanged;
        LoadQuickSend();
    }

    private void NotifySendFeatureState()
    {
        StartTimedSendCommand.NotifyCanExecuteChanged();
        StopTimedSendCommand.NotifyCanExecuteChanged();
        QuickSendCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsTimedSendingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEditTimedInterval));
        OnPropertyChanged(nameof(TimedSendStateText));
        NotifySendFeatureState();
    }

    private Task<bool> SendThroughPortAsync(string text, int mode, SendSource source)
    {
        var task = SendCoreAsync(text, mode, source);
        _sendTask = task;
        return task;
    }

    private void RecordSend(SendSource source, string text, int mode, int byteCount)
    {
        SendHistory.Add(new SendHistoryEntry(DateTime.Now, source, mode, text, byteCount));
        if (SendHistory.Count > HistoryLimit) SendHistory.RemoveAt(0);
    }

    // ---- Timed sending ----

    public static bool TryParseInterval(string text, out int milliseconds) =>
        int.TryParse(text.Trim(), out milliseconds) && milliseconds is >= MinTimedInterval and <= MaxTimedInterval;

    private bool CanStartTimedSend() => IsOpen && !IsTimedSending && !_shuttingDown;

    [RelayCommand(CanExecute = nameof(CanStartTimedSend))]
    private void StartTimedSend()
    {
        if (!TryParseInterval(TimedIntervalText, out var interval))
        {
            Status = $"定时间隔请输入 {MinTimedInterval} 到 {MaxTimedInterval} 之间的整数毫秒。";
            return;
        }
        try { ParseSendBytes(SendText, SendMode); }
        catch (Exception ex) { Status = $"发送内容无效：{ex.Message}"; return; }
        _sendTimer.Stop();
        _sendTimer.Interval = TimeSpan.FromMilliseconds(interval);
        _activeInterval = interval;
        _timedStopReason = "";
        IsTimedSending = true;
        _sendTimer.Start();
        Status = $"已开始定时发送，每 {interval} ms 发送一次发送框内容。";
    }

    private bool CanStopTimedSend() => IsTimedSending;

    [RelayCommand(CanExecute = nameof(CanStopTimedSend))]
    private void StopTimedSend()
    {
        StopTimedSending("已手动停止");
        Status = "已停止定时发送。";
    }

    private void StopTimedSending(string reason)
    {
        _sendTimer.Stop();
        if (!IsTimedSending) return;
        _timedStopReason = reason;
        IsTimedSending = false;
    }

    private async void SendTimer_Tick(object? sender, EventArgs e)
    {
        // Skip a tick while a write, open or close is still running instead of queueing writes.
        if (!IsTimedSending || !IsOpen || IsBusy || _shuttingDown) return;
        try
        {
            if (await SendThroughPortAsync(SendText, SendMode, SendSource.Timed)) return;
            // Failed ticks are not recorded in history, so the reason must stay visible.
            if (IsTimedSending) StopTimedSending("发送内容无效");
            else
            {
                // A write failure has already closed the port, which stopped the timer.
                _timedStopReason = "发送失败";
                OnPropertyChanged(nameof(TimedSendStateText));
            }
            Status = "定时发送已停止：" + Status;
        }
        catch (Exception ex)
        {
            StopTimedSending("发生错误");
            Status = $"定时发送已停止：{Explain(ex)}";
        }
    }

    // ---- Quick-send groups ----

    private bool CanQuickSend(QuickSendEntry? entry) => IsOpen && !IsBusy && !_shuttingDown;

    [RelayCommand(CanExecute = nameof(CanQuickSend))]
    private async Task QuickSendAsync(QuickSendEntry? entry)
    {
        if (entry == null || !CanQuickSend(entry)) return;
        if (!await SendThroughPortAsync(entry.Content, entry.IsHex ? 1 : 0, SendSource.Quick))
            Status = $"快捷条目“{entry.Name}”：" + Status;
    }

    [RelayCommand]
    private void AddQuickSendGroup()
    {
        var group = new QuickSendGroup { Name = $"分组 {QuickSendGroups.Count + 1}" };
        QuickSendGroups.Add(group);
        SelectedQuickSendGroup = group;
    }

    public void DeleteSelectedQuickSendGroup()
    {
        if (SelectedQuickSendGroup is not { } group) return;
        var index = QuickSendGroups.IndexOf(group);
        QuickSendGroups.Remove(group);
        SelectedQuickSendGroup = QuickSendGroups.Count == 0 ? null : QuickSendGroups[Math.Min(index, QuickSendGroups.Count - 1)];
    }

    [RelayCommand]
    private void AddQuickSendEntry()
    {
        if (SelectedQuickSendGroup == null)
        {
            AddQuickSendGroup();
            SelectedQuickSendGroup!.Name = "默认分组";
        }
        var group = SelectedQuickSendGroup!;
        group.Entries.Add(new QuickSendEntry { Name = $"条目 {group.Entries.Count + 1}", Content = SendText, IsHex = SendMode == 1 });
    }

    [RelayCommand]
    private void DeleteQuickSendEntry(QuickSendEntry? entry)
    {
        if (entry == null) return;
        foreach (var group in QuickSendGroups)
            if (group.Entries.Remove(entry)) return;
    }

    public bool ImportQuickSend(string path)
    {
        List<QuickSendGroupData> data;
        try { data = QuickSendStore.Load(path); }
        catch (Exception ex)
        {
            Status = $"导入失败，现有快捷发送未改变：{ex.GetBaseException().Message}";
            return false;
        }
        ReplaceQuickSend(data);
        SaveQuickSend();
        Status = $"已导入 {data.Count} 个分组、{data.Sum(g => g.Entries.Count)} 个条目。";
        return true;
    }

    public bool ExportQuickSend(string path)
    {
        try
        {
            var data = SnapshotQuickSend();
            QuickSendStore.Save(path, data);
            Status = $"已导出 {data.Count} 个分组、{data.Sum(g => g.Entries.Count)} 个条目。";
            return true;
        }
        catch (Exception ex)
        {
            Status = $"导出失败：{ex.GetBaseException().Message}";
            return false;
        }
    }

    private void LoadQuickSend()
    {
        if (!File.Exists(_quickSendPath)) return;
        try { ReplaceQuickSend(QuickSendStore.Load(_quickSendPath)); }
        catch (Exception ex)
        {
            // Keep the unreadable file aside rather than overwriting it on the next save.
            var backup = $"{_quickSendPath}.bad-{DateTime.Now:yyyyMMddHHmmss}";
            try { File.Move(_quickSendPath, backup); } catch { backup = _quickSendPath; }
            Status = $"快捷发送文件无法读取，已保留为 {backup}：{ex.GetBaseException().Message}";
        }
    }

    private void ReplaceQuickSend(List<QuickSendGroupData> data)
    {
        _quickSendLoading = true;
        try
        {
            QuickSendGroups.Clear();
            foreach (var groupData in data)
            {
                var group = new QuickSendGroup { Name = groupData.Name };
                foreach (var e in groupData.Entries)
                    group.Entries.Add(new QuickSendEntry { Name = e.Name, Content = e.Content, IsHex = e.IsHex });
                QuickSendGroups.Add(group);
            }
            SelectedQuickSendGroup = QuickSendGroups.FirstOrDefault();
        }
        finally { _quickSendLoading = false; }
    }

    private List<QuickSendGroupData> SnapshotQuickSend() => QuickSendGroups.Select(g => new QuickSendGroupData
    {
        Name = g.Name,
        Entries = g.Entries.Select(e => new QuickSendEntryData { Name = e.Name, Content = e.Content, IsHex = e.IsHex }).ToList()
    }).ToList();

    private void SaveQuickSend()
    {
        if (_quickSendLoading) return;
        try { QuickSendStore.Save(_quickSendPath, SnapshotQuickSend()); }
        catch (Exception ex) { Status = $"快捷发送保存失败：{ex.GetBaseException().Message}"; }
    }

    private void QuickSendGroups_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (QuickSendGroup group in e.NewItems)
            {
                group.PropertyChanged += QuickSendItem_PropertyChanged;
                group.Entries.CollectionChanged += QuickSendEntries_CollectionChanged;
                foreach (var entry in group.Entries) entry.PropertyChanged += QuickSendItem_PropertyChanged;
            }
        SaveQuickSend();
    }

    private void QuickSendEntries_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (QuickSendEntry entry in e.NewItems) entry.PropertyChanged += QuickSendItem_PropertyChanged;
        SaveQuickSend();
    }

    private void QuickSendItem_PropertyChanged(object? sender, PropertyChangedEventArgs e) => SaveQuickSend();

    // ---- Send history ----

    [RelayCommand]
    private void FillFromHistory(SendHistoryEntry? entry)
    {
        if (entry == null) return;
        // Fill only. Sending stays an explicit user action.
        SendMode = entry.Mode;
        SendText = entry.Content;
        Status = "已将历史记录填入发送框，未发送。";
    }

    [RelayCommand]
    private void ClearHistory()
    {
        SendHistory.Clear();
        Status = "已清空发送历史。";
    }
}
