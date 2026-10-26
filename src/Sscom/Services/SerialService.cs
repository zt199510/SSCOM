using System.IO;
using System.IO.Ports;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace Sscom.Services;

public sealed record SerialSettings(string PortName, int BaudRate, int DataBits,
    StopBits StopBits, Parity Parity, Handshake Handshake);

// The interface also permits deterministic queue/lifecycle verification without a UART.
public interface ISerialConnection
{
    string[] GetPortNames();
    Task<ChannelReader<byte[]>> OpenAsync(SerialSettings settings);
    Task WriteAsync(byte[] bytes);
    Task CloseAsync();
}

public sealed class SerialService : ISerialConnection
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SerialPort? _port;
    private CancellationTokenSource? _stop;
    private Task? _readerTask;

    public string[] GetPortNames() => SerialPort.GetPortNames()
        .Where(name => Regex.IsMatch(name, @"^COM\d+$", RegexOptions.IgnoreCase))
        .Select(name => (Name: name.ToUpperInvariant(), Number: int.TryParse(name[3..], out var n) ? n : 0))
        .Where(p => p.Number is >= 1 and <= 256)
        .OrderBy(p => p.Number).Select(p => p.Name).Distinct().ToArray();

    public async Task<ChannelReader<byte[]>> OpenAsync(SerialSettings settings)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_port != null) throw new InvalidOperationException("请先关闭当前串口。");
            var port = new SerialPort(settings.PortName, settings.BaudRate, settings.Parity,
                settings.DataBits, settings.StopBits)
            {
                Handshake = settings.Handshake, ReadTimeout = 100, WriteTimeout = 1000,
                ReadBufferSize = 65536, DiscardNull = false
            };
            try { await Task.Run(port.Open).ConfigureAwait(false); }
            catch { port.Dispose(); throw; }
            var queue = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
            {
                SingleReader = true, SingleWriter = true, AllowSynchronousContinuations = false
            });
            _port = port;
            _stop = new CancellationTokenSource();
            var token = _stop.Token;
            _readerTask = Task.Run(() => ReadLoop(port, queue.Writer, token));
            return queue.Reader;
        }
        finally { _gate.Release(); }
    }

    private static void ReadLoop(SerialPort port, ChannelWriter<byte[]> writer, CancellationToken stop)
    {
        Exception? error = null;
        Exception? lineError = null;
        SerialErrorReceivedEventHandler onError = (_, e) => Interlocked.CompareExchange(
            ref lineError, new IOException($"串口报告 {e.EventType}，数据可能不完整，请检查波特率、接线和流控。"), null);
        port.ErrorReceived += onError;
        try
        {
            var buffer = new byte[4096];
            while (!stop.IsCancellationRequested)
            {
                if (lineError != null) throw lineError;
                try
                {
                    var count = port.Read(buffer, 0, buffer.Length);
                    if (count > 0) writer.TryWrite(buffer.AsSpan(0, count).ToArray());
                }
                catch (TimeoutException) { /* Periodically check cancellation and device removal. */ }
                // Some drivers only report removal when querying the device.
                _ = port.BytesToRead;
            }
            // Snapshot a finite tail at the close boundary; never chase a continuous sender forever.
            var remaining = port.BytesToRead;
            while (remaining > 0)
            {
                var count = port.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                if (count <= 0) break;
                writer.TryWrite(buffer.AsSpan(0, count).ToArray());
                remaining -= count;
            }
            if (lineError != null) throw lineError;
        }
        catch (Exception ex) { error = ex; }
        finally
        {
            port.ErrorReceived -= onError;
            writer.TryComplete(error);
        }
    }

    public async Task WriteAsync(byte[] bytes)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var port = _port ?? throw new InvalidOperationException("串口未打开。");
            await Task.Run(() => port.Write(bytes, 0, bytes.Length)).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task CloseAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_port == null) return;
            _stop!.Cancel();
            if (_readerTask != null) await _readerTask.ConfigureAwait(false);
            var port = _port;
            _port = null;
            _stop.Dispose();
            _stop = null;
            _readerTask = null;
            await Task.Run(port.Dispose).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}
