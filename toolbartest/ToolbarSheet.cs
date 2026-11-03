using System;
using System.IO;
using System.IO.Compression;

namespace VoiceBotBridge
{
    /// <summary>Preview sheet PNG: 2 glyphs x normal/lit at 24, 30 and 64 px on a dark background (no System.Drawing).</summary>
    internal static class ToolbarSheet
    {
        public static void Write(string path)
        {
            int[] sizes = { 24, 30, 64 };
            int pad = 8, cell = 64, w = pad + 4 * (cell + pad) * sizes.Length / sizes.Length, rows = sizes.Length;
            w = pad + 4 * (cell + pad);
            int h = pad + rows * (cell + pad);
            var img = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++) { img[i * 4] = 0x20; img[i * 4 + 1] = 0x24; img[i * 4 + 2] = 0x2A; img[i * 4 + 3] = 255; }
            for (int r = 0; r < rows; r++)
            {
                int col = 0;
                foreach (var key in ToolbarGlyphs.Keys)
                    foreach (uint color in new[] { ToolbarGlyphs.Normal, ToolbarGlyphs.Lit })
                    {
                        int px = sizes[r];
                        var rgba = ToolbarGlyphs.Rgba(ToolbarGlyphs.Mask(key, px), color, false);
                        int ox = pad + col * (cell + pad) + (cell - px) / 2, oy = pad + r * (cell + pad) + (cell - px) / 2;
                        for (int y = 0; y < px; y++)
                            for (int x = 0; x < px; x++)
                            {
                                int s = (y * px + x) * 4, d = ((oy + y) * w + ox + x) * 4, a = rgba[s + 3];
                                for (int c = 0; c < 3; c++) img[d + c] = (byte)((rgba[s + c] * a + img[d + c] * (255 - a)) / 255);
                            }
                        col++;
                    }
            }
            File.WriteAllBytes(path, Png(img, w, h));
        }

        private static byte[] Png(byte[] rgba, int w, int h)
        {
            var raw = new byte[(w * 4 + 1) * h];
            for (int y = 0; y < h; y++) Buffer.BlockCopy(rgba, y * w * 4, raw, y * (w * 4 + 1) + 1, w * 4);
            byte[] z;
            using (var ms = new MemoryStream())
            {
                using (var zs = new ZLibStream(ms, CompressionLevel.Optimal, true)) zs.Write(raw, 0, raw.Length);
                z = ms.ToArray();
            }
            using (var o = new MemoryStream())
            {
                o.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
                var ihdr = new byte[13];
                Be(ihdr, 0, w); Be(ihdr, 4, h); ihdr[8] = 8; ihdr[9] = 6;
                Chunk(o, "IHDR", ihdr); Chunk(o, "IDAT", z); Chunk(o, "IEND", new byte[0]);
                return o.ToArray();
            }
        }

        private static void Be(byte[] b, int i, int v) { b[i] = (byte)(v >> 24); b[i + 1] = (byte)(v >> 16); b[i + 2] = (byte)(v >> 8); b[i + 3] = (byte)v; }

        private static void Chunk(Stream o, string type, byte[] data)
        {
            var len = new byte[4]; Be(len, 0, data.Length); o.Write(len, 0, 4);
            var td = new byte[4 + data.Length];
            for (int i = 0; i < 4; i++) td[i] = (byte)type[i];
            Buffer.BlockCopy(data, 0, td, 4, data.Length);
            o.Write(td, 0, td.Length);
            var crc = new byte[4]; Be(crc, 0, (int)Crc(td)); o.Write(crc, 0, 4);
        }

        private static uint Crc(byte[] d)
        {
            uint c = 0xFFFFFFFF;
            foreach (byte b in d)
            {
                c ^= b;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }
            return c ^ 0xFFFFFFFF;
        }
    }
}
