using AiVideoEditor.Core.Telemetry;
using Xunit;
using W = AiVideoEditor.Core.Tests.Telemetry.FitTestWriter;

namespace AiVideoEditor.Core.Tests.Telemetry;

public class FitFileTests
{
    private static FitTestWriter Simple() => new FitTestWriter()
        .Define(0, 0, (0, 1, W.Enum), (1, 2, W.UInt16), (8, 8, W.String))
        .Data(0, 4L, 294L, "APEX")
        .Define(1, 20, (253, 4, W.UInt32), (3, 1, W.UInt8), (2, 2, W.UInt16))
        .Data(1, 1_000_000_000L, 120L, null);

    [Fact]
    public void Reads_the_header_and_the_messages_with_their_values()
    {
        var (info, messages) = FitFile.Read(Simple().Build());

        Assert.Equal(14, info.HeaderSize);
        Assert.Equal(0x20, info.ProtocolVersion);
        Assert.Equal(2, messages.Count);
        Assert.Equal(0, messages[0].GlobalNumber);
        Assert.Equal(4, messages[0].Get(0));
        Assert.Equal(294, messages[0].Get(1));
        Assert.Equal("APEX", messages[0].GetText(8));
        Assert.Equal(20, messages[1].GlobalNumber);
        Assert.Equal(1_000_000_000, messages[1].Get(253));
        Assert.Equal(120, messages[1].Get(3));
        Assert.Null(messages[1].Get(2));      // the uint16 invalid value
        Assert.Null(messages[1].Get(99));     // absent
    }

    [Fact]
    public void A_twelve_byte_header_and_a_zero_header_crc_are_accepted()
    {
        Assert.Equal(2, FitFile.Read(Simple().Build(headerSize: 12)).Messages.Count);
        Assert.Equal(2, FitFile.Read(Simple().Build(headerCrc: false)).Messages.Count);
    }

    [Fact]
    public void Signed_values_and_big_endian_definitions_are_decoded()
    {
        var file = new FitTestWriter()
            .Define(0, 312, bigEndian: true, developer: null, (0, 1, W.Enum), (1, 4, W.UInt32), (2, 2, W.SInt16), (3, 1, W.SInt8))
            .Data(0, 9L, 20_170L, -5L, -1L)
            .Build();

        var message = Assert.Single(FitFile.Read(file).Messages);
        Assert.Equal(312, message.GlobalNumber);
        Assert.Equal(9, message.Get(0));
        Assert.Equal(20_170, message.Get(1));
        Assert.Equal(-5, message.Get(2));
        Assert.Equal(-1, message.Get(3));
    }

    [Fact]
    public void Compressed_timestamps_are_reconstructed_across_the_five_bit_rollover()
    {
        const long t0 = 1_159_426_959;   // low five bits = 15
        var file = new FitTestWriter()
            .Define(0, 20, (253, 4, W.UInt32), (3, 1, W.UInt8))
            .Define(1, 20, (3, 1, W.UInt8))
            .Data(0, t0, 100L)
            .Compressed(1, t0 + 1, 101L)
            .Compressed(1, t0 + 20, 102L)  // the low bits wrap: 15 + 20 = 35 → 3
            .Build();

        var times = FitFile.Read(file).Messages.Select(m => m.Get(253)).ToArray();
        Assert.Equal(new long?[] { t0, t0 + 1, t0 + 20 }, times);
    }

    [Fact]
    public void Developer_fields_are_skipped()
    {
        var file = new FitTestWriter()
            .Define(0, 20, bigEndian: false, developer: new (byte, byte, byte)[] { (0, 3, 0), (1, 2, 0) }, (253, 4, W.UInt32), (3, 1, W.UInt8))
            .Data(0, 1_000_000_000L, 120L)
            .Data(0, 1_000_000_001L, 121L)
            .Build();

        var messages = FitFile.Read(file).Messages;
        Assert.Equal(new long?[] { 120, 121 }, messages.Select(m => m.Get(3)).ToArray());
    }

    [Fact]
    public void A_file_without_the_signature_is_not_a_fit_file()
    {
        var file = Simple().Build();
        file[9] = (byte)'X';
        Assert.Equal(TrainingFileError.NotAFitFile, Assert.Throws<TrainingFileException>(() => FitFile.Read(file)).Error);
        Assert.Equal(TrainingFileError.NotAFitFile, Assert.Throws<TrainingFileException>(() => FitFile.Read(new byte[5])).Error);
    }

    [Fact]
    public void A_cut_file_is_truncated()
    {
        var file = Simple().Build();
        Assert.Equal(TrainingFileError.Truncated,
            Assert.Throws<TrainingFileException>(() => FitFile.Read(file.AsSpan(0, file.Length - 3))).Error);
    }

    [Fact]
    public void A_changed_byte_breaks_the_file_crc()
    {
        var file = Simple().Build();
        file[20] ^= 0x01;
        Assert.Equal(TrainingFileError.Corrupted, Assert.Throws<TrainingFileException>(() => FitFile.Read(file)).Error);
    }

    [Fact]
    public void A_wrong_header_crc_is_corruption()
    {
        var file = Simple().Build();
        file[12] ^= 0xFF;
        Assert.Equal(TrainingFileError.Corrupted, Assert.Throws<TrainingFileException>(() => FitFile.Read(file)).Error);
    }

    [Fact]
    public void Data_of_an_undefined_local_type_is_malformed()
    {
        var file = new FitTestWriter().Raw(0x05, 0x00).Build();
        Assert.Equal(TrainingFileError.Malformed, Assert.Throws<TrainingFileException>(() => FitFile.Read(file)).Error);
    }

    [Fact]
    public void A_compressed_timestamp_without_a_full_one_is_malformed()
    {
        var file = new FitTestWriter()
            .Define(1, 20, (3, 1, W.UInt8))
            .Compressed(1, 5, 100L)
            .Build();
        Assert.Equal(TrainingFileError.Malformed, Assert.Throws<TrainingFileException>(() => FitFile.Read(file)).Error);
    }

    [Fact]
    public void A_message_running_past_the_data_is_truncated()
    {
        // A valid CRC over a definition promising a 4-byte field, followed by a data message with only 2 bytes.
        var file = new FitTestWriter()
            .Raw(0x40, 0x00, 0x00, 0x14, 0x00, 0x01, 253, 4, W.UInt32)
            .Raw(0x00, 0x01, 0x02)
            .Build();
        Assert.Equal(TrainingFileError.Truncated, Assert.Throws<TrainingFileException>(() => FitFile.Read(file)).Error);
    }

    [Fact]
    public void The_crc_matches_the_fit_sdk_reference_value()
    {
        // The SDK's CRC of the ASCII "123456789" (CRC-16/ARC): 0xBB3D.
        Assert.Equal(0xBB3D, FitFile.Crc16("123456789"u8));
    }
}
