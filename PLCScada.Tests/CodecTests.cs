using System.Buffers.Binary;
using PLCScada.Application;
using PLCScada.Core;
namespace PLCScada.Tests;
public sealed class CodecTests
{
    [Theory]
    [InlineData(TagDataType.Int16, "FF85", -123)]
    [InlineData(TagDataType.UInt16, "FEDC", 65244)]
    [InlineData(TagDataType.Int32, "FFFEDCC0", -74560)]
    [InlineData(TagDataType.UInt32, "FEDCBA98", 4275878552d)]
    [InlineData(TagDataType.Float32, "41480000", 12.5)]
    [InlineData(TagDataType.Float64, "4029000000000000", 12.5)]
    public void NumericTypesDecodeInNetworkOrder(TagDataType type, string hex, double expected)
        => Assert.Equal(expected, (double)TagValueCodec.Decode(new() { DataType = type }, Convert.FromHexString(hex)));
    [Theory]
    [InlineData(ByteOrder.BigEndian, WordOrder.HighWordFirst, "41480000")]
    [InlineData(ByteOrder.LittleEndian, WordOrder.HighWordFirst, "48410000")]
    [InlineData(ByteOrder.BigEndian, WordOrder.LowWordFirst, "00004148")]
    [InlineData(ByteOrder.LittleEndian, WordOrder.LowWordFirst, "00004841")]
    public void ByteAndWordOrderAreIndependent(ByteOrder bytes, WordOrder words, string hex)
        => Assert.Equal(26d, TagValueCodec.Decode(new() { DataType = TagDataType.Float32, ByteOrder = bytes, WordOrder = words, Scale = 2, Offset = 1 }, Convert.FromHexString(hex)));

    [Theory]
    [InlineData(ByteOrder.BigEndian, WordOrder.HighWordFirst, "41480000")]
    [InlineData(ByteOrder.LittleEndian, WordOrder.HighWordFirst, "48410000")]
    [InlineData(ByteOrder.BigEndian, WordOrder.LowWordFirst, "00004148")]
    [InlineData(ByteOrder.LittleEndian, WordOrder.LowWordFirst, "00004841")]
    public void EncodeUsesConfiguredByteAndWordOrder(ByteOrder bytes, WordOrder words, string hex)
        => Assert.Equal(hex, Convert.ToHexString(TagValueCodec.Encode(
            new() { DataType = TagDataType.Float32, ByteOrder = bytes, WordOrder = words, Scale = 2, Offset = 1 },
            26)));

    [Theory]
    [InlineData(TagDataType.Int16, -123, 0.1, 2, "FF85")]
    [InlineData(TagDataType.UInt16, 65244, 0.5, 3, "FEDC")]
    [InlineData(TagDataType.Int32, -74560, 0.25, -2, "FFFEDCC0")]
    [InlineData(TagDataType.UInt32, 4275878552d, 2, 1, "FEDCBA98")]
    [InlineData(TagDataType.Float32, 12.5, 2, 1, "41480000")]
    [InlineData(TagDataType.Float64, 12.5, 0.5, -1, "4029000000000000")]
    public void NumericEngineeringValuesEncodeToRawRegisters(
        TagDataType type, double raw, double scale, double offset, string expectedHex)
    {
        var tag = new Tag { DataType = type, Scale = scale, Offset = offset };
        var engineeringValue = raw * scale + offset;

        Assert.Equal(expectedHex, Convert.ToHexString(TagValueCodec.Encode(tag, engineeringValue)));
    }

    [Fact]
    public void EncoderRejectsBoolFractionalIntegerOverflowAndNonFiniteValues()
    {
        Assert.Throws<InvalidDataException>(() => TagValueCodec.Encode(new() { DataType = TagDataType.Bool }, 1));
        Assert.Throws<InvalidDataException>(() => TagValueCodec.Encode(new() { DataType = TagDataType.Int16 }, 1.5));
        Assert.Throws<InvalidDataException>(() => TagValueCodec.Encode(new() { DataType = TagDataType.UInt16 }, -1));
        Assert.Throws<InvalidDataException>(() => TagValueCodec.Encode(new() { DataType = TagDataType.Float32 }, double.PositiveInfinity));
    }
    [Fact]
    public void Float64WordOrderAndBoolDecode()
    {
        Assert.Equal(12.5, TagValueCodec.Decode(new() { DataType = TagDataType.Float64, WordOrder = WordOrder.LowWordFirst }, Convert.FromHexString("0000000000004029")));
        Assert.Equal(true, TagValueCodec.Decode(new() { DataType = TagDataType.Bool }, [1]));
        Assert.Equal(false, TagValueCodec.Decode(new() { DataType = TagDataType.Bool }, [0]));
    }
    [Fact]
    public void MalformedAndNonFiniteValuesAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => TagValueCodec.Decode(new() { DataType = TagDataType.Float32 }, [0, 1]));
        var bytes = new byte[4]; BinaryPrimitives.WriteSingleBigEndian(bytes, float.NaN);
        Assert.Throws<InvalidDataException>(() => TagValueCodec.Decode(new() { DataType = TagDataType.Float32 }, bytes));
    }
}
