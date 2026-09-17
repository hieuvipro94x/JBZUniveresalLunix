using System.Globalization;
using JBZUniveresalLunix.Core;

namespace JBZUniveresalLunix.Models;

/// <summary>
/// Cấu hình phần cứng máy leak. Đây là cổng UART/RS232 riêng, hoàn toàn độc lập
/// với bo JBZ D2XX.
/// </summary>
public sealed class WaterProofMachineSettings
{
    public const int DefaultBaudRate = 115200;

    public string PortName { get; set; } = string.Empty;
    public int BaudRate { get; set; } = DefaultBaudRate;
    public bool AutoConnect { get; set; } = true;
    public int ReadTimeoutMs { get; set; } = 1000;
    public int WriteTimeoutMs { get; set; } = 1000;
}

/// <summary>
/// Cấu hình leak theo từng file/model THT. Chỉ model được bật mới chạy bước leak.
/// </summary>
public sealed class WaterProofModelSettings
{
    public bool Enabled { get; set; }
    public bool Channel1Enabled { get; set; } = true;
    public bool Channel2Enabled { get; set; } = true;
    public bool Channel3Enabled { get; set; }
    public string Channel1Connector { get; set; } = string.Empty;
    public string Channel2Connector { get; set; } = string.Empty;
    public string Channel3Connector { get; set; } = string.Empty;
    public double PressMin { get; set; } = 35.0;
    public double LeakLimit { get; set; } = 20.0;
    public int PressTimeMs { get; set; } = 1000;
    public int WaitTimeMs { get; set; } = 500;

    public bool IsChannelEnabled(int channel) => channel switch
    {
        1 => Channel1Enabled,
        2 => Channel2Enabled,
        3 => Channel3Enabled,
        _ => false
    };

    public string ConnectorForChannel(int channel) => channel switch
    {
        1 => Channel1Connector,
        2 => Channel2Connector,
        3 => Channel3Connector,
        _ => string.Empty
    };

    public int EnabledChannelCount =>
        (Channel1Enabled ? 1 : 0) +
        (Channel2Enabled ? 1 : 0) +
        (Channel3Enabled ? 1 : 0);

    public WaterProofModelSettings Clone() => new()
    {
        Enabled = Enabled,
        Channel1Enabled = Channel1Enabled,
        Channel2Enabled = Channel2Enabled,
        Channel3Enabled = Channel3Enabled,
        Channel1Connector = Channel1Connector,
        Channel2Connector = Channel2Connector,
        Channel3Connector = Channel3Connector,
        PressMin = PressMin,
        LeakLimit = LeakLimit,
        PressTimeMs = PressTimeMs,
        WaitTimeMs = WaitTimeMs
    };
}

public enum WaterProofStage
{
    Idle = 0,
    Connecting = 1,
    Pressurizing = 2,
    Waiting = 3,
    Evaluating = 4,
    Passed = 5,
    Failed = 6,
    Error = 7
}

/// <summary>Trạng thái từng kênh dùng trực tiếp cho card Leak trên TestWindow.</summary>
public sealed class WaterProofChannelResult : ObservableObject
{
    private double? _pressPressure;
    private double? _waitPressure;
    private double? _firstResultPressure;
    private double? _secondResultPressure;
    private double? _leak;
    private double? _leakLimit;
    private double? _liveMachineValue;
    private bool _isMeasured;
    private bool _passed;

    public int Channel { get; init; }
    public bool Enabled { get; init; } = true;
    public string Connector { get; init; } = string.Empty;
    public string ChannelText => string.IsNullOrWhiteSpace(Connector)
        ? $"CH{Channel}"
        : $"CH{Channel} • {Connector}";

    public double? PressPressure
    {
        get => _pressPressure;
        set
        {
            if (Set(ref _pressPressure, value))
                RaiseDisplay();
        }
    }

    public double? WaitPressure
    {
        get => _waitPressure;
        set
        {
            if (Set(ref _waitPressure, value))
                RaiseDisplay();
        }
    }

