using JBZUniveresalLunix.Models;

namespace JBZUniveresalLunix.Services;

/// <summary>
/// Canonical R1-R10 production measurement plan. Slot order, enabled/channel
/// filtering and normalized Min/Max values must come from this one helper so
/// TestEngine and TestViewModel cannot select different physical channels.
/// </summary>
public static class ResistanceMeasurementPlan
{
    public const int SlotCount = 10;
    public const int DisabledChannel = 0;
    public const int MinChannel = 1;
    public const int MaxChannel = 10;

    public static ResistanceChannelSetting[] Normalize(
        IEnumerable<ResistanceChannelSetting?>? configured,
        Action<string>? warning = null)
    {
        ResistanceChannelSetting?[] source = configured?.ToArray() ?? [];
        var candidates = new Dictionary<int, List<ResistanceChannelSetting>>();

        for (int index = 0; index < source.Length; index++)
        {
            ResistanceChannelSetting? item = source[index];
            if (item is null)
            {
                warning?.Invoke($"Bỏ qua resistance slot null tại vị trí {index + 1}.");
                continue;
            }

            if (!TryGetSlotOrdinal(item.Name, out int ordinal))
            {
                // Cấu hình rất cũ có thể không ghi Name nhưng vẫn lưu theo vị
                // trí. Giữ dữ liệu theo index khi còn nằm trong R1-R10.
                if (string.IsNullOrWhiteSpace(item.Name) && index < SlotCount)
                {
                    ordinal = index + 1;
                    warning?.Invoke($"Resistance slot trống tên tại vị trí {index + 1}; khôi phục thành R{ordinal}.");
                }
                else
                {
                    warning?.Invoke($"Bỏ qua resistance slot không hợp lệ '{item.Name}'.");
                    continue;
                }
            }

            if (!candidates.TryGetValue(ordinal, out List<ResistanceChannelSetting>? list))
            {
                list = [];
                candidates.Add(ordinal, list);
            }
            list.Add(item);
        }

        var normalized = new ResistanceChannelSetting[SlotCount];
        for (int ordinal = 1; ordinal <= SlotCount; ordinal++)
        {
            ResistanceChannelSetting? selected = null;
            if (candidates.TryGetValue(ordinal, out List<ResistanceChannelSetting>? list))
            {
                selected = list.FirstOrDefault(IsValidWithoutNormalization) ?? list[0];
                if (list.Count > 1)
                    warning?.Invoke($"Trùng tên R{ordinal}; giữ bản ghi hợp lệ đầu tiên trong {list.Count} bản ghi.");
            }

            selected ??= new ResistanceChannelSetting
            {
                Enabled = false,
                Name = $"R{ordinal}",
                Channel = ordinal
            };

            int channel = Math.Clamp(
                selected.Channel,
                DisabledChannel,
                MaxChannel);
            double minOhm = NormalizeMinimum(selected.MinOhm);
            double maxOhm = NormalizeMaximum(selected.MaxOhm, minOhm);

            if (channel != selected.Channel ||
                minOhm != selected.MinOhm ||
                maxOhm != selected.MaxOhm)
            {
                warning?.Invoke(
                    $"Chuẩn hóa R{ordinal}: Channel {selected.Channel}->{channel}, " +
                    $"Min {selected.MinOhm}->{minOhm}, Max {selected.MaxOhm}->{maxOhm}.");
            }

            normalized[ordinal - 1] = new ResistanceChannelSetting
            {
                Enabled = selected.Enabled,
                Name = $"R{ordinal}",
                Channel = channel,
                MinOhm = minOhm,
                MaxOhm = maxOhm
            };
        }

        return normalized;
    }

    public static List<ResistanceStep> BuildEnabledSteps(ProductionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return Normalize(settings.ResistanceChannels)
            .Where(slot => slot.Enabled &&
                slot.Channel is >= MinChannel and
                    <= MaxChannel)
            .Select(slot => new ResistanceStep(
                slot.Name,
                slot.Channel,
                slot.MinOhm,
                slot.MaxOhm,
                string.Empty,
                string.Empty))
            .ToList();
    }

    /// <summary>
    /// Manual Settings measurement: 0 measures every enabled slot; CH1-CH10
    /// measures the first configured slot mapped to that physical channel even
    /// when that slot is disabled for automatic Production measurement.
    /// </summary>
    public static List<ResistanceStep> BuildManualSteps(
        ProductionSettings settings,
        int selectedChannel)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (selectedChannel is < DisabledChannel or > MaxChannel)
            throw new ArgumentOutOfRangeException(nameof(selectedChannel));

        if (selectedChannel == DisabledChannel)
            return BuildEnabledSteps(settings);

        ResistanceChannelSetting? slot = Normalize(settings.ResistanceChannels)
            .Where(item => item.Channel == selectedChannel)
            .OrderByDescending(item => item.Enabled)
            .FirstOrDefault();
        if (slot is null)
            return [];

        return
        [
            new ResistanceStep(
                slot.Name,
                slot.Channel,
                slot.MinOhm,
                slot.MaxOhm,
                string.Empty,
                string.Empty)
        ];
    }

    public static bool TryGetSlotOrdinal(string? name, out int ordinal)
    {
        string value = (name ?? string.Empty).Trim();
        ordinal = 0;
        bool parsed = value.Length >= 2 &&
                      (value[0] is 'R' or 'r') &&
                      int.TryParse(value.AsSpan(1), out ordinal) &&
                      ordinal is >= 1 and <= SlotCount;
        if (!parsed)
            ordinal = 0;
        return parsed;
    }

    private static bool IsValidWithoutNormalization(ResistanceChannelSetting item) =>
        item.Channel is >= DisabledChannel and <= MaxChannel &&
        double.IsFinite(item.MinOhm) &&
        double.IsFinite(item.MaxOhm) &&
        item.MinOhm >= 0 &&
        item.MaxOhm >= item.MinOhm;

    private static double NormalizeMinimum(double value) =>
        double.IsFinite(value) ? Math.Max(0, value) : 0;

    private static double NormalizeMaximum(double value, double minimum) =>
        double.IsFinite(value) ? Math.Max(minimum, value) : minimum;
}
