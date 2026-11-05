using System.IO;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using MyCapture.Ocr;
using Xunit;
using Stage = MyCapture.Ocr.NeuralOcrEngine.WholeObservationStage;
using Observation = MyCapture.App.Tests.OcrWholeSpinExperimentTests.StageObservation;

namespace MyCapture.App.Tests;

public sealed class NeuralWorkerObservationContractTests
{
    [Fact]
    public void ExistingConstructorSignatureAndOptionalDefaultsRemainAvailable()
    {
        Type[] originalTypes = [typeof(OcrModelStore), typeof(ILogger<NeuralOcrEngine>),
            typeof(OnnxRuntimeInitialization), typeof(TimeSpan?), typeof(bool),
            typeof(Action<SessionOptions, string, string, string, string>),
            typeof(Action<RapidOcrNet.OcrResult>), typeof(IProgress<(int Completed, int Total)>)];
        ConstructorInfo? constructor = typeof(NeuralOcrEngine).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, binder: null, originalTypes, modifiers: null);
        Assert.NotNull(constructor);
        ParameterInfo[] parameters = constructor!.GetParameters();
        Assert.All(parameters.Skip(2), parameter => Assert.True(parameter.IsOptional));
        Assert.False(Assert.IsType<bool>(parameters[4].DefaultValue));
        Assert.Null(parameters[7].DefaultValue);
    }

    [Fact]
    public void UnusedWorkerObservationKeepsLegacyJsonShape()
    {
        var observation = new Observation { NeuralStartedTimestamp = 100, NeuralFinishedTimestamp = 200 };
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(observation, OcrCommercialCorpusData.Json));
        Assert.Equal(100, json.RootElement.GetProperty("neuralStartedTimestamp").GetInt64());
        foreach (string property in new[] { "beforeTaskRunTimestamp", "workerEnteredTimestamp", "beforeDetectTimestamp",
            "afterDetectTimestamp", "workerEventCount", "workerEventOrderErrors" })
            Assert.False(json.RootElement.TryGetProperty(property, out _));
    }

    [Fact]
    public void OrderedWorkerEventsAllowSameClockTickAtAdjacentBoundaries()
    {
        var observation = Create([100, 100, 150, 150]);
        observation.RequireWorkerStages();
        Assert.Equal(4, observation.WorkerEventCount);
        Assert.Equal(0, observation.WorkerEventOrderErrors);
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
    public void MissingDuplicateReversedUnknownOrOutsideBoundsIsRejected(int defect)
    {
        var observation = new Observation { NeuralStartedTimestamp = 100, NeuralFinishedTimestamp = 200 };
        if (defect == 0)
        {
            observation.RecordWorkerStage(Stage.BeforeTaskRun, 110);
            observation.RecordWorkerStage(Stage.WorkerEntered, 120);
            observation.RecordWorkerStage(Stage.BeforeDetect, 130);
        }
        else if (defect == 1)
        {
            observation = Create([110, 120, 130, 140]);
            observation.RecordWorkerStage(Stage.AfterDetect, 140);
        }
        else if (defect == 2)
        {
            observation.RecordWorkerStage(Stage.WorkerEntered, 110);
            observation.RecordWorkerStage(Stage.BeforeTaskRun, 110);
            observation.RecordWorkerStage(Stage.BeforeDetect, 130);
            observation.RecordWorkerStage(Stage.AfterDetect, 140);
        }
        else if (defect == 3) observation = Create([0, 120, 130, 140]);
        else if (defect == 4) observation = Create([99, 120, 130, 140]);
        else if (defect == 5) observation = Create([110, 120, 130, 201]);
        else if (defect == 6) observation = Create([110, 130, 120, 140]);
        else
        {
            observation.RecordWorkerStage(Stage.BeforeTaskRun, 110);
            observation.RecordWorkerStage(Stage.WorkerEntered, 120);
            observation.RecordWorkerStage(Stage.BeforeDetect, 130);
            observation.RecordWorkerStage((Stage)99, 140);
        }
        Assert.Throws<InvalidDataException>(() => observation.RequireWorkerStages());
    }

    private static Observation Create(long[] times)
    {
        var observation = new Observation { NeuralStartedTimestamp = 100, NeuralFinishedTimestamp = 200 };
        for (int index = 0; index < times.Length; index++) observation.RecordWorkerStage((Stage)index, times[index]);
        return observation;
    }
}
