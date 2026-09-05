using System.Text.Json;
using OpenCvSharp;
using SkiaSharp;

namespace lawar4.Services;

/// <summary>
/// Persistent multi-signal avatar fingerprint (multi-scale dHash + ORB descriptors),
/// ported from avatars.py. Serialized as compact JSON so it can be stored per member.
/// </summary>
public static class Fingerprinter
{
    private static readonly double[] CropScales = { 0.60, 0.72, 0.84 };
    private const int HashWidth = 17;
    private const int HashHeight = 16;
    private const int HashBits = (HashWidth - 1) * HashHeight; // 256
    private const int OrbSize = 160;
    private const int OrbFeatures = 300;

    // Standard Rec. 601 luma coefficients, replicated across R/G/B so any channel can be read back.
    private static readonly SKColorFilter GrayscaleFilter = SKColorFilter.CreateColorMatrix(new float[]
    {
        0.299f, 0.587f, 0.114f, 0, 0,
        0.299f, 0.587f, 0.114f, 0, 0,
        0.299f, 0.587f, 0.114f, 0, 0,
        0,      0,      0,      1, 0,
    });

    private static readonly SKSamplingOptions HighQualitySampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    public static string? FingerprintFromBBox(string imagePath, (int X, int Y, int Width, int Height)? bbox)
    {
        if (bbox is null)
            return null;
        var (_, _, w, h) = bbox.Value;
        if (w < 10 || h < 10)
            return null;
        try
        {
            using var avatar = CropAvatarFromNormalizedBBox(imagePath, bbox.Value);
            if (Math.Min(avatar.Width, avatar.Height) < 12)
                return null;
            return FingerprintImage(avatar);
        }
        catch
        {
            return null;
        }
    }

    public static SKBitmap CropAvatarFromNormalizedBBox(string imagePath, (int X, int Y, int Width, int Height) bbox)
    {
        var (x, y, w, h) = bbox;
        using var oriented = LoadOriented(imagePath);
        int width = oriented.Width, height = oriented.Height;
        int left = Math.Clamp((int)Math.Round(x * width / 1000.0), 0, width - 1);
        int top = Math.Clamp((int)Math.Round(y * height / 1000.0), 0, height - 1);
        int right = Math.Clamp((int)Math.Round((x + w) * width / 1000.0), left + 1, width);
        int bottom = Math.Clamp((int)Math.Round((y + h) * height / 1000.0), top + 1, height);
        return CropBitmap(oriented, new SKRectI(left, top, right, bottom));
    }

    public static string FingerprintImage(SKBitmap image)
    {
        var hashes = new List<string>();
        foreach (var scale in CropScales)
        {
            using var crop = CenterCrop(image, scale);
            hashes.Add(DHashHex(crop));
        }

        var descriptors = OrbDescriptors(image);
        var payload = new Dictionary<string, object> { ["v"] = 2, ["hashes"] = hashes };
        if (descriptors is { Length: > 0 })
        {
            payload["orb_rows"] = descriptors.Length / 32;
            payload["orb"] = Convert.ToBase64String(descriptors);
        }
        return JsonSerializer.Serialize(payload);
    }

