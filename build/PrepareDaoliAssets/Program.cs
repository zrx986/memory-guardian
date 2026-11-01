using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text.Json;

if (args.Length != 3) throw new ArgumentException("Usage: source.gif output.png provenance.json");
var source = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
var record = Path.GetFullPath(args[2]);
var sourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
if (sourceHash != "377DC0908797D02F0A7CD846C445D107462A71C1C920F58FB1BE7A6331134E0F")
    throw new InvalidDataException("Source GIF differs from the verified repository asset.");
using var original = Image.FromFile(source);
var dimension = FrameDimension.Time;
var originalFrames = original.GetFrameCount(dimension);
var delays = original.GetPropertyItem(0x5100)?.Value ?? throw new InvalidDataException("Missing GIF frame timing.");
var ends = new int[originalFrames];
var duration = 0;
for (var i = 0; i < originalFrames; i++)
{
    duration += Math.Max(10, BitConverter.ToInt32(delays, i * 4) * 10);
    ends[i] = duration;
}
const int fps = 12, size = 128, columns = 8;
var frameCount = (int)Math.Ceiling(duration / 1000d * fps);
if (original.Width != 280 || original.Height != 280 || originalFrames != 324 || duration != 13500 || frameCount != 162)
    throw new InvalidDataException("Verified source dimensions/frame timing changed.");
var rows = (frameCount + columns - 1) / columns;
using var atlas = new Bitmap(columns * size, rows * size, PixelFormat.Format32bppArgb);
using (var graphics = Graphics.FromImage(atlas))
{
    graphics.Clear(Color.Transparent);
    graphics.CompositingMode = CompositingMode.SourceCopy;
    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
    var sourceFrame = 0;
    for (var i = 0; i < frameCount; i++)
    {
        var timeMs = i * 1000d / fps;
        while (sourceFrame < originalFrames - 1 && ends[sourceFrame] <= timeMs) sourceFrame++;
        original.SelectActiveFrame(dimension, sourceFrame);
        graphics.DrawImage(original, new Rectangle((i % columns) * size, (i / columns) * size, size, size),
            0, 0, original.Width, original.Height, GraphicsUnit.Pixel);
    }
}
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
atlas.Save(output, ImageFormat.Png);
Directory.CreateDirectory(Path.GetDirectoryName(record)!);
var provenance = new
{
    Source = "https://github.com/Derpyu520/ottopet/blob/c00306cc020e27dec1ee608696432d359ca74e91/%E8%AF%B4%E7%9A%84%E9%81%93%E7%90%86%E5%8A%A8%E6%80%81%E5%AE%89%E5%85%A8.gif",
    SourceSHA256 = sourceHash, SourceWidth = original.Width, SourceHeight = original.Height,
    SourceFrames = originalFrames, DurationMilliseconds = duration,
    FrameWidth = size, FrameHeight = size, FramesPerSecond = fps, Frames = frameCount,
    Columns = columns, Rows = rows, AtlasWidth = atlas.Width, AtlasHeight = atlas.Height,
    DecodedPixelBytes = (long)atlas.Width * atlas.Height * 4,
    EncodedBytes = new FileInfo(output).Length,
    AtlasSHA256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output))),
    Processing = "Original frames only; transparent scale to 128x128; full 13.5-second timeline sampled at 12fps; no audio."
};
var json = JsonSerializer.Serialize(provenance, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(record, json);
Console.WriteLine(json);
