using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;

using static TutorialDrawing;

internal static class TutorialGifPipeline
{
    internal static void Generate(ITutorialSceneRenderer renderer, string path)
    {
        using var stream = new FileStream(path, FileMode.CreateNew);
        using var writer = new AnimatedGifWriter(stream, Width, Height, TutorialStoryboard.MotionDelayCentiseconds, differencing: GifFrameDifferencing.None, encoder: GifImageEncoder.Wpf);
        foreach (TutorialFramePlan plan in renderer.Storyboard.Frames)
        {
            BitmapSource indexed = Quantize(renderer.Render(plan));
            byte[] encoded = AnimatedGifWriter.EncodeFrame(indexed).ToArray();
            writer.AddEncodedFrame(new GifFrameUpdate(new GifPixelRect(0, 0, Width, Height), encoded), plan.DelayCentiseconds);
        }
        writer.Complete();
    }
    private static BitmapSource Quantize(BitmapSource source)
    {
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[Width * Height * 4];
        bgra.CopyPixels(pixels, Width * 4, 0);
        var histogram = new Dictionary<int, int>();
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int color = (pixels[i + 2] << 16) | (pixels[i + 1] << 8) | pixels[i];
            histogram[color] = histogram.GetValueOrDefault(color) + 1;
        }
        int[] chosen = histogram.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).Take(256).Select(pair => pair.Key).ToArray();
        var colors = chosen.Select(color => Color.FromRgb((byte)(color >> 16), (byte)(color >> 8), (byte)color)).ToList();
        var lookup = new Dictionary<int, byte>();
        foreach (int color in histogram.Keys)
        {
            int best = 0, minimum = int.MaxValue;
            for (int c = 0; c < chosen.Length; c++)
            {
                int dr = ((color >> 16) & 255) - colors[c].R;
                int dg = ((color >> 8) & 255) - colors[c].G;
                int db = (color & 255) - colors[c].B;
                int distance = dr * dr + dg * dg + db * db;
                if (distance < minimum) { minimum = distance; best = c; }
            }
            lookup[color] = (byte)best;
        }
        var indexes = new byte[Width * Height];
        for (int i = 0; i < indexes.Length; i++)
            indexes[i] = lookup[(pixels[i * 4 + 2] << 16) | (pixels[i * 4 + 1] << 8) | pixels[i * 4]];
        BitmapSource result = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Indexed8, new BitmapPalette(colors), indexes, Width);
        result.Freeze();
        return result;
    }
    private static void Require(bool condition, string detail) { if (!condition) throw new InvalidDataException(detail); }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    internal static object Validate(string path, string language, TutorialStoryboard story, out GifSampleFrames decoded)
    {
        int[] delays = story.Frames.Select(frame => frame.DelayCentiseconds).ToArray();
        decoded = GifSampleDecoder.Decode(path);
        Require(decoded.PixelWidth == Width && decoded.PixelHeight == Height, "Canvas");
        Require(decoded.Frames.Count == delays.Length && decoded.Frames.Count <= 160, "GIF frame count");
        Require(decoded.DelaysCentiseconds.SequenceEqual(delays), "Preserved holds and drag delays");
        Require(decoded.Timeline.TotalMs is >= 10000 and <= 12000, "Duration");
        byte[] bytes = File.ReadAllBytes(path);
        Require(bytes.Length <= 3 * 1024 * 1024, "GIF budget");
        int netscape = bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes("NETSCAPE2.0"));
        Require(netscape >= 0 && bytes[netscape + 11] == 3 && bytes[netscape + 12] == 1
            && bytes[netscape + 13] == 0 && bytes[netscape + 14] == 0, "Infinite GIF loop extension");
        using var stream = new MemoryStream(bytes, false);
        var raw = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        long memory = 0;
        var hashes = new List<string>();
        for (int i = 0; i < decoded.Frames.Count; i++)
        {
            BitmapSource frame = decoded.Frames[i];
            Require(frame.IsFrozen, "Detached frozen playback frame");
            var metadata = (BitmapMetadata)raw.Frames[i].Metadata;
            Require(raw.Frames[i].PixelWidth == Width && raw.Frames[i].PixelHeight == Height
                && Convert.ToInt32(metadata.GetQuery("/imgdesc/Left")) == 0
                && Convert.ToInt32(metadata.GetQuery("/imgdesc/Top")) == 0, "Full frame descriptor");
            Require(Convert.ToInt32(metadata.GetQuery("/grctlext/Disposal")) == 1, "Keep-frame disposal");
            var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[Width * Height * 4]; bgra.CopyPixels(pixels, Width * 4, 0);
            Require(Enumerable.Range(0, Width * Height).All(p => pixels[p * 4 + 3] == 255), "Full opaque frame");
            hashes.Add(Convert.ToHexString(SHA256.HashData(pixels)));
            memory += (long)((Width * frame.Format.BitsPerPixel + 7) / 8) * Height;
            Require(decoded.Timeline.IndexAt(decoded.Timeline.StartOf(i)) == i, "Timeline boundary");
        }
        Require(memory <= 40 * 1024 * 1024, "Decoded memory");
        Require(hashes.Distinct().Count() >= Math.Max(1, story.Frames.Count(frame => frame.DelayCentiseconds == 4) - 16), "Distinct continuous-motion frames");
        var motionSegments = story.Frames.Select((frame, index) => (frame, index)).Where(item => item.frame.DelayCentiseconds == 4)
            .GroupBy(item => item.frame.Segment).Select(group => new
            {
                Segment = group.Key, StartFrame = group.First().index, EndFrame = group.Last().index,
                Count = group.Count(), DurationMs = group.Count() * 40,
                MaximumCursorStep = group.Zip(group.Skip(1), (a, b) => Math.Sqrt(Math.Pow(a.frame.Cursor.X - b.frame.Cursor.X, 2) + Math.Pow(a.frame.Cursor.Y - b.frame.Cursor.Y, 2))).DefaultIfEmpty().Max(),
                DistinctFrames = group.Select(item => hashes[item.index]).Distinct().Count(),
            }).ToArray();
        Require(motionSegments.All(segment => segment.Count >= 1 && segment.MaximumCursorStep <= 25), "Continuous eased paths");
        Require(motionSegments.All(segment => segment.DistinctFrames >= Math.Max(1, segment.Count - 2)), "Continuous decoded cursor motion");
        Require(decoded.Timeline.IndexAt(decoded.Timeline.TotalMs) == 0, "Timeline loops");
        var player = new GifSamplePlayer(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        player.Load(decoded.Timeline);
        for (int i = 0; i < decoded.Frames.Count; i++) { player.Seek(i); Require(player.Index == i, "Player seeks every stage"); }
        player.Play(); Require(player.IsPlaying, "Player plays"); player.Pause(); Require(!player.IsPlaying, "Player pauses");
        return new { Language = language, Bytes = bytes.Length, Sha256 = Hash(path), Frames = decoded.Frames.Count,
            DurationMs = decoded.Timeline.TotalMs, DelaysCentiseconds = decoded.DelaysCentiseconds,
            DecodedFrameBytes = memory, LoopCount = 0, Disposal = 1, FullOpaqueFrames = true, FrameSha256 = hashes,
            MotionFrames = motionSegments.Sum(segment => segment.Count), MotionFramesPerSecond = 25, MotionSegments = motionSegments,
            PlaybackChecks = "decode all; exact delays; each frame boundary and seek; infinite wrap; play/pause" };
    }
}