    private static SKBitmap LoadOriented(string imagePath)
    {
        using var stream = File.OpenRead(imagePath);
        using var codec = SKCodec.Create(stream) ?? throw new InvalidOperationException("Unsupported image format.");
        var bitmap = new SKBitmap(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var result = codec.GetPixels(bitmap.Info, bitmap.GetPixels());
        if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
            throw new InvalidOperationException($"Failed to decode image: {result}");
        return ApplyExifOrientation(bitmap, codec.EncodedOrigin);
    }

    private static SKBitmap ApplyExifOrientation(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        switch (origin)
        {
            case SKEncodedOrigin.TopLeft:
                return bitmap;
            case SKEncodedOrigin.TopRight:
                using (bitmap)
                    return FlipHorizontal(bitmap);
            case SKEncodedOrigin.BottomRight:
                using (bitmap)
                    return Rotate180(bitmap);
            case SKEncodedOrigin.BottomLeft:
                using (bitmap)
                    return FlipVertical(bitmap);
            case SKEncodedOrigin.RightTop:
                using (bitmap)
                    return RotateCw90(bitmap);
            case SKEncodedOrigin.LeftBottom:
                using (bitmap)
                    return RotateCcw90(bitmap);
            case SKEncodedOrigin.LeftTop:
                using (bitmap)
                using (var flipped = FlipHorizontal(bitmap))
                    return RotateCw90(flipped);
            case SKEncodedOrigin.RightBottom:
                using (bitmap)
                using (var flipped = FlipHorizontal(bitmap))
                    return RotateCcw90(flipped);
            default:
                return bitmap;
        }
    }

    private static SKBitmap Rotate180(SKBitmap src)
    {
        var dst = new SKBitmap(src.Width, src.Height, src.ColorType, src.AlphaType);
        using var canvas = new SKCanvas(dst);
        canvas.RotateDegrees(180, src.Width / 2f, src.Height / 2f);
        canvas.DrawBitmap(src, 0, 0, SKSamplingOptions.Default);
        return dst;
    }

    private static SKBitmap RotateCw90(SKBitmap src)
    {
        var dst = new SKBitmap(src.Height, src.Width, src.ColorType, src.AlphaType);
        using var canvas = new SKCanvas(dst);
        canvas.Translate(dst.Width, 0);
        canvas.RotateDegrees(90);
        canvas.DrawBitmap(src, 0, 0, SKSamplingOptions.Default);
        return dst;
    }

    private static SKBitmap RotateCcw90(SKBitmap src)
    {
        var dst = new SKBitmap(src.Height, src.Width, src.ColorType, src.AlphaType);
        using var canvas = new SKCanvas(dst);
        canvas.Translate(0, dst.Height);
        canvas.RotateDegrees(-90);
        canvas.DrawBitmap(src, 0, 0, SKSamplingOptions.Default);
        return dst;
    }

    private static SKBitmap FlipHorizontal(SKBitmap src)
    {
        var dst = new SKBitmap(src.Width, src.Height, src.ColorType, src.AlphaType);
        using var canvas = new SKCanvas(dst);
        canvas.Scale(-1, 1, src.Width / 2f, 0);
        canvas.DrawBitmap(src, 0, 0, SKSamplingOptions.Default);
        return dst;
    }

    private static SKBitmap FlipVertical(SKBitmap src)
    {
        var dst = new SKBitmap(src.Width, src.Height, src.ColorType, src.AlphaType);
        using var canvas = new SKCanvas(dst);
        canvas.Scale(1, -1, 0, src.Height / 2f);
        canvas.DrawBitmap(src, 0, 0, SKSamplingOptions.Default);
        return dst;
    }

    private static SKBitmap CropBitmap(SKBitmap src, SKRectI rect)
    {
        var dst = new SKBitmap(rect.Width, rect.Height, src.ColorType, src.AlphaType);
        if (!src.ExtractSubset(dst, rect))
            throw new InvalidOperationException("Failed to crop bitmap.");
        return dst;
    }

    private static SKBitmap CenterCrop(SKBitmap image, double fraction)
    {
        int width = image.Width, height = image.Height;
        int cropW = Math.Max(1, (int)(width * fraction));
        int cropH = Math.Max(1, (int)(height * fraction));
        int left = Math.Max(0, (width - cropW) / 2);
        int top = Math.Max(0, (height - cropH) / 2);
        return CropBitmap(image, new SKRectI(left, top, left + cropW, top + cropH));
    }

    private static SKBitmap GrayscaleResize(SKBitmap src, int width, int height)
    {
        var dst = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(dst);
        using var paint = new SKPaint { ColorFilter = GrayscaleFilter };
        canvas.DrawBitmap(src, new SKRect(0, 0, width, height), HighQualitySampling, paint);
        return dst;
    }

    // Extracts the R channel (== G == B after grayscale) as a flat row-major byte plane.
    private static byte[] GrayscalePlane(SKBitmap grayBitmap)
    {
        int width = grayBitmap.Width, height = grayBitmap.Height;
        int rowBytes = grayBitmap.RowBytes;
        int bpp = grayBitmap.BytesPerPixel;
        var raw = new byte[rowBytes * height];
        System.Runtime.InteropServices.Marshal.Copy(grayBitmap.GetPixels(), raw, 0, raw.Length);

        var plane = new byte[width * height];
        for (int yy = 0; yy < height; yy++)
        {
            int rowStart = yy * rowBytes;
            for (int xx = 0; xx < width; xx++)
                plane[yy * width + xx] = raw[rowStart + xx * bpp];
        }
        return plane;
    }

    private static string DHashHex(SKBitmap image)
    {
        using var gray = GrayscaleResize(image, HashWidth, HashHeight);
        var pixels = GrayscalePlane(gray);

        var bytes = new byte[HashBits / 8]; // 32 bytes
        int bit = 0;
        for (int yy = 0; yy < HashHeight; yy++)
        {
            int rowStart = yy * HashWidth;
            for (int xx = 0; xx < HashWidth - 1; xx++)
            {
                if (pixels[rowStart + xx] > pixels[rowStart + xx + 1])
                    bytes[bit >> 3] |= (byte)(1 << (bit & 7));
                bit++;
            }
        }
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static byte[]? OrbDescriptors(SKBitmap image)
    {
        using var inner = CenterCrop(image, 0.76);
        using var resized = GrayscaleResize(inner, OrbSize, OrbSize);
        var gray = GrayscalePlane(resized);

        using var mat = Mat.FromPixelData(OrbSize, OrbSize, MatType.CV_8UC1, gray);
        using var orb = ORB.Create(nFeatures: OrbFeatures, fastThreshold: 5);
        using var descriptors = new Mat();
        orb.DetectAndCompute(mat, null, out _, descriptors);
        if (descriptors.Empty() || descriptors.Rows == 0)
            return null;

        int count = descriptors.Rows * 32;
        var buffer = new byte[count];
        System.Runtime.InteropServices.Marshal.Copy(descriptors.Data, buffer, 0, count);
        return buffer;
    }

    // --- Similarity ---

    private sealed record Decoded(List<byte[]> Hashes, byte[]? Orb, int OrbRows);

    private static Decoded DecodeFingerprint(string value)
    {
        try
        {
            using var doc = JsonDocument.Parse(value);
            var root = doc.RootElement;
            // Backward-compat: earliest builds stored only a JSON array of hex hashes.
            if (root.ValueKind == JsonValueKind.Array)
                return new Decoded(root.EnumerateArray().Select(e => Convert.FromHexString(e.GetString()!)).ToList(), null, 0);

            var hashes = new List<byte[]>();
            if (root.TryGetProperty("hashes", out var hashesEl) && hashesEl.ValueKind == JsonValueKind.Array)
                hashes = hashesEl.EnumerateArray().Select(e => Convert.FromHexString(e.GetString()!)).ToList();

            byte[]? orb = null;
            int rows = 0;
            if (root.TryGetProperty("orb", out var orbEl) && orbEl.ValueKind == JsonValueKind.String &&
                root.TryGetProperty("orb_rows", out var rowsEl) && rowsEl.TryGetInt32(out rows) && rows > 0)
            {
                var raw = Convert.FromBase64String(orbEl.GetString()!);
                if (raw.Length == rows * 32)
                    orb = raw;
                else
                    rows = 0;
            }
            return new Decoded(hashes, orb, rows);
        }
        catch
        {
            return new Decoded(new List<byte[]>(), null, 0);
        }
    }

    private static double HashSimilarity(List<byte[]> ah, List<byte[]> bh)
    {
        if (ah.Count == 0 || bh.Count == 0)
            return 0.0;
        var scores = new List<double>();
        for (int i = 0; i < ah.Count; i++)
        {
            double best = double.NaN;
            for (int j = 0; j < bh.Count; j++)
            {
                if (Math.Abs(i - j) <= 1)
                {
                    int distance = HammingDistance(ah[i], bh[j]);
                    double local = 1.0 - (double)distance / HashBits;
                    if (double.IsNaN(best) || local > best)
                        best = local;
                }
            }
            if (!double.IsNaN(best))
                scores.Add(best);
        }
        return scores.Count > 0 ? scores.Average() : 0.0;
    }

    private static int HammingDistance(byte[] a, byte[] b)
    {
        int len = Math.Min(a.Length, b.Length);
        int distance = 0;
        for (int i = 0; i < len; i++)
            distance += System.Numerics.BitOperations.PopCount((uint)(byte)(a[i] ^ b[i]));
        return distance;
    }

    private static double? OrbSimilarity(byte[]? a, int aRows, byte[]? b, int bRows)
    {
        if (a is null || b is null || aRows < 8 || bRows < 8)
            return null;
        using var matA = Mat.FromPixelData(aRows, 32, MatType.CV_8UC1, a);
        using var matB = Mat.FromPixelData(bRows, 32, MatType.CV_8UC1, b);
        using var matcher = new BFMatcher(NormTypes.Hamming);
        var pairs = matcher.KnnMatch(matA, matB, 2);
        int good = 0;
        foreach (var pair in pairs)
        {
            if (pair.Length >= 2 && pair[0].Distance < 0.75 * pair[1].Distance)
                good++;
        }
        int denominator = Math.Max(1, Math.Min(aRows, bRows));
        double goodRatio = (double)good / denominator;
        return Math.Min(1.0, goodRatio / 0.35);
    }

    public static double FingerprintSimilarity(string a, string b)
    {
        var da = DecodeFingerprint(a);
        var db = DecodeFingerprint(b);
        double hashScore = HashSimilarity(da.Hashes, db.Hashes);
        double? orbScore = OrbSimilarity(da.Orb, da.OrbRows, db.Orb, db.OrbRows);
        if (orbScore is null)
            return hashScore;
        return 0.82 * orbScore.Value + 0.18 * hashScore;
    }
}
