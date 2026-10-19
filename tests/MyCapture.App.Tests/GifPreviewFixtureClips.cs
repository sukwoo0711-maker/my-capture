using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Platform.Recording;

namespace MyCapture.App.Tests;

/// <summary>
/// Synthetic screen recordings for GIF preview estimates: anti-aliased document text that
/// stays still, scrolls, or switches to a denser page, a moving pointer, and a photo-like
/// panning gradient that is hard for a 256-colour palette. Must run on an STA thread.
/// </summary>
internal static class GifPreviewFixtureClips
{
    internal enum Kind
    {
        /// <summary>A document page scrolling for the whole clip (the lead's baseline shape).</summary>
        DocumentScroll,

        /// <summary>Still page, then scrolling, then a denser dark code page (non-uniform content).</summary>
        Mixed,

        /// <summary>Colourful gradients panning slowly.</summary>
        Photo,

        /// <summary>A still document page with only the pointer moving.</summary>
        StillPage,

        /// <summary>A completely still page and pointer, followed by scrolling.</summary>
        StillStretch,
    }

    private static readonly string[] Lines =
    [
        "MyCapture 영상 내보내기 — 선택 구간을 GIF로 저장합니다.",
        "The quick brown fox jumps over the lazy dog 0123456789",
        "보고서 3분기 매출 추이와 고객 지원 응답 시간을 정리했습니다.",
        "Settings › Recording › Frame rate 30 fps · Quality 높음",
        "public void Export(RecordingResult recording, string path)",
        "회의록: 다음 배포 일정과 남은 작업을 확인하십시오.",
    ];

    internal static RecordingResult Encode(string path, Kind kind, int width, int height, int fps, double durationMs)
    {
        int frames = (int)Math.Round(durationMs * fps / 1000);
        int stride = width * 4;
        byte[] pageA = RenderDocument(width, height * 3, height / 1080.0, dark: false, out int pageAHeight);
        byte[] pageB = RenderDocument(width, height, height / 1080.0, dark: true, out _);
        int photoWidth = width + (frames * 3) + 1;
        byte[] photo = kind == Kind.Photo ? RenderPhoto(photoWidth, height) : [];
        var frame = new byte[stride * height];
        var options = new VideoEncoderOptions(path, width, height, fps, VideoEncoderOptions.DeriveBitrate(width, height, fps));
        using (var encoder = new MediaFoundationVideoEncoder(options, NullLogger<MediaFoundationVideoEncoder>.Instance))
        {
            for (int index = 0; index < frames; index++)
            {
                double progress = (double)index / Math.Max(1, frames - 1);
                switch (kind)
                {
                    case Kind.DocumentScroll:
                        CopyRows(pageA, stride, ScrollOffset(progress, pageAHeight - height), frame, height);
                        break;
                    case Kind.Mixed when progress < 0.3:
                        CopyRows(pageA, stride, 0, frame, height);
                        break;
                    case Kind.Mixed when progress < 0.7:
                        CopyRows(pageA, stride, ScrollOffset((progress - 0.3) / 0.4, pageAHeight - height), frame, height);
                        break;
                    case Kind.Mixed:
                        CopyRows(pageB, stride, 0, frame, height);
                        break;
                    case Kind.StillPage:
                        CopyRows(pageA, stride, 0, frame, height);
                        break;
                    case Kind.StillStretch:
                        CopyRows(pageA, stride, ScrollOffset(Math.Max(0, (progress - 0.5) * 2), pageAHeight - height), frame, height);
                        break;
                    default:
                        CopyColumns(photo, photoWidth * 4, index * 3, frame, width, height);
                        break;
                }

                DrawPointer(frame, width, height, kind == Kind.StillStretch ? Math.Max(0, (progress - 0.5) * 2) : progress);
                encoder.WriteFrame(new EncoderFrame(frame, width, height, stride, index * (1000.0 / fps)));
            }

            encoder.Complete();
        }

        return new RecordingResult(path, frames * 1000.0 / fps, fps, frames, width, height);
    }

    private static int ScrollOffset(double progress, int range) => (int)Math.Round(Math.Clamp(progress, 0, 1) * range);

    private static void CopyRows(byte[] page, int stride, int firstRow, byte[] frame, int height) =>
        Buffer.BlockCopy(page, firstRow * stride, frame, 0, stride * height);

