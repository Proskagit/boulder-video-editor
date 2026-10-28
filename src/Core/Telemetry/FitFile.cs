using System.Buffers.Binary;
using System.Text;

namespace AiVideoEditor.Core.Telemetry;

/// <summary>
/// One field of a decoded FIT data message: its integer elements (an invalid element — the base type's "invalid"
/// value — is null) or its text. Floating-point base types are not decoded (no field this reader uses has one) and
/// come out with neither.
/// </summary>
public sealed class FitField
{
    private static readonly long?[] NoValues = [];

    public FitField(IReadOnlyList<long?> values) => Values = values;

    public FitField(string? text)
    {
        Values = NoValues;
        Text = text;
    }

    /// <summary>The integer elements in file order; one element for a scalar field.</summary>
    public IReadOnlyList<long?> Values { get; }

    /// <summary>The value of a string field up to its first NUL; null when empty.</summary>
    public string? Text { get; }

    /// <summary>The first element, or null when the field is invalid, textual or empty.</summary>
    public long? Value => Values.Count > 0 ? Values[0] : null;
}

/// <summary>A decoded FIT data message: its global message number and its fields by field definition number.
/// Developer fields are skipped. A message read with a compressed-timestamp header carries the reconstructed time as
/// field 253, like a message that stores it.</summary>
public sealed class FitMessage
{
    public FitMessage(ushort globalNumber, IReadOnlyDictionary<byte, FitField> fields)
    {
        GlobalNumber = globalNumber;
        Fields = fields;
    }

    public ushort GlobalNumber { get; }

    public IReadOnlyDictionary<byte, FitField> Fields { get; }

    /// <summary>The field's first element, or null when the field is absent or invalid.</summary>
    public long? Get(byte fieldNumber) => Fields.TryGetValue(fieldNumber, out var field) ? field.Value : null;

    /// <summary>The field's text, or null when the field is absent, empty or not a string.</summary>
    public string? GetText(byte fieldNumber) => Fields.TryGetValue(fieldNumber, out var field) ? field.Text : null;
}

/// <summary>The header facts of a FIT file.</summary>
public sealed record FitFileInfo(int HeaderSize, byte ProtocolVersion, ushort ProfileVersion, int DataSize);

/// <summary>
/// A minimal, read-only reader of the Garmin FIT binary format (protocol 1.0 / 2.0): the header and its CRC, the
/// file CRC, definition and data messages in either byte order, compressed-timestamp headers and developer fields
/// (skipped). It decodes field values by base type and leaves their meaning (scale, units, enums) to the caller. Only
/// the first FIT file of a chained file is read. Every structural fault is a <see cref="TrainingFileException"/>; the
/// reader never reads outside the buffer.
/// </summary>
public static class FitFile
{
    /// <summary>Field definition number of every message's timestamp (a FIT <c>date_time</c>).</summary>
    public const byte TimestampField = 253;

    private const int MinimumHeaderSize = 12;
    private const int CrcSize = 2;

    public static (FitFileInfo Info, IReadOnlyList<FitMessage> Messages) Read(ReadOnlySpan<byte> file)
    {
        if (file.Length < MinimumHeaderSize)
            throw new TrainingFileException(TrainingFileError.NotAFitFile, "The file is shorter than a FIT header.");

        int headerSize = file[0];
        if (headerSize < MinimumHeaderSize || file.Length < headerSize || !file.Slice(8, 4).SequenceEqual(".FIT"u8))
            throw new TrainingFileException(TrainingFileError.NotAFitFile, "The file has no FIT signature.");

        var protocol = file[1];
        var profile = BinaryPrimitives.ReadUInt16LittleEndian(file.Slice(2, 2));
        var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(file.Slice(4, 4));

        if (headerSize >= 14)
        {
            var headerCrc = BinaryPrimitives.ReadUInt16LittleEndian(file.Slice(12, 2));
            if (headerCrc != 0 && headerCrc != Crc16(file[..12]))
                throw new TrainingFileException(TrainingFileError.Corrupted, "The FIT header checksum does not match.");
        }

        if ((long)headerSize + dataSize + CrcSize > file.Length)
            throw new TrainingFileException(TrainingFileError.Truncated, "The FIT file is shorter than its header states.");

        var end = headerSize + (int)dataSize;
        var fileCrc = BinaryPrimitives.ReadUInt16LittleEndian(file.Slice(end, CrcSize));
        if (fileCrc != Crc16(file[..end]))
            throw new TrainingFileException(TrainingFileError.Corrupted, "The FIT file checksum does not match.");

        var info = new FitFileInfo(headerSize, protocol, profile, (int)dataSize);
        return (info, ReadMessages(file[headerSize..end]));
    }

