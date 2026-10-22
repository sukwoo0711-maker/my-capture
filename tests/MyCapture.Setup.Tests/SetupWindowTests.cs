using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Xunit;

namespace MyCapture.Setup.Tests;

public sealed class SetupWindowTests
{
    [Theory]
    [InlineData(false, true, false, 0, true)]
    [InlineData(false, false, false, 0, false)]
    [InlineData(false, true, true, 0, false)]
    [InlineData(false, true, false, 14, false)]
    [InlineData(true, true, false, 0, false)]
    public void WindowGuardsWorkAndRequiresFinishToRequestLaunch(bool uninstall, bool checkLaunch, bool titleClose, int result, bool launchRequested)
    {
        Fixture.Sta(() =>
        {
            Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo("ko-KR");
            using (var started = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            using (var timer = new System.Windows.Forms.Timer { Interval = 20 })
            {
                int uiThread = Thread.CurrentThread.ManagedThreadId, workThread = uiThread;
                bool guarded = false;
                Exception? error = null;
                var log = new SetupLog(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "setup-window-tests.log"), AppDomain.CurrentDomain.BaseDirectory, true, uninstall);
                Func<ISetupProgress, int> work = progress =>
                {
                    workThread = Thread.CurrentThread.ManagedThreadId;
                    using (new OperationMutex())
                    {
                        progress.Version("4.5.2"); progress.Stage(SetupStage.ExtractingFiles); progress.Files(2, 3);
                        started.Set();
                        if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test did not release setup work.");
                        return result;
                    }
                };
                using (var window = (Form)Activator.CreateInstance(typeof(SetupWindow), BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new object[] { uninstall, log, work, (Action)(() => { }) }, CultureInfo.InvariantCulture)!)
                {
                    DateTime deadline = DateTime.UtcNow.AddSeconds(10);
                    timer.Tick += (_, __) =>
                    {
                        try
                        {
                            Assert.True(DateTime.UtcNow < deadline, "Setup window did not complete.");
                            if (!started.IsSet) return;
                            var finish = Field<Button>(window, "finish");
                            if (!guarded)
                            {
                                Assert.NotEqual(uiThread, workThread);
                                Assert.False(finish.Enabled);
                                Assert.True(finish.Parent!.ClientRectangle.Contains(finish.Bounds), "Finish must stay inside the footer.");
                                window.Close(); Assert.False(window.IsDisposed);
                                guarded = true; release.Set(); return;
                            }
                            if (!finish.Enabled) return;
                            var choice = Field<CheckBox>(window, "launchChoice");
                            Assert.Equal(!uninstall && result == 0, choice.Visible);
                            Assert.Equal(result != 0, Field<LinkLabel>(window, "openLog").Visible);
                            Assert.True(choice.Checked);
                            Assert.Same(finish, window.AcceptButton);
                            var text = new SetupStrings(CultureInfo.GetCultureInfo("ko-KR"));
                            Assert.Equal(result == 0 ? text.SuccessHeading(uninstall) : text.FailureHeading(uninstall), Field<Label>(window, "heading").Text);
                            choice.Checked = checkLaunch;
                            timer.Stop();
                            if (titleClose) window.Close(); else finish.PerformClick();
                        }
                        catch (Exception exception)
                        {
                            error = exception; timer.Stop(); release.Set();
                            typeof(SetupWindow).GetField("completed", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
                            window.Close();
                        }
                    };
                    timer.Start(); Application.Run(window);
                    if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
                    Assert.True(guarded);
                    Assert.Equal(launchRequested, Field<bool>(window, "launchRequested"));
                    Assert.Equal(result, Field<int>(window, "exitCode"));
                }
            }
            return 0;
        });
    }

    [Theory][InlineData(1F)][InlineData(1.5F)][InlineData(2F)]
    public void ResultControlsRemainWithinTheDialogWhenScaled(float scale)
    {
        Fixture.Sta(() =>
        {
            Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo("ko-KR");
            var log = new SetupLog(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "setup-window-layout.log"), AppDomain.CurrentDomain.BaseDirectory, true, false);
            using (var window = (Form)Activator.CreateInstance(typeof(SetupWindow), BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[] { false, log, (Func<ISetupProgress, int>)(_ => 0), (Action)(() => { }) }, CultureInfo.InvariantCulture)!)
            {
                window.CreateControl(); window.Scale(new SizeF(scale, scale)); window.PerformLayout();
                foreach (int result in new[] { 0, 15 })
                {
                    typeof(SetupWindow).GetMethod("Complete", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { result });
                    foreach (string field in new[] { "heading", "status", "progress", "launchChoice", "openLog", "finish" })
                    {
                        var control = Field<Control>(window, field);
                        Assert.True(control.Parent!.ClientRectangle.Contains(control.Bounds), field + " must remain within its parent.");
                    }
                    var status = Field<Label>(window, "status");
                    Size required = TextRenderer.MeasureText(status.Text, status.Font, new Size(status.Width, int.MaxValue), TextFormatFlags.WordBreak);
                    Assert.True(required.Height <= status.Height, "The result sentence must fit without clipping.");
                }
            }
            return 0;
        });
    }

    private static T Field<T>(Form window, string name) =>
        (T)typeof(SetupWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
}
