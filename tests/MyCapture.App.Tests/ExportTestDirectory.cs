using System.IO;

namespace MyCapture.App.Tests;

internal static class ExportTestDirectory
{
    internal static string Create()
    {
        DirectoryInfo? repository = new(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "MyCapture.slnx")))
            repository = repository.Parent;
        if (repository is null) throw new DirectoryNotFoundException("Could not locate the export test worktree.");
        // Keep owned fixtures in the worktree for inspection; cleanup belongs to the caller.
        string root = Path.Combine(repository.FullName, "artifacts", "export-regression-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
