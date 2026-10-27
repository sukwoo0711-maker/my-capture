using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Serialization;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>
/// The stamp's remembered placement and opacity survive every path settings take: the file, a
/// clone, the settings window's draft, export and import. The saved edit format does not change:
/// a stamp is an ordinary image annotation whose own opacity is already part of the layer.
/// </summary>
public sealed class StampSettingsTests
{
    private static StampSettings Remembered() => new()
    {
        Anchor = StampAnchor.TopLeft,
        MarginX = 40,
        MarginY = 12,
        WidthFraction = 0.25,
        OpacityPercent = 50,
    };

    [Fact]
    public void ANewInstallStampsBottomRightAtFifteenPercentFullyOpaque()
    {
        var stamp = new AppSettings().Annotation.Stamp;

        Assert.Equal(StampAnchor.BottomRight, stamp.Anchor);
        Assert.Equal(16, stamp.MarginX);
        Assert.Equal(16, stamp.MarginY);
        Assert.Equal(0.15, stamp.WidthFraction);
        Assert.Equal(100, stamp.OpacityPercent);
        Assert.Equal([100, 70, 50, 30], StampSettings.OpacityChoices);
    }

    [Fact]
    public void ThePlacementRoundTripsThroughTheSettingsFile()
    {
        using var workspace = new TempWorkspace();
        var store = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance);
        var settings = new AppSettings();
        settings.Annotation.Stamp = Remembered();

        store.Save(settings);
        StampSettings loaded = store.Load().Annotation.Stamp;

        Assert.Equal(StampAnchor.TopLeft, loaded.Anchor);
        Assert.Equal(40, loaded.MarginX);
        Assert.Equal(12, loaded.MarginY);
        Assert.Equal(0.25, loaded.WidthFraction);
        Assert.Equal(50, loaded.OpacityPercent);
        string json = File.ReadAllText(workspace.Paths.SettingsFile);
        Assert.Contains("\"topLeft\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ASettingsFileWithoutAStampSectionKeepsTheDefaults()
    {
        using var workspace = new TempWorkspace();
        workspace.Paths.EnsureCreated();
        File.WriteAllText(workspace.Paths.SettingsFile, "{\"annotation\":{\"strokeThickness\":5}}");
        var store = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance);

        AppSettings loaded = store.Load();

        Assert.Equal(5, loaded.Annotation.StrokeThickness);
        Assert.Equal(StampAnchor.BottomRight, loaded.Annotation.Stamp.Anchor);
        Assert.Equal(100, loaded.Annotation.Stamp.OpacityPercent);
    }

    [Fact]
    public void AnExplicitNullStampSectionReadsAsTheDefaults()
    {
        var parsed = JsonSerializer.Deserialize<AppSettings>("{\"annotation\":{\"stamp\":null}}", JsonDefaults.Readable)!;

        Assert.NotNull(parsed.Annotation.Stamp);
        Assert.Equal(StampAnchor.BottomRight, parsed.Annotation.Stamp.Anchor);
    }

    [Fact]
    public void HandEditedValuesAreRepairedWithoutAWarningWhenSettingsLoad()
    {
        using var workspace = new TempWorkspace();
        workspace.Paths.EnsureCreated();
        File.WriteAllText(
            workspace.Paths.SettingsFile,
            "{\"annotation\":{\"stamp\":{\"anchor\":42,\"marginX\":-5,\"marginY\":999999,\"widthFraction\":7,\"opacityPercent\":3}}}");
        var store = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance);

        StampSettings stamp = store.Load().Annotation.Stamp;