    private sealed record Definition(ushort GlobalNumber, bool LittleEndian, (byte Number, byte Size, byte BaseType)[] Fields,
        int DeveloperBytes);

    private static List<FitMessage> ReadMessages(ReadOnlySpan<byte> data)
    {
        var definitions = new Definition?[16];
        var messages = new List<FitMessage>();
        long? lastTimestamp = null;
        var pos = 0;

        while (pos < data.Length)
        {
            var header = data[pos++];
            long? compressedTime = null;
            int localType;

            if ((header & 0x80) != 0)
            {
                localType = (header >> 5) & 0x03;
                var offset = header & 0x1F;
                if (lastTimestamp is not { } last)
                    throw new TrainingFileException(TrainingFileError.Malformed, "A compressed timestamp precedes every full timestamp.");
                var time = (last & ~0x1FL) + offset;
                if (offset < (last & 0x1F)) time += 0x20;
                compressedTime = time;
            }
            else if ((header & 0x40) != 0)
            {
                localType = header & 0x0F;
                definitions[localType] = ReadDefinition(data, ref pos, hasDeveloperFields: (header & 0x20) != 0);
                continue;
            }
            else
            {
                localType = header & 0x0F;
            }

            var definition = definitions[localType]
                ?? throw new TrainingFileException(TrainingFileError.Malformed, $"Data message of undefined local type {localType}.");

            var fields = new Dictionary<byte, FitField>(definition.Fields.Length + 1);
            foreach (var (number, size, baseType) in definition.Fields)
            {
                fields[number] = DecodeField(Take(data, ref pos, size), baseType, definition.LittleEndian);
            }

            Take(data, ref pos, definition.DeveloperBytes);

            if (compressedTime is { } reconstructed)
                fields[TimestampField] = new FitField(new long?[] { reconstructed });

            if (fields.TryGetValue(TimestampField, out var timestamp) && timestamp.Value is { } seconds)
                lastTimestamp = seconds;

            messages.Add(new FitMessage(definition.GlobalNumber, fields));
        }

        return messages;
    }

    private static Definition ReadDefinition(ReadOnlySpan<byte> data, ref int pos, bool hasDeveloperFields)
    {
        var fixedPart = Take(data, ref pos, 5);
        var littleEndian = fixedPart[1] == 0;
        var globalNumber = littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(fixedPart.Slice(2, 2))
            : BinaryPrimitives.ReadUInt16BigEndian(fixedPart.Slice(2, 2));
        int fieldCount = fixedPart[4];

        var raw = Take(data, ref pos, 3 * fieldCount);
        var fields = new (byte, byte, byte)[fieldCount];
        for (var i = 0; i < fieldCount; i++)
            fields[i] = (raw[3 * i], raw[3 * i + 1], raw[3 * i + 2]);

        var developerBytes = 0;
        if (hasDeveloperFields)
        {
            int developerCount = Take(data, ref pos, 1)[0];
            var developer = Take(data, ref pos, 3 * developerCount);
            for (var i = 0; i < developerCount; i++)
                developerBytes += developer[3 * i + 1];
        }

        return new Definition(globalNumber, littleEndian, fields, developerBytes);
    }

