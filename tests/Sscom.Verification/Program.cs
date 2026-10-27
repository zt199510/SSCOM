using System.Diagnostics;
using System.IO;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Sscom;
using Sscom.Services;
using Sscom.ViewModels;

internal static class Program
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try { await VerifyAsync(); Console.WriteLine("ALL CHECKS PASSED"); app.Shutdown(0); }
            catch (Exception ex) { Console.Error.WriteLine(ex); app.Shutdown(1); }
        };
        return app.Run();
    }

    private static async Task WaitAsync(Func<bool> ready)
    {
        var watch = Stopwatch.StartNew();
        while (!ready())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Verification timed out");
            await Task.Delay(10);
        }
    }

    private static byte[] ReadBack(MainViewModel vm) => vm.Rows
        .SelectMany(row => Convert.FromHexString(row.Hex.Replace(" ", ""))).ToArray();

    private static readonly string TempRoot = Path.Combine(Path.GetTempPath(), "SscomVerification-" + Guid.NewGuid().ToString("N"));
    private static string TempStore(string name) => Path.Combine(TempRoot, name, "quick-send.json");

    private static async Task VerifyAsync()
    {
        try
        {
            await VerifyDay12Async();
            await VerifySendFeaturesAsync();
        }
        finally
        {
            try { Directory.Delete(TempRoot, true); } catch { /* best effort */ }
        }
    }

    private static async Task VerifyDay12Async()
    {
        var fake = new FakeSerial();
        var vm = new MainViewModel(fake, Dispatcher.CurrentDispatcher, TempStore("day12"));
        var window = new MainWindow(vm);
        window.Show();
        Check(!vm.TogglePortCommand.CanExecute(null) && vm.Status.Contains("没有可用串口"), "No-port state");
        Check(!vm.SendCommand.CanExecute(null), "Send must require an open port");
        fake.Ports = new[] { "COM7" };
        vm.RefreshPortsCommand.Execute(null);
        vm.BaudRate = "1199";
        await vm.TogglePortCommand.ExecuteAsync(null);
        Check(!vm.IsOpen && vm.Status.Contains("1200"), "Invalid baud rate");
        vm.BaudRate = "12345";
        fake.OpenFailure = new UnauthorizedAccessException();
        await vm.TogglePortCommand.ExecuteAsync(null);
        Check(!vm.IsOpen && vm.Status.Contains("占用"), "Busy port explanation");
        fake.OpenFailure = null;
        await vm.TogglePortCommand.ExecuteAsync(null);
        Check(vm.IsOpen && fake.Settings!.BaudRate == 12345, "Custom baud/open state");
        Console.WriteLine("PASS no-port, validation, busy-port, custom baud and open state");

        vm.SendText = "Hello";
        await vm.SendCommand.ExecuteAsync(null);
        await WaitAsync(() => vm.ReceivedBytes == 5);
        Check(vm.Rows[0].Hex == "48 65 6C 6C 6F" && vm.Rows[0].Ascii == "Hello" && vm.SentBytes == 5, "Hello pair/counters");
        var list = (ListView)window.FindName("ReceiveList");
        var view = (GridView)list.View;
        vm.DisplayMode = 2;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(view.Columns[1].Width == 0 && vm.ReceivedBytes == 5, "ASCII switch binding");
        vm.DisplayMode = 1;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(view.Columns[2].Width == 0 && vm.Rows[0].Ascii == "Hello", "HEX switch binding");
        vm.DisplayMode = 0;
        vm.SendMode = 1;
        vm.SendText = "00 1F 20 7E 7F 80 FF";
        await vm.SendCommand.ExecuteAsync(null);
        await WaitAsync(() => vm.ReceivedBytes == 12);
        Check(vm.Rows[0].Ascii == "Hello.. ~..." && vm.SentBytes == 12, "ASCII printable boundaries");
        vm.SendText = "0Z";
        await vm.SendCommand.ExecuteAsync(null);
        Check(vm.IsOpen && vm.SentBytes == 12 && vm.Status.Contains("无效"), "Invalid HEX changes no counters");
        Console.WriteLine("PASS Hello dual-view, actual column switching, ASCII boundaries and HEX validation");

        var expected = new List<byte>(ReadBack(vm));
        var payload = new byte[9216];
        new Random(20261026).NextBytes(payload);
        var lastTick = Stopwatch.GetTimestamp();
        double maximumDelayMs = 0;
        var heartbeat = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(10) };
        heartbeat.Tick += (_, _) =>
        {
            var now = Stopwatch.GetTimestamp();
            maximumDelayMs = Math.Max(maximumDelayMs, (now - lastTick) * 1000.0 / Stopwatch.Frequency);
            lastTick = now;
        };
        heartbeat.Start();
        var duration = Stopwatch.StartNew();
        await Task.Run(async () =>
        {
            for (var i = 0; i < 100; i++)
            {
                // Uneven serial reads intentionally cut through display-row boundaries.
                fake.Feed(payload[..7]);
                fake.Feed(payload[7..4095]);
                fake.Feed(payload[4095..]);
                expected.AddRange(payload);
                await Task.Delay(100);
            }
        });
        await WaitAsync(() => vm.ReceivedBytes == expected.Count);
        heartbeat.Stop();
        Check(ReadBack(vm).SequenceEqual(expected), "Sustained receive byte identity/order");
        foreach (var row in vm.Rows)
        {
            var raw = Convert.FromHexString(row.Hex.Replace(" ", ""));
            Check(row.Ascii == new string(raw.Select(b => b is >= 32 and <= 126 ? (char)b : '.').ToArray()), "View identity");
        }
        Check(vm.Rows.Select((row, i) => row.Offset == i * ReceiveBuffer.RowSize).All(v => v), "Offsets");
        Check(maximumDelayMs < 1000, "UI dispatcher stalled for at least a second");
        Console.WriteLine($"PASS rendered WPF sustained queue: 921600 bytes / {duration.Elapsed.TotalSeconds:F2}s; max dispatcher tick gap {maximumDelayMs:F1} ms; full byte/order/view/counter match");

        fake.Feed(new byte[] { 0xA1, 0xB2, 0xC3 });
        expected.AddRange(new byte[] { 0xA1, 0xB2, 0xC3 });
        await vm.TogglePortCommand.ExecuteAsync(null);
        Check(!vm.IsOpen && ReadBack(vm).SequenceEqual(expected), "Close drains queue");
        await vm.TogglePortCommand.ExecuteAsync(null);
        fake.Feed(new byte[] { 0x44, 0x45 });
        expected.AddRange(new byte[] { 0x44, 0x45 });
        fake.Disconnect();
        await WaitAsync(() => !vm.IsOpen);
        Check(vm.Status.Contains("中断") && ReadBack(vm).SequenceEqual(expected), "Disconnect drains queued bytes and retains history");
        await vm.TogglePortCommand.ExecuteAsync(null);
        vm.SendMode = 0;
        vm.SendText = "Hello";
        await vm.SendCommand.ExecuteAsync(null);
        expected.AddRange("Hello".Select(c => (byte)c));
        await WaitAsync(() => vm.ReceivedBytes == expected.Count);
        Check(ReadBack(vm).SequenceEqual(expected) && vm.SentBytes == 17, "Reopen clean session");
        fake.WriteFailure = new TimeoutException();
        await vm.SendCommand.ExecuteAsync(null);
        Check(!vm.IsOpen && vm.SentBytes == 17 && vm.Status.Contains("可能部分写入"), "Write timeout closes and excludes uncertain writes");
        Console.WriteLine("PASS queued-tail close, simulated disconnect, reopen, retained history and write timeout");

        var real = new SerialService();
        var names = real.GetPortNames();
        Console.WriteLine("Real serial inventory: " + (names.Length == 0 ? "none" : string.Join(", ", names)));
        var missing = Enumerable.Range(1, 256).Select(i => "COM" + i).FirstOrDefault(p => !names.Contains(p));
        if (missing != null)
        {
            try
            {
                await real.OpenAsync(new SerialSettings(missing, 115200, 8, System.IO.Ports.StopBits.One, System.IO.Ports.Parity.None, System.IO.Ports.Handshake.None));
                throw new Exception("Missing port unexpectedly opened");
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            { Console.WriteLine("PASS actual SerialPort missing-port failure: " + ex.GetType().Name); }
            finally { await real.CloseAsync(); }
        }
        await vm.ShutdownAsync();
        var closed = new TaskCompletionSource<bool>();
        window.Closed += (_, _) => closed.SetResult(true);
        window.Close();
        await closed.Task;
        Console.WriteLine("PASS window close after synchronous cleanup (Closing reentrancy regression)");

        var activeFake = new FakeSerial { Ports = new[] { "COM8" } };
        var activeVm = new MainViewModel(activeFake, Dispatcher.CurrentDispatcher, TempStore("day12-active"));
        var activeWindow = new MainWindow(activeVm);
        activeWindow.Show();
        await activeVm.TogglePortCommand.ExecuteAsync(null);
        activeFake.Feed(payload);
        var activeClosed = new TaskCompletionSource<bool>();
        activeWindow.Closed += (_, _) => activeClosed.SetResult(true);
        activeWindow.Close();
        await activeClosed.Task;
        Check(!activeVm.IsOpen && ReadBack(activeVm).SequenceEqual(payload), "Window shutdown drains active session");
        Check(!activeVm.TogglePortCommand.CanExecute(null), "Shutdown prevents reopen");
        Console.WriteLine("PASS window close with active queued session retains every byte");
    }

    private static T? Find<T>(DependencyObject root, Func<T, bool>? match = null) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t && (match == null || match(t))) return t;
            if (Find(child, match) is { } found) return found;
        }
        return null;
    }

    // Same route as a mouse click: the button's own command invocation.
    private static async Task ClickAsync(Button button)
    {
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!).Invoke();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static int TimedCount(MainViewModel vm) => vm.SendHistory.Count(h => h.Source == SendSource.Timed);

    private static string Snapshot(MainViewModel vm) => string.Join("\n", vm.QuickSendGroups.Select(g =>
        $"[{g.Name}]" + string.Concat(g.Entries.Select(e => $"|{e.Name}\u0001{e.Content}\u0001{e.IsHex}"))));

    private static async Task VerifySendFeaturesAsync()
    {
        var store = TempStore("features");
        var fake = new FakeSerial { Ports = new[] { "COM9" } };
        var vm = new MainViewModel(fake, Dispatcher.CurrentDispatcher, store);
        var window = new MainWindow(vm);
        window.Show();
        Check(!vm.StartTimedSendCommand.CanExecute(null) && !vm.StopTimedSendCommand.CanExecute(null), "Timed send requires an open port");
        Check(vm.TimedSendStateText == "定时发送已停止", "Initial timed state");
        await vm.TogglePortCommand.ExecuteAsync(null);
        Check(vm.IsOpen, "Open for Day 3-4 checks");

        // ---- Timed sending ----
        vm.TimedIntervalText = "5";
        vm.StartTimedSendCommand.Execute(null);
        Check(!vm.IsTimedSending && vm.Status.Contains("定时间隔"), "Interval below minimum rejected");
        vm.TimedIntervalText = "100";
        vm.SendMode = 1;
        vm.SendText = "0Z";
        vm.StartTimedSendCommand.Execute(null);
        Check(!vm.IsTimedSending && vm.Status.Contains("发送内容无效"), "Invalid content refuses to start");

        vm.SendMode = 0;
        vm.SendText = "T";
        var writes0 = fake.Writes;
        var received0 = vm.ReceivedBytes;
        vm.StartTimedSendCommand.Execute(null);
        Check(vm.IsTimedSending && !vm.StartTimedSendCommand.CanExecute(null) && vm.StopTimedSendCommand.CanExecute(null)
              && !vm.CanEditTimedInterval && vm.TimedSendStateText.Contains("100 ms"), "Running state");
        await Task.Delay(1000);
        var firstSecond = fake.Writes - writes0;
        vm.SendText = "U"; // the timer must pick up the current send-box text
        await Task.Delay(400);
        vm.StopTimedSendCommand.Execute(null);
        var total = fake.Writes - writes0;
        Check(!vm.IsTimedSending && vm.StartTimedSendCommand.CanExecute(null) && vm.TimedSendStateText.Contains("手动停止"), "Stopped state");
        Check(firstSecond is >= 6 and <= 11, $"100 ms interval sent {firstSecond} times in 1 s");
        Check(vm.SendHistory.Any(h => h.Source == SendSource.Timed && h.Content == "U"), "Timed send uses current send-box text");
        Check(TimedCount(vm) == total && vm.SentBytes == total, "Every timed write recorded and counted");
        await WaitAsync(() => vm.ReceivedBytes == received0 + total);
        var afterStop = fake.Writes;
        await Task.Delay(500);
        Check(fake.Writes == afterStop, "No sends after Stop");
        Console.WriteLine($"PASS timed send: 100 ms interval → {firstSecond} sends in first 1.0 s, {total} in 1.4 s; live text; stop halts sends");

        // Rapid start/stop must leave exactly one running timer.
        for (var i = 0; i < 5; i++) { vm.StartTimedSendCommand.Execute(null); vm.StopTimedSendCommand.Execute(null); }
        vm.StartTimedSendCommand.Execute(null);
        vm.StartTimedSendCommand.Execute(null); // CanExecute is false; an ignored second start must not double the rate
        var w = fake.Writes;
        await Task.Delay(1000);
        vm.StopTimedSendCommand.Execute(null);
        Check(fake.Writes - w is >= 6 and <= 11, $"Single timer after restart cycles ({fake.Writes - w} sends/s)");

        vm.StartTimedSendCommand.Execute(null);
        await Task.Delay(250);
        await vm.TogglePortCommand.ExecuteAsync(null);
        Check(!vm.IsOpen && !vm.IsTimedSending && vm.TimedSendStateText.Contains("串口已关闭"), "Close stops timed send");
        w = fake.Writes;
        await Task.Delay(400);
        Check(fake.Writes == w, "No sends after close");
        await vm.TogglePortCommand.ExecuteAsync(null);
        await Task.Delay(400);
        Check(vm.IsOpen && !vm.IsTimedSending && fake.Writes == w, "Reopen does not resume a timer");
        vm.StartTimedSendCommand.Execute(null);
        w = fake.Writes;
        await Task.Delay(1000);
        vm.StopTimedSendCommand.Execute(null);
        Check(fake.Writes - w is >= 6 and <= 11, $"Single timer after reopen ({fake.Writes - w} sends/s)");

        vm.StartTimedSendCommand.Execute(null);
        await Task.Delay(250);
        fake.Disconnect();
        await WaitAsync(() => !vm.IsOpen);
        // A tick racing the unplug reports the failed write instead; either way the port and timer stop.
        Check(!vm.IsTimedSending && (vm.Status.Contains("中断") || vm.Status.Contains("发送失败")), "Unplug stops timed send");
        w = fake.Writes;
        await Task.Delay(400);
        Check(fake.Writes == w, "No sends after unplug");
        await vm.TogglePortCommand.ExecuteAsync(null);
        await Task.Delay(400);
        Check(vm.IsOpen && !vm.IsTimedSending && fake.Writes == w, "Reopen after unplug does not resume a timer");
        vm.StartTimedSendCommand.Execute(null);
        w = fake.Writes;
        await Task.Delay(1000);
        vm.StopTimedSendCommand.Execute(null);
        Check(fake.Writes - w is >= 6 and <= 11, $"Single timer after unplug/reopen ({fake.Writes - w} sends/s)");
        Console.WriteLine("PASS timed send stops on close and simulated unplug; reopen never resumes or duplicates the timer");

        // Failed ticks are not recorded in history, so they must leave a visible reason.
        var historyBeforeFailures = vm.SendHistory.Count;
        vm.StartTimedSendCommand.Execute(null);
        vm.SendMode = 1;
        vm.SendText = "0Z";
        await WaitAsync(() => !vm.IsTimedSending);
        Check(vm.IsOpen && vm.Status.StartsWith("定时发送已停止：发送内容无效") && vm.TimedSendStateText.Contains("发送内容无效"), "Invalid tick is visible");
        vm.SendMode = 0;
        vm.SendText = "T";
        vm.StartTimedSendCommand.Execute(null);
        fake.WriteFailure = new TimeoutException();
        await WaitAsync(() => vm.Status.StartsWith("定时发送已停止：发送失败"));
        Check(!vm.IsOpen && !vm.IsTimedSending && vm.Status.Contains("可能部分写入") && vm.TimedSendStateText.Contains("发送失败"), "Failed write tick is visible");
        Check(vm.SendHistory.Count == historyBeforeFailures, "Failed ticks are not recorded");
        fake.WriteFailure = null;
        await vm.TogglePortCommand.ExecuteAsync(null);
        Check(vm.IsOpen && !vm.IsTimedSending, "Reopen after timed write failure");
        Console.WriteLine("PASS failed timed ticks (invalid content, write failure) stop the timer with a visible reason and no history entry");

        // ---- Quick-send groups ----
        Check(vm.QuickSendGroups.Count == 0, "Empty store starts empty");
        vm.SendMode = 0;
        vm.SendText = "AT+GMR";
        vm.AddQuickSendEntryCommand.Execute(null); // creates a default group when none exists
        var groupA = vm.SelectedQuickSendGroup!;
        Check(groupA.Name == "默认分组" && groupA.Entries.Count == 1 && groupA.Entries[0].Content == "AT+GMR" && !groupA.Entries[0].IsHex, "Add entry from send box");
        groupA.Name = "设备 A \"引号\" \\ 斜杠";
        groupA.Entries[0].Name = "版本查询";
        vm.AddQuickSendEntryCommand.Execute(null);
        groupA.Entries[1].Name = "Modbus 读寄存器";
        groupA.Entries[1].Content = "01 03 00 00 00 0A C5 CD";
        groupA.Entries[1].IsHex = true;
        vm.AddQuickSendEntryCommand.Execute(null);
        groupA.Entries[2].Name = "空内容";
        groupA.Entries[2].Content = "";
        vm.AddQuickSendEntryCommand.Execute(null);
        vm.DeleteQuickSendEntryCommand.Execute(groupA.Entries[3]);
        Check(groupA.Entries.Count == 3, "Delete entry");
        vm.AddQuickSendGroupCommand.Execute(null);
        var groupB = vm.SelectedQuickSendGroup!;
        groupB.Name = "分组 B";
        vm.AddQuickSendEntryCommand.Execute(null);
        groupB.Entries[0].Name = "坏 HEX";
        groupB.Entries[0].Content = "0Z";
        groupB.Entries[0].IsHex = true;
        vm.AddQuickSendGroupCommand.Execute(null);
        vm.SelectedQuickSendGroup!.Name = "待删除";
        vm.DeleteSelectedQuickSendGroup();
        vm.AddQuickSendGroupCommand.Execute(null);
        vm.SelectedQuickSendGroup!.Name = "空分组";
        Check(vm.QuickSendGroups.Count == 3 && File.Exists(store), "Group add/delete and auto-save");
        var expectedSnapshot = Snapshot(vm);

        var restarted = new MainViewModel(new FakeSerial(), Dispatcher.CurrentDispatcher, store);
        Check(Snapshot(restarted) == expectedSnapshot, "Quick groups survive restart");
        var exportPath = Path.Combine(TempRoot, "export", "groups.json");
        Check(vm.ExportQuickSend(exportPath), "Export");
        var imported = new MainViewModel(new FakeSerial(), Dispatcher.CurrentDispatcher, TempStore("imported"));
        Check(imported.ImportQuickSend(exportPath) && Snapshot(imported) == expectedSnapshot, "Import equals export");
        Check(imported.QuickSendGroups.Count == 3 && imported.QuickSendGroups.Sum(g => g.Entries.Count) == 4, "Imported counts");
        var importedRestart = new MainViewModel(new FakeSerial(), Dispatcher.CurrentDispatcher, TempStore("imported"));
        Check(Snapshot(importedRestart) == expectedSnapshot, "Imported groups persisted");
        var badPath = Path.Combine(TempRoot, "export", "bad.json");
        File.WriteAllText(badPath, "{ \"version\": 1, \"groups\": [");
        Check(!imported.ImportQuickSend(badPath) && Snapshot(imported) == expectedSnapshot, "Bad import changes nothing");
        Console.WriteLine("PASS quick groups: add/edit/delete, auto-save, restart, export → import (3 groups / 4 entries, exact content), bad import rejected");

        // Quick send through the real DataGrid button.
        vm.SelectedQuickSendGroup = groupA;
        ((TabControl)window.FindName("SidePanel")).SelectedIndex = 0;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var grid = (DataGrid)window.FindName("QuickSendGrid");
        grid.UpdateLayout();
        var modbusRow = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(groupA.Entries[1]);
        var quickButton = Find<Button>(modbusRow, b => Equals(b.Content, "发送"))!;
        var sentBefore = vm.SentBytes;
        var receivedBefore = vm.ReceivedBytes;
        await ClickAsync(quickButton);
        await WaitAsync(() => vm.ReceivedBytes == receivedBefore + 8);
        var tail = ReadBack(vm)[^8..];
        Check(tail.SequenceEqual(new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x0A, 0xC5, 0xCD }) && vm.SentBytes == sentBefore + 8, "Quick HEX send bytes/counters");
        Check(vm.SendHistory[^1].Source == SendSource.Quick && vm.SendHistory[^1].Mode == 1 && vm.SendText == "AT+GMR", "Quick send recorded, send box untouched");
        var historyCount = vm.SendHistory.Count;
        await vm.QuickSendCommand.ExecuteAsync(groupB.Entries[0]);
        Check(vm.IsOpen && vm.Status.StartsWith("快捷条目“坏 HEX”：发送内容无效") && vm.SendHistory.Count == historyCount && vm.SentBytes == sentBefore + 8, "Quick send uses shared validation");
        await vm.QuickSendCommand.ExecuteAsync(groupA.Entries[0]);
        Check(vm.SendHistory[^1].Source == SendSource.Quick && vm.SendHistory[^1].Content == "AT+GMR" && vm.SentBytes == sentBefore + 14, "Quick ASCII send");
        Console.WriteLine("PASS quick send via grid button uses the shared send path (bytes, counters, validation, history)");

        // ---- Send history ----
        vm.SendMode = 0;
        vm.SendText = "Manual";
        await vm.SendCommand.ExecuteAsync(null);
        var sources = vm.SendHistory.Select(h => h.Source).Distinct().ToArray();
        Check(sources.Contains(SendSource.Manual) && sources.Contains(SendSource.Timed) && sources.Contains(SendSource.Quick), "History has all three sources");
        Check(vm.SendHistory.Zip(vm.SendHistory.Skip(1)).All(p => p.First.Time <= p.Second.Time), "History in time order");
        Check(vm.SendHistory.Sum(h => h.ByteCount) == vm.SentBytes, "History covers every counted send");

        ((TabControl)window.FindName("SidePanel")).SelectedIndex = 1;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var historyList = (ListBox)window.FindName("HistoryList");
        var hexEntry = vm.SendHistory.Last(h => h.Mode == 1);
        historyList.ScrollIntoView(hexEntry);
        historyList.UpdateLayout();
        var item = (ListBoxItem)historyList.ItemContainerGenerator.ContainerFromItem(hexEntry);
        var writesBefore = fake.Writes;
        sentBefore = vm.SentBytes;
        historyCount = vm.SendHistory.Count;
        vm.SendText = "something else";
        await ClickAsync(Find<Button>(item)!);
        await Task.Delay(300);
        Check(vm.SendText == "01 03 00 00 00 0A C5 CD" && vm.SendMode == 1, "History click fills send box and mode");
        Check(fake.Writes == writesBefore && vm.SentBytes == sentBefore && vm.SendHistory.Count == historyCount, "History click does not send");
        vm.ClearHistoryCommand.Execute(null);
        Check(vm.SendHistory.Count == 0, "Clear history");
        Console.WriteLine("PASS history: manual/timed/quick in time order; clicking an entry fills only; clear works");

        await vm.ShutdownAsync();
        window.Close();
    }
}

