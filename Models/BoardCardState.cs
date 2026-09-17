using JBZUniveresalLunix.Core;

namespace JBZUniveresalLunix.Models;

/// <summary>Trạng thái một scan-card 64 I/O trên TestView.</summary>
public sealed class BoardCardState : ObservableObject
{
    private bool _hasProbeActivity;
    private bool _isScanning;

    public int CardNumber { get; init; }
    public int ExpansionCardNumber { get; init; }
    public int ExpansionModuleNumber => ExpansionCardNumber;
    public int FirstGlobalIo { get; init; }
    public int LastGlobalIo { get; init; }
    public bool IsEnabled { get; init; } = true;

    public bool IsScanning
    {
        get => _isScanning;
        set
        {
            if (Set(ref _isScanning, value))
                Raise(nameof(StateText));
        }
    }

    public bool HasProbeActivity
    {
        get => _hasProbeActivity;
        set => Set(ref _hasProbeActivity, value);
    }

    public string CardText => $"CARD {CardNumber}";
    public string StateText => !IsEnabled ? "TẮT" : IsScanning ? "ĐANG QUÉT" : "BẬT";
    public string RangeText => IsEnabled ? $"IO {FirstGlobalIo}-{LastGlobalIo}" : "—";
}