    private static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> data, ref int pos, int count)
    {
        if (count > data.Length - pos)
            throw new TrainingFileException(TrainingFileError.Truncated, "A FIT message runs past the end of the data.");
        var slice = data.Slice(pos, count);
        pos += count;
        return slice;
    }

    /// <summary>Decodes one field by its base type (the low five bits; bit 7 only flags a multi-byte type).</summary>
    private static FitField DecodeField(ReadOnlySpan<byte> raw, byte baseType, bool littleEndian)
    {
        var type = baseType & 0x1F;
        if (type == 7)
        {
            var nul = raw.IndexOf((byte)0);
            var bytes = nul >= 0 ? raw[..nul] : raw;
            return new FitField(bytes.IsEmpty ? null : Encoding.UTF8.GetString(bytes));
        }

        var (size, signed, invalid) = type switch
        {
            0 or 2 or 13 => (1, false, 0xFFUL),       // enum, uint8, byte
            1 => (1, true, 0x7FUL),                     // sint8
            10 => (1, false, 0x00UL),                   // uint8z
            3 => (2, true, 0x7FFFUL),                   // sint16
            4 => (2, false, 0xFFFFUL),                  // uint16
            11 => (2, false, 0x0000UL),                 // uint16z
            5 => (4, true, 0x7FFFFFFFUL),               // sint32
            6 => (4, false, 0xFFFFFFFFUL),              // uint32
            12 => (4, false, 0x00000000UL),             // uint32z
            14 => (8, true, 0x7FFFFFFFFFFFFFFFUL),      // sint64
            15 => (8, false, 0xFFFFFFFFFFFFFFFFUL),     // uint64
            16 => (8, false, 0x0000000000000000UL),     // uint64z
            _ => (0, false, 0UL),                       // float32 / float64 / unknown: not decoded
        };

        if (size == 0 || raw.Length < size || raw.Length % size != 0)
            return new FitField(Array.Empty<long?>());

        var values = new long?[raw.Length / size];
        for (var i = 0; i < values.Length; i++)
        {
            var element = raw.Slice(i * size, size);
            ulong bits = size switch
            {
                1 => element[0],
                2 => littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(element) : BinaryPrimitives.ReadUInt16BigEndian(element),
                4 => littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(element) : BinaryPrimitives.ReadUInt32BigEndian(element),
                _ => littleEndian ? BinaryPrimitives.ReadUInt64LittleEndian(element) : BinaryPrimitives.ReadUInt64BigEndian(element),
            };

            if (bits == invalid)
            {
                values[i] = null;
                continue;
            }

            values[i] = unchecked(signed
                ? size switch
                {
                    1 => (long)(sbyte)bits,
                    2 => (short)bits,
                    4 => (int)bits,
                    _ => (long)bits,
                }
                : (long)bits);
        }

        return new FitField(values);
    }

    private static readonly ushort[] CrcTable =
    [
        0x0000, 0xCC01, 0xD801, 0x1400, 0xF001, 0x3C00, 0x2800, 0xE401,
        0xA001, 0x6C00, 0x7800, 0xB401, 0x5000, 0x9C01, 0x8801, 0x4400,
    ];

    /// <summary>The FIT CRC-16 (the SDK's nibble-table algorithm) of <paramref name="bytes"/>.</summary>
    public static ushort Crc16(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0;
        foreach (var b in bytes)
        {
            var tmp = CrcTable[crc & 0xF];
            crc = (ushort)((crc >> 4) & 0x0FFF);
            crc = (ushort)(crc ^ tmp ^ CrcTable[b & 0xF]);
            tmp = CrcTable[crc & 0xF];
            crc = (ushort)((crc >> 4) & 0x0FFF);
            crc = (ushort)(crc ^ tmp ^ CrcTable[(b >> 4) & 0xF]);
        }

        return crc;
    }
}
