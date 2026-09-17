namespace JBZUniveresalLunix.Models;

/// <summary>
/// Cấu hình continuity chẩn đoán học từ frame thật. Không phải THT và không
/// mang semantics Production/relay/resistance/leak/label.
/// </summary>
public sealed class LearnedTopologyProfile
{
    public int SchemaVersion { get; init; } = 1;
    public string ProfileType { get; init; } = "DiagnosticContinuity";
    public string ProductCode { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public int ExpansionCardCount { get; init; }
    public int FirstIo { get; init; }
    public int LastIo { get; init; }
    public int RequiredStableFrames { get; init; }
    public int ObservedStableFrames { get; init; }
    public List<LearnedTopologyNetwork> Networks { get; init; } = [];
}

public sealed class LearnedTopologyNetwork
{
    public string Name { get; init; } = string.Empty;
    public List<int> Ios { get; init; } = [];
}

public sealed record LearnedTopologyRow(int Number, string Connection);

public sealed record LearnedTopologySnapshot(
    string Signature,
    IReadOnlyList<LearnedTopologyNetwork> Networks,
    IReadOnlyList<LearnedTopologyRow> Rows);
