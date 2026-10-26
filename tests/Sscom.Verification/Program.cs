using System.Diagnostics;
using System.IO;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
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

    private static async Task VerifyAsync()
    {
        var fake = new FakeSerial();
        var vm = new MainViewModel(fake, Dispatcher.CurrentDispatcher);
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
        var activeVm = new MainViewModel(activeFake, Dispatcher.CurrentDispatcher);
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
}

internal sealed class FakeSerial : ISerialConnection
{
    private Channel<byte[]>? _channel;
    public string[] Ports { get; set; } = Array.Empty<string>();
    public SerialSettings? Settings { get; private set; }
    public Exception? OpenFailure { get; set; }
    public Exception? WriteFailure { get; set; }
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
        return Task.CompletedTask;
    }
    public Task CloseAsync()
    {
        _channel?.Writer.TryComplete();
        return Task.CompletedTask;
    }
}
