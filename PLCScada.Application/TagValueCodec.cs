using System.Buffers.Binary;
using PLCScada.Core;

namespace PLCScada.Application;

public static class TagValueCodec
{
    public static ushort RegisterCount(TagDataType type) => type switch
    { TagDataType.Float64 => 4, TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Float32 => 2, _ => 1 };

    // String 的寄存器数由 Tag 配置决定（1–125）；其余类型取固定宽度。
    public static ushort RegisterCount(Tag tag) => tag.DataType == TagDataType.String
        ? (ushort)Math.Clamp(tag.StringRegisters, 1, 125)
        : RegisterCount(tag.DataType);

    public static byte[] Encode(Tag tag, double engineeringValue)
    {
        if (tag.DataType == TagDataType.Bool)
            throw new InvalidDataException("Bool Tag 必须通过 Coil 写入。");
        if (tag.DataType == TagDataType.String)
            throw new InvalidDataException("String Tag 仅支持读取，不支持写入。");
        if (!double.IsFinite(engineeringValue) || !double.IsFinite(tag.Scale) ||
            tag.Scale == 0 || !double.IsFinite(tag.Offset))
            throw new InvalidDataException("工程值或 Tag 缩放配置无效。");

        var raw = (engineeringValue - tag.Offset) / tag.Scale;
        if (!double.IsFinite(raw))
            throw new InvalidDataException("工程值无法转换为有限原始值。");

        var data = tag.DataType switch
        {
            TagDataType.Int16 => EncodeInt16(raw),
            TagDataType.UInt16 => EncodeUInt16(raw),
            TagDataType.Int32 => EncodeInt32(raw),
            TagDataType.UInt32 => EncodeUInt32(raw),
            TagDataType.Float32 => EncodeFloat32(raw),
            TagDataType.Float64 => EncodeFloat64(raw),
            _ => throw new InvalidDataException("不支持的数据类型。")
        };

        ApplyWordOrder(data, tag.WordOrder);
        ApplyByteOrder(data, tag.ByteOrder);
        return data;
    }

    public static object Decode(Tag tag, byte[] bytes)
    {
        if (tag.DataType == TagDataType.Bool)
        {
            if (bytes.Length != 1) throw new InvalidDataException("Modbus 位数据长度不符。");
            return (bytes[0] & 1) != 0;
        }
        if (tag.DataType == TagDataType.String)
            return DecodeString(tag, bytes);
        var data = bytes.ToArray();
        if (data.Length != RegisterCount(tag) * 2) throw new InvalidDataException("Modbus 返回数据长度不符。");
        ApplyByteOrder(data, tag.ByteOrder);
        ApplyWordOrder(data, tag.WordOrder);
        double raw = tag.DataType switch
        {
            TagDataType.Int16 => BinaryPrimitives.ReadInt16BigEndian(data),
            TagDataType.UInt16 => BinaryPrimitives.ReadUInt16BigEndian(data),
            TagDataType.Int32 => BinaryPrimitives.ReadInt32BigEndian(data),
            TagDataType.UInt32 => BinaryPrimitives.ReadUInt32BigEndian(data),
            TagDataType.Float32 => BinaryPrimitives.ReadSingleBigEndian(data),
            TagDataType.Float64 => BinaryPrimitives.ReadDoubleBigEndian(data),
            _ => throw new InvalidDataException("不支持的数据类型。")
        };
        var value = raw * tag.Scale + tag.Offset;
        if (!double.IsFinite(value)) throw new InvalidDataException("设备返回非有限数值。");
        return value;
    }

    // 字符串解码：ASCII 直接取字节；UTF-16LE 按 Modbus 大端字序逐字交换后解码。尾部 NUL/空格清理。
    private static string DecodeString(Tag tag, byte[] bytes)
    {
        var expected = RegisterCount(tag) * 2;
        if (bytes.Length < expected)
            throw new InvalidDataException("Modbus 返回字符串数据长度不符。");
        var buffer = bytes.AsSpan(0, expected).ToArray();
        string text;
        if (tag.StringEncoding == StringEncoding.Utf16LE)
        {
            for (var index = 0; index + 1 < buffer.Length; index += 2)
                (buffer[index], buffer[index + 1]) = (buffer[index + 1], buffer[index]);
            text = System.Text.Encoding.Unicode.GetString(buffer);
        }
        else
        {
            text = System.Text.Encoding.ASCII.GetString(buffer);
        }
        return text.TrimEnd('\0', ' ');
    }

    private static byte[] EncodeInt16(double raw)
    {
        var value = ToIntegral(raw, short.MinValue, short.MaxValue, "Int16");
        var bytes = new byte[2];
        BinaryPrimitives.WriteInt16BigEndian(bytes, (short)value);
        return bytes;
    }

    private static byte[] EncodeUInt16(double raw)
    {
        var value = ToIntegral(raw, ushort.MinValue, ushort.MaxValue, "UInt16");
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)value);
        return bytes;
    }

    private static byte[] EncodeInt32(double raw)
    {
        var value = ToIntegral(raw, int.MinValue, int.MaxValue, "Int32");
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, (int)value);
        return bytes;
    }

    private static byte[] EncodeUInt32(double raw)
    {
        var value = ToIntegral(raw, uint.MinValue, uint.MaxValue, "UInt32");
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)value);
        return bytes;
    }

    private static byte[] EncodeFloat32(double raw)
    {
        var value = (float)raw;
        if (!float.IsFinite(value))
            throw new InvalidDataException("Float32 原始值超出有效范围。");
        var bytes = new byte[4];
        BinaryPrimitives.WriteSingleBigEndian(bytes, value);
        return bytes;
    }

    private static byte[] EncodeFloat64(double raw)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(bytes, raw);
        return bytes;
    }

    private static double ToIntegral(double raw, double minimum, double maximum, string type)
    {
        var rounded = Math.Round(raw, MidpointRounding.AwayFromZero);
        if (Math.Abs(raw - rounded) > 1e-8 || rounded < minimum || rounded > maximum)
            throw new InvalidDataException($"{type} 原始值必须是范围内的整数。");
        return rounded;
    }

    private static void ApplyByteOrder(byte[] data, ByteOrder byteOrder)
    {
        if (byteOrder != ByteOrder.LittleEndian) return;
        for (var i = 0; i < data.Length; i += 2)
            (data[i], data[i + 1]) = (data[i + 1], data[i]);
    }

    private static void ApplyWordOrder(byte[] data, WordOrder wordOrder)
    {
        if (wordOrder != WordOrder.LowWordFirst) return;
        for (var i = 0; i < data.Length / 2; i += 2)
        {
            var j = data.Length - i - 2;
            if (i >= j) break;
            (data[i], data[j]) = (data[j], data[i]);
            (data[i + 1], data[j + 1]) = (data[j + 1], data[i + 1]);
        }
    }
}
