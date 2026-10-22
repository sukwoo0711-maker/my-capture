using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Xunit;

namespace MyCapture.Setup.Tests;

public sealed class UpdateWindowTests
{
    [Theory]
    [InlineData("ko-KR", "MyCapture 업데이트", "MyCapture 4.5.2 업데이트 중", "앱 종료 대기 중", "MyCapture 열기", "업데이트하지 못했습니다")]
    [InlineData("en-US", "MyCapture Update", "Updating MyCapture 4.5.2", "Waiting for the app to close", "Open MyCapture", "Could not update MyCapture")]
    [InlineData("ja-JP", "MyCapture Update", "Updating MyCapture 4.5.2", "Waiting for the app to close", "Open MyCapture", "Could not update MyCapture")]
    public void UpdateCopyUsesKoreanOrEnglish(string culture, string title, string heading, string status, string open, string failure)
    {
        var text = new SetupStrings(CultureInfo.GetCultureInfo(culture));
        Assert.Equal(title, text.UpdateTitle); Assert.Equal(heading, text.UpdateHeading("4.5.2"));
        Assert.Equal(status, text.Stage(SetupStage.WaitingForParent)); Assert.Equal(open, text.OpenApplication);
        Assert.Equal(failure, text.UpdateFailureHeading);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, true, false)]
    public void ProgressRequiresValidatedOptInWithoutQuietOrVerify(bool requested, bool quiet, bool verify, bool expected)
    {
        var options = new Options { Quiet = quiet, VerifyOnly = verify };
        Assert.Equal(expected, SetupInteraction.ShowUpdateProgress(options, new UpdaterSession { ShowProgress = requested }));
        Assert.False(SetupInteraction.ShowUpdateProgress(options, null));
    }

    [Theory]
    [InlineData(0, true, "auto", false)]
    [InlineData(17, true, "open", true)]
    [InlineData(17, true, "close", false)]
    [InlineData(15, true, "title", false)]
    [InlineData(14, false, "close", false)]
    public void UpdateWindowWaitsThenClosesOrOffersExplicitRecovery(int result, bool appExists, string action, bool expectedLaunch)
    {
        Fixture.Sta(() =>
        {
            Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo("ko-KR");
            using (var started = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            using (var timer = new System.Windows.Forms.Timer { Interval = 20 })
            {
                bool inspectedWorking = false, closed = false, sawFinishPage = false;
                int launchCount = 0;
                Exception? failure = null;
                var log = new SetupLog(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "update-window-tests.log"), AppDomain.CurrentDomain.BaseDirectory, true, false);
                Action launch = () => launchCount++;
                Func<ISetupProgress, int> work = _ =>
                {
                    started.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The update window test did not release its worker.");
                    return result;
                };
                using (var window = (Form)Activator.CreateInstance(typeof(SetupWindow), BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new object[] { false, log, work, launch, true, "4.5.2", true, (Func<bool>)(() => appExists) }, CultureInfo.InvariantCulture)!)
                {
                    typeof(SetupWindow).GetField("closeOnSuccess", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
                    window.FormClosed += (_, __) =>
                    {
                        closed = true;
                        if (result == 0) sawFinishPage = Field<Button>(window, "finish").Enabled;
                    };
                    DateTime deadline = DateTime.UtcNow.AddSeconds(10);
                    timer.Tick += (_, __) =>
                    {
                        try
                        {
                            Assert.True(DateTime.UtcNow < deadline, "The update window did not finish.");
                            if (!started.IsSet) return;
                            var finish = Field<Button>(window, "finish");
                            var open = Field<Button>(window, "openApplication");
                            if (!inspectedWorking)
                            {
                                Assert.Equal("MyCapture 업데이트", window.Text);
                                Assert.Equal("MyCapture 4.5.2 업데이트 중", Field<Label>(window, "heading").Text);
                                Assert.Equal("앱 종료 대기 중", Field<Label>(window, "status").Text);
                                Assert.False(finish.Enabled); Assert.False(open.Visible);
                                Assert.False(Field<LinkLabel>(window, "openLog").Visible);
                                window.Close(); Assert.False(window.IsDisposed);
                                inspectedWorking = true; release.Set(); return;
                            }
                            if (!finish.Enabled) return;
                            Assert.NotEqual(0, result);
                            Assert.Equal("업데이트하지 못했습니다", Field<Label>(window, "heading").Text);
                            Assert.Equal(new SetupStrings(CultureInfo.GetCultureInfo("ko-KR")).Failure(result), Field<Label>(window, "status").Text);
                            Assert.False(Field<CheckBox>(window, "launchChoice").Visible);
                            Assert.True(Field<LinkLabel>(window, "openLog").Visible);
                            Assert.Equal("닫기", finish.Text); Assert.Equal("MyCapture 열기", open.Text);
                            Assert.Equal(appExists, open.Visible);
                            Assert.Same(appExists ? open : finish, window.AcceptButton);
                            Assert.True(finish.Parent!.ClientRectangle.Contains(finish.Bounds));
                            Assert.True(open.Parent!.ClientRectangle.Contains(open.Bounds));
                            timer.Stop();
                            if (action == "open") open.PerformClick();
                            else if (action == "title") window.Close();
                            else finish.PerformClick();
                        }
                        catch (Exception error)
                        {
                            failure = error; timer.Stop(); release.Set();
                            typeof(SetupWindow).GetField("completed", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
                            window.Close();
                        }
                    };
                    timer.Start(); Application.Run(window); timer.Stop();
                    if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                    Assert.True(inspectedWorking); Assert.True(closed); Assert.False(sawFinishPage);
                    Assert.Equal(result, Field<int>(window, "exitCode"));
                    Assert.Equal(expectedLaunch, Field<bool>(window, "launchRequested"));
                    // RunCore invokes the stored callback only after this explicit request.
                    if (Field<bool>(window, "launchRequested")) Field<Action>(window, "launch")();
                    Assert.Equal(expectedLaunch ? 1 : 0, launchCount);
                }
            }
            return 0;
        });
    }

    private static T Field<T>(Form window, string name) =>
        (T)typeof(SetupWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
}
