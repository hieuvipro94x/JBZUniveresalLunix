using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using JBZUniveresalLunix.Models;

namespace JBZUniveresalLunix.Services;

public static class JbzIntelHexParser
{
    public const int ProgramBlockSize = 1024;
    // The supplied successful trace proves this application region only.
    public const uint ProvenFirstAddress = 0x08008000;
    public const uint ProvenLastAddressExclusive = 0x08014B48;

    public static JbzFirmwareImage Load(string path, bool requireProvenImageRange = true)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Firmware HEX file was not found.", path);
        var flash = new SortedDictionary<uint, byte>();
        uint baseAddress = 0; bool eof = false; int lineNumber = 0;
        foreach (string raw in File.ReadLines(path))
        {
            lineNumber++; string line = raw.Trim(); if (line.Length == 0) continue;
            if (eof) throw new InvalidDataException($"HEX line {lineNumber}: data after EOF.");
            byte[] record = ParseRecord(line, lineNumber);
            int count = record[0]; ushort offset = (ushort)((record[1] << 8) | record[2]); byte type = record[3];
            ReadOnlySpan<byte> data = record.AsSpan(4, count);
            switch (type)
            {
                case 0x00:
                    for (int i = 0; i < data.Length; i++)
                    {
                        uint address = checked(baseAddress + offset + (uint)i);
                        if (flash.TryGetValue(address, out byte old) && old != data[i])
                            throw new InvalidDataException($"HEX line {lineNumber}: conflicting byte at 0x{address:X8}.");
                        flash[address] = data[i];
                    }
                    break;
                case 0x01:
                    if (count != 0 || offset != 0) throw new InvalidDataException($"HEX line {lineNumber}: invalid EOF.");
                    eof = true; break;
                case 0x02:
                    RequireMetadata(count == 2 && offset == 0, lineNumber, "segment address");
                    baseAddress = (uint)(((data[0] << 8) | data[1]) << 4); break;
                case 0x04:
                    RequireMetadata(count == 2 && offset == 0, lineNumber, "linear address");
                    baseAddress = (uint)(((data[0] << 8) | data[1]) << 16); break;
                case 0x03:
                case 0x05:
                    RequireMetadata(count == 4 && offset == 0, lineNumber, "start address"); break;
                default: throw new InvalidDataException($"HEX line {lineNumber}: unsupported record 0x{type:X2}.");
            }
        }
        if (!eof || flash.Count == 0) throw new InvalidDataException("HEX requires program data and a final EOF record.");
        uint first = flash.First().Key, last = checked(flash.Last().Key + 1);
        if (requireProvenImageRange && (first != ProvenFirstAddress || last != ProvenLastAddressExclusive))
            throw new InvalidDataException($"Firmware range 0x{first:X8}..0x{last - 1:X8} is not the trace-proven Universal New 1.2 range.");
        uint expected = first;
        foreach (uint address in flash.Keys)
        {
            if (address != expected) throw new InvalidDataException($"Sparse firmware is not proven by the supplied trace (gap at 0x{expected:X8}).");
            expected++;
        }
        var blocks = new List<JbzFirmwareBlock>();
        for (uint address = first; address < last; address += ProgramBlockSize)
        {
            int length = (int)Math.Min(ProgramBlockSize, last - address);
            blocks.Add(new(address, Enumerable.Range(0, length).Select(i => flash[address + (uint)i]).ToArray()));
        }
        string sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        return new(blocks, flash.Count, first, last, sha);
    }

    private static byte[] ParseRecord(string line, int number)
    {
        if (!line.StartsWith(':') || line.Length < 11 || ((line.Length - 1) & 1) != 0)
            throw new InvalidDataException($"HEX line {number}: invalid record.");
        byte[] bytes = new byte[(line.Length - 1) / 2];
        for (int i = 0; i < bytes.Length; i++)
            if (!byte.TryParse(line.AsSpan(1 + i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
                throw new InvalidDataException($"HEX line {number}: invalid hexadecimal data.");
        if (bytes.Length != 5 + bytes[0]) throw new InvalidDataException($"HEX line {number}: byte count mismatch.");
        if ((bytes.Sum(value => value) & 0xff) != 0) throw new InvalidDataException($"HEX line {number}: checksum error.");
        return bytes;
    }
    private static void RequireMetadata(bool valid, int line, string kind)
    {
        if (!valid) throw new InvalidDataException($"HEX line {line}: invalid {kind} record.");
    }
}
