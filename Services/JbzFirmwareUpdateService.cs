using System.Buffers.Binary;
using System.Text;
using System.Threading.Channels;
using JBZUniveresalLunix.Models;

namespace JBZUniveresalLunix.Services;

public sealed class JbzFirmwareUpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Trace-verified Universal New 1.2 bootloader exchange.</summary>
public sealed class JbzFirmwareUpdateService
{
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, Task> _write;
    private Channel<string>? _lines;
    public bool IsRunning { get; private set; }

    public JbzFirmwareUpdateService(Func<ReadOnlyMemory<byte>, CancellationToken, Task> write) => _write = write;

    public void NotifyLine(string line) => _lines?.Writer.TryWrite(line.Trim('\r', '\n', ' ', '\t'));

    public async Task UpdateAsync(JbzFirmwareImage image, IProgress<JbzFirmwareProgress>? progress, CancellationToken ct)
    {
        if (IsRunning) throw new InvalidOperationException("Firmware update is already running.");
        if (image.Blocks.Count == 0) throw new ArgumentException("Firmware image is empty.", nameof(image));
        IsRunning = true;
        _lines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        int completed = 0;
        try
        {
            Report(JbzFirmwareStage.EnteringBootloader, "Đang chuyển bo vào BootLoader...", completed);
            await _write(Encoding.ASCII.GetBytes(":DOWNLOAD\r\n"), ct);
            try
            {
                await WaitAsync("BOOT", TimeSpan.FromSeconds(5), ct);
            }
            catch (TimeoutException)
            {
                // Recovery mode: the board may already be in BootLoader, or the
                // application firmware may have restarted without echoing BOOT to
                // the newly opened serial session. Try the trace-verified CFT
                // handshake directly before declaring the board unrecoverable.
                Report(
                    JbzFirmwareStage.Connecting,
                    "Không thấy BOOT; thử bắt tay BootLoader trực tiếp...",
                    completed);
            }

            Report(JbzFirmwareStage.Connecting, "Đang kết nối BootLoader...", completed);
            await _write(Encoding.ASCII.GetBytes("CFT"), ct);
            await WaitAsync("OK,CONNECT", TimeSpan.FromSeconds(3), ct);
            foreach (JbzFirmwareBlock block in image.Blocks)
            {
                ct.ThrowIfCancellationRequested();
                await _write(BuildProgramPacket(block), ct);
                await WaitAsync("OK,PROGRAM", TimeSpan.FromMilliseconds(2500), ct);
                completed++;
                Report(JbzFirmwareStage.Programming, $"Đã ghi packet {completed}/{image.Blocks.Count}", completed, block.Address);
            }
            Report(JbzFirmwareStage.Finishing, "Đang hoàn tất firmware...", completed);
            await _write(Encoding.ASCII.GetBytes("F"), ct);
            await WaitAsync("OK,FINISH", TimeSpan.FromSeconds(3), ct);
            await WaitAsync("START PROCESS", TimeSpan.FromSeconds(3), ct);
            Report(JbzFirmwareStage.Completed, "Nạp firmware hoàn tất.", completed);
        }
        catch (OperationCanceledException) { Report(JbzFirmwareStage.Failed, "Nạp firmware đã bị dừng.", completed); throw; }
        catch (Exception ex) { Report(JbzFirmwareStage.Failed, "Nạp firmware thất bại.", completed); throw ex is JbzFirmwareUpdateException ? ex : new JbzFirmwareUpdateException(ex.Message, ex); }
        finally { _lines?.Writer.TryComplete(); _lines = null; IsRunning = false; }

        void Report(JbzFirmwareStage stage, string status, int done, uint address = 0) =>
            progress?.Report(new(stage, status, done, image.Blocks.Count, address));
    }

    public static byte[] BuildProgramPacket(JbzFirmwareBlock block)
    {
        if (block.Data.Length is < 1 or > JbzIntelHexParser.ProgramBlockSize)
            throw new ArgumentOutOfRangeException(nameof(block));
        byte[] packet = new byte[8 + block.Data.Length]; packet[0] = (byte)'P';
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1, 4), block.Address);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(5, 2), (ushort)block.Data.Length);
        block.Data.CopyTo(packet, 7); packet[^1] = (byte)(block.Data.Sum(value => value) & 0xff);
        return packet;
    }

    private async Task WaitAsync(string expected, TimeSpan timeout, CancellationToken ct)
    {
        Channel<string> channel = _lines ?? throw new InvalidOperationException("Firmware channel is inactive.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct); linked.CancelAfter(timeout);
        try
        {
            while (await channel.Reader.WaitToReadAsync(linked.Token))
                while (channel.Reader.TryRead(out string? line))
                    if (line.Equals(expected, StringComparison.OrdinalIgnoreCase)) return;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException($"Timeout waiting for {expected}."); }
        throw new TimeoutException($"Connection ended while waiting for {expected}.");
    }
}
