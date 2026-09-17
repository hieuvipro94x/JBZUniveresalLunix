using System;
using System.Collections.Generic;

namespace JBZUniveresalLunix.Services;

/// <summary>
/// Hardware/test settings compiled into the application. These internal values
/// are intentionally not exposed through the operator configuration file.
/// </summary>
public sealed class AppSettings
{
    public BoardSettings Board { get; set; } = new();
    public KeysightSettings Keysight { get; set; } = new();
    public TestSettings Test { get; set; } = new();

    public static AppSettings Load()
    {
        var settings = new AppSettings();
        settings.Normalize();
        return settings;
    }

    private void Normalize()
    {
        Board ??= new BoardSettings();
        Keysight ??= new KeysightSettings();
        Test ??= new TestSettings();
        Test.ResistanceChannels ??= [];
        Test.ResistanceMinimumSettleMs = Math.Clamp(Test.ResistanceMinimumSettleMs, 0, 60_000);
        Test.ResistanceSampleIntervalMs = Math.Clamp(Test.ResistanceSampleIntervalMs, 0, 10_000);
        Test.ResistanceStableSampleCount = Math.Clamp(Test.ResistanceStableSampleCount, 2, 10);
        Test.ResistanceStableAbsoluteToleranceOhm = Math.Max(0, Test.ResistanceStableAbsoluteToleranceOhm);
        Test.ResistanceStableRelativeTolerancePercent = Math.Max(0, Test.ResistanceStableRelativeTolerancePercent);
        Test.ResistanceStabilityTimeoutMs = Math.Clamp(Test.ResistanceStabilityTimeoutMs, 100, 120_000);
    }
}

public sealed class BoardSettings
{
    public int RequiredStableFrames { get; set; } = 1;
}

public sealed class KeysightSettings
{
    public string Resource { get; set; } = "";
    public string Command { get; set; } = ":MEASURE:RES?";
    public int SettleDelayMs { get; set; } = 300;
}

public sealed class TestSettings
{
    public int RelayPulseMs { get; set; } = 250;
    public int RelayInterlockMs { get; set; } = 430;
    public int PostResistanceRelayDelayMs { get; set; } = 0;
    public int PostRelayRestartDelayMs { get; set; } = 200;
    public bool AutoRestartAfterPass { get; set; } = true;

    // Legacy compatibility only. Từ V12.7, lỗi/chập/probe KHÔNG được phép
    // kích relay; hai field này chỉ còn để đọc appsettings cũ, không dùng
    // trong workflow lỗi. Relay tự động chỉ chạy khi PASS hợp lệ.
    public int FaultEjectRelay { get; set; } = 1;
    public int FaultEjectPulseMs { get; set; } = 250;

    public double ResistanceOpenThreshold { get; set; } = 1e30;
    public int ResistanceMinimumSettleMs { get; set; } = 300;
    public int ResistanceSampleIntervalMs { get; set; } = 50;
    public int ResistanceStableSampleCount { get; set; } = 2;
    public double ResistanceStableAbsoluteToleranceOhm { get; set; } = 5;
    public double ResistanceStableRelativeTolerancePercent { get; set; } = 0.2;
    public int ResistanceStabilityTimeoutMs { get; set; } = 2000;

    public List<ResistanceChannelSettings> ResistanceChannels
    {
        get;
        set;
    } = [];
}

public sealed class ResistanceChannelSettings
{
    public string Name { get; set; } = "R1";
    public int Channel { get; set; } = 1;

    public double MinOhm { get; set; } = 8000;
    public double MaxOhm { get; set; } = 10000;

    public string RouteA { get; set; } = "90 00 00 01";
    public string RouteB { get; set; } = "91 00 00 01";
}
