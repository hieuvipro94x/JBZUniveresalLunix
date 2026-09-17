using System.Globalization;
using System.IO;
using System.Text;
using JBZUniveresalLunix.Models;

namespace JBZUniveresalLunix.Services;

public sealed class JbzModelCompileException(string message) : Exception(message);

public static class JbzModelCompiler
{
    private sealed record Pin(int Row, int Physical, string Connector, string LocalPin,
        string Wire, string Splice, string Section, string Color, string Special,
        string Parent, int[] Targets);

    public static JbzCompiledModel Compile(string path)
    {
        string fullPath = Path.GetFullPath(path);
        Dictionary<string, Dictionary<string, string>> ini = ReadIni(fullPath);
        Dictionary<string, string> connectorSection = Require(ini, "Connector");
        Dictionary<string, string> pinSection = Require(ini, "Pin");
        Dictionary<string, string> commonSection = Require(ini, "Common");
        string fileModelName = Path.GetFileNameWithoutExtension(fullPath);
        string modelName = commonSection.TryGetValue("Model", out string? declaredModel) &&
                           !string.IsNullOrWhiteSpace(declaredModel)
            ? declaredModel.Trim()
            : fileModelName;
        int connectorCount = Integer(Get(connectorSection, "Count"), "Connector/Count");
        var connectorNames = new List<string>();
        var connectorPinCounts = new List<int>();
        for (int i = 1; i <= connectorCount; i++)
        {
            string[] fields = Get(connectorSection, $"C{i}").Split('|');
            if (fields.Length < 2) throw Error($"Connector/C{i} must contain name|pin-count");
            string name = fields[0].Trim();
            if (name.Length == 0 || connectorNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                throw Error($"Connector/C{i} has an empty or duplicate name");
            connectorNames.Add(name);
            connectorPinCounts.Add(Integer(fields[1], $"Connector/C{i} pin count"));
        }
        int pinCount = Integer(Get(pinSection, "Count"), "Pin/Count");
        if (connectorPinCounts.Sum() != pinCount) throw Error("Connector pin total does not equal Pin/Count");

        var pins = new List<Pin>();
        var physical = new HashSet<int>();
        for (int i = 1; i <= pinCount; i++)
        {
            string[] f = Get(pinSection, $"P{i}").Split('|');
            if (f.Length != 10) throw Error($"Pin/P{i} must contain exactly 10 fields");
            int io = Integer(f[0], $"Pin/P{i} physical pin");
            if (!physical.Add(io)) throw Error($"Duplicate physical pin {io}");
            if (!connectorNames.Contains(f[1].Trim(), StringComparer.OrdinalIgnoreCase))
                throw Error($"Pin/P{i} references unknown connector {f[1]}");
            int[] rowTargets = f[9].Trim().Length == 0 ? [] : f[9].Split('/').Select(v => Integer(v, $"Pin/P{i} target")).ToArray();
            pins.Add(new(i, io, f[1].Trim(), f[2].Trim(), f[3].Trim(), f[4].Trim(), f[5].Trim(),
                f[6].Trim(), f[7].Trim(), f[8].Trim(), rowTargets));
        }

        NormalizeAo(pins);
        // Legacy Htdrv models also use numeric values in field 8 (for example
        // 322137 has Special=2 on P321/P323/P325). The original application
        // accepts these rows. They are model/setup metadata and must not make
        // the UART compiler reject an otherwise valid production model.
        // AO/A remains the only special type with a proven firmware flag.
        foreach (Pin pin in pins)
        {
            if (pin.Parent != "-1" || pin.Special.Length == 0)
                continue;

            bool knownAo = pin.Special.Equals("A", StringComparison.OrdinalIgnoreCase) ||
                           pin.Special.Equals("AO", StringComparison.OrdinalIgnoreCase);
            bool legacyNumeric = int.TryParse(pin.Special, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
            if (!knownAo && !legacyNumeric)
                throw Error($"Unproven special pin type at P{pin.Row}: {pin.Special}");
        }
        foreach (Pin pin in pins)
        {
            if (pin.Targets.Any(target => !physical.Contains(target))) throw Error($"P{pin.Row} references a missing target");
            if (pin.Parent is not ("" or "-1") && !physical.Contains(Integer(pin.Parent, $"Pin/P{pin.Row} parent")))
                throw Error($"P{pin.Row} references a missing parent");
        }
        Pin[] sources = pins.Where(p => p.Parent == "-1").ToArray();
        int[] targets = sources.SelectMany(p => p.Targets).ToArray();
        if (targets.Length != pins.Count(p => p.Parent is not ("" or "-1")))
            throw Error("Source target count does not equal target row count");

        var text = new List<string> { $":MODEL,{modelName}", $":PINCOUNT,{sources.Length}" };
        int offset = 0;
        for (int i = 0; i < sources.Length; i++)
        {
            Pin p = sources[i];
            text.Add($":PINDATA,{i},{p.Physical},{offset},{p.Targets.Length},0,{FirmwareFlags(p)}");
            offset += p.Targets.Length;
        }
        AddPackets(text, "ARRAY", targets);
        // Match the legacy JBZ model-loader behavior: CON contains one
        // connector-index entry for EVERY physical pin, followed by the two
        // legacy sentinels 5000 and 65535. AddPackets splits the complete map
        // into 64-value packets, so large models (322137: 349 pins) are not
        // silently truncated.
        int[] connectorMap = pins
            .Select(p => connectorNames.FindIndex(n => n.Equals(p.Connector, StringComparison.OrdinalIgnoreCase)))
            .Concat([5000, 65535])
            .ToArray();
        AddPackets(text, "CON", connectorMap);
        AddPackets(text, "CONNECTOR", connectorPinCounts.ToArray());
        text.Add(":FINISH");
        var commands = text.Select(v => new JbzProtocolCommand(v, Expectation(v))).ToArray();
        ProductModel product = BuildProduct(fullPath, modelName, commonSection, pins, connectorNames, connectorPinCounts);
        return new(modelName, product, commands, pinCount, sources.Length, targets.Length, connectorCount);
    }

    private static int FirmwareFlags(Pin pin)
    {
        // Proven by captured legacy uploads: Special=A uses firmware flag 16.
        // Numeric legacy types (for example Special=2 in 322137) are accepted
        // by the original model format, but no captured upload proves that the
        // numeric value itself is a UART bit-mask. Preserve legacy acceptance
        // without inventing firmware bits: they use the normal continuity flag 0.
        return pin.Special.Equals("A", StringComparison.OrdinalIgnoreCase) ||
               pin.Special.Equals("AO", StringComparison.OrdinalIgnoreCase)
            ? 16
            : 0;
    }

    private static ProductModel BuildProduct(string path, string modelName,
        IReadOnlyDictionary<string, string> common, List<Pin> pins, List<string> connectorNames, List<int> counts)
    {
        string declaredNo = common.TryGetValue("No", out string? no) ? no.Trim() : string.Empty;
        string customer = common.TryGetValue("Customer", out string? customerValue) ? customerValue.Trim() : string.Empty;
        var product = new ProductModel
        {
            ModelName = modelName,
            PartNumber = declaredNo.Length > 0 ? declaredNo :
                customer.Length > 0 ? customer : Path.GetFileNameWithoutExtension(path),
            ProductName = common.TryGetValue("Name", out string? name) ? name.Trim() : string.Empty,
            VehicleType = common.TryGetValue("Kind", out string? kind) ? kind.Trim() : string.Empty,
            CustomerCode = customer.Length > 0 ? customer : declaredNo,
            SourcePath = path
        };
        product.Pins = pins.Select((p, i) => new PinRecord(p.Connector, p.Wire, p.Physical, p.LocalPin, p.Splice, p.Section, p.Color, OriginalOrder: i)).ToList();
        product.Connectors = connectorNames.Select((name, i) => new ConnectorDefinition(name, counts[i], product.Pins.Where(p => p.Connector.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(p => new ConnectorPin(p.PinNumber, p.IoNumber, p.WireName, p)).ToArray())).ToList();
        product.Nets = pins.Where(p => p.Parent == "-1").Select(p => new WireNet(p.Wire, new[] { p.Physical }.Concat(p.Targets).ToArray(), product.Pins.Where(x => x.IoNumber == p.Physical || p.Targets.Contains(x.IoNumber)).ToArray())).ToList();
        return product;
    }

    private static void NormalizeAo(List<Pin> pins)
    {
        for (int index = 0; index < pins.Count; index++)
        {
            Pin source = pins[index];
            if (source.Parent != "-1" || !source.Connector.Equals("AO", StringComparison.OrdinalIgnoreCase)) continue;
            Pin[] branches = pins.Where(p => p.Connector.Length > 1 && p.Connector[0] is 'A' or 'a' && int.TryParse(p.Connector.AsSpan(1), out _)).OrderBy(p => int.Parse(p.Connector.AsSpan(1), CultureInfo.InvariantCulture)).ToArray();
            if (branches.Length == 0) continue;
            int[] branchIo = branches.Select(p => p.Physical).ToArray();
            if (source.Targets.Except(branchIo).Any()) throw Error($"AO P{source.Row} contains a target outside A1..An");
            pins[index] = source with { Targets = branchIo };
            foreach (Pin branch in branches)
            {
                if (branch.Targets.Length > 0 || branch.Parent is not ("" or "-1") && branch.Parent != source.Physical.ToString(CultureInfo.InvariantCulture)) throw Error($"Invalid AO branch P{branch.Row}");
                int branchIndex = pins.FindIndex(p => p.Row == branch.Row);
                pins[branchIndex] = branch with { Parent = source.Physical.ToString(CultureInfo.InvariantCulture) };
            }
        }
    }

    private static void AddPackets(List<string> commands, string family, int[] values)
    {
        int count = (values.Length + 63) / 64;
        commands.Add($":{family}COUNT,{count}");
        for (int i = 0; i < count; i++) { int[] chunk = values.Skip(i * 64).Take(64).ToArray(); commands.Add($":{family},{i},{chunk.Length},{string.Join(',', chunk)}"); }
    }

    private static JbzCommandExpectation Expectation(string command)
    {
        string[] p = command.TrimStart(':').Split(',');
        string family = p[0];
        return family switch
        {
            "MODEL" => new(":OK,MODEL", false, TimeSpan.FromSeconds(3)),
            "FINISH" => new(":OK,FINISH,", true, TimeSpan.FromSeconds(4)),
            "PINDATA" or "ARRAY" or "CON" or "CONNECTOR" => new($":OK,{family},{p[1]}", false, TimeSpan.FromSeconds(2)),
            _ => new($":OK,{family}", false, TimeSpan.FromSeconds(2))
        };
    }

    private static Dictionary<string, Dictionary<string, string>> ReadIni(string path)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        byte[] bytes = File.ReadAllBytes(path); string content = "";
        foreach (Encoding enc in new[] { new UTF8Encoding(true, true), Encoding.GetEncoding(949), Encoding.GetEncoding(1252) }) { try { content = enc.GetString(bytes); break; } catch (DecoderFallbackException) { } }
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase); Dictionary<string, string>? section = null;
        foreach (string raw in content.Split(["\r\n", "\n", "\r"], StringSplitOptions.None)) { string line = raw.Trim(); if (line.Length == 0 || line[0] is ';' or '#') continue; if (line.StartsWith('[') && line.EndsWith(']')) { section = new(StringComparer.OrdinalIgnoreCase); result[line[1..^1].Trim()] = section; continue; } int equals = line.IndexOf('='); if (section is not null && equals > 0) section[line[..equals].Trim()] = line[(equals + 1)..].Trim(); }
        return result;
    }
    private static Dictionary<string, string> Require(Dictionary<string, Dictionary<string, string>> ini, string section) => ini.TryGetValue(section, out var value) ? value : throw Error($"Missing [{section}] section");
    private static string Get(Dictionary<string, string> section, string key) => section.TryGetValue(key, out string? value) ? value : throw Error($"Missing {key}");
    private static int Integer(string value, string label) => int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : throw Error($"{label} is not an integer: {value}");
    private static JbzModelCompileException Error(string message) => new(message);
}
