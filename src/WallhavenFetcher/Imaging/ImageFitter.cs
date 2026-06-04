using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
// Aliases so we don't collide with System.Drawing.* (brought in globally by
// ImplicitUsings + UseWindowsForms). The generic Image<T> is unique to
// SixLabors so it doesn't need aliasing — only the non-generic factories do.
using ImgImage     = SixLabors.ImageSharp.Image;
using ImgPoint     = SixLabors.ImageSharp.Point;
using ImgSize      = SixLabors.ImageSharp.Size;
using ImgRectangle = SixLabors.ImageSharp.Rectangle;

namespace WallhavenFetcher.Imaging;

/// <summary>
/// Make a downloaded image's aspect ratio match the display target.
///
/// Decision flow per-image:
///   1. If ratio drift ≤ fit_tolerance_pct → don't touch (return false).
///   2. If we'd lose ≤ crop_threshold_pct of off-axis pixels → center-crop.
///   3. Otherwise → composite original (native size, no upscale) onto a
///      blurred-background copy of itself stretched to target ratio.
///
/// Background blur runs at target-resolution intermediate (cheap), then is
/// upscaled to canvas size before composite. Blur removes high-frequency
/// detail anyway so this is visually identical to blurring at canvas size
/// but much faster (Gaussian blur cost is O(w·h·radius²)).
/// </summary>
public sealed class ImageFitter
{
    private readonly int _targetW;
    private readonly int _targetH;
    private readonly double _tolerance;
    private readonly double _cropThreshold;
    private readonly double _maxDimFactor;

    public ImageFitter(int targetW, int targetH, int tolerancePct, int cropThresholdPct,
                       double maxDimFactor = 2.0)
    {
        _targetW = targetW;
        _targetH = targetH;
        _tolerance = Math.Max(0, tolerancePct) / 100.0;
        _cropThreshold = Math.Max(0, cropThresholdPct) / 100.0;
        // <1 disables (we never upscale). 0 explicitly disables. Anything
        // >=1 caps each axis at monitor_dim × factor.
        _maxDimFactor = maxDimFactor;
    }

    public async Task<(bool Modified, string Reason)> FitInPlaceAsync(
        string path, CancellationToken ct = default)
    {
        try
        {
            using var img = await ImgImage.LoadAsync<Rgba32>(path, ct);
            int iw = img.Width;
            int ih = img.Height;
            double targetRatio = (double)_targetW / _targetH;

            // ── Phase 0: DOWNSCALE oversized images ────────────────────────
            // Cap whichever axis exceeds monitor_dim × factor; keep aspect.
            string downscalePrefix = "";
            if (_maxDimFactor >= 1.0)
            {
                int capW = (int)(_targetW * _maxDimFactor);
                int capH = (int)(_targetH * _maxDimFactor);
                double scaleW = iw > capW ? (double)capW / iw : 1.0;
                double scaleH = ih > capH ? (double)capH / ih : 1.0;
                double scale  = Math.Min(scaleW, scaleH);
                if (scale < 1.0)
                {
                    int newW = Math.Max(1, (int)Math.Round(iw * scale));
                    int newH = Math.Max(1, (int)Math.Round(ih * scale));
                    img.Mutate(x => x.Resize(newW, newH));
                    downscalePrefix = $"downscaled {iw}x{ih} → {newW}x{newH}, then ";
                    iw = newW;
                    ih = newH;
                }
            }

            double imgRatio = (double)iw / ih;
            if (Math.Abs(imgRatio - targetRatio) / targetRatio <= _tolerance)
            {
                if (downscalePrefix.Length > 0)
                {
                    // Within tolerance now — save the downscaled version.
                    await img.SaveAsync(path, ct);
                    return (true, $"{downscalePrefix}(within tolerance after downscale)");
                }
                return (false, $"within tolerance ({iw}x{ih})");
            }

            // Compute crop vs pad
            int cropW, cropH;
            double cropLoss;
            if (imgRatio > targetRatio)
            {
                // Too wide — would crop sides
                cropW = (int)Math.Round(ih * targetRatio);
                cropH = ih;
                cropLoss = (iw - cropW) / (double)iw;
            }
            else
            {
                // Too tall — would crop top/bottom
                cropH = (int)Math.Round(iw / targetRatio);
                cropW = iw;
                cropLoss = (ih - cropH) / (double)ih;
            }

            if (cropLoss <= _cropThreshold)
            {
                // CROP path
                img.Mutate(x => x
                    .Crop(new ImgRectangle((iw - cropW) / 2, (ih - cropH) / 2, cropW, cropH)));
                await img.SaveAsync(path, ct);
                return (true,
                    $"{downscalePrefix}{iw}x{ih} → {cropW}x{cropH} " +
                    $"(cropped {cropLoss * 100:F1}% off-axis)");
            }

            // PAD path: blur-background composite
            int canvasW, canvasH;
            if (imgRatio > targetRatio) { canvasW = iw; canvasH = (int)Math.Round(iw / targetRatio); }
            else                         { canvasH = ih; canvasW = (int)Math.Round(ih * targetRatio); }

            await BuildPaddedComposite(img, canvasW, canvasH, path, ct);

            return (true,
                $"{downscalePrefix}{iw}x{ih} → {canvasW}x{canvasH} " +
                $"(padded; crop would lose {cropLoss * 100:F1}% > {_cropThreshold * 100:F0}%)");
        }
        catch (Exception ex)
        {
            return (false, $"fit failed: {ex.Message}");
        }
    }

    private async Task BuildPaddedComposite(
        Image<Rgba32> original, int canvasW, int canvasH, string destPath,
        CancellationToken ct)
    {
        // Background: blur at target resolution (small, fast), then upscale to canvas.
        // ImageMagick's `-blur 0x12` ≈ Gaussian sigma 4 (sigma = radius / 3).
        // Visual result matches the macOS version's padded-bg look.
        using var bg = original.Clone(x => x
            .Resize(new ResizeOptions
            {
                Size = new ImgSize(_targetW, _targetH),
                Mode = ResizeMode.Crop,
                Position = AnchorPositionMode.Center,
            })
            .GaussianBlur(4f)
            .Resize(canvasW, canvasH));

        // Composite original at native size, centered.
        int offsetX = (canvasW - original.Width) / 2;
        int offsetY = (canvasH - original.Height) / 2;
        bg.Mutate(x => x.DrawImage(original, new ImgPoint(offsetX, offsetY), 1.0f));

        await bg.SaveAsync(destPath, ct);
    }
}
