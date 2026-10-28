using System.Buffers.Binary;
using System.Text;
using AiVideoEditor.Core.Telemetry;
using Xunit;

namespace AiVideoEditor.Core.Tests.Telemetry;

/// <summary>Builds small FIT files for tests: definitions in either byte order, data messages with a normal or a
/// compressed-timestamp header, developer fields (zero bytes), the header and file CRCs. Scalars and strings only.</summary>
internal sealed class FitTestWriter
{
    public const byte Enum = 0x00;
    public const byte SInt8 = 0x01;
    public const byte UInt8 = 0x02;
    public const byte SInt16 = 0x83;
    public const byte UInt16 = 0x84;
    public const byte UInt32 = 0x86;
    public const byte String = 0x07;

    private sealed record Def(ushort Global, bool BigEndian, (byte Num, byte Size, byte Base)[] Fields, int DevBytes);

    private readonly List<byte> _data = new();
    private readonly Dictionary<int, Def> _defs = new();

    public FitTestWriter Define(int local, ushort global, params (byte Num, byte Size, byte Base)[] fields) =>
        Define(local, global, bigEndian: false, developer: null, fields);

    public FitTestWriter Define(int local, ushort global, bool bigEndian, (byte Num, byte Size, byte Index)[]? developer,
        params (byte Num, byte Size, byte Base)[] fields)
    {
        _data.Add((byte)(0x40 | (developer is null ? 0 : 0x20) | local));
        _data.Add(0);
        _data.Add(bigEndian ? (byte)1 : (byte)0);
        var g = new byte[2];
        if (bigEndian) BinaryPrimitives.WriteUInt16BigEndian(g, global); else BinaryPrimitives.WriteUInt16LittleEndian(g, global);
        _data.AddRange(g);
        _data.Add((byte)fields.Length);
        foreach (var (num, size, baseType) in fields) _data.AddRange(new[] { num, size, baseType });
        var devBytes = 0;
        if (developer is not null)
        {
            _data.Add((byte)developer.Length);
            foreach (var (num, size, index) in developer)
            {
                _data.AddRange(new[] { num, size, index });
                devBytes += size;
            }
        }

        _defs[local] = new Def(global, bigEndian, fields, devBytes);
        return this;
    }

    /// <summary>A data message; <paramref name="values"/> are longs (null = the type's invalid value) or strings.</summary>
    public FitTestWriter Data(int local, params object?[] values)
    {
        _data.Add((byte)local);
        WriteValues(local, values);
        return this;
    }

    /// <summary>A data message with a compressed-timestamp header carrying the low five bits of
    /// <paramref name="fitTime"/>.</summary>
    public FitTestWriter Compressed(int local, long fitTime, params object?[] values)
    {
        _data.Add((byte)(0x80 | (local << 5) | (int)(fitTime & 0x1F)));
        WriteValues(local, values);
        return this;
    }

    public FitTestWriter Raw(params byte[] bytes)
    {
        _data.AddRange(bytes);
        return this;
    }

    private void WriteValues(int local, object?[] values)
    {
        var def = _defs[local];
        Assert.Equal(def.Fields.Length, values.Length);
        for (var i = 0; i < values.Length; i++)
        {
            var (_, size, baseType) = def.Fields[i];
            var buffer = new byte[size];
            if ((baseType & 0x1F) == 7)
            {
                var text = Encoding.UTF8.GetBytes((string?)values[i] ?? "");
                Array.Copy(text, buffer, Math.Min(text.Length, size));
            }
            else
            {
                var value = values[i] is null ? Invalid(baseType) : unchecked((ulong)Convert.ToInt64(values[i]));
                for (var b = 0; b < size; b++)
                {
                    var shift = 8 * (def.BigEndian ? size - 1 - b : b);
                    buffer[b] = (byte)(value >> shift);
                }
            }

            _data.AddRange(buffer);
        }

        _data.AddRange(new byte[def.DevBytes]);
    }

    private static ulong Invalid(byte baseType) => (baseType & 0x1F) switch
    {
        0 or 2 or 13 => 0xFF,
        1 => 0x7F,
        3 => 0x7FFF,
        4 => 0xFFFF,
        6 => 0xFFFFFFFF,
        _ => 0,
    };

    public byte[] Build(int headerSize = 14, bool headerCrc = true)
    {
        var header = new byte[headerSize];
        header[0] = (byte)headerSize;
        header[1] = 0x20;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2), 2158);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)_data.Count);
        ".FIT"u8.CopyTo(header.AsSpan(8));
        if (headerSize >= 14 && headerCrc)
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), FitFile.Crc16(header.AsSpan(0, 12)));

        var body = header.Concat(_data).ToArray();
        var crc = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(crc, FitFile.Crc16(body));
        return body.Concat(crc).ToArray();
    }

    /// <summary>FIT seconds of an absolute UTC time.</summary>
    public static long FitTime(DateTimeOffset utc) =>
        (utc.UtcTicks - new DateTimeOffset(1989, 12, 31, 0, 0, 0, TimeSpan.Zero).UtcTicks) / TimeSpan.TicksPerSecond;
}
