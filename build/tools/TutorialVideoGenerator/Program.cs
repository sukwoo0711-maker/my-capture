using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using MyCapture.App;
using MyCapture.App.Themes;
using MyCapture.Core.Themes;

namespace MyCapture.TutorialGenerator;

/// <summary>
/// Renders every feature tutorial (TutorialCatalog × ko/en) from the current product source to
/// H.264 MP4, plus the player's first-frame still (JPEG) for each clip. Usage:
/// <code>
/// dotnet run --project build/tools/TutorialVideoGenerator -c Release -- [--features id,id] [--languages ko,en]
///     [--out folder] [--width 960] [--quality 60|bitrate] [--bitrate 300000] [--profile high] [--install] [--no-sheets]
///     [--theme glass] [--accent red]
/// </code>
/// Output defaults to artifacts/tutorials/&lt;timestamp&gt; (Git ignores artifacts/). --install copies the
/// clips and their stills to src/MyCapture.App/Assets/Tutorials.
/// </summary>
internal static class Program
{
    internal const int DefaultBitrate = 300_000, DefaultQuality = 60;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "review")
        {
            string folder = Path.GetFullPath(args[1]);
            foreach (string language in new[] { "ko", "en" })
            {
                string[] clips = Directory.GetFiles(folder, $"*.{language}.mp4").Order(StringComparer.Ordinal).ToArray();
                ClipReview.SaveContactSheet(clips, Path.Combine(folder, $"contact-sheet.{language}.png"));
                Console.WriteLine($"REVIEW={language} CLIPS={clips.Length}");
            }
            return 0;
        }
        if (args.Length >= 3 && args[0] == "inspect")
        {
            // inspect <clip.mp4> <ms>[,<ms>…]: full-size decoded frames next to the clip, for review.
            using MyCapture.Platform.Recording.MediaFoundationVideoFrameReader reader = ClipReview.Open(args[1]);
            foreach (string ms in args[2].Split(','))
            {
                double at = double.Parse(ms, CultureInfo.InvariantCulture);
                ClipReview.SavePng(ClipReview.Frame(reader, at), Path.ChangeExtension(args[1], null) + $".{at:00000}ms.png");
            }

            return 0;
        }

        Options options;
        try { options = Options.Parse(args); }
        catch (ArgumentException error)
        {
            Console.Error.WriteLine(error.Message);
            Console.Error.WriteLine("MyCapture.TutorialGenerator [--features id,id|all] [--languages ko,en] [--out folder] [--width 960] [--quality 60|bitrate] [--bitrate 300000] [--profile high|main|baseline|default] [--install] [--no-sheets] [--theme system|glass|glass-light|high-contrast] [--accent red]");
            return 2;
        }

        // Product code awaits on the UI thread; continuations must come back to this dispatcher
        // even though the generator drives it from Main rather than from a dispatcher callback.
        System.Threading.SynchronizationContext.SetSynchronizationContext(
            new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
        Application app = CreateResourceHost();
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        // App normally wires this preference (General.AlwaysShowShortcutHints) at startup. The
        // resource-only host uses the new-install value: key captions stay hidden, as they do in
        // the product until Alt is held, and no scene holds Alt.
        bool showShortcutHints = MyCapture.Core.Settings.NewInstallDefaults.Create().General.AlwaysShowShortcutHints;
        ShortcutPresentation.ReadEnabled = () => showShortcutHints;
        ShortcutPresentation.Refresh();
        // The product clips use the defaults (Glass, red accent); review renders may pick others.
        ThemeService.ApplyFromSettings(options.Theme, captureGuideColor: null, accentColor: options.Accent);
        Stage.Scale = options.Width / Stage.Width;
        // The generator drives real product windows. A previous version created the resident App,
        // which ran retention cleanup on the owner's library and rewrote the sign-in Run entry to
        // this tool, so every run now proves it left the real profile and the Run entry untouched.
        string before = UserStateFingerprint();
        int exitCode;
        try
        {
            exitCode = Run(options);
        }
        finally
        {
            app.Shutdown();
        }

        string after = UserStateFingerprint();
        if (!string.Equals(before, after, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("The real MyCapture profile or the sign-in Run entry changed during generation.");
            Console.Error.WriteLine("before: " + before);
            Console.Error.WriteLine("after:  " + after);
            return 3;
        }

        return exitCode;
    }

    /// <summary>
    /// Sizes and write times of the owner's real profile files plus the HKCU Run value. Only metadata
    /// is read; nothing in the profile is opened for writing.
    /// </summary>
    private static string UserStateFingerprint()
    {
        var parts = new List<string>();
        string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyCapture");
        foreach (string name in new[] { "index.json", "settings.json", "folders.json" })
        {
            var file = new FileInfo(Path.Combine(profile, name));
            parts.Add(file.Exists ? $"{name}:{file.Length}:{file.LastWriteTimeUtc.Ticks}" : $"{name}:-");
        }

        using Microsoft.Win32.RegistryKey? run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run", writable: false);
        parts.Add("run:" + (run?.GetValue("MyCapture") as string ?? "-"));
        return string.Join("|", parts);
    }

    /// <summary>
    /// A plain Application carrying exactly App.xaml's resources. Never the product's App class:
    /// WPF posts Application.OnStartup on construction, which would start the resident app (the
    /// user's settings, library, retention, tray icon and hotkeys) inside the generator.
    /// </summary>
    private static Application CreateResourceHost()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string dictionary in new[] { "Tokens", "Symbols", "Controls" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{dictionary}.xaml") });
        app.Resources["BoolToVisibility"] = new MyCapture.App.Gallery.BoolToVisibilityConverter();
        app.Resources["PinLabel"] = new MyCapture.App.Gallery.PinLabelConverter();
        app.Resources["IsHeaderRow"] = new MyCapture.App.Gallery.IsHeaderRowConverter();
        return app;
    }

    /// <summary>
    /// Scratch folder inside the output folder for files a scene must create (for example a
    /// synthetic recording the real video editor opens, or what an export writes). Never the
    /// user's profile.
    /// </summary>
    internal static string WorkFolder => s_workFolder ?? throw new InvalidOperationException("The output folder is not chosen yet.");

    private static string? s_workFolder;

    /// <summary>
    /// The run's output folder. Scenes that need files (for example a fixture library under a
    /// temporary AppPaths) create them below it, never in the user's profile.
    /// </summary>
    internal static string OutputFolder { get; private set; } = string.Empty;

    private static int Run(Options options)
    {
        Directory.CreateDirectory(options.Output);
        s_workFolder = Path.Combine(options.Output, "work");
        OutputFolder = options.Output;
        var receipts = new List<ClipReceipt>();
        var failures = new List<string>();
        var total = Stopwatch.StartNew();
        foreach (string feature in options.Features)
        foreach (string language in options.Languages)
        {
            try
            {
                receipts.Add(Render(feature, language, options));
            }
            catch (Exception error)
            {
                failures.Add($"{feature}.{language}: {error}");
                Console.Error.WriteLine($"CLIP=FAIL ID={feature} LANGUAGE={language} {error}");
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        if (failures.Count == 0)
        {
            try
            {
                if (options.Sheets)
                    foreach (string language in options.Languages)
                        ClipReview.SaveContactSheet(options.Features.Select(feature => Path.Combine(options.Output, $"{feature}.{language}.mp4")).ToArray(),
                            Path.Combine(options.Output, $"contact-sheet.{language}.png"));
                if (options.Install) Install(options);
            }
            catch (Exception error)
            {
                failures.Add($"Review or installation: {error}");
                Console.Error.WriteLine($"OUTPUT=FAIL {error}");
            }
        }

        string dll = typeof(TutorialCatalog).Assembly.Location;
        var summary = new
        {
            Product = typeof(TutorialCatalog).Assembly.GetName().Version?.ToString(),
            ProductDllSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll))),
            options.Theme, options.Accent,
            Resolution = $"{Stage.PixelWidth}x{Stage.PixelHeight}",
            options.Bitrate, options.Quality, options.Profile,
            Fps = TutorialStoryboard.Fps,
            Encoder = "Media Foundation H.264 (software MFT), full range, MP4",
            Poster = $"first rendered frame, JPEG quality {ClipReview.PosterQuality}",
            Clips = receipts.Count,
            Bytes = receipts.Sum(receipt => receipt.Bytes),
            PosterBytes = receipts.Sum(receipt => receipt.PosterBytes),
            ElapsedSeconds = Math.Round(total.Elapsed.TotalSeconds, 1),
            Failures = failures,
            Receipts = receipts,
        };
        File.WriteAllText(Path.Combine(options.Output, "receipt.json"),
            JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        Console.WriteLine($"RESULT={(failures.Count == 0 ? "PASS" : "FAIL")} CLIPS={receipts.Count} BYTES={summary.Bytes} POSTER_BYTES={summary.PosterBytes} OUT={options.Output}");
        return failures.Count == 0 ? 0 : 1;
    }

    private sealed record ClipReceipt(string Feature, string Language, long Bytes, int Frames, double Seconds, int UniquePictures,
        long RenderMs, string Sha256, long PosterBytes, string PosterSha256, string Provenance);

    private static ClipReceipt Render(string feature, string language, Options options)
    {
        UiText.Configure(language);
        using IDisposable scope = UiText.UseLanguage(language);
        var clock = Stopwatch.StartNew();
        using TutorialScene scene = SceneCatalog.Create(feature, language);
        TutorialStoryboard story = scene.Story;
        if (story.DurationSeconds is < 7 or > 15)
            throw new InvalidDataException($"Clip length {story.DurationSeconds:0.0}s is outside 7–15 s.");
        if ((Scenes.PracticePinScene.Features.Contains(feature) || Scenes.PracticeGuideScene.Features.Contains(feature))
            && story.DurationSeconds > 9)
            throw new InvalidDataException($"Practice clip length {story.DurationSeconds:0.0}s is outside 7–9 s.");
        string mp4 = Path.Combine(options.Output, $"{feature}.{language}.mp4");
        string poster = Path.ChangeExtension(mp4, ".jpg");
        var keys = new List<(double Ms, string Label)>();
        int frame = 0;
        using (var writer = new ClipWriter(mp4, Stage.PixelWidth, Stage.PixelHeight, options.Bitrate, options.Quality, options.Profile))
        {
            string? previous = null;
            foreach (TutorialFramePlan plan in story.Frames)
            {
                BitmapSource picture = Stage.Render(dc => scene.Draw(dc, plan));
                if (frame == 0) ClipReview.SavePoster(picture, poster);
                writer.Write(picture, plan.Repeat);
                if (plan.Repeat > 1 || (plan.IsLast && plan.Segment != previous))
                    keys.Add(((frame + plan.Repeat / 2.0) * 1000 / TutorialStoryboard.Fps, plan.Segment));
                previous = plan.IsLast ? plan.Segment : previous;
                frame += plan.Repeat;
            }

            writer.Complete();
        }

        long renderMs = clock.ElapsedMilliseconds;
        if (options.Sheets)
        {
            // Every clip's review starts with its exact first, middle and last frames. The
            // remaining cells preserve the storyboard's important actions and held states.
            List<(double Ms, string Label)> chosen =
            [
                (0, "first"),
                (frame * 500.0 / TutorialStoryboard.Fps, "middle"),
                ((frame - 1) * 1000.0 / TutorialStoryboard.Fps, "last"),
            ];
            chosen.AddRange(keys.Count <= 9 ? keys
                : Enumerable.Range(0, 9).Select(i => keys[(int)Math.Round(i * (keys.Count - 1) / 8.0)]));
            ClipReview.SaveSheet(mp4, chosen, Path.Combine(options.Output, $"{feature}.{language}.sheet.png"));
        }

        long bytes = new FileInfo(mp4).Length, posterBytes = new FileInfo(poster).Length;
        Console.WriteLine($"CLIP=PASS ID={feature} LANGUAGE={language} FRAMES={frame} SECONDS={story.DurationSeconds:0.00} BYTES={bytes} POSTER={posterBytes} MS={renderMs}");
        var receipt = new ClipReceipt(feature, language, bytes, frame, Math.Round(story.DurationSeconds, 2), story.Frames.Count, renderMs,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(mp4))), posterBytes,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(poster))), scene.Provenance);
        File.WriteAllText(Path.Combine(options.Output, $"{feature}.{language}.receipt.json"),
            JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        return receipt;
    }

    private static void Install(Options options)
    {
        string target = Path.Combine(RepositoryRoot(), "src", "MyCapture.App", "Assets", "Tutorials");
        Directory.CreateDirectory(target);
        foreach (string feature in options.Features)
        foreach (string language in options.Languages)
        foreach (string extension in new[] { "mp4", "jpg" })
        {
            string name = $"{feature}.{language}.{extension}";
            File.Copy(Path.Combine(options.Output, name), Path.Combine(target, name), overwrite: true);
        }

        Console.WriteLine($"INSTALLED={options.Features.Count * options.Languages.Count} TARGET={target}");
    }

    internal static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "MyCapture.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("MyCapture.slnx was not found above the generator.");
    }

    private sealed record Options(IReadOnlyList<string> Features, IReadOnlyList<string> Languages, string Output, int Width, int Bitrate,
        int? Quality, int? Profile, bool Install, bool Sheets, string Theme, string Accent)
    {
        internal static Options Parse(string[] args)
        {
            string[] all = TutorialCatalog.All.Select(feature => feature.Id).ToArray();
            IReadOnlyList<string> features = all, languages = ["ko", "en"];
            string? output = null;
            int width = 960, bitrate = DefaultBitrate;
            int? quality = DefaultQuality, profile = 100;
            bool install = false, sheets = true;
            string theme = AppThemeNames.Glass, accent = AppAccentNames.Red;
            for (int i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
                switch (args[i])
                {
                    case "--features":
                        string value = Next();
                        features = value == "all" ? all : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        break;
                    case "--languages": languages = Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); break;
                    case "--out": output = Path.GetFullPath(Next()); break;
                    case "--width": width = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--bitrate": bitrate = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--quality": string q = Next(); quality = q == "bitrate" ? null : int.Parse(q, CultureInfo.InvariantCulture); break;
                    case "--profile": profile = Next() switch { "high" => 100, "main" => 77, "baseline" => 66, "default" => null, _ => throw new ArgumentException("Profile is high, main, baseline or default.") }; break;
                    case "--install": install = true; break;
                    case "--no-sheets": sheets = false; break;
                    case "--theme": theme = Next(); break;
                    case "--accent": accent = Next(); break;
                    default: throw new ArgumentException($"Unknown argument {args[i]}.");
                }
            }

            // Accept the previous CLI names before canonicalizing; comparing the saved name
            // with the input would reject valid legacy aliases after the palette migration.
            string[] acceptedThemes = ["system", "glass", "glass-light", "midnight", "daylight", "workspace",
                "high-contrast", "highcontrast", "light", "mica", "acrylic", "mica-light", "light-glass"];
            if (!acceptedThemes.Contains(theme, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException("Theme is system, glass, glass-light or high-contrast; legacy theme names are accepted.");
            if (!new[] { AppAccentNames.Red, AppAccentNames.Violet }.Contains(accent, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException("Accent is red or violet.");
            theme = AppThemeNames.ToSetting(AppThemeNames.Parse(theme));
            accent = AppAccentNames.ToSetting(AppAccentNames.Parse(accent));
            // The product assets are the default look; another palette is for review only.
            if (install && (theme != AppThemeNames.Glass || accent != AppAccentNames.Red))
                throw new ArgumentException("--install takes the default theme and accent only.");

            if (features.Count == 0 || features.Distinct().Count() != features.Count || features.Except(all).Any())
                throw new ArgumentException("Unknown or duplicate feature id.");
            if (languages.Count == 0 || languages.Except(["ko", "en"]).Any()) throw new ArgumentException("Languages are ko and en.");
            if (width is < 640 or > 1920 || width % 16 != 0 || width * 9 % 16 != 0) throw new ArgumentException("Width must be a multiple of 16 between 640 and 1920.");
            if (bitrate is < 50_000 or > 4_000_000) throw new ArgumentException("Bitrate is in bits per second (50000–4000000).");
            output ??= Path.Combine(RepositoryRoot(), "artifacts", "tutorials", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
                throw new ArgumentException($"Output folder is not empty: {output}");
            return new Options(features, languages, output, width, bitrate, quality, profile, install, sheets, theme, accent);
        }
    }
}
