using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.App.Ocr;
using MyCapture.App.Recording;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Imaging;
using MyCapture.Platform.Shell;

namespace MyCapture.TutorialGenerator.Scenes;

/// <summary>
/// A real library over a fixture queue: CaptureQueue on a temporary AppPaths below the run's
/// output folder, real records with synthetic captures, and the product's GalleryController,
/// GalleryViewModel, CaptureCommitService, VideoLibraryService, RecoverableLibraryService and
/// GalleryWindow, built the way StoreScreenshotSelfTest.LibraryScene builds them. Removal never
/// reaches the Recycle Bin: the recovery service's trash moves a capture folder to a folder inside
/// the fixture, and its journal lives in the fixture's own data folder.
/// </summary>
internal sealed class LibraryFixture : IDisposable
{
    private readonly ILoggerFactory _logs = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
    private readonly OcrResultPresenter _presenter;
    private readonly AppSettings _settings = NewInstallDefaults.Create();

    internal LibraryFixture(bool korean)
    {
        Korean = korean;
        if (string.IsNullOrEmpty(Program.OutputFolder)) throw new InvalidOperationException("The output folder is not set.");
        string root = Path.Combine(Program.OutputFolder, "fixtures", "library-" + Guid.NewGuid().ToString("N")[..12]);
        Paths = AppPaths.CreateForRoot(root);
        Directory.CreateDirectory(Paths.CapturesRoot);
        Queue = new CaptureQueue(Paths, _settings.Queue, _logs.CreateLogger<CaptureQueue>());
        // What the app wires: user deletions go through the recovery service, which journals them
        // (Undo, Restore recently deleted) and hands the folder to its trash.
        Recovery = new RecoverableLibraryService(Queue, Paths, new FixtureTrash(Path.Combine(root, "removed")),
            _logs.CreateLogger<RecoverableLibraryService>());
        Queue.PrepareRemoval = Recovery.PrepareRemoval;

        // Folders from yesterday, so today's captures lead the library and the folders follow.
        DevelopmentFolder = Wait(Queue.CreateFolderAsync(L("개발 기록", "Development notes"), null, Now.AddDays(-1).AddHours(-3)));
        ReferenceFolder = Wait(Queue.CreateFolderAsync(L("참고 자료", "References"), null, Now.AddDays(-1).AddHours(-3)));
        foreach (Sample sample in Samples()) AddRecord(sample);

        Controller = new GalleryController(Queue, _logs.CreateLogger<GalleryController>()) { Recovery = Recovery };
        var persistence = new CapturePersistenceService(Queue, Paths, () => _settings.Queue, _logs.CreateLogger<CapturePersistenceService>());
        var commit = new CaptureCommitService(persistence, () => _settings, () => Paths, _logs.CreateLogger<CaptureCommitService>(),
            (_, _) => Task.FromResult(true));
        var videoLibrary = new VideoLibraryService(Queue, Paths, _logs.CreateLogger<VideoLibraryService>());
        var ocr = new UxReviewSelfTest.FixtureOcr();
        _presenter = new OcrResultPresenter(ocr, System.Windows.Threading.Dispatcher.CurrentDispatcher, _ => { }, NullLogger.Instance);
        ViewModel = new GalleryViewModel(Controller,
            record => Path.Combine(Queue.GetDirectory(record), CaptureFileNames.Thumbnail), 480, clock: () => Now,
            imageRetentionHours: () => _settings.Queue.ImageRetentionHours);
        var indexing = new OcrIndexingService(Controller, ocr, Queue.GetDirectory, () => _settings.Ocr,
            _logs.CreateLogger<OcrIndexingService>(), System.Windows.Threading.Dispatcher.CurrentDispatcher);
        ReeditLoader = new GalleryReeditLoader(Queue, _logs.CreateLogger<GalleryReeditLoader>());
        Window = new GalleryWindow(ViewModel, Controller, ReeditLoader, commit, Queue,
            videoLibrary, Paths, _logs, _presenter, () => _settings.Ocr, indexing,
            new UxReviewSelfTest.FixturePrivacy(), NullLogger.Instance);
    }

