using System.IO;
using System.Security.Cryptography;
using System.Text;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;

namespace MyCapture.App.Preview;

/// <summary>An explicit, separate process mode; never inferred from a portable data override.</summary>
internal sealed record PreviewLaunchOptions(string ProfileRoot, string InstanceKey)
{
    internal const string CommandLineSwitch = "--preview-pin";
    internal string MutexName => @"Local\MyCapture.PinPreview.Mutex." + InstanceKey;
    internal string ActivationName => @"Local\MyCapture.PinPreview.Activate." + InstanceKey;
    internal AppPaths Paths => AppPaths.CreateForRoot(ProfileRoot);

    internal static bool IsRequested(IReadOnlyList<string> args) =>
        args.Any(argument => string.Equals(argument, CommandLineSwitch, StringComparison.OrdinalIgnoreCase));

    internal static PreviewLaunchOptions? Parse(IReadOnlyList<string> args, string roamingAppData, string localAppData)
    {
        if (!IsRequested(args)) return null;
        if (args.Count != 2 || !string.Equals(args[0], CommandLineSwitch, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("미리보기에는 별도의 절대 프로필 경로가 필요합니다.");
        string candidate = args[1];
        if (!Path.IsPathFullyQualified(candidate) || candidate.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("미리보기 프로필은 로컬 드라이브의 절대 경로여야 합니다.");
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        if (root.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar })
            .Any(segment => segment.EndsWith('.') || segment.EndsWith(' ')))
            throw new ArgumentException("미리보기 프로필에는 점이나 공백으로 끝나는 폴더 이름을 사용할 수 없습니다.");
        if (string.Equals(root, Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("드라이브 루트를 미리보기 프로필로 사용할 수 없습니다.");
        foreach (string appData in new[] { roamingAppData, localAppData })
        {
            if (string.IsNullOrWhiteSpace(appData)) continue;
            string personal = Path.GetFullPath(Path.Combine(appData, AppPaths.AppFolderName));
            if (Contains(root, personal) || Contains(personal, root))
                throw new ArgumentException("기존 MyCapture 프로필과 겹치지 않는 경로를 사용하십시오.");
        }
        if (!string.IsNullOrWhiteSpace(localAppData)
            && Contains(Path.GetFullPath(Path.Combine(localAppData, "Packages")), root))
            throw new ArgumentException("Store 앱 데이터 밖의 미리보기 프로필을 사용하십시오.");
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..24];
        return new PreviewLaunchOptions(root, key);
    }

    private static bool Contains(string parent, string path) =>
        string.Equals(parent, path, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal static AppSettings CreateSettings() => new()
    {
        General = new GeneralSettings { LaunchAtLogin = false },
        Pin = new PinSettings { RestoreOpenPinsOnLaunch = false },
    };
}

/// <summary>No registry or packaged-startup adapter exists in the isolated preview.</summary>
internal sealed class PreviewStartupRegistration : IStartupRegistration
{
    public bool IsEnabled() => false;
    public bool ReconcileOnStartup(bool settingEnabled) => false;
    public StartupApplyResult Apply(bool desiredEnabled) => desiredEnabled
        ? StartupApplyResult.Fail(true, "미리보기에서는 Windows 시작 시 실행을 사용하지 않습니다.")
        : StartupApplyResult.Ok(false);
}

/// <summary>The preview's clipboard replacement contains only values the user sees here.</summary>
internal sealed class PreviewCopyBuffer
{
    internal string Text { get; private set; } = string.Empty;
    internal event Action<string>? Changed;

    internal Task<bool> CopyTextAsync(string text)
    {
        Text = text;
        Changed?.Invoke(text);
        return Task.FromResult(true);
    }
}
