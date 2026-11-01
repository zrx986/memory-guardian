using System.Diagnostics;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using MemoryGuardian.App;

internal static class Program
{
    private static int passed, failed;

    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
        Application.EnableVisualStyles();
        var output = Path.GetFullPath(args.Length == 1 ? args[0] : AppContext.BaseDirectory);
        Directory.CreateDirectory(output);
        TestFrames(output);
        TestVisibility();
        RenderWindow(output);
        Console.WriteLine($"RESULT: {passed} passed, {failed} failed; no memory cleanup or startup changes");
        return failed == 0 ? 0 : 1;
    }

    private static void TestFrames(string output)
    {
        using var idle = DaoliMascot.RenderPreview(new Size(248, 248), false, TimeSpan.Zero);
        using var later = DaoliMascot.RenderPreview(new Size(248, 248), false, TimeSpan.FromSeconds(0.7));
        using var cleaning = DaoliMascot.RenderPreview(new Size(248, 248), true, TimeSpan.FromSeconds(0.7));
        using var looped = DaoliMascot.RenderPreview(new Size(248, 248), false, TimeSpan.FromSeconds(13.5));
        Check(Hash(idle) != Hash(later), "idle animation changes between moments");
        Check(Hash(later) != Hash(cleaning), "cleanup has a distinct pose");
        Check(Hash(idle) == Hash(looped), "original animation repeats after full 13.5-second timeline");
        idle.Save(Path.Combine(output, "mascot-idle.png"));
        cleaning.Save(Path.Combine(output, "mascot-cleaning.png"));
        var frames = Path.Combine(output, "animation-frames");
        Directory.CreateDirectory(frames);
        for (var i = 0; i < 48; i++)
        {
            using var frame = DaoliMascot.RenderPreview(new Size(248, 248), i >= 24, TimeSpan.FromSeconds(i / 12d));
            frame.Save(Path.Combine(frames, $"frame-{i:D3}.png"));
        }
    }

    private static void TestVisibility()
    {
        // Invisible, non-activating test host; no user desktop input is synthesized.
        using var owner = new QuietForm();
        using var mascot = new DaoliMascot { Size = new Size(124, 124) };
        owner.Controls.Add(mascot);
        _ = owner.Handle;
        _ = mascot.Handle;
        mascot.SetAnimationEnabled(true);
        Check(!mascot.IsAnimationRunning, "hidden parent never runs animation");
        Check(!mascot.HasDecodedFrames, "hidden initial control does not decode animation images");
        owner.Show();
        Pump(120);
        Check(mascot.IsAnimationRunning, "visible parent starts requested animation");
        using (var painted = new Bitmap(124, 124)) mascot.DrawToBitmap(painted, new Rectangle(0, 0, 124, 124));
        Check(mascot.HasDecodedFrames, "visible drawing loads original animation images");
        owner.WindowState = FormWindowState.Minimized;
        Pump(80);
        Check(!mascot.IsAnimationRunning, "minimized parent stops animation timer");
        Check(!mascot.HasDecodedFrames, "minimization releases decoded animation images");
        owner.WindowState = FormWindowState.Normal;
        Pump(80);
        Check(mascot.IsAnimationRunning, "restored parent resumes animation");
        mascot.Visible = false;
        Check(!mascot.IsAnimationRunning, "hidden control stops animation timer");
        mascot.Visible = true;
        Check(mascot.IsAnimationRunning, "visible control resumes animation");
        owner.Hide();
        Check(!mascot.IsAnimationRunning, "tray-style hide stops animation timer");
        Check(!mascot.HasDecodedFrames, "tray-style hide releases decoded animation images");
        mascot.SetAnimationEnabled(true);
        Check(!mascot.IsAnimationRunning, "explicit enable cannot animate hidden parent");
        owner.Show();
        Pump(80);
        mascot.SetAnimationEnabled(false);
        Check(!mascot.IsAnimationRunning, "explicit disable stops animation timer");
        mascot.SetAnimationEnabled(true);
        mascot.Dispose();
        Check(!mascot.IsAnimationRunning, "dispose releases animation timer");
        Check(!mascot.HasDecodedFrames, "dispose releases decoded animation images");
    }

    private static void RenderWindow(string output)
    {
        using var form = new MainForm(true, true, "MemoryGuardian_Animation_Render_" + Guid.NewGuid().ToString("N"));
        _ = form.Handle;
        form.PerformLayout();
        var mascot = Descendants(form).OfType<DaoliMascot>().Single();
        Check(!mascot.IsAnimationRunning, "actual hidden application has no animation timer");
        Check(!mascot.HasDecodedFrames, "actual hidden application has no decoded animation images");
        var root = form.Controls.OfType<TableLayoutPanel>().Single();
        var policy = Descendants(form).OfType<Label>().Single(label => label.Text.StartsWith("自动："));
        Check(root.GetRowHeights()[root.GetRow(policy)] >= policy.GetPreferredSize(new Size(root.DisplayRectangle.Width, 0)).Height + policy.Margin.Vertical,
            "policy instructions remain fully visible after adding mascot");
        // DrawToBitmap skips hidden child windows. Render the actual content in a
        // transparent, non-activating host so its child controls have paint handles.
        using var previewHost = new QuietForm { ClientSize = form.ClientSize, Text = form.Text, Font = form.Font, BackColor = form.BackColor };
        form.Controls.Remove(root);
        previewHost.Controls.Add(root);
        previewHost.Show();
        Pump(800);
        using var bitmap = new Bitmap(root.Width, root.Height);
        root.DrawToBitmap(bitmap, new Rectangle(Point.Empty, root.Size));
        bitmap.Save(Path.Combine(output, "window-preview.png"));
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static string Hash(Bitmap image)
    {
        using var stream = new MemoryStream();
        image.Save(stream, ImageFormat.Png);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static void Pump(int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
    }

    private static void Check(bool condition, string name)
    {
        if (condition) passed++; else failed++;
        Console.WriteLine($"{(condition ? "PASS" : "FAIL")} {name}");
    }

    private sealed class QuietForm : Form
    {
        public QuietForm()
        {
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            Opacity = 0;
            Size = new Size(160, 160);
        }

        protected override bool ShowWithoutActivation => true;
    }
}
