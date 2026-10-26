using System.IO;
using System.Windows;
using MyCapture.App.Preview;
using MyCapture.App.Themes;

namespace MyCapture.App;

public partial class App
{
    private PinPreviewSession? _pinPreview;

    private bool TryStartPinPreview(string[] args)
    {
        if (!PreviewLaunchOptions.IsRequested(args)) return false;
        try
        {
            PreviewLaunchOptions options = PreviewLaunchOptions.Parse(args,
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify))!;
            _pinPreview = new PinPreviewSession(options, Dispatcher);
            if (!_pinPreview.Start())
            {
                Shutdown(0);
                return true;
            }
            MainWindow = _pinPreview.Window;
            MainWindow!.Closed += (_, _) => Shutdown(0);
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _pinPreview?.Dispose();
            ThemedDialog.Show("미리보기를 시작할 수 없습니다. 별도의 로컬 프로필 경로를 확인하십시오.",
                "MyCapture 미리보기", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
        }
        return true;
    }
}
