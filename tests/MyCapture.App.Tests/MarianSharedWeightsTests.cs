using System.IO;
using System.Text;
using MyCapture.Ocr.Translation;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class MarianSharedWeightsTests
{
    [Fact]
    public void ReadsOnlyTheGraphInitializersStoredInExternalFiles()
    {
        byte[] external = Message(
            Field(1, Message(Varint(2), Varint(3))),
            VarintField(2, 3),
            Field(8, "decoder.weight"),
            Field(13, Message(Field(1, "location"), Field(2, "weights-2.bin"))),
            Field(13, Message(Field(1, "offset"), Field(2, "65536"))),
            Field(13, Message(Field(1, "length"), Field(2, "6"))),
            VarintField(14, 1));
        byte[] inline = Message(VarintField(1, 4), VarintField(2, 1), Field(8, "scale"), Field(9, new byte[16]));
        byte[] graph = Message(Field(1, Message(Field(4, "MatMulInteger"))), Field(5, external), Field(5, inline));
        byte[] model = Message(VarintField(1, 8), Field(7, graph));

        OnnxExternalInitializer initializer = Assert.Single(OnnxExternalInitializer.Parse(model));

        Assert.Equal("decoder.weight", initializer.Name);
        Assert.Equal(3, initializer.DataType);
        Assert.Equal([2L, 3L], initializer.Dimensions);
        Assert.Equal("weights-2.bin", initializer.Location);
        Assert.Equal(65536, initializer.Offset);
        Assert.Equal(6, initializer.Length);
    }

    [Fact]
    public void RejectsATruncatedModelInsteadOfReadingPastIt()
    {
        byte[] model = Message(Field(7, Message(Field(5, Message(Field(8, "w"))))));

        Assert.Throws<InvalidDataException>(() => OnnxExternalInitializer.Parse(model.AsSpan(0, model.Length - 1)));
    }

    private static byte[] Message(params byte[][] parts) => [.. parts.SelectMany(part => part)];

    private static byte[] Field(int number, string text) => Field(number, Encoding.UTF8.GetBytes(text));

    private static byte[] Field(int number, byte[] payload) => [.. Varint((ulong)(number << 3 | 2)), .. Varint((ulong)payload.Length), .. payload];

    private static byte[] VarintField(int number, ulong value) => [.. Varint((ulong)(number << 3)), .. Varint(value)];

    private static byte[] Varint(ulong value)
    {
        var bytes = new List<byte>();
        do
        {
            byte next = (byte)(value & 0x7F);
            value >>= 7;
            bytes.Add(value == 0 ? next : (byte)(next | 0x80));
        }
        while (value != 0);
        return [.. bytes];
    }
}
