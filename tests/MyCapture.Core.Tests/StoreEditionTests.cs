using MyCapture.Core.Localization;
using MyCapture.Core.Platform;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class PackagedStartupRegistrationTests
{
    private sealed class FakeStartupTask(PackagedStartupState state) : IPackagedStartupTask
    {
        public PackagedStartupState State { get; set; } = state;
        public PackagedStartupState EnableResult { get; set; } = PackagedStartupState.Enabled;
        public bool Throw { get; set; }
        public int EnableRequests { get; private set; }
        public int DisableCalls { get; private set; }

        public PackagedStartupState GetState() => Throw ? throw new InvalidOperationException("task unavailable") : State;

        public PackagedStartupState RequestEnable()
        {
            EnableRequests++;
            State = EnableResult;
            return State;
        }

        public void Disable()
        {
            DisableCalls++;
            State = PackagedStartupState.Disabled;
        }
    }

    [Theory]
    [InlineData(PackagedStartupState.Disabled, false)]
    [InlineData(PackagedStartupState.DisabledByUser, false)]
    [InlineData(PackagedStartupState.DisabledByPolicy, false)]
    [InlineData(PackagedStartupState.Enabled, true)]
    [InlineData(PackagedStartupState.EnabledByPolicy, true)]
    public void IsEnabled_ReportsWhetherWindowsStartsTheApp(PackagedStartupState state, bool expected) =>
        Assert.Equal(expected, new PackagedStartupRegistration(new FakeStartupTask(state)).IsEnabled());

    [Fact]
    public void Apply_On_RequestsTheTaskAndVerifiesIt()
    {
        var task = new FakeStartupTask(PackagedStartupState.Disabled);
        StartupApplyResult result = new PackagedStartupRegistration(task).Apply(true);
        Assert.True(result.Succeeded);
        Assert.Equal(1, task.EnableRequests);
        Assert.Equal(PackagedStartupState.Enabled, task.State);
    }

    [Fact]
    public void Apply_On_FailsWhenWindowsDoesNotEnableTheTask()
    {
        var task = new FakeStartupTask(PackagedStartupState.Disabled) { EnableResult = PackagedStartupState.Disabled };
        StartupApplyResult result = new PackagedStartupRegistration(task).Apply(true);
        Assert.False(result.Succeeded);
        Assert.Equal(UiText.Get("Text_4D69B33A2FD7"), result.Error);
    }

    [Theory]
    [InlineData(PackagedStartupState.DisabledByUser, "Startup.StoreDisabledByUser")]
    [InlineData(PackagedStartupState.DisabledByPolicy, "Startup.StoreManagedByPolicy")]
    public void Apply_On_NeverOverridesTheUserOrPolicy(PackagedStartupState state, string messageKey)
    {
        var task = new FakeStartupTask(state);
        StartupApplyResult result = new PackagedStartupRegistration(task).Apply(true);
        Assert.False(result.Succeeded);
        Assert.Equal(UiText.Get(messageKey), result.Error);
        Assert.Equal(0, task.EnableRequests);
        Assert.Equal(state, task.State);
    }

    [Fact]
    public void Apply_Off_DisablesAnEnabledTask()
    {
        var task = new FakeStartupTask(PackagedStartupState.Enabled);
        Assert.True(new PackagedStartupRegistration(task).Apply(false).Succeeded);
        Assert.Equal(1, task.DisableCalls);
        Assert.Equal(PackagedStartupState.Disabled, task.State);
    }

    [Theory]
    [InlineData(PackagedStartupState.Disabled)]
    [InlineData(PackagedStartupState.DisabledByUser)]
    [InlineData(PackagedStartupState.DisabledByPolicy)]
    public void Apply_Off_LeavesAnAlreadyOffTaskUntouched(PackagedStartupState state)
    {
        var task = new FakeStartupTask(state);
        Assert.True(new PackagedStartupRegistration(task).Apply(false).Succeeded);
        Assert.Equal(0, task.DisableCalls);
        Assert.Equal(state, task.State);
    }

    [Fact]
    public void Apply_Off_ReportsAPolicyThatKeepsTheTaskOn()
    {
        var task = new FakeStartupTask(PackagedStartupState.EnabledByPolicy);
        StartupApplyResult result = new PackagedStartupRegistration(task).Apply(false);
        Assert.False(result.Succeeded);
        Assert.Equal(UiText.Get("Startup.StoreManagedByPolicy"), result.Error);
        Assert.Equal(0, task.DisableCalls);
    }

    [Fact]
    public void Apply_ReportsAnUnavailableTaskInsteadOfThrowing()
    {
        var task = new FakeStartupTask(PackagedStartupState.Disabled) { Throw = true };
        StartupApplyResult result = new PackagedStartupRegistration(task).Apply(true);
        Assert.False(result.Succeeded);
        Assert.Equal("task unavailable", result.Error);
    }

    [Theory]
    [InlineData(PackagedStartupState.Disabled, true)]
    [InlineData(PackagedStartupState.Disabled, false)]
    [InlineData(PackagedStartupState.DisabledByUser, true)]
    [InlineData(PackagedStartupState.Enabled, false)]
    [InlineData(PackagedStartupState.Enabled, true)]
    public void ReconcileOnStartup_NeverChangesWhatWindowsOwns(PackagedStartupState state, bool setting)
    {
        // Store policy requires consent before changing how Windows starts: a default-on
        // setting must not enable the task, and a choice made in Windows Settings must survive.
        var task = new FakeStartupTask(state);
        Assert.False(new PackagedStartupRegistration(task).ReconcileOnStartup(setting));
        Assert.Equal(0, task.EnableRequests);
        Assert.Equal(0, task.DisableCalls);
        Assert.Equal(state, task.State);
    }
}