        Assert.Equal(StampAnchor.BottomRight, stamp.Anchor);
        Assert.Equal(0, stamp.MarginX);
        Assert.Equal(StampLayout.MaxMargin, stamp.MarginY);
        Assert.Equal(StampLayout.MaxWidthFraction, stamp.WidthFraction);
        Assert.Equal(StampSettings.MinOpacityPercent, stamp.OpacityPercent);
    }

    [Fact]
    public void NormalizeRepairsNonFiniteNumbersInCode()
    {
        var stamp = new StampSettings
        {
            MarginX = double.NaN,
            MarginY = double.PositiveInfinity,
            WidthFraction = double.NegativeInfinity,
            OpacityPercent = 500,
        };

        stamp.Normalize();

        Assert.Equal(StampLayout.DefaultMargin, stamp.MarginX);
        Assert.Equal(StampLayout.DefaultMargin, stamp.MarginY);
        Assert.Equal(StampLayout.DefaultWidthFraction, stamp.WidthFraction);
        Assert.Equal(100, stamp.OpacityPercent);
    }

    [Fact]
    public void ACloneIsIndependent()
    {
        var source = new AppSettings();
        source.Annotation.Stamp = Remembered();

        AppSettings clone = source.DeepClone();
        clone.Annotation.Stamp.Anchor = StampAnchor.Center;
        clone.Annotation.Stamp.OpacityPercent = 30;

        Assert.Equal(StampAnchor.TopLeft, source.Annotation.Stamp.Anchor);
        Assert.Equal(50, source.Annotation.Stamp.OpacityPercent);
        Assert.Equal(40, clone.Annotation.Stamp.MarginX);
        Assert.NotSame(source.Annotation.Stamp, clone.Annotation.Stamp);
    }

    [Fact]
    public void TheSettingsWindowsDraftKeepsTheStampAndDoesNotCallItAChange()
    {
        var source = new AppSettings();
        source.Annotation.Stamp = Remembered();

        var draft = new SettingsDraft(source);
        AppSettings mapped = draft.ToAppSettings();

        Assert.False(draft.HasUnappliedChanges);
        Assert.Equal(StampAnchor.TopLeft, mapped.Annotation.Stamp.Anchor);
        Assert.Equal(40, mapped.Annotation.Stamp.MarginX);
        Assert.Equal(12, mapped.Annotation.Stamp.MarginY);
        Assert.Equal(0.25, mapped.Annotation.Stamp.WidthFraction);
        Assert.Equal(50, mapped.Annotation.Stamp.OpacityPercent);
        Assert.NotSame(source.Annotation.Stamp, mapped.Annotation.Stamp);
    }

    [Fact]
    public void ExportAndImportCarryThePlacement()
    {
        using var workspace = new TempWorkspace();
        var store = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance);
        var settings = new AppSettings();
        settings.Annotation.Stamp = Remembered();
        string export = Path.Combine(workspace.Root, "export.json");

        store.ExportTo(settings, export);
        StampSettings imported = store.ImportFrom(export).Annotation.Stamp;

        Assert.Equal(StampAnchor.TopLeft, imported.Anchor);
        Assert.Equal(40, imported.MarginX);
        Assert.Equal(0.25, imported.WidthFraction);
        Assert.Equal(50, imported.OpacityPercent);
    }

    [Fact]
    public void ARememberedPlacementBecomesTheNextStampsRectangleOnAnyCaptureSize()
    {
        StampSettings stamp = Remembered();

        RectD small = StampLayout.Place(stamp.Anchor, stamp.MarginX, stamp.MarginY, stamp.WidthFraction, 800, 600, 2000, 1000);
        RectD large = StampLayout.Place(stamp.Anchor, stamp.MarginX, stamp.MarginY, stamp.WidthFraction, 1600, 1200, 2000, 1000);

        Assert.Equal(new RectD(40, 12, 200, 100), small);
        Assert.Equal(new RectD(40, 12, 400, 200), large);
    }

    [Fact]
    public void AStampedImageAnnotationKeepsItsOpacityInTheSavedLayerWithoutANewField()
    {
        var document = AnnotationDocument.CreateFor(640, 360);
        document.Items.Add(new ImageAnnotation
        {
            AssetFileName = "asset-01.png",
            SourceWidth = 400,
            SourceHeight = 200,
            Rect = new RectD(480, 260, 96, 48),
            Opacity = 0.5,
        });

        string json = document.ToJson();
        AnnotationDocument? loaded = AnnotationDocument.TryFromJson(json);

        var image = Assert.IsType<ImageAnnotation>(Assert.Single(loaded!.Items));
        Assert.Equal(0.5, image.Opacity);
        Assert.Equal(new RectD(480, 260, 96, 48), image.Rect);
        Assert.DoesNotContain("stamp", json, StringComparison.OrdinalIgnoreCase);
    }
}
