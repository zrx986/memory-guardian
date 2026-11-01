using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace MemoryGuardian.App;

/// <summary>Plays the original Daoli animation from a compact, embedded sprite sheet.</summary>
public sealed class DaoliMascot : Control
{
    private const string ResourceName = "MemoryGuardian.Daoli.frames.png";
    private const int FrameCount = 162;
    private const int FrameWidth = 128;
    private const int FrameHeight = 128;
    private const int Columns = 8;
    private const int FPS = 12;
    private readonly System.Windows.Forms.Timer frameTimer = new() { Interval = 1000 / FPS };
    private readonly Stopwatch animationClock = new();
    private readonly List<Control> observedAncestors = [];
    private Form? ownerForm;
    private Bitmap? decodedFrames;
    private Bitmap? currentFrame;
    private int preparedFrame = -1;
    private bool animationEnabled = true;
    private bool isCleaning;
    private bool disposed;

    public DaoliMascot()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Size = new Size(124, 124);
        TabStop = false;
        AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = "说的道理";
        AccessibleDescription = "说的道理原版动画";
        frameTimer.Tick += AdvanceFrame;
    }

    [DefaultValue(false)]
    public bool IsCleaning
    {
        get => isCleaning;
        set
        {
            if (isCleaning == value || disposed) return;
            isCleaning = value;
            AccessibleDescription = value ? "说的道理原版动画，正在整理内存" : "说的道理原版动画";
            Invalidate();
        }
    }

    [Browsable(false)]
    public bool IsAnimationRunning => !disposed && frameTimer.Enabled;

    [Browsable(false)]
    public bool HasDecodedFrames => !disposed && decodedFrames is not null;

    /// <summary>Call on the UI thread. Hidden or minimized windows pause and release the decoded frames.</summary>
    public void SetAnimationEnabled(bool enabled)
    {
        if (disposed) return;
        animationEnabled = enabled;
        UpdateAnimationState();
    }

    /// <summary>Renders one original animation frame without creating a window or timer. Dispose the returned bitmap.</summary>
    public static Bitmap RenderPreview(Size size, bool isCleaning, TimeSpan animationPosition)
    {
        if (size.Width <= 0 || size.Height <= 0 || size.Width > 4096 || size.Height > 4096)
            throw new ArgumentOutOfRangeException(nameof(size), "Preview dimensions must be between 1 and 4096 pixels.");
        using var sheet = LoadSpriteSheet();
        using var frame = new Bitmap(FrameWidth, FrameHeight, PixelFormat.Format32bppPArgb);
        CopyFrame(sheet, frame, FrameAt(animationPosition));
        var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Transparent);
            DrawFrame(graphics, new Rectangle(Point.Empty, size), frame, isCleaning);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!CanDisplayFrames()) return;
        decodedFrames ??= LoadSpriteSheet();
        currentFrame ??= new Bitmap(FrameWidth, FrameHeight, PixelFormat.Format32bppPArgb);
        var index = FrameAt(animationClock.Elapsed);
        if (index != preparedFrame)
        {
            CopyFrame(decodedFrames, currentFrame, index);
            preparedFrame = index;
        }
        DrawFrame(e.Graphics, ClientRectangle, currentFrame, isCleaning);
    }

    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        ObserveParents();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ObserveParents();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (!disposed)
        {
            StopAnimation();
            ReleaseFrames();
        }
        base.OnHandleDestroyed(e);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        UpdateAnimationState();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        UpdateAnimationState();
    }

    private void ObserveParents()
    {
        DetachParents();
        if (disposed) return;
        for (var ancestor = Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            observedAncestors.Add(ancestor);
            ancestor.ParentChanged += ParentTreeChanged;
            ancestor.VisibleChanged += ParentStateChanged;
            ancestor.Disposed += ParentStateChanged;
        }
        ownerForm = FindForm();
        if (ownerForm is not null) ownerForm.Resize += ParentStateChanged;
        UpdateAnimationState();
    }

    private void DetachParents()
    {
        if (ownerForm is not null) ownerForm.Resize -= ParentStateChanged;
        ownerForm = null;
        foreach (var ancestor in observedAncestors)
        {
            ancestor.ParentChanged -= ParentTreeChanged;
            ancestor.VisibleChanged -= ParentStateChanged;
            ancestor.Disposed -= ParentStateChanged;
        }
        observedAncestors.Clear();
    }

    private void ParentTreeChanged(object? sender, EventArgs e) => ObserveParents();
    private void ParentStateChanged(object? sender, EventArgs e) => UpdateAnimationState();

    private bool CanDisplayFrames() => !disposed && IsHandleCreated && Visible &&
        !DesignMode && LicenseManager.UsageMode != LicenseUsageMode.Designtime &&
        ownerForm is { IsDisposed: false, Disposing: false, Visible: true } &&
        ownerForm.WindowState != FormWindowState.Minimized;

    private void UpdateAnimationState()
    {
        if (disposed) return;
        var canDisplay = CanDisplayFrames();
        var shouldRun = canDisplay && Enabled && animationEnabled;
        if (shouldRun && !frameTimer.Enabled)
        {
            animationClock.Start();
            frameTimer.Start();
            Invalidate();
        }
        else if (!shouldRun) StopAnimation();
        if (!canDisplay) ReleaseFrames();
    }

    private void StopAnimation()
    {
        frameTimer.Stop();
        animationClock.Stop();
    }

    private void ReleaseFrames()
    {
        currentFrame?.Dispose();
        currentFrame = null;
        decodedFrames?.Dispose();
        decodedFrames = null;
        preparedFrame = -1;
    }

    private void AdvanceFrame(object? sender, EventArgs e)
    {
        UpdateAnimationState();
        if (frameTimer.Enabled && FrameAt(animationClock.Elapsed) != preparedFrame) Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true;
            StopAnimation();
            ReleaseFrames();
            DetachParents();
            frameTimer.Tick -= AdvanceFrame;
            frameTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    private static int FrameAt(TimeSpan position) =>
        (int)(Math.Floor(Math.Max(0, position.TotalSeconds) * FPS) % FrameCount);

    private static Bitmap LoadSpriteSheet()
    {
        using var stream = typeof(DaoliMascot).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Missing embedded animation resource: {ResourceName}");
        using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
        if (image.Width != Columns * FrameWidth || image.Height != ((FrameCount + Columns - 1) / Columns) * FrameHeight)
            throw new InvalidOperationException("The embedded Daoli sprite sheet has unexpected dimensions.");
        // Detach the decoded pixels from the resource stream, which can then be closed.
        return new Bitmap(image);
    }

    private static void CopyFrame(Bitmap sheet, Bitmap target, int index)
    {
        using var graphics = Graphics.FromImage(target);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        // Exact pixel copy avoids sampling an adjacent sprite when the displayed frame is scaled.
        graphics.DrawImageUnscaled(sheet, -(index % Columns) * FrameWidth, -(index / Columns) * FrameHeight);
    }

    private static void DrawFrame(Graphics graphics, Rectangle bounds, Bitmap frame, bool cleaning)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var saved = graphics.Save();
        try
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var side = (float)Math.Min(bounds.Width, bounds.Height);
            var square = new RectangleF(bounds.Left + (bounds.Width - side) / 2,
                bounds.Top + (bounds.Height - side) / 2, side, side);
            var inset = Math.Min(Math.Max(1f, side / 64f), side / 8f);
            var faceBounds = RectangleF.Inflate(square, -inset, -inset);
            if (cleaning)
            {
                using var background = RoundedRectangle(RectangleF.Inflate(square, -inset / 2, -inset / 2), side / 10);
                using var fill = new SolidBrush(Color.FromArgb(233, 247, 239));
                using var border = new Pen(Color.FromArgb(20, 147, 120), Math.Max(1f, side / 100f));
                graphics.FillPath(fill, background);
                graphics.DrawPath(border, background);
            }
            using var attributes = new ImageAttributes();
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            graphics.DrawImage(frame, Rectangle.Round(faceBounds), 0, 0, FrameWidth, FrameHeight,
                GraphicsUnit.Pixel, attributes);
        }
        finally { graphics.Restore(saved); }
    }

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
