using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Storage;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class CaptureOcrServiceConstructionTests
{
    [Fact]
    public void FactoryRegistration_ResolvesIOcrService()
    {
        using var workspace = new TempWorkspaceAdapter();
        var services = new ServiceCollection();
        services.AddSingleton(workspace.Paths);
        services.AddSingleton<ILogger<OcrModelStore>>(NullLogger<OcrModelStore>.Instance);
        services.AddSingleton<ILogger<NeuralOcrEngine>>(NullLogger<NeuralOcrEngine>.Instance);
        services.AddSingleton<ILogger<WindowsOcrService>>(NullLogger<WindowsOcrService>.Instance);
        services.AddSingleton<ILogger<CaptureOcrService>>(NullLogger<CaptureOcrService>.Instance);
        services.AddSingleton(sp => new OcrModelStore(
            sp.GetRequiredService<AppPaths>(),
            sp.GetRequiredService<ILogger<OcrModelStore>>()));
        services.AddSingleton(sp => new NeuralOcrEngine(
            sp.GetRequiredService<OcrModelStore>(),
            sp.GetRequiredService<ILogger<NeuralOcrEngine>>()));
        services.AddSingleton<WindowsOcrService>();
        services.AddSingleton<IOcrService>(sp => new CaptureOcrService(
            sp.GetRequiredService<WindowsOcrService>(),
            sp.GetRequiredService<NeuralOcrEngine>(),
            sp.GetRequiredService<ILogger<CaptureOcrService>>()));

        using ServiceProvider provider = services.BuildServiceProvider();
        IOcrService ocr = provider.GetRequiredService<IOcrService>();
        Assert.IsType<CaptureOcrService>(ocr);
    }

    private sealed class TempWorkspaceAdapter : IDisposable
    {
        private readonly string _root = OwnedTestDirectory.Create("ocr-di-");
        public AppPaths Paths { get; }
        public TempWorkspaceAdapter() => Paths = AppPaths.CreateForRoot(_root);
        public void Dispose() => OwnedTestDirectory.Delete(_root);
    }
}
