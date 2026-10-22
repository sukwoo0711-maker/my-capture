namespace MyCapture.Setup;

public enum SetupStage
{
    VerifyingPackage, ExtractingFiles, ReplacingVersion, RegisteringStartMenu, FinalizingInstallation,
    RollingBack, VerifyingInstallation, StoppingApplication, PreparingRemoval, RemovingFiles, RemovingRegistration
}

public interface ISetupProgress
{
    void Stage(SetupStage stage);
    void Files(int done, int total);
    void Version(string version);
}

public sealed class NullSetupProgress : ISetupProgress
{
    public static readonly NullSetupProgress Instance = new NullSetupProgress();
    private NullSetupProgress() { }
    public void Stage(SetupStage stage) { }
    public void Files(int done, int total) { }
    public void Version(string version) { }
}
