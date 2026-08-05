using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Intercom.Chat;

public enum ChatImageFormat { Jpeg, Png }

public sealed record OptimizedChatImage(byte[] Bytes, int Width, int Height, ChatImageFormat Format);

public static class ChatImageOptimizer
{
    public const int MaximumEdge = 2560;
    public const int MinimumEdge = 1024;
    public const int MaxTransferBytes = 10 * 1024 * 1024;

    public static async Task<OptimizedChatImage> OptimizeAsync(Stream source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var image = await Image.LoadAsync<Rgba32>(source, cancellationToken).ConfigureAwait(false);
        var sourceWasPng = image.Metadata.DecodedImageFormat?.Name.Equals("PNG", StringComparison.OrdinalIgnoreCase) == true;
        while (image.Frames.Count > 1) image.Frames.RemoveFrame(1);
        image.Mutate(context => context.AutoOrient());
        StripMetadata(image);

        var longest = Math.Max(image.Width, image.Height);
        if (longest > MaximumEdge) ResizeLongestEdge(image, MaximumEdge);

        var usePng = sourceWasPng || HasTransparencyOrLooksGraphical(image);
        while (true)
        {
            var bytes = await EncodeAsync(image, usePng, cancellationToken).ConfigureAwait(false);
            if (bytes.Length <= MaxTransferBytes)
                return new OptimizedChatImage(bytes, image.Width, image.Height, usePng ? ChatImageFormat.Png : ChatImageFormat.Jpeg);

            var currentLongest = Math.Max(image.Width, image.Height);
            if (currentLongest <= MinimumEdge)
                throw new ChatImageTooLargeException(MaxTransferBytes);
            ResizeLongestEdge(image, Math.Max(MinimumEdge, (int)(currentLongest * 0.8)));
        }
    }

    static void StripMetadata(Image image)
    {
        image.Metadata.ExifProfile = null;
        image.Metadata.IptcProfile = null;
        image.Metadata.XmpProfile = null;
    }

    static void ResizeLongestEdge(Image image, int edge)
    {
        var scale = edge / (double)Math.Max(image.Width, image.Height);
        image.Mutate(context => context.Resize(new ResizeOptions
        {
            Size = new Size(Math.Max(1, (int)Math.Round(image.Width * scale)), Math.Max(1, (int)Math.Round(image.Height * scale))),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Lanczos3,
        }));
    }

    static bool HasTransparencyOrLooksGraphical(Image<Rgba32> image)
    {
        var colors = new HashSet<uint>();
        var transparent = false;
        var stepX = Math.Max(1, image.Width / 64);
        var stepY = Math.Max(1, image.Height / 64);
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y += stepY)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < row.Length; x += stepX)
                {
                    var pixel = row[x];
                    if (pixel.A < byte.MaxValue) transparent = true;
                    if (colors.Count <= 256) colors.Add(((uint)pixel.R << 24) | ((uint)pixel.G << 16) | ((uint)pixel.B << 8) | pixel.A);
                }
            }
        });
        return transparent || colors.Count <= 256;
    }

    static async Task<byte[]> EncodeAsync(Image image, bool png, CancellationToken cancellationToken)
    {
        await using var output = new MemoryStream();
        if (png)
            await image.SaveAsync(output, new PngEncoder { CompressionLevel = PngCompressionLevel.BestCompression }, cancellationToken).ConfigureAwait(false);
        else
            await image.SaveAsync(output, new JpegEncoder { Quality = 80 }, cancellationToken).ConfigureAwait(false);
        return output.ToArray();
    }
}

public sealed class ChatImageTooLargeException(int maximumBytes)
    : Exception($"The optimized image still exceeds the {maximumBytes}-byte chat limit.");
