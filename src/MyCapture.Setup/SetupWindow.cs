using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;

namespace MyCapture.Setup;

internal sealed class SetupWindow : Form, ISetupProgress
{
    private readonly bool uninstall;
    private readonly bool update;
    private readonly Func<bool>? canLaunch;
    private readonly SetupLog log;
    private readonly SetupStrings strings = new SetupStrings(CultureInfo.CurrentUICulture);
    private readonly Func<ISetupProgress, int> work;
    private readonly Action? launch;
    private readonly Label heading = new Label();
    private readonly Label status = new Label();
    private readonly ProgressBar progress = new ProgressBar();
    private readonly CheckBox launchChoice = new CheckBox();
    private readonly LinkLabel openLog = new LinkLabel();
    private readonly Button finish = new Button();
    private readonly Button openApplication = new Button();
    private bool completed, launchRequested, closeOnSuccess;
    private int exitCode = ExitCodes.Unexpected;

    private SetupWindow(bool uninstall, SetupLog log, Func<ISetupProgress, int> work, Action? launch)
        : this(uninstall, log, work, launch, false, null, false, null) { }

    private SetupWindow(bool uninstall, SetupLog log, Func<ISetupProgress, int> work, Action? launch,
        bool update, string? version, bool waitingForParent, Func<bool>? canLaunch)
    {
        this.uninstall = uninstall; this.log = log; this.work = work; this.launch = launch;
        this.update = update; this.canLaunch = canLaunch;
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        Text = update ? strings.UpdateTitle : strings.Title(uninstall); BackColor = Color.White; ClientSize = new Size(460, 200);
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.Manual;
        Icon = Icon.ExtractAssociatedIcon(Program.Executable);
        heading.SetBounds(24, 22, 412, 30); heading.Font = new Font(Font, FontStyle.Bold);
        heading.Text = update ? strings.UpdateHeading(version) : strings.WorkingHeading(uninstall); heading.AutoEllipsis = true;
        status.SetBounds(24, 58, 412, 45); status.Text = strings.Stage(waitingForParent ? SetupStage.WaitingForParent : uninstall ? SetupStage.VerifyingInstallation : SetupStage.VerifyingPackage);
        progress.SetBounds(24, 111, 412, 18); progress.Style = ProgressBarStyle.Continuous;
        launchChoice.SetBounds(24, 108, 300, 24); launchChoice.Text = strings.Launch;
        launchChoice.Checked = true; launchChoice.Visible = false;
        openLog.SetBounds(24, 110, 300, 24); openLog.Text = strings.OpenLog; openLog.Visible = false;
        openLog.LinkClicked += (_, __) => OpenLog();
        var footer = new Panel { BackColor = SystemColors.Control, Dock = DockStyle.Bottom, Size = new Size(ClientSize.Width, 52) };
        finish.SetBounds(361, 13, 75, 27); finish.Text = strings.Close; finish.Enabled = false;
        finish.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        finish.Click += (_, __) => { launchRequested = !uninstall && exitCode == 0 && launchChoice.Checked && launchChoice.Visible; Close(); };
        openApplication.SetBounds(214, 13, 139, 27); openApplication.Text = strings.OpenApplication;
        openApplication.Anchor = AnchorStyles.Top | AnchorStyles.Right; openApplication.Visible = false;
        openApplication.Click += (_, __) => { launchRequested = true; Close(); };
        footer.Controls.AddRange(new Control[] { finish, openApplication });
        Controls.AddRange(new Control[] { heading, status, progress, launchChoice, openLog, footer });
        AcceptButton = finish;
        FormClosing += (_, e) => { if (!completed) e.Cancel = true; };
        CenterOnPointerScreen();
    }

    internal static int Run(bool uninstall, SetupLog log, Func<ISetupProgress, int> work, Action? launch = null) =>
        RunCore(uninstall, log, work, launch, false);

    internal static int RunUpdate(string version, bool waitingForParent, SetupLog log, Func<ISetupProgress, int> work,
        Action launch, Func<bool> canLaunch) => RunCore(false, log, work, launch, true, true, version, waitingForParent, canLaunch);