public sealed class StorePackagePathTests
{
    [Fact]
    public void PackagedPaths_KeepTheLibraryInRealPackageFolders()
    {
        AppPaths paths = AppPaths.CreateForPackage(
            @"C:\Users\u\AppData\Local\Packages\MyCapture_pfn\LocalState",
            @"C:\Users\u\AppData\Local\Packages\MyCapture_pfn\LocalCache",
            @"C:\Users\u\Pictures");
        Assert.Equal(@"C:\Users\u\AppData\Local\Packages\MyCapture_pfn\LocalState", paths.DataRoot);
        Assert.Equal(@"C:\Users\u\AppData\Local\Packages\MyCapture_pfn\LocalState\settings.json", paths.SettingsFile);
        Assert.Equal(@"C:\Users\u\AppData\Local\Packages\MyCapture_pfn\LocalState\captures", paths.CapturesRoot);
        Assert.Equal(@"C:\Users\u\AppData\Local\Packages\MyCapture_pfn\LocalCache\ocr-models", paths.OcrModelsRoot);
        Assert.Equal(@"C:\Users\u\Pictures\Captures", paths.QuickSaveRoot);
    }

    [Fact]
    public void PackagedPaths_FallBackToLocalStateWithoutPictures()
    {
        AppPaths paths = AppPaths.CreateForPackage(@"C:\Pkg\LocalState", @"C:\Pkg\LocalCache", null);
        Assert.Equal(@"C:\Pkg\LocalState\Captures", paths.QuickSaveRoot);
    }

    [Fact]
    public void UnpackagedTestProcess_KeepsTheStandardLayout()
    {
        Assert.False(AppPackage.IsPackaged);
        Assert.False(AppPackage.TryGetDataFolders(out _, out _, out _));
        Assert.False(AppPackage.IsStartupTaskActivation());
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Assert.Equal(Path.Combine(appData, AppPaths.AppFolderName), AppPaths.CreateDefault().DataRoot);
    }

    [Fact]
    public void MisdetectedPackage_WithoutPackageData_FallsBackInsteadOfThrowing()
    {
        AppPackage.IsPackagedOverride = true;
        try
        {
            Assert.False(AppPackage.TryGetDataFolders(out _, out _, out _));
            Assert.False(AppPackage.IsStartupTaskActivation());
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            Assert.Equal(Path.Combine(appData, AppPaths.AppFolderName), AppPaths.CreateDefault().DataRoot);
        }
        finally
        {
            AppPackage.IsPackagedOverride = null;
        }
    }
}

