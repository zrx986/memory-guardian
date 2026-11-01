using System;
using System.IO;

internal static class GifTiming
{
    // WPF encodes the image frames but omits GIF frame metadata. Apply the
    // GIF89a graphic-control delays and the NETSCAPE infinite-loop extension.
    public static byte[] AddLoopAndDelays(byte[] gif, int expectedFrames)
    {
        if (gif.Length < 14 || gif[0] != 'G' || gif[1] != 'I' || gif[2] != 'F')
            throw new InvalidDataException("Invalid generated GIF header");
        var headerEnd = 13 + ((gif[10] & 128) == 0 ? 0 : 3 * (1 << ((gif[10] & 7) + 1)));
        var position = headerEnd;
        var images = 0;
        var controls = 0;
        var trailer = false;
        void Need(int count)
        {
            if (position < 0 || count < 0 || position > gif.Length - count)
                throw new InvalidDataException("Truncated generated GIF");
        }
        void SkipBlocks()
        {
            while (true)
            {
                Need(1);
                var length = gif[position++];
                Need(length);
                position += length;
                if (length == 0) return;
            }
        }
        while (position < gif.Length)
        {
            var kind = gif[position++];
            if (kind == 0x3B) { trailer = true; break; }
            if (kind == 0x21)
            {
                Need(1);
                var label = gif[position++];
                if (label == 0xF9)
                {
                    Need(6);
                    if (gif[position] != 4 || gif[position + 5] != 0)
                        throw new InvalidDataException("Invalid graphics-control block");
                    gif[position + 1] = (byte)((gif[position + 1] & 0xE3) | 8);
                    gif[position + 2] = (byte)(controls % 3 == 2 ? 16 : 17);
                    gif[position + 3] = 0;
                    controls++;
                }
                SkipBlocks();
            }
            else if (kind == 0x2C)
            {
                Need(9);
                var packed = gif[position + 8];
                position += 9;
                var paletteBytes = (packed & 128) == 0 ? 0 : 3 * (1 << ((packed & 7) + 1));
                Need(paletteBytes + 1);
                position += paletteBytes + 1;
                SkipBlocks();
                images++;
            }
            else throw new InvalidDataException("Unexpected generated GIF block");
        }
        if (!trailer || images != expectedFrames || controls != expectedFrames)
            throw new InvalidDataException("Generated GIF block counts differ from rendered frames");
        using var output = new MemoryStream(gif.Length + 19);
        output.Write(gif.AsSpan(0, headerEnd));
        output.Write(new byte[] { 0x21, 0xFF, 11, (byte)'N', (byte)'E', (byte)'T', (byte)'S', (byte)'C', (byte)'A', (byte)'P', (byte)'E', (byte)'2', (byte)'.', (byte)'0', 3, 1, 0, 0, 0 });
        output.Write(gif.AsSpan(headerEnd));
        return output.ToArray();
    }
}
