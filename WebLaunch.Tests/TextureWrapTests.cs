using System.Buffers.Binary;
using El_Garnan_Plugin_Loader.Rendering;
using Xunit;

namespace WebLaunch.Tests;

/// <summary>Regression coverage for asset decoding without requiring a native graphics device.</summary>
public sealed class TextureWrapTests
{
    [Fact]
    public void DecoderProducesContiguousRgbaPixels()
    {
        var decoded = TextureWrap.DecodeRgba(CreateOnePixelRedBitmap());

        Assert.Equal(1, decoded.Width);
        Assert.Equal(1, decoded.Height);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, decoded.Data);
    }

    [Fact]
    public void DecoderRejectsMalformedImageBytes()
    {
        Assert.ThrowsAny<Exception>(() => TextureWrap.DecodeRgba([0x00, 0x01, 0x02, 0x03]));
    }

    [Fact]
    public void DecoderRejectsNullInput()
    {
        Assert.Throws<ArgumentNullException>(() => TextureWrap.DecodeRgba(null!));
    }

    private static byte[] CreateOnePixelRedBitmap()
    {
        // A minimal 1x1, 24-bit BMP. Its BGR pixel occupies three bytes plus one row-padding byte.
        var bytes = new byte[58];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(2, 4), (uint)bytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(10, 4), 54);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(14, 4), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18, 4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22, 4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(26, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28, 2), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(34, 4), 4);
        bytes[54] = 0;   // Blue
        bytes[55] = 0;   // Green
        bytes[56] = 255; // Red
        bytes[57] = 0;   // Row padding
        return bytes;
    }
}
