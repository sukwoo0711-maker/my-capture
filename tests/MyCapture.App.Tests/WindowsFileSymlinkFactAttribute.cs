using System.IO;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>Local capability limit is explicit; GitHub's release gate must run the real case.</summary>
internal sealed class WindowsFileSymlinkFactAttribute : FactAttribute
{
    public WindowsFileSymlinkFactAttribute()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase))
            return; // Never skip this security regression on hosted CI, including missing privilege.

        string root = OwnedTestDirectory.Create("MyCapture-symlink-capability-");
        string link = Path.Combine(root, "link");
        try
        {
            string target = Path.Combine(root, "target");
            File.WriteAllText(target, "capability probe");
            File.CreateSymbolicLink(link, target);
        }
        catch (IOException error) when ((error.HResult & 0xffff) == 1314)
        {
            Skip = "Local Windows token cannot create file symlinks (1314). The actual file-symlink case is mandatory on GITHUB_ACTIONS; junction tests do not replace it.";
        }
        finally
        {
            if (File.Exists(link)) File.Delete(link);
            OwnedTestDirectory.Delete(root);
        }
    }
}
