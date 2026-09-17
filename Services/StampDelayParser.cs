using System.Globalization;
using JBZUniveresalLunix.Models;

namespace JBZUniveresalLunix.Services;

public static class StampDelayParser
{
    public static bool TryParse(string? text, out int relay1Ms, out int relay2Ms)
    {
        relay1Ms = 0;
        relay2Ms = 0;

        string[] parts = (text ?? string.Empty)
            .Split(new[] { ',', ';', '/', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0)
            return false;

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out relay1Ms))
            return false;

        relay2Ms = relay1Ms;
        if (parts.Length >= 2 &&
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out relay2Ms))
        {
            return false;
        }

        relay1Ms = Math.Clamp(relay1Ms, 0, 60_000);
        relay2Ms = Math.Clamp(relay2Ms, 0, 60_000);
        return true;
    }

    public static (int Relay1Ms, int Relay2Ms) Get(ProductionSettings settings, int fallbackMs = 250)
    {
        if (TryParse(settings.StampDelay, out int relay1, out int relay2))
            return (relay1, relay2);

        int fallback = Math.Clamp(fallbackMs, 0, 60_000);
        return (fallback, fallback);
    }
}