    public double? FirstResultPressure
    {
        get => _firstResultPressure;
        set
        {
            if (Set(ref _firstResultPressure, value))
                RaiseDisplay();
        }
    }

    public double? SecondResultPressure
    {
        get => _secondResultPressure;
        set
        {
            if (Set(ref _secondResultPressure, value))
                RaiseDisplay();
        }
    }

    public double? Leak
    {
        get => _leak;
        set
        {
            if (Set(ref _leak, value))
            {
                Raise(nameof(LeakText));
                Raise(nameof(PressureText));
            }
        }
    }

    public double? LeakLimit
    {
        get => _leakLimit;
        set
        {
            if (Set(ref _leakLimit, value))
                Raise(nameof(LeakLimitText));
        }
    }

    /// <summary>
    /// Giá trị mới nhất nhận trực tiếp từ kênh máy Leak để hiển thị realtime.
    /// Giá trị này chỉ phục vụ giao diện; PASS/FAIL vẫn chỉ dùng :RESULT cuối.
    /// </summary>
    public double? LiveMachineValue
    {
        get => _liveMachineValue;
        set
        {
            if (Set(ref _liveMachineValue, value))
                Raise(nameof(LiveMachineValueText));
        }
    }

    public bool IsMeasured
    {
        get => _isMeasured;
        set
        {
            if (Set(ref _isMeasured, value))
            {
                Raise(nameof(ResultText));
                Raise(nameof(LiveCellBackground));
                Raise(nameof(LiveCellForeground));
            }
        }
    }

    public bool Passed
    {
        get => _passed;
        set
        {
            if (Set(ref _passed, value))
            {
                Raise(nameof(ResultText));
                Raise(nameof(LiveCellBackground));
                Raise(nameof(LiveCellForeground));
            }
        }
    }

    // Thẻ tổng quan phải hiển thị độ sụt do máy Leak trả về, không hiển thị
    // áp suất nạp hoặc áp suất giữ. Trước khi có RESULT, độ sụt chưa xác định.
    public string PressureText => LeakText;

    public string FirstPressureText => FirstResultPressure.HasValue
        ? FirstResultPressure.Value.ToString("0.0##", CultureInfo.InvariantCulture)
        : "---";

    public string SecondPressureText => SecondResultPressure.HasValue
        ? SecondResultPressure.Value.ToString("0.0##", CultureInfo.InvariantCulture)
        : "---";

    public string LeakText => Leak.HasValue
        ? Leak.Value.ToString("0.0##", CultureInfo.InvariantCulture)
        : "---";

    public string LeakLimitText => LeakLimit.HasValue
        ? LeakLimit.Value.ToString("0.0##", CultureInfo.InvariantCulture)
        : "---";

    public string LiveMachineValueText => LiveMachineValue.HasValue
        ? LiveMachineValue.Value.ToString("0.0##", CultureInfo.InvariantCulture)
        : "---";

    public string LiveCellBackground => !IsMeasured
        ? "#FFFFFF"
        : Passed
            ? "#2AA84A"
            : "#C62828";

    public string LiveCellForeground => IsMeasured ? "#FFFFFF" : "#0F172A";

    public string ResultText => !IsMeasured ? "---" : Passed ? "PASS" : "FAIL";

    private void RaiseDisplay()
    {
        Raise(nameof(PressureText));
        Raise(nameof(FirstPressureText));
        Raise(nameof(SecondPressureText));
    }
}

public sealed record WaterProofProgress(
    WaterProofStage Stage,
    IReadOnlyList<double> Values,
    string RawLine);

public sealed record WaterProofRunResult(
    IReadOnlyList<WaterProofChannelMeasurement> Channels,
    bool Passed,
    string RawResult);

public sealed record WaterProofChannelMeasurement(
    int Channel,
    bool Enabled,
    double FirstPressure,
    double SecondPressure,
    double Leak,
    bool Passed);
