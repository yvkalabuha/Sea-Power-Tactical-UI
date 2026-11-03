using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace VoiceBotBridge
{
    /// <summary>Offline preview of the radial glyphs (T-140): a PNG sheet, one row per glyph, 6 columns, written with the BCL only.</summary>
    internal static class PngSheet
    {
        public const int Cell = 48, Cols = 6;
        // Backdrop and sector colours repeat Radial.cs (Base / Hot / Grey); the backdrop stands for the game scene.
        private static readonly float[] Scene = { 0.18f, 0.22f, 0.2f, 1f };
        private static readonly float[] Base = { 0.07f, 0.09f, 0.13f, 0.78f }, HotBg = { 0.16f, 0.42f, 0.74f, 0.92f }, Grey = { 0.25f, 0.25f, 0.25f, 0.45f };

        private static void Over(float[] px, float[] src, float a)
        {
            float sa = src[3] * a;
            for (int i = 0; i < 3; i++) px[i] = src[i] * sa + px[i] * (1 - sa);
        }

        public static int Write(string path, int zoom)
        {
            zoom = Math.Max(1, Math.Min(4, zoom));
            var keys = RadialGlyphs.Keys;
            int w = Cols * Cell, h = keys.Length * Cell;
            var img = new float[w * h * 3];
            var states = new[] { GlyphState.Normal, GlyphState.Hot, GlyphState.Disabled };
            var bgs = new[] { Base, HotBg, Grey };
            var sizes = new[] { RadialGlyphs.RingBasePx, RadialGlyphs.ArcBasePx };
            for (int r = 0; r < keys.Length; r++)
                for (int c = 0; c < Cols; c++)
                {
                    int si = c / 2, px0 = sizes[c % 2];
                    var st = states[si];
                    int px = st == GlyphState.Hot ? RadialGlyphs.HotPx(px0) : px0;
                    var mask = NtdsIcons.RasterMask(Parsed(keys[r]), false, px, RadialGlyphs.StrokePx(px));
                    var tint = RadialGlyphs.Tint(st);
                    int ox = c * Cell + (Cell - px) / 2, oy = r * Cell + (Cell - px) / 2;
                    for (int y = 0; y < Cell; y++)
                        for (int x = 0; x < Cell; x++)
                        {
                            var p = new[] { Scene[0], Scene[1], Scene[2] };
                            Over(p, bgs[si], 1f);
                            int mx = c * Cell + x - ox, my = r * Cell + y - oy;
                            float sh = Sample(mask, px, mx - 1, my - 1);   // shadow: +1 px
                            Over(p, new[] { 0f, 0f, 0f, 1f }, sh * (float)RadialGlyphs.ShadowAlpha);
                            float g = Sample(mask, px, mx, my);
                            Over(p, new[] { tint[0], tint[1], tint[2], 1f }, g * tint[3]);
                            int o = ((r * Cell + y) * w + c * Cell + x) * 3;
                            img[o] = p[0]; img[o + 1] = p[1]; img[o + 2] = p[2];
                        }
                }
            int zw = w * zoom, zh = h * zoom;
            var rgba = new byte[zh * (zw * 4 + 1)];
            for (int y = 0; y < zh; y++)
            {
                int row = y * (zw * 4 + 1);
                rgba[row] = 0;
                for (int x = 0; x < zw; x++)
                {
                    int o = ((y / zoom) * w + x / zoom) * 3;
                    for (int k = 0; k < 3; k++) rgba[row + 1 + x * 4 + k] = (byte)Math.Round(Math.Max(0, Math.Min(1, img[o + k])) * 255);
                    rgba[row + 1 + x * 4 + 3] = 255;
                }
            }
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, Encode(zw, zh, rgba));
            Console.WriteLine("sheet " + zw + "x" + zh + " glyphs=" + keys.Length);
            Console.WriteLine("keys " + string.Join(",", keys));
            return 0;
        }

        private static List<double[]> Parsed(string key)
        {
            string err;
            return NtdsIcons.ParsePath(RadialGlyphs.Paths[key], out err);
        }

        private static float Sample(byte[] m, int px, int x, int y)
        {
            if (m == null || x < 0 || y < 0 || x >= px || y >= px) return 0f;
            return m[y * px + x] / 255f;
        }

        private static byte[] Encode(int w, int h, byte[] raw)
        {
            var ms = new MemoryStream();
            ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
            var ihdr = new byte[13];
            Be(ihdr, 0, w); Be(ihdr, 4, h); ihdr[8] = 8; ihdr[9] = 6;
            Chunk(ms, "IHDR", ihdr);
            var z = new MemoryStream();
            using (var zs = new ZLibStream(z, CompressionLevel.Optimal, true)) zs.Write(raw, 0, raw.Length);
            Chunk(ms, "IDAT", z.ToArray());
            Chunk(ms, "IEND", new byte[0]);
            return ms.ToArray();
        }

        private static void Be(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

        private static void Chunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4]; Be(len, 0, data.Length); s.Write(len, 0, 4);
            var td = new byte[4 + data.Length];
            for (int i = 0; i < 4; i++) td[i] = (byte)type[i];
            Array.Copy(data, 0, td, 4, data.Length);
            s.Write(td, 0, td.Length);
            var crc = new byte[4]; Be(crc, 0, unchecked((int)Crc32(td))); s.Write(crc, 0, 4);
        }

        private static uint[] _table;
        private static uint Crc32(byte[] d)
        {
            if (_table == null)
            {
                _table = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    _table[n] = c;
                }
            }
            uint crc = 0xFFFFFFFFu;
            foreach (byte b in d) crc = _table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }
    }
}
