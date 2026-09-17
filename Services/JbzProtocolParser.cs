using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using JBZUniveresalLunix.Models;

namespace JBZUniveresalLunix.Services;

public sealed class JbzProtocolParser
{
    private readonly StringBuilder _pending = new();

    public IReadOnlyList<JbzBoardEvent> Push(ReadOnlySpan<byte> bytes)
    {
        var events = new List<JbzBoardEvent>();
        foreach (byte value in bytes)
        {
            if (value is (byte)'\r' or (byte)'\n')
            {
                if (_pending.Length > 0)
                {
                    events.Add(ParseLine(_pending.ToString()));
                    _pending.Clear();
                }
                continue;
            }
            _pending.Append(value <= 0x7f ? (char)value : '\uFFFD');
        }
        return events;
    }

    public void Reset() => _pending.Clear();

    public static bool IsUniversalTesterIdentity(string? line)
    {
        string compact = string.Concat((line ?? string.Empty)
            .Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant();
        return compact.Contains("universaltester", StringComparison.Ordinal);
    }

    public static string ExtractFirmwareVersion(string? line)
    {
        string text = (line ?? string.Empty).Trim();
        if (text.Length == 0)
            return string.Empty;

        Match match = Regex.Match(
            text,
            @"(?:^|\s)V(?:ERSION)?\s*[:=]?\s*(?<version>\d+(?:\.\d+)+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["version"].Value : string.Empty;
    }

    public static JbzBoardEvent ParseLine(string? line)
    {
        string text = (line ?? string.Empty).Trim('\r', '\n', '\0', ' ');
        if (text.Length == 0) return new(JbzEventFamily.Empty, text);
        if (IsUniversalTesterIdentity(text))
            return new(JbzEventFamily.Idn, text, Values: [text]);
        if (text.StartsWith(":MODELNAME,", StringComparison.Ordinal))
            return Strings(JbzEventFamily.ModelName, text);
        if (text == ":START,ON") return new(JbzEventFamily.Start, text, Values: ["ON"]);
        if (text == ":MEASURE") return new(JbzEventFamily.Measure, text);
        if (text == ":CLEAR") return new(JbzEventFamily.Clear, text);
        if (text.StartsWith(":OPEN,", StringComparison.Ordinal)) return Numbers(JbzEventFamily.Open, text);
        if (text.StartsWith(":SHORT,", StringComparison.Ordinal)) return Numbers(JbzEventFamily.Short, text);
        if (text.StartsWith(":OTHER,", StringComparison.Ordinal)) return Numbers(JbzEventFamily.Other, text);
        if (text.StartsWith(":SEQ,", StringComparison.Ordinal)) return Strings(JbzEventFamily.Sequence, text);
        if (text == ":NEWTEST" || text.StartsWith(":NEWTEST,", StringComparison.Ordinal))
            return Strings(JbzEventFamily.NewTest, text);
        if (text == ":CLEARCONNECTOR" || text.StartsWith(":CLEARCONNECTOR,", StringComparison.Ordinal))
            return Strings(JbzEventFamily.ClearConnector, text);
        if (text.StartsWith(":TESTPIN,", StringComparison.Ordinal))
        {
            string[] parts = text.Split(',');
            if (parts.Length != 3 || !int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int pin) || pin <= 0)
                return new(JbzEventFamily.Raw, text, Values: [text]);
            string state = parts[2].Trim().ToUpperInvariant();
            return state is "ON" or "OFF"
                ? new(JbzEventFamily.TestPin, text, [pin], [state])
                : new(JbzEventFamily.Raw, text, Values: [text]);
        }
        if (text.StartsWith(":PIN,", StringComparison.Ordinal))
        {
            string[] parts = text.Split(',');
            if (parts.Length == 3 && int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int pin) && pin > 0 &&
                parts[2].Trim() is "0" or "1")
                return new(JbzEventFamily.TestPin, text, [pin], [parts[2].Trim() == "1" ? "ON" : "OFF"]);
            return new(JbzEventFamily.Raw, text, Values: [text]);
        }
        if (text.StartsWith(":CIRCUIT,", StringComparison.Ordinal))
        {
            int value = int.TryParse(text.AsSpan(9), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : -1;
            return new(JbzEventFamily.Circuit, text, [value]);
        }
        if (text.StartsWith(":INPUT,", StringComparison.Ordinal)) return ChannelState(JbzEventFamily.Input, text);
        if (text.StartsWith(":OUTPUT,", StringComparison.Ordinal)) return ChannelState(JbzEventFamily.Output, text);
        if (text.StartsWith(":RESISTOR,", StringComparison.Ordinal)) return Strings(JbzEventFamily.Resistor, text);
        if (text.StartsWith(":AMPARE,", StringComparison.Ordinal)) return Strings(JbzEventFamily.Ampare, text);
        if (text.StartsWith(":VOLTAGE,", StringComparison.Ordinal)) return Strings(JbzEventFamily.Voltage, text);
        if (text.StartsWith(":ERROR", StringComparison.Ordinal) || text.StartsWith(":NAK", StringComparison.Ordinal))
            return new(JbzEventFamily.Error, text, Values: [text]);
        if (text.StartsWith(":OK,", StringComparison.Ordinal)) return Strings(JbzEventFamily.Ok, text);
        if (text == ":ACK") return new(JbzEventFamily.Ack, text);
        if (text is "BOOT" or "BootLoader" or "START PROCESS") return new(JbzEventFamily.Boot, text, Values: [text]);
        return text switch
        {
            ":PASS" => new(JbzEventFamily.Pass, text), ":PEN" => new(JbzEventFamily.Pen, text),
            ":REMOVAL" => new(JbzEventFamily.Removal, text), ":UNCONNECT" => new(JbzEventFamily.Unconnect, text),
            ":STOP" => new(JbzEventFamily.Stop, text), _ => new(JbzEventFamily.Raw, text, Values: [text])
        };
    }

    private static JbzBoardEvent Numbers(JbzEventFamily family, string text) => new(
        family, text,
        text.Split(',').Skip(1).Select(v => int.TryParse(v.Trim(), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int n) ? (int?)n : null).Where(v => v.HasValue).Select(v => v!.Value).ToArray());

    private static JbzBoardEvent Strings(JbzEventFamily family, string text) =>
        new(family, text, Values: text.Split(',').Skip(1).Select(v => v.Trim()).ToArray());

    private static JbzBoardEvent ChannelState(JbzEventFamily family, string text)
    {
        string[] parts = text.Split(',');
        if (parts.Length < 2 || !int.TryParse(parts[1].Trim(), out int channel))
            return new(JbzEventFamily.Raw, text, Values: [text]);
        return new(family, text, [channel], [parts.Length > 2 ? parts[2].Trim() : string.Empty]);
    }
}
