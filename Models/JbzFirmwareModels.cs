namespace JBZUniveresalLunix.Models;

public sealed record JbzFirmwareBlock(uint Address, byte[] Data);

public sealed record JbzFirmwareImage(
    IReadOnlyList<JbzFirmwareBlock> Blocks,
    int TotalBytes,
    uint FirstAddress,
    uint LastAddressExclusive,
    string Sha256);

public enum JbzFirmwareStage
{
    Idle, EnteringBootloader, Connecting, Programming, Finishing,
    Verifying, Completed, Failed
}

public sealed record JbzFirmwareProgress(
    JbzFirmwareStage Stage,
    string Status,
    int CompletedPackets,
    int TotalPackets,
    uint CurrentAddress = 0)
{
    public int Percent => TotalPackets == 0 ? 0 :
        Math.Clamp((int)Math.Round(CompletedPackets * 100d / TotalPackets), 0, 100);
}
