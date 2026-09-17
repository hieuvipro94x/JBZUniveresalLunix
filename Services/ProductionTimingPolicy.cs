using JBZUniveresalLunix.Models;

namespace JBZUniveresalLunix.Services;

public static class ProductionTimingPolicy
{
    public const int DefaultIoScanIntervalMs = 2;
    public const int DefaultShortCircuitConfirmMs = 100;
    public const int DefaultWrongConnectionConfirmMs = 100;
    public const int DefaultProductSettleTimeMs = 200;
    public const int DefaultJigContactUnstableWindowMs = 1000;
    public const int StartupStopToHandshakeMs = 500;
    public const int StartupHandshakeToInit1Ms = 15;
    public const int StartupInit1ToInit2Ms = 350;
    public const int D2xxControlWaitSleepMs = 1;
    public const int D2xxIdlePollSleepMs = 2;

    public static void Normalize(ProductionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // Legacy config compatibility only: these timings are no longer operator settings.
        // Runtime code uses the fixed production policy constants below.
        settings.IoScanIntervalMs = DefaultIoScanIntervalMs;
        settings.ShortCircuitConfirmMs = DefaultShortCircuitConfirmMs;
        settings.ShortConfirmMs = settings.ShortCircuitConfirmMs;
        settings.WrongConnectionConfirmMs = DefaultWrongConnectionConfirmMs;
        settings.ProductSettleTimeMs = DefaultProductSettleTimeMs;
        settings.JigContactUnstableWindowMs = DefaultJigContactUnstableWindowMs;
        settings.ProbeReplacementThreshold = Math.Clamp(settings.ProbeReplacementThreshold, 1_000, 100_000_000);
    }
}
