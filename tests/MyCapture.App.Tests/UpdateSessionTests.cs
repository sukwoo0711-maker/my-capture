using MyCapture.App.Updates;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class UpdateSessionTests
{
    [Fact]
    public async Task CancellingDoesNotReleaseSingleFlightUntilDownloadSettles()
    {
        using var fixture = new UpdateInstallerTests.PackageFixture();
        var service = new DelayedService();
        using var session = new UpdateSession(service);
        var progress = new Progress<UpdateProgress>();
        var first = session.DownloadAsync(new UpdateVersion(1, 7, 0), fixture.Directory, progress);
        Assert.True(session.IsBusy);
        session.Cancel();
        Assert.True(service.Token.IsCancellationRequested);
        Assert.Null(await session.DownloadAsync(new UpdateVersion(1, 7, 0), fixture.Directory, progress));
        // Even a provider that completes successfully after cancellation must lose its result.
        service.Completion.SetResult(StagedUpdateResult.Success(fixture.Package));
        var result = await first;
        Assert.Equal(UpdateErrorKind.Cancelled, result!.ErrorKind);
        Assert.Null(result.VerifiedPackage);
        Assert.False(session.IsBusy);
        Assert.Equal(1, service.Calls);
    }

    [Fact]
    public async Task ClosingDuringDownloadDisposesLateSuccessInsteadOfOfferingInstallation()
    {
        using var fixture = new UpdateInstallerTests.PackageFixture();
        var service = new DelayedService();
        using var session = new UpdateSession(service);
        var pending = session.DownloadAsync(new UpdateVersion(1, 7, 0), fixture.Directory, new Progress<UpdateProgress>());
        session.Dispose();
        service.Completion.SetResult(StagedUpdateResult.Success(fixture.Package));
        var result = await pending;
        Assert.Equal(UpdateErrorKind.Cancelled, result!.ErrorKind);
        Assert.Null(result.VerifiedPackage);
        Assert.False(System.IO.File.Exists(fixture.Package.InstallerPath));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.DownloadAsync(new UpdateVersion(1, 7, 0), fixture.Directory, new Progress<UpdateProgress>()));
    }
    private sealed class DelayedService : IUpdateService
    {
        internal TaskCompletionSource<StagedUpdateResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Token { get; private set; }
        internal int Calls { get; private set; }
        public Task<StagedUpdateResult> CheckAndStageAsync(UpdateVersion version, string root, IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
        { Calls++; Token = cancellationToken; return Completion.Task; }
        public Task<StagedUpdateResult> CheckAndStageAsync(Version v, string r, IProgress<UpdateProgress>? p, CancellationToken c) => throw new NotSupportedException();
        public Task<UpdateCheckResult> CheckForUpdateAsync(UpdateVersion v, CancellationToken c) => throw new NotSupportedException();
        public Task<UpdateCheckResult> CheckForUpdateAsync(Version v, CancellationToken c) => throw new NotSupportedException();
        public Task<StagedUpdateResult> DownloadAndStageAsync(UpdatePackageInfo p, string r, IProgress<UpdateProgress>? progress, CancellationToken c) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
