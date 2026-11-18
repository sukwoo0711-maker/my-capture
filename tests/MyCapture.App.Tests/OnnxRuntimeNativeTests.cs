using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using MyCapture.Core.Platform;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// OCR and translation both run on the native ONNX Runtime library. The test loads the library the
/// build placed beside the tests for this process architecture and runs a bundled model through it,
/// so a native x64 or ARM64 test run proves its own library rather than the managed wrapper alone.
/// </summary>
public sealed class OnnxRuntimeNativeTests
{
    [Fact]
    public void BundledModelRunsOnTheNativeLibraryOfThisArchitecture()
    {
        // The x86 edition ships no ONNX Runtime; X86CompatibilityTests covers its refusal.
        if (!AppArchitecture.SupportsNeuralOcr) return;

        string model = Path.Combine(AppContext.BaseDirectory, "models", "v5", "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx");
        Assert.True(File.Exists(model), $"The bundled orientation model is missing: {model}");

        OnnxRuntimeInitialization.Shared.EnsureInitialized();
        using var options = new SessionOptions();
        using var session = new InferenceSession(model, options);
        KeyValuePair<string, NodeMetadata> input = Assert.Single(session.InputMetadata);
        Assert.Equal(typeof(float), input.Value.ElementType);
        // Image input in NCHW order; free dimensions take the size the product feeds a text line.
        int[] fallback = [1, 3, 80, 160];
        int[] shape = input.Value.Dimensions.Select((size, index) => size > 0 ? size : fallback[index]).ToArray();
        Assert.Equal(4, shape.Length);

        var pixels = new DenseTensor<float>(shape);
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs =
            session.Run([NamedOnnxValue.CreateFromTensor(input.Key, pixels)]);
        float[] scores = Assert.Single(outputs).AsEnumerable<float>().ToArray();
        Assert.NotEmpty(scores);
        Assert.All(scores, score => Assert.True(float.IsFinite(score), $"Non-finite score {score}."));

        using Process process = Process.GetCurrentProcess();
        ProcessModule native = Assert.Single(process.Modules.Cast<ProcessModule>(),
            module => string.Equals(module.ModuleName, "onnxruntime.dll", StringComparison.OrdinalIgnoreCase));
        string path = Path.GetFullPath(native.FileName);
        // Recent Windows releases carry their own onnxruntime.dll; the product must use the packaged one.
        Assert.StartsWith(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory) + Path.DirectorySeparatorChar,
            path, StringComparison.OrdinalIgnoreCase);
        int expected = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => 0x8664,
            Architecture.Arm64 => 0xAA64,
            Architecture other => throw new InvalidOperationException($"No ONNX Runtime is packaged for {other}."),
        };
        Assert.Equal(expected, ReadMachine(path));
    }

    private static int ReadMachine(string path)
    {
        using FileStream file = File.OpenRead(path);
        using var reader = new BinaryReader(file);
        Assert.Equal(0x5A4D, reader.ReadUInt16());
        file.Position = 0x3C;
        file.Position = reader.ReadInt32();
        Assert.Equal(0x00004550u, reader.ReadUInt32());
        return reader.ReadUInt16();
    }
}
