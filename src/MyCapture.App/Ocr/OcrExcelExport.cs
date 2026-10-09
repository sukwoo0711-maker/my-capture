using System.Diagnostics;
using System.IO;
using MyCapture.Core.Storage;
using MyCapture.Ocr;

namespace MyCapture.App.Ocr;

internal static class OcrExcelExport
{
    internal static string Open(OcrTable table, Action<ProcessStartInfo>? launch = null, string? temporaryRoot = null)
    {
        string directory = temporaryRoot is null ? ValidatedFilePaths.CreateTemporaryDirectory()
            : ValidatedFilePaths.CreateDirectory(Path.Combine(ValidatedFilePaths.DirectoryRoot(temporaryRoot),
                Guid.NewGuid().ToString("N")));
        string path = ValidatedFilePaths.FileWithinRoot(directory, "MyCapture-OCR.xlsx", ".xlsx");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            OcrTableExport.WriteWorkbook(stream, table);
        var start = new ProcessStartInfo(path) { UseShellExecute = true };
        if (launch is not null) launch(start);
        else Process.Start(start)?.Dispose();
        // The associated application can hold the file after this window closes.
        return path;
    }
}
