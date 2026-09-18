using System.Text;

namespace CloudRemoteShouter.Receiver;

internal static class AppIconFactory
{
    public static Stream CreateStream()
    {
        const int size = 32;
        const int xorBytes = size * size * 4;
        const int andBytes = size * 4;
        const int imageBytes = 40 + xorBytes + andBytes;
        var stream = new MemoryStream(22 + imageBytes);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)1);
        writer.Write((byte)size); writer.Write((byte)size); writer.Write((byte)0); writer.Write((byte)0);
        writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(imageBytes); writer.Write(22);
        writer.Write(40); writer.Write(size); writer.Write(size * 2); writer.Write((ushort)1); writer.Write((ushort)32);
        writer.Write(0); writer.Write(xorBytes); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
        const double center = 15.5; const double outer = 14.5; const double inner = 7.4;
        for (var y = size - 1; y >= 0; y--)
        for (var x = 0; x < size; x++)
        {
            var coverage = 0;
            for (var sy = 0; sy < 4; sy++) for (var sx = 0; sx < 4; sx++)
            {
                var dx = x + (sx + .5) / 4 - center; var dy = y + (sy + .5) / 4 - center;
                var radius = Math.Sqrt(dx * dx + dy * dy); var angle = Math.Abs(Math.Atan2(dy, dx));
                if (radius >= inner && radius <= outer && (angle <= 35 * Math.PI / 180 || angle >= 45 * Math.PI / 180)) coverage++;
            }
            var alpha = (byte)(coverage * 255 / 16); writer.Write((byte)0x5D); writer.Write((byte)0x6B); writer.Write((byte)0x18); writer.Write(alpha);
        }
        writer.Write(new byte[andBytes]); stream.Position = 0; return stream;
    }
}
