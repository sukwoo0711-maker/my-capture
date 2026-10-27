using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows.Interop;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.App.Updates;
using MyCapture.Core.Platform;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Ocr;
using MyCapture.Platform.Display;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class X86CompatibilityTests
{
    [Fact]
    public void StartupGateUsesTheProcessArchitecture()
    {
        bool blocked = HostRequirementGate.BlockUnsupportedHost(new Version(10, 0, 19045), _ => { }, _ => { });
        Assert.False(blocked);
        Assert.Contains("Windows 10", WindowsSupportPolicy.DescribeRequirement());
        Assert.Contains(AppArchitecture.Name, WindowsSupportPolicy.DescribeRequirement());
    }

    [Theory]
    [InlineData("x86")]
    [InlineData("x64")]
    public void InstallerNamesAreArchitectureSpecific(string architecture) =>
        Assert.Equal($"MyCapture-3.0.1-win-{architecture}-setup.exe",
            UpdatePaths.InstallerName(new UpdateVersion(3, 0, 1), architecture));

    [Fact]
    public void UpdateDefaultsFollowProcessArchitectureAndRejectUnknownArchitectures()
    {
        Assert.Contains(AppArchitecture.RuntimeIdentifier, UpdatePaths.InstallerName(new UpdateVersion(3, 0, 1)));
        Assert.Equal(AppArchitecture.IsX86 ? "SHA256SUMS-win-x86.txt" : "SHA256SUMS.txt", UpdatePaths.ChecksumName);
        Assert.Throws<System.IO.IOException>(() => UpdatePaths.InstallerName(new UpdateVersion(3, 0, 1), "../x86"));
    }

    [Theory]
    [InlineData(OcrQuality.Fast)]
    [InlineData(OcrQuality.Balanced)]
    [InlineData(OcrQuality.Accurate)]
    public void OcrRequestsKeepWindowsPreprocessingWithoutNeuralFeaturesOnX86(OcrQuality quality)
    {
        var settings = new OcrSettings { Quality = quality, UpscaleFactor = OcrQualityProfile.UpscaleFactor(quality) };
        OcrRequest request = OcrRequestFactory.FromFile("unused-fixture.png", settings);
        Assert.Equal(!AppArchitecture.IsX86 && OcrQualityProfile.UseNeuralModel(quality), request.UseNeuralModel);
        Assert.Equal(!AppArchitecture.IsX86 && OcrQualityProfile.UseSuperResolution(quality), request.UseSuperResolution);
        Assert.Equal(OcrQualityProfile.LocalCorrection(quality), request.LocalCorrection);
        Assert.Equal(OcrQualityProfile.EnhanceContrast(quality), request.EnhanceContrast);
    }

    [Fact]
    public void NativePropVariantMatchesTheProcessAbi() =>
        Assert.Equal(IntPtr.Size == 4 ? 16 : 24, MediaFoundationVideoFrameReader.NativePropVariantSize);

    [Fact]
    public void WindowStyleCallsResolveAndRoundTripOnThisArchitecture()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var window = new HwndSource(new HwndSourceParameters("MyCapture architecture test")
                { Width = 1, Height = 1, WindowStyle = 0 });
                WindowStyleFacade.ExcludeFromAltTab(window.Handle);
                Assert.True(WindowStyleFacade.SetClickThrough(window.Handle, true));
                Assert.True(WindowStyleFacade.IsClickThrough(window.Handle));
                Assert.False(WindowStyleFacade.SetClickThrough(window.Handle, false));
                Assert.False(WindowStyleFacade.IsClickThrough(window.Handle));
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Native window test did not complete.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public async Task UpdateCheckSelectsOnlyTheRunningArchitecture()
    {
        const string root = "https://github.com/sukwoo0711-maker/my-capture/releases/download/v3.0.1/";
        string release = """
            {"tag_name":"v3.0.1","draft":false,"prerelease":false,"assets":[
              {"name":"MyCapture-3.0.1-win-x64-setup.exe","size":100,"browser_download_url":"ROOTMyCapture-3.0.1-win-x64-setup.exe"},
              {"name":"MyCapture-3.0.1-win-x86-setup.exe","size":100,"browser_download_url":"ROOTMyCapture-3.0.1-win-x86-setup.exe"},
              {"name":"SHA256SUMS.txt","size":100,"browser_download_url":"ROOTSHA256SUMS.txt"},
              {"name":"SHA256SUMS-win-x86.txt","size":100,"browser_download_url":"ROOTSHA256SUMS-win-x86.txt"}]}
            """.Replace("ROOT", root, StringComparison.Ordinal);
        using var client = new HttpClient(new ReleaseHandler(release));
        using var service = new GitHubUpdateService(client);
        var result = await service.CheckForUpdateAsync(new UpdateVersion(3, 0, 0));
        Assert.True(result.IsUpdateAvailable, result.ErrorMessage);
        Assert.NotNull(result.PackageInfo);
        Assert.Equal(UpdatePaths.InstallerName(new UpdateVersion(3, 0, 1)), result.PackageInfo.SetupAssetName);
        Assert.EndsWith(UpdatePaths.ChecksumName, result.PackageInfo.ChecksumDownloadUrl.AbsolutePath);
    }

    [Fact]
    public async Task X86DoesNotDownloadOrInitializeNeuralModels()
    {
        if (!AppArchitecture.IsX86) return;
        string root = System.IO.Path.Combine(AppContext.BaseDirectory, "unused-ocr-" + Guid.NewGuid().ToString("N"));
        using var http = new HttpClient(new RejectNetworkHandler());
        var store = new OcrModelStore(AppPaths.CreateForRoot(root), http, NullLogger.Instance);
        Assert.False(await store.EnsureAsync(CancellationToken.None));
        using var neural = new NeuralOcrEngine(store, NullLogger<NeuralOcrEngine>.Instance);
        await store.EnsureFileAsync(OcrModelCatalog.Required[0], CancellationToken.None);
        await store.PreVerifyInstalledModelsAsync();
        Assert.Throws<PlatformNotSupportedException>(() => OnnxRuntimeInitialization.Shared.EnsureInitialized());
        Assert.False(neural.CanRun);
        Assert.False(neural.TryInitialize());
        var translation = new MyCapture.Ocr.Translation.TranslationModelStore(AppPaths.CreateForRoot(root), http, NullLogger.Instance);
        Assert.False(translation.IsInstalled);
        Assert.False(await translation.IsInstalledAsync(CancellationToken.None));
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => translation.DownloadAsync(null, CancellationToken.None));
        Assert.False(System.IO.Directory.Exists(root));
    }

    private sealed class RejectNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("x86 OCR must not download neural models.");
    }

    private sealed class ReleaseHandler(string release) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(release, Encoding.UTF8, "application/json") });
    }
}
