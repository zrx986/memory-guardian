using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Media.Imaging;
using MemoryGuardian.App;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static int Main(string[] args)
    {
        var output = Path.GetFullPath(args.Length == 1 ? args[0] : "docs/assets");
        Directory.CreateDirectory(output);
        Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
        Application.EnableVisualStyles();
        using var form = new MainForm(true, true, "MemoryGuardian_Preview_" + Guid.NewGuid().ToString("N"));
        ((System.Windows.Forms.Timer)Field(form, "timer")).Stop();
        var host = (MonitorHost)Field(form, "host");
        var mascot = (DaoliMascot)Field(form, "mascot");
        var content = form.Controls.OfType<TableLayoutPanel>().Single();
        using var canvas = new PreviewHost { ClientSize = form.ClientSize, Font = form.Font, BackColor = form.BackColor };
        form.Controls.Remove(content);
        canvas.Controls.Add(content);
        canvas.Show();
        Pump(100);
        mascot.SetAnimationEnabled(false);
        var mascotBounds = content.RectangleToClient(mascot.RectangleToScreen(mascot.ClientRectangle));
        var encoder = new GifBitmapEncoder();
        const int count = 81; // 13.5 seconds at 6 fps for a compact README preview.
        for (var i = 0; i < count; i++)
        {
            var seconds = i / 6d;
            var stage = seconds < 4.5 ? 0 : seconds < 6 ? 1 : 2;
            SetDemoState(form, host, stage);
            content.PerformLayout();
            using var full = new Bitmap(content.Width, content.Height, PixelFormat.Format32bppArgb);
            content.DrawToBitmap(full, new Rectangle(Point.Empty, content.Size));
            using (var graphics = Graphics.FromImage(full))
            {
                using var white = new SolidBrush(content.BackColor);
                graphics.FillRectangle(white, mascotBounds);
                using var face = DaoliMascot.RenderPreview(mascotBounds.Size, stage == 1, TimeSpan.FromSeconds(seconds));
                graphics.DrawImageUnscaled(face, mascotBounds.Location);
                var progress = (ProgressBar)Field(form, "bar");
                var progressBounds = content.RectangleToClient(progress.RectangleToScreen(progress.ClientRectangle));
                progressBounds.Inflate(-1, -1);
                using var track = new SolidBrush(Color.FromArgb(231, 236, 234));
                using var fill = new SolidBrush(Color.FromArgb(20, 147, 120));
                graphics.FillRectangle(track, progressBounds);
                graphics.FillRectangle(fill, progressBounds.X, progressBounds.Y, (float)(progressBounds.Width * (stage == 2 ? .488 : .675)), progressBounds.Height);
            }
            if (i == 0) full.Save(Path.Combine(output, "main-window.png"), ImageFormat.Png);
            if (i == 40) full.Save(Path.Combine(output, "result-window.png"), ImageFormat.Png);
            using var preview = new Bitmap(full, new Size(560, 496));
            using var encodedPng = new MemoryStream();
            preview.Save(encodedPng, ImageFormat.Png);
            encodedPng.Position = 0;
            var pixels = BitmapFrame.Create(encodedPng, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            encoder.Frames.Add(BitmapFrame.Create(pixels));
        }
        using (var stream = new MemoryStream())
        {
            encoder.Save(stream);
            File.WriteAllBytes(Path.Combine(output, "demo.gif"), GifTiming.AddLoopAndDelays(stream.ToArray(), count));
        }
        using var verified = Image.FromFile(Path.Combine(output, "demo.gif"));
        if (verified.GetFrameCount(FrameDimension.Time) != count) throw new InvalidDataException("GIF frame count mismatch");
        Console.WriteLine($"Preview generated: {count} frames, 13.5 seconds, simulated readings; no native cleanup.");
        Console.WriteLine(Path.Combine(output, "demo.gif"));
        return 0;
    }

    private static object Field(object instance, string name) => instance.GetType().GetField(name, Private)!.GetValue(instance)!;
    private static void Set(object instance, string name, object? value) => instance.GetType().GetField(name, Private)!.SetValue(instance, value);

    private static void SetDemoState(MainForm form, MonitorHost host, int stage)
    {
        var now = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.FromHours(8));
        const ulong total = 16UL << 30;
        var before = new MemorySnapshot(now, total, (ulong)(total * .325), 20UL << 30);
        var after = new MemorySnapshot(now, total, (ulong)(total * .512), 20UL << 30);
        Set(host, "sample", stage == 2 ? after : before);
        Set(host, "busy", stage == 1);
        Set(host, "lastResult", stage == 2 ? new CleanupObservation(now, "preview-simulation", true, 0, 0,
            "工作集整理完成", 1.5, before, after, ((double)after.AvailableBytes - before.AvailableBytes) / 1048576d) : null);
        typeof(MainForm).GetMethod("RefreshDisplay", Private)!.Invoke(form, null);
        ((Label)Field(form, "status")).Text = stage == 1 ? "正在整理工作集…" : stage == 2 ? "整理间隔保护中，仍可手动整理" : "自动监测中 · 暂不需要整理";
        ((Button)Field(form, "clean")).Enabled = stage != 1;
        Set(form, "updating", true);
        ((CheckBox)Field(form, "startup")).Checked = true;
        ((CheckBox)Field(form, "startup")).Enabled = true;
        Set(form, "updating", false);
    }

    private static void Pump(int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(5); }
    }

    private sealed class PreviewHost : Form
    {
        public PreviewHost()
        {
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            Opacity = 0;
        }
        protected override bool ShowWithoutActivation => true;
    }
}