    internal bool Korean { get; }

    internal AppPaths Paths { get; }

    internal CaptureQueue Queue { get; }

    internal GalleryController Controller { get; }

    internal GalleryViewModel ViewModel { get; }

    internal GalleryReeditLoader ReeditLoader { get; }

    internal GalleryWindow Window { get; }

    internal CaptureFolder DevelopmentFolder { get; }

    internal CaptureFolder ReferenceFolder { get; }

    internal UxReviewSelfTest.FixturePrivacy Privacy { get; } = new();

    internal RecoverableLibraryService Recovery { get; }

    /// <summary>
    /// The library's clock: the current minute, so the gallery and the editor (which reads the
    /// system clock for its retention banner) agree. Captures date from the last two and a half
    /// hours; before 03:00 the clock reads 03:00 so they all stay under Today.
    /// </summary>
    internal static DateTimeOffset Now { get; } = CurrentMinute();

    private static DateTimeOffset CurrentMinute()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        now = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Offset);
        DateTimeOffset earliest = new(DateTime.Today.AddHours(3), TimeZoneInfo.Local.GetUtcOffset(DateTime.Today));
        return now < earliest ? earliest : now;
    }

    internal Dictionary<string, CaptureRecord> Records { get; } = [];

    private string L(string korean, string english) => Korean ? korean : english;

    /// <summary>Runs the dispatcher until <paramref name="task"/> finishes (product tasks continue on it).</summary>
    internal static T Wait<T>(Task<T> task, int timeoutMs = 15000)
    {
        Wait((Task)task, timeoutMs);
        return task.GetAwaiter().GetResult();
    }

    internal static void Wait(Task task, int timeoutMs = 15000)
    {
        var clock = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            if (clock.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("A fixture library operation did not finish.");
            Stage.Pump(10);
        }

        task.GetAwaiter().GetResult();
    }

    // ---- Records ---------------------------------------------------------------------------

    private sealed record Sample(string Key, string Title, string Source, CaptureFolder? Folder, int MinutesAgo, BitmapSource Image);

    private IEnumerable<Sample> Samples()
    {
        // Oldest first: the queue lists the newest at the head.
        yield return new("palette", L("색상 팔레트", "Color palette"), "Figma", ReferenceFolder, 146, Palette());
        yield return new("signin", L("로그인 화면 시안", "Sign-in screen draft"), "Figma", ReferenceFolder, 128, SignIn());
        yield return new("api", L("API 응답 예시", "API response sample"), "Windows Terminal", DevelopmentFolder, 114, Api());
        yield return new("build", L("빌드 오류 로그", "Build error log"), "Windows Terminal", DevelopmentFolder, 101, BuildLog());
        yield return new("schedule", L("배포 일정", "Release schedule"), L("일정 - Microsoft Edge", "Calendar - Microsoft Edge"), null, 89, Schedule());
        yield return new("checklist", L("릴리스 체크리스트", "Release checklist"), L("릴리스 준비 - Microsoft Edge", "Release prep - Microsoft Edge"), null, 65, Checklist());
        yield return new("dashboard", L("매출 대시보드", "Sales dashboard"), "Northwind Analytics - Microsoft Edge", null, 41, Dashboard());
        yield return new("meeting", L("주간 회의 메모", "Weekly meeting notes"), L("메모", "Notes"), null, 17, Meeting());
    }

    private void AddRecord(Sample sample)
    {
        byte[] png = ImageCodec.EncodePng(sample.Image);
        var record = new CaptureRecord
        {
            Width = sample.Image.PixelWidth,
            Height = sample.Image.PixelHeight,
            Title = sample.Title,
            SourceWindowTitle = sample.Source,
            CreatedAt = Now.AddMinutes(-sample.MinutesAgo),
            UpdatedAt = Now.AddMinutes(-sample.MinutesAgo),
            OcrText = sample.Title,
            OcrContentRevision = 0,
            TotalBytes = png.LongLength * 3,
            FolderId = sample.Folder?.Id,
        };
        Queue.Add(record);
        string directory = Queue.GetDirectory(record);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, CaptureFileNames.Thumbnail), png);
        File.WriteAllBytes(Path.Combine(directory, CaptureFileNames.Rendered), png);
        File.WriteAllBytes(Path.Combine(directory, CaptureFileNames.Original), png);
        Records[sample.Key] = record;
    }

    // ---- Synthetic captures (other people's apps; large type so thumbnails stay readable) ----

    private const int ImageWidth = 960, ImageHeight = 600;

    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0x1F, 0x24, 0x2E));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x5B, 0x64, 0x73));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xEB));

    private static BitmapSource Draw(Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen()) draw(dc);
        var bitmap = new RenderTargetBitmap(ImageWidth, ImageHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static SolidColorBrush Rgb(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));

    private static void Paper(DrawingContext dc, Brush band)
    {
        dc.DrawRectangle(Rgb(0xFB, 0xFC, 0xFE), null, new Rect(0, 0, ImageWidth, ImageHeight));
        dc.DrawRectangle(band, null, new Rect(0, 0, ImageWidth, 24));
    }

    private BitmapSource Meeting() => Draw(dc =>
    {
        Paper(dc, Rgb(0xF2, 0xC9, 0x4C));
        Fixtures.Text(dc, L("주간 회의 메모", "Weekly meeting"), 56, 64, 64, Ink, bold: true);
        Fixtures.Text(dc, L($"{Now.Month}월 {Now.Day}일 · 제품팀",
            Now.ToString("MMMM d", System.Globalization.CultureInfo.GetCultureInfo("en-US")) + " · Product team"), 58, 156, 34, Muted);
        string[] lines = Korean ? ["출시 범위 확정", "QA 일정 공유", "다음 회의 금요일"] : ["Scope agreed", "Share QA dates", "Next: Friday"];
        for (int i = 0; i < lines.Length; i++)
        {
            dc.DrawEllipse(Rgb(0xE0, 0x9F, 0x1F), null, new Point(76, 268 + i * 92), 11, 11);
            Fixtures.Text(dc, lines[i], 108, 240 + i * 92, 48, Ink);
        }
    });

    private BitmapSource Dashboard() => Draw(dc =>
    {
        dc.DrawRectangle(Rgb(0xF4, 0xF6, 0xFB), null, new Rect(0, 0, ImageWidth, ImageHeight));
        dc.DrawRectangle(Rgb(0x1A, 0x1E, 0x2E), null, new Rect(0, 0, ImageWidth, 96));
        Fixtures.Text(dc, L("월간 매출", "Monthly sales"), 48, 16, 52, Brushes.White, bold: true);
        Fixtures.Text(dc, L("1억 2,480만", "$93.6K"), 48, 124, 72, Ink, bold: true);
        Fixtures.Text(dc, "+12.4%", 52, 222, 40, Rgb(0x16, 0xA0, 0x6E), bold: true);
        double[] values = [0.42, 0.55, 0.5, 0.63, 0.7, 0.66, 0.82, 0.94];
        for (int i = 0; i < values.Length; i++)
        {
            double h = 300 * values[i];
            dc.DrawRoundedRectangle(i == values.Length - 1 ? Rgb(0x7C, 0x6C, 0xFF) : Rgb(0xC4, 0xBE, 0xFF), null,
                new Rect(420 + i * 64, 560 - h, 44, h), 6, 6);
        }
    });

    private BitmapSource Checklist() => Draw(dc =>
    {
        Paper(dc, Accent);
        Fixtures.Text(dc, L("릴리스 체크리스트", "Release checklist"), 56, 64, 60, Ink, bold: true);
        string[] items = Korean ? ["요구 사항 확인", "변경 사항 검토", "배포 일정 공유"] : ["Requirements", "Review changes", "Share the date"];
        for (int i = 0; i < items.Length; i++)
        {
            double y = 196 + i * 112;
            var box = new Rect(60, y + 6, 52, 52);
            dc.DrawRoundedRectangle(i < 2 ? Accent : Brushes.White, new Pen(i < 2 ? Accent : Muted, 4), box, 10, 10);
            if (i < 2) dc.DrawGeometry(null, new Pen(Brushes.White, 7) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                Geometry.Parse($"M72,{y + 32} L84,{y + 45} L101,{y + 20}"));
            Fixtures.Text(dc, items[i], 140, y, 50, Ink);
        }
    });

    private BitmapSource Schedule() => Draw(dc =>
    {
        Paper(dc, Rgb(0x16, 0xA0, 0x6E));
        Fixtures.Text(dc, L("배포 일정", "Release schedule"), 56, 60, 60, Ink, bold: true);
        string[] days = Korean ? ["월", "화", "수", "목", "금"] : ["Mon", "Tue", "Wed", "Thu", "Fri"];
        for (int i = 0; i < days.Length; i++)
        {
            var cell = new Rect(56 + i * 172, 200, 156, 330);
            dc.DrawRoundedRectangle(Brushes.White, new Pen(Rgb(0xDD, 0xE2, 0xEA), 3), cell, 14, 14);
            Fixtures.Text(dc, days[i], cell.X + 22, cell.Y + 18, 40, Muted, bold: true);
            if (i is 1 or 3)
                dc.DrawRoundedRectangle(i == 3 ? Rgb(0x16, 0xA0, 0x6E) : Rgb(0x9D, 0xB9, 0xF2), null,
                    new Rect(cell.X + 16, cell.Y + 110, cell.Width - 32, 120), 10, 10);
        }
    });

    private BitmapSource BuildLog() => Draw(dc =>
    {
        dc.DrawRectangle(Rgb(0x0C, 0x0C, 0x0C), null, new Rect(0, 0, ImageWidth, ImageHeight));
        dc.DrawRectangle(Rgb(0x2B, 0x2B, 0x2B), null, new Rect(0, 0, ImageWidth, 64));
        Fixtures.Text(dc, "Windows Terminal", 32, 12, 34, Rgb(0xCC, 0xCC, 0xCC));
        Fixtures.Text(dc, "> " + Fixtures.TerminalCommand, 40, 110, 50, Rgb(0xCC, 0xCC, 0xCC), mono: true);
        Fixtures.Text(dc, "ERROR  TS2304", 40, 220, 58, Rgb(0xF1, 0x4C, 0x4C), bold: true, mono: true);
        Fixtures.Text(dc, "src/app.ts:42", 40, 320, 46, Rgb(0xE5, 0xC0, 0x7B), mono: true);
        Fixtures.Text(dc, L("빌드 실패", "Build failed"), 40, 430, 54, Rgb(0xF1, 0x4C, 0x4C), bold: true);
    });

    private BitmapSource Api() => Draw(dc =>
    {
        dc.DrawRectangle(Rgb(0x1E, 0x1E, 0x1E), null, new Rect(0, 0, ImageWidth, ImageHeight));
        Fixtures.Text(dc, "GET /api/orders/42", 40, 40, 50, Rgb(0x4E, 0xC9, 0xB0), bold: true, mono: true);
        (string Text, Color Color)[] lines =
        [
            ("{", Color.FromRgb(0xD4, 0xD4, 0xD4)),
            ("  \"id\": 42,", Color.FromRgb(0x9C, 0xDC, 0xFE)),
            ("  \"status\": \"paid\",", Color.FromRgb(0xCE, 0x91, 0x78)),
            ("  \"total\": 128000", Color.FromRgb(0xB5, 0xCE, 0xA8)),
            ("}", Color.FromRgb(0xD4, 0xD4, 0xD4)),
        ];
        for (int i = 0; i < lines.Length; i++)
            Fixtures.Text(dc, lines[i].Text, 48, 150 + i * 84, 48, new SolidColorBrush(lines[i].Color), mono: true);
    });

    private BitmapSource SignIn() => Draw(dc =>
    {
        dc.DrawRectangle(Rgb(0xEE, 0xF1, 0xF8), null, new Rect(0, 0, ImageWidth, ImageHeight));
        var phone = new Rect(300, 30, 360, 540);
        dc.DrawRoundedRectangle(Brushes.White, new Pen(Rgb(0xC9, 0xCF, 0xDB), 4), phone, 36, 36);
        dc.DrawEllipse(Rgb(0x7C, 0x6C, 0xFF), null, new Point(480, 150), 54, 54);
        Fixtures.Text(dc, L("로그인", "Sign in"), 400, 226, 52, Ink, bold: true);
        dc.DrawRoundedRectangle(Rgb(0xF1, 0xF3, 0xF8), new Pen(Rgb(0xC9, 0xCF, 0xDB), 3), new Rect(340, 320, 280, 58), 12, 12);
        dc.DrawRoundedRectangle(Rgb(0xF1, 0xF3, 0xF8), new Pen(Rgb(0xC9, 0xCF, 0xDB), 3), new Rect(340, 396, 280, 58), 12, 12);
        dc.DrawRoundedRectangle(Rgb(0x7C, 0x6C, 0xFF), null, new Rect(340, 482, 280, 58), 12, 12);
        Fixtures.Text(dc, L("시작", "Go"), Korean ? 448 : 456, 488, 36, Brushes.White, bold: true);
        Fixtures.Text(dc, L("시안 2", "Draft 2"), 48, 48, 48, Muted, bold: true);
    });

    private BitmapSource Palette() => Draw(dc =>
    {
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, ImageWidth, ImageHeight));
        Fixtures.Text(dc, L("색상 팔레트", "Color palette"), 48, 40, 60, Ink, bold: true);
        Color[] colors = [Color.FromRgb(0x7C, 0x6C, 0xFF), Color.FromRgb(0x2F, 0x6F, 0xEB), Color.FromRgb(0x16, 0xA0, 0x6E),
            Color.FromRgb(0xF2, 0xC9, 0x4C), Color.FromRgb(0xEF, 0x44, 0x44)];
        for (int i = 0; i < colors.Length; i++)
            dc.DrawRoundedRectangle(new SolidColorBrush(colors[i]), null, new Rect(48 + i * 176, 170, 152, 360), 20, 20);
    });

    public void Dispose()
    {
        try { Window.CloseForExit(); }
        catch (InvalidOperationException) { }
        _presenter.Dispose();
        _logs.Dispose();
    }

    /// <summary>
    /// Stands in for the Recycle Bin: the recovery service's trash moves a capture folder into a
    /// folder inside the fixture and back. No Shell call is made.
    /// </summary>
    private sealed class FixtureTrash(string root) : IRecoverableTrash
    {
        private readonly Dictionary<string, RecycledPathReceipt> _receipts = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>A moved directory keeps its creation time, which tells this folder from a new one at the same path.</summary>
        public string GetIdentity(string path) =>
            Directory.GetCreationTimeUtc(path).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public RecycledPathReceipt RecycleDirectory(string path)
        {
            Directory.CreateDirectory(root);
            string target = Path.Combine(root, Guid.NewGuid().ToString("N"));
            string identity = GetIdentity(path);
            Directory.Move(path, target);
            var receipt = new RecycledPathReceipt(path, target, identity, "tutorial-fixture", true);
            _receipts[path] = receipt;
            return receipt;
        }

        public void RestoreDirectory(RecycledPathReceipt receipt)
        {
            if (Directory.Exists(receipt.OriginalPath) || !Directory.Exists(receipt.RecycledPath))
                throw new IOException("The fixture capture cannot be restored.");
            Directory.Move(receipt.RecycledPath, receipt.OriginalPath);
        }

        public RecycledPathReceipt? FindRecycledDirectory(string originalPath, string identity) =>
            _receipts.TryGetValue(originalPath, out RecycledPathReceipt? receipt) && receipt.Identity == identity
                && Directory.Exists(receipt.RecycledPath) ? receipt : null;
    }
}