internal sealed class FakeSerial : ISerialConnection
{
    private Channel<byte[]>? _channel;
    public string[] Ports { get; set; } = Array.Empty<string>();
    public SerialSettings? Settings { get; private set; }
    public Exception? OpenFailure { get; set; }
    public Exception? WriteFailure { get; set; }
    public int Writes { get; private set; }
    public string[] GetPortNames() => Ports;
    public Task<ChannelReader<byte[]>> OpenAsync(SerialSettings settings)
    {
        if (OpenFailure != null) throw OpenFailure;
        Settings = settings;
        _channel = Channel.CreateUnbounded<byte[]>();
        return Task.FromResult(_channel.Reader);
    }
    public void Feed(byte[] bytes)
    {
        if (_channel == null || !_channel.Writer.TryWrite(bytes.ToArray())) throw new Exception("Feed into closed session");
    }
    public void Disconnect() => _channel!.Writer.TryComplete(new IOException("Simulated unplug"));
    public Task WriteAsync(byte[] bytes)
    {
        if (WriteFailure != null) throw WriteFailure;
        Feed(bytes);
        Writes++;
        return Task.CompletedTask;
    }
    public Task CloseAsync()
    {
        _channel?.Writer.TryComplete();
        return Task.CompletedTask;
    }
}