    private static int RunCore(bool uninstall, SetupLog log, Func<ISetupProgress, int> work, Action? launch, bool closeOnSuccess,
        bool update = false, string? version = null, bool waitingForParent = false, Func<bool>? canLaunch = null)
    {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        using (var window = new SetupWindow(uninstall, log, work, launch, update, version, waitingForParent, canLaunch) { closeOnSuccess = closeOnSuccess })
        {
            Application.Run(window);
            if (window.launchRequested) window.launch?.Invoke();
            return window.exitCode;
        }
    }

    internal static int LaunchUninstall(Options options, string directory, SetupLog log) =>
        RunCore(true, log, progress => Uninstaller.Run(options, directory, log, progress), null, true);

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        CenterOnPointerScreen();
    }

    private void CenterOnPointerScreen()
    {
        Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        var worker = new Thread(() =>
        {
            int result;
            try { result = work(this); }
            catch (Exception error)
            {
                result = ExitCodes.FromException(error, uninstall);
                log.Write("ERROR", "Setup failed (exit " + result + "): " + error.Message);
            }
            Post(() => Complete(result));
        });
        worker.SetApartmentState(ApartmentState.STA); worker.Start();
    }

    private void Complete(int result)
    {
        exitCode = result; completed = true;
        if (result == 0 && closeOnSuccess) { Close(); return; }
        heading.Text = result == 0 ? strings.SuccessHeading(uninstall) : update ? strings.UpdateFailureHeading : strings.FailureHeading(uninstall);
        status.Text = result == 0 ? strings.SuccessBody(uninstall) : strings.Failure(result, uninstall);
        progress.Visible = false;
        launchChoice.Visible = result == 0 && !uninstall && !update && launch != null;
        openLog.Visible = result != 0 && log.Path != null;
        finish.Text = result == 0 && !uninstall ? strings.Finish : strings.Close;
        finish.Enabled = true;
        openApplication.Visible = update && result != 0 && launch != null && canLaunch?.Invoke() == true;
        AcceptButton = openApplication.Visible ? openApplication : finish;
        if (openApplication.Visible) openApplication.Focus(); else finish.Focus();
    }

    private void OpenLog()
    {
        if (log.Path == null) return;
        try { using (Process.Start(new ProcessStartInfo(log.Path) { UseShellExecute = true })) { } }
        catch (Exception error) { log.Write("WARN", "Could not open setup log: " + error.Message); }
    }

    private void Post(Action action)
    {
        if (IsDisposed || Disposing) return;
        try { BeginInvoke(action); }
        catch (InvalidOperationException) { }
    }

    public void Version(string version) => Post(() => heading.Text = update ? strings.UpdateHeading(version) : strings.WorkingHeading(uninstall, version));
    public void Files(int done, int total) => Post(() =>
    {
        status.Text = strings.Files(done, total);
        if (total > 0) progress.Value = 10 + (int)(60L * Math.Max(0, Math.Min(done, total)) / total);
    });
    public void Stage(SetupStage stage) => Post(() =>
    {
        status.Text = strings.Stage(stage);
        progress.Style = stage == SetupStage.RollingBack ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
        switch (stage)
        {
            case SetupStage.VerifyingPackage: case SetupStage.VerifyingInstallation: case SetupStage.WaitingForParent: progress.Value = 0; break;
            case SetupStage.ExtractingFiles: progress.Value = 10; break;
            case SetupStage.ReplacingVersion: progress.Value = 75; break;
            case SetupStage.RegisteringStartMenu: progress.Value = 90; break;
            case SetupStage.FinalizingInstallation: progress.Value = 95; break;
            case SetupStage.StoppingApplication: progress.Value = uninstall ? 10 : 70; break;
            case SetupStage.PreparingRemoval: progress.Value = 20; break;
            case SetupStage.RemovingFiles: progress.Value = 30; break;
            case SetupStage.RemovingRegistration: progress.Value = 85; break;
        }
    });

    protected override void Dispose(bool disposing)
    {
        if (disposing) { heading.Font.Dispose(); Font.Dispose(); Icon?.Dispose(); }
        base.Dispose(disposing);
    }
}
