using Intercom.Chat;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Intercom.App.Tests.Chat;

public sealed class ChatImageOptimizerTests
{
    [Fact]
    public async Task OptimizeAsync_StripsMetadataResizesAndPreservesTransparencyAsPng()
    {
        using var source = new Image<Rgba32>(3000, 1500, new Rgba32(20, 40, 60, 120));
        source.Metadata.ExifProfile = new ExifProfile();
        source.Metadata.ExifProfile.SetValue(ExifTag.GPSLatitudeRef, "S");
        await using var input = new MemoryStream();
        await source.SaveAsPngAsync(input);
        input.Position = 0;

        var result = await ChatImageOptimizer.OptimizeAsync(input, CancellationToken.None);

        Assert.Equal(ChatImageFormat.Png, result.Format);
        Assert.Equal(2560, result.Width);
        Assert.Equal(1280, result.Height);
        Assert.InRange(result.Bytes.Length, 1, ChatImageOptimizer.MaxTransferBytes);
        using var decoded = Image.Load(result.Bytes);
        Assert.Null(decoded.Metadata.ExifProfile);
        Assert.Single(decoded.Frames);
    }

    [Fact]
    public async Task OptimizeAsync_OpaquePhotographUsesQuality80Jpeg()
    {
        using var source = new Image<Rgba32>(128, 128);
        var random = new Random(42);
        source.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                    row[x] = new Rgba32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
            }
        });
        await using var input = new MemoryStream();
        await source.SaveAsJpegAsync(input);
        input.Position = 0;

        var result = await ChatImageOptimizer.OptimizeAsync(input, CancellationToken.None);

        Assert.Equal(ChatImageFormat.Jpeg, result.Format);
        Assert.Equal(128, result.Width);
        Assert.Equal(128, result.Height);
        Assert.Equal([0xFF, 0xD8], result.Bytes[..2]);
    }
}
