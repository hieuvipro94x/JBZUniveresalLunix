using JBZUniveresalLunix.Models;

namespace JBZUniveresalLunix.Services;

public readonly record struct StartupIoContactPair(int FirstIo, int SecondIo);

/// <summary>
/// Detects real electrical pairs already present before a production cycle is armed.
/// Universal Tester New provides semantic UART topology, so no legacy binary/probe
/// heuristics are needed here. This interlock never creates a product FAIL.
/// </summary>
public static class StartupIoInterlock
{
    public static IReadOnlyList<StartupIoContactPair> FindConnectedPairs(
        ScanFrame frame,
        ProductModel? model = null,
        BoardCapacity? capacity = null)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (frame.Mode != BoardScanMode.Production || !frame.Complete || frame.UnknownBytes > 0)
            return Array.Empty<StartupIoContactPair>();

        return frame.Connections
            .SelectMany(source => source.Value
                .Where(target => source.Key > 0 && target > 0 && source.Key != target)
                .Select(target => Normalize(source.Key, target)))
            .Distinct()
            .OrderBy(pair => pair.FirstIo)
            .ThenBy(pair => pair.SecondIo)
            .ToArray();
    }

    private static StartupIoContactPair Normalize(int sourceIo, int targetIo) =>
        sourceIo <= targetIo
            ? new StartupIoContactPair(sourceIo, targetIo)
            : new StartupIoContactPair(targetIo, sourceIo);
}
