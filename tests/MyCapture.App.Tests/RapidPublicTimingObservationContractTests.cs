using System.IO;
using System.Text.Json;
using Xunit;
using Evidence = MyCapture.App.Tests.OcrWholeSpinExperimentEvidence;
using Observation = MyCapture.App.Tests.OcrWholeSpinExperimentTests.StageObservation;

namespace MyCapture.App.Tests;

public sealed class RapidPublicTimingObservationContractTests
{
    [Fact]
    public void CaptureCopiesTimingValuesAndLeavesExactQualityUnchanged()
    {
        var raw = Result();
        string quality = JsonSerializer.Serialize(Evidence.Raw(raw), OcrCommercialCorpusData.Json);
        Evidence.RapidTimingSnapshot timing = Evidence.CaptureRapidTimings(raw);
        timing.RequireValid();
        Assert.Equal(quality, JsonSerializer.Serialize(Evidence.Raw(raw), OcrCommercialCorpusData.Json));
        raw.TextBlocks[0] = Block(4, 5);
        Assert.Equal(quality, JsonSerializer.Serialize(Evidence.Raw(raw), OcrCommercialCorpusData.Json));
        Assert.Equal(1.25f, timing.ReturnedBlocks[0].AngleTimeMs);
        Assert.Equal(8.5f, timing.ReturnedBlocks[0].CrnnTimeMs);
        Assert.Equal(9.75f, timing.ReturnedBlocks[0].BlockTimeMs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void V1AndV2ObservationsOmitUnusedRapidTimings(bool worker)
    {
        var value = new Observation { NeuralStartedTimestamp = 100, NeuralFinishedTimestamp = 200 };
        if (worker)
            for (int index = 0; index < 4; index++)
                value.RecordWorkerStage((MyCapture.Ocr.NeuralOcrEngine.WholeObservationStage)index, 110 + index);
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(value, OcrCommercialCorpusData.Json));
        Assert.False(json.RootElement.TryGetProperty("rapidTimes", out _));
        Assert.Equal(worker, json.RootElement.TryGetProperty("beforeDetectTimestamp", out _));
    }

    [Fact]
    public void V3WritesMillisecondsAndAllowsOverlappingRecognitionTimes()
    {
        var timing = Evidence.CaptureRapidTimings(Result());
        // Two returned crop walls overlap: their sum exceeds DetectTime and is still valid.
        Assert.True(timing.ReturnedBlocks.Sum(block => block.CrnnTimeMs) > timing.DetectTimeMs);
        var value = new Observation { RapidTimes = timing };
        value.RequireRapidTimings();
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(value, OcrCommercialCorpusData.Json));
        JsonElement saved = json.RootElement.GetProperty("rapidTimes");
        Assert.Equal("ms", saved.GetProperty("units").GetString());
        Assert.Equal("rapidocr-4.2.0-returned-wall-v1", saved.GetProperty("schema").GetString());
        Assert.Equal(2, saved.GetProperty("returnedBlockCount").GetInt32());
        Assert.Equal(8.5f, saved.GetProperty("returnedBlocks")[0].GetProperty("crnnTimeMs").GetSingle());
    }

    [Fact]
    public void ZeroReturnedBlocksRetainsDetectorTimeWithoutInventingCrops()
    {
        var raw = new RapidOcrNet.OcrResult { TextBlocks = [], StrRes = "", DbNetTime = 2, DetectTime = 3 };
        Evidence.RapidTimingSnapshot timing = Evidence.CaptureRapidTimings(raw);
        timing.RequireValid();
        Assert.Empty(timing.ReturnedBlocks);
        Assert.Equal(0, timing.ReturnedBlockCount);
        Assert.Equal(2f, timing.DbNetTimeMs);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void MissingNonfiniteNegativeReversedCountIndexAndSumAreRejected(int defect)
    {
        Evidence.RapidTimingSnapshot value = Evidence.CaptureRapidTimings(Result());
        if (defect == 0)
        {
            Assert.Throws<InvalidDataException>(() => new Observation().RequireRapidTimings());
            return;
        }
        value = defect switch
        {
            1 => value with { DbNetTimeMs = float.NaN },
            2 => value with { DetectTimeMs = float.PositiveInfinity },
            3 => value with { DbNetTimeMs = -1 },
            4 => value with { DetectTimeMs = 1 },
            5 => value with { ReturnedBlockCount = 3 },
            6 => value with { ReturnedBlocks = [value.ReturnedBlocks[0] with { Index = 1 }, value.ReturnedBlocks[1]] },
            7 => value with { ReturnedBlocks = [value.ReturnedBlocks[0] with { BlockTimeMs = 1 }, value.ReturnedBlocks[1]] },
            _ => throw new ArgumentOutOfRangeException(nameof(defect)),
        };
        Assert.Throws<InvalidDataException>(value.RequireValid);
    }

    private static RapidOcrNet.OcrResult Result() => new()
    {
        DbNetTime = 2, DetectTime = 10, StrRes = "test\ntest\n",
        TextBlocks = [Block(1.25f, 8.5f), Block(0.5f, 8.25f)],
    };
    private static RapidOcrNet.TextBlock Block(float angle, float crnn) => new()
    {
        Text = "test", BoxPoints = [], Chars = ["t", "e", "s", "t"], CharScores = [1, 1, 1, 1],
        AngleTime = angle, CrnnTime = crnn, BlockTime = angle + crnn,
    };
}
