using SkiaSharp;

namespace WinProvision.Core.Services;

public sealed record DecodedWebpImage(byte[] PngBytes, int Width, int Height, bool HasAlpha);

/// <summary>Decodes catalog WebP without relying on installed Windows codecs.</summary>
public static class WebpImageDecoder
{
    private const long MaximumSourcePixels = 32_000_000;

    public static DecodedWebpImage Decode(ReadOnlySpan<byte> webpBytes, int requestedWidth)
    {
        byte[] bytes = webpBytes.ToArray();
        using var input = new MemoryStream(bytes, writable: false);
        using SKCodec codec = SKCodec.Create(input) ?? throw new InvalidDataException("A imagem WebP do catálogo não pôde ser decodificada.");
        SKImageInfo source = codec.Info;
        if (source.Width < 1 || source.Height < 1 || (long)source.Width * source.Height > MaximumSourcePixels)
            throw new InvalidDataException("As dimensões da imagem WebP excedem o limite permitido.");
        bool hasAlpha = source.AlphaType != SKAlphaType.Opaque;
        using SKBitmap decoded = SKBitmap.Decode(bytes) ?? throw new InvalidDataException("A imagem WebP do catálogo está danificada.");
        int edge = Math.Clamp(requestedWidth <= 0 ? 2048 : requestedWidth, 16, 2048);
        double scale = Math.Min(1d, Math.Min(edge / (double)decoded.Width, edge / (double)decoded.Height));
        int width = Math.Max(1, (int)Math.Round(decoded.Width * scale));
        int height = Math.Max(1, (int)Math.Round(decoded.Height * scale));
        using SKBitmap resized = decoded.Width == width && decoded.Height == height
            ? decoded.Copy()
            : decoded.Resize(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.High)
                ?? throw new InvalidDataException("A imagem WebP não pôde ser redimensionada.");
        using SKImage image = SKImage.FromBitmap(resized);
        using SKData png = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidDataException("A imagem WebP não pôde ser convertida para o formato de exibição.");
        return new DecodedWebpImage(png.ToArray(), width, height, hasAlpha);
    }

    public static bool IsWebp(ReadOnlySpan<byte> bytes) => bytes.Length >= 12
        && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8);
}