    private static void CopyColumns(byte[] source, int sourceStride, int firstColumn, byte[] frame, int width, int height)
    {
        for (int y = 0; y < height; y++)
        {
            Buffer.BlockCopy(source, (y * sourceStride) + (firstColumn * 4), frame, y * width * 4, width * 4);
        }
    }

    private static void DrawPointer(byte[] frame, int width, int height, double progress)
    {
        double angle = progress * Math.PI * 4;
        int x0 = (int)((width * 0.5) + (Math.Cos(angle) * width * 0.35));
        int y0 = (int)((height * 0.5) + (Math.Sin(angle * 1.5) * height * 0.3));
        int size = Math.Max(8, height / 45);
        for (int y = 0; y < size * 3 / 2; y++)
        {
            for (int x = 0; x <= y * 2 / 3 && x < size; x++)
            {
                int px = x0 + x, py = y0 + y;
                if (px < 0 || py < 0 || px >= width || py >= height) continue;
                bool edge = x == 0 || x == y * 2 / 3 || y == (size * 3 / 2) - 1;
                byte value = edge ? (byte)255 : (byte)0;
                int offset = (py * width * 4) + (px * 4);
                frame[offset] = frame[offset + 1] = frame[offset + 2] = value;
                frame[offset + 3] = 255;
            }
        }
    }

    private static byte[] RenderDocument(int width, int height, double scale, bool dark, out int renderedHeight)
    {
        var visual = new DrawingVisual();
        var typeface = new Typeface(new FontFamily(dark ? "Cascadia Code, Consolas" : "Segoe UI, Malgun Gothic"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        Brush background = new SolidColorBrush(dark ? Color.FromRgb(30, 30, 30) : Color.FromRgb(247, 247, 244));
        Brush[] inks = dark
            ? [new SolidColorBrush(Color.FromRgb(212, 212, 212)), new SolidColorBrush(Color.FromRgb(86, 156, 214)), new SolidColorBrush(Color.FromRgb(206, 145, 120)), new SolidColorBrush(Color.FromRgb(106, 153, 85))]
            : [new SolidColorBrush(Color.FromRgb(34, 34, 34)), new SolidColorBrush(Color.FromRgb(70, 70, 70))];
        double lineHeight = (dark ? 22 : 30) * scale;
        double fontSize = (dark ? 14 : 16) * scale;
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(background, null, new Rect(0, 0, width, height));
            if (!dark)
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(43, 87, 151)), null, new Rect(0, 0, width, 48 * scale));
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(232, 236, 242)), null, new Rect(0, 48 * scale, width * 0.18, height));
            }

            double left = dark ? 24 * scale : (width * 0.18) + (32 * scale);
            int line = 0;
            for (double y = 64 * scale; y < height - lineHeight; y += lineHeight, line++)
            {
                if (!dark && line % 9 == 8)
                {
                    // Coloured chart/image block every few paragraphs.
                    dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(255, 183, 77), Color.FromRgb(66, 165, 245), 0),
                        null, new Rect(left, y, width * 0.35, lineHeight * 3));
                    y += lineHeight * 3;
                    continue;
                }

                var text = new FormattedText(Lines[line % Lines.Length], CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    typeface, fontSize, inks[line % inks.Length], 1.0);
                dc.DrawText(text, new Point(left + ((line % 4) * 12 * scale), y));
            }
        }

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        var pixels = new byte[width * height * 4];
        target.CopyPixels(pixels, width * 4, 0);
        renderedHeight = height;
        return pixels;
    }

    private static byte[] RenderPhoto(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        var random = new Random(7);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double u = (double)x / width, v = (double)y / height;
                int offset = ((y * width) + x) * 4;
                int noise = random.Next(-6, 7);
                pixels[offset] = (byte)Math.Clamp((int)(128 + (127 * Math.Sin((u * 9) + (v * 3)))) + noise, 0, 255);
                pixels[offset + 1] = (byte)Math.Clamp((int)(128 + (127 * Math.Sin((u * 4) - (v * 7) + 1))) + noise, 0, 255);
                pixels[offset + 2] = (byte)Math.Clamp((int)(128 + (127 * Math.Cos((u * 6) + (v * 5)))) + noise, 0, 255);
                pixels[offset + 3] = 255;
            }
        }

        return pixels;
    }
}
