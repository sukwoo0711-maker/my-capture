using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

public sealed class EditorWatermarkWordingTests
{
    [Theory]
    [InlineData("ko-KR", "워터마크", "워터마크 추가", "워터마크 이미지 변경…", "등록 이미지 해제", "워터마크 이미지 선택")]
    [InlineData("en-US", "Watermark", "Add watermark", "Change watermark image…", "Remove registered image", "Choose watermark image")]
    public void LocalizedMenuAndPickerUseWatermark(string language, string name, string place, string change, string remove, string picker) => StaTestHost.Run(() =>
    {
        using var locale = UiText.UseLanguage(language);
        using EditorWindowHost host = EditorWindowHost.Create();
        MenuItem[] items = host.Editor.EnsureStampMenu().Items.OfType<MenuItem>().ToArray();
        Assert.Equal(name, UiText.Get("Stamp_Name"));
        Assert.Equal(place, items[0].Header);
        Assert.Equal(change, items[1].Header);
        Assert.Equal(remove, items[^1].Header);
        Assert.Equal(picker, UiText.Get("Stamp_PickerTitle"));
        Assert.Equal(Key.S, ShortcutCatalog.Get(ShortcutScope.Annotation, "Stamp").Key);
    });

    [Theory]
    [InlineData("ko-KR", "도장 스탬프 로고 서명 워터마크")]
    [InlineData("en-US", "stamp logo signature watermark")]
    public void ShortcutHelpFindsWatermarkByEveryAlias(string language, string aliases) => StaTestHost.Run(() =>
    {
        using var locale = UiText.UseLanguage(language);
        var help = new ShortcutHelpWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -6000, Top = -6000, ShowActivated = false, ShowInTaskbar = false,
        };
        help.Show();
        try
        {
            var view = ShortcutHelpSearchTests.View(help);
            foreach (string alias in aliases.Split(' '))
            {
                view.Search.Text = alias;
                Assert.Contains(view.VisibleResults(), row => Descendants(row).OfType<TextBlock>()
                    .Any(text => KoreanWordWrap.Strip(text.Text) == UiText.Get("Stamp_Place")));
            }
        }
        finally { help.Close(); }
    });
}