public sealed class StoreManifestContractTests
{
    private static string RepositoryPath(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !(File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))
            && Directory.Exists(Path.Combine(dir.FullName, "src")) && Directory.Exists(Path.Combine(dir.FullName, "build"))))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static System.Xml.Linq.XDocument Manifest() =>
        System.Xml.Linq.XDocument.Load(RepositoryPath("build/store/AppxManifest.xml"));

    private static System.Xml.Linq.XElement Single(System.Xml.Linq.XDocument document, string localName) =>
        document.Descendants().Single(element => element.Name.LocalName == localName);

    [Fact]
    public void StartupTask_MatchesTheAppAndShipsDisabled()
    {
        System.Xml.Linq.XElement task = Single(Manifest(), "StartupTask");
        Assert.Equal(PackagedStartupRegistration.TaskId, (string?)task.Attribute("TaskId"));
        // Store policy 10.2.8: launch-at-login needs the user's consent, so the task starts off.
        Assert.Equal("false", (string?)task.Attribute("Enabled"));
        Assert.Equal("MyCapture.exe", (string?)task.Parent!.Attribute("Executable"));
    }

    [Fact]
    public void Package_IsFullTrustWithoutUnvirtualizedResources()
    {
        System.Xml.Linq.XDocument manifest = Manifest();
        string[] capabilities = manifest.Descendants()
            .Where(element => element.Name.LocalName == "Capability")
            .Select(element => (string)element.Attribute("Name")!)
            .ToArray();
        // unvirtualizedResources is reserved for games and is not granted to Store apps; the
        // app keeps its data in package folders instead of relying on it. Borderless graphics
        // capture is the only other capability: WGC recordings need it to drop the yellow border.
        Assert.Equal(new[] { "runFullTrust", "graphicsCaptureWithoutBorder" }, capabilities);
        Assert.Equal("MyCapture.exe", (string?)Single(manifest, "Application").Attribute("Executable"));
        Assert.Equal(
            "10.0." + WindowsSupportPolicy.MinimumBuild + ".0",
            (string?)Single(manifest, "TargetDeviceFamily").Attribute("MinVersion"));
    }

    [Fact]
    public void BorderlessGraphicsCapture_IsAnIgnorableUap11Capability()
    {
        const string Uap11 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/11";
        System.Xml.Linq.XDocument manifest = Manifest();
        System.Xml.Linq.XElement package = manifest.Root!;
        System.Xml.Linq.XElement borderless = manifest.Descendants()
            .Single(element => element.Name.LocalName == "Capability"
                && (string?)element.Attribute("Name") == "graphicsCaptureWithoutBorder");

        // learn.microsoft.com uap11:Capability: namespace .../uap/windows10/11, minimum build 19645.
        Assert.Equal(Uap11, borderless.Name.NamespaceName);
        string prefix = package.GetPrefixOfNamespace(Uap11)!;
        Assert.Equal("uap11", prefix);
        Assert.Contains(prefix, ((string)package.Attribute("IgnorableNamespaces")!).Split(' '));
        // The supported Windows 10 host predates uap11 and must ignore this optional capability.
        Assert.True(WindowsSupportPolicy.MinimumBuild < 19645);
        Assert.Same(
            manifest.Descendants().Single(element => element.Name.LocalName == "Capabilities"),
            borderless.Parent);

        // CreateForMonitor (HMONITOR interop) does not need the WindowId/DisplayId capability.
        Assert.DoesNotContain(
            manifest.Descendants().Where(element => element.Name.LocalName == "Capability"),
            element => (string?)element.Attribute("Name") == "graphicsCaptureProgrammatic");
    }

    [Fact]
    public void Identity_IsSuppliedAtPackagingTimeOnly()
    {
        System.Xml.Linq.XElement identity = Single(Manifest(), "Identity");
        Assert.Equal("{IdentityName}", (string?)identity.Attribute("Name"));
        Assert.Equal("{Publisher}", (string?)identity.Attribute("Publisher"));
        Assert.Equal("{PackageVersion}", (string?)identity.Attribute("Version"));
        Assert.Equal("x64", (string?)identity.Attribute("ProcessorArchitecture"));
    }
}
