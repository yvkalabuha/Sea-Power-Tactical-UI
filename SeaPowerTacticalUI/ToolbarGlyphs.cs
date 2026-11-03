using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-174 (ADR-0073 section 3): own stroke glyphs of the two game-panel buttons (ASW, CAP). Pure C# (BCL only): path data in the 22.5 grid,
    /// rasterised by <see cref="NtdsIcons.RasterMask"/>; ToolbarButtons turns the masks into 64x64 textures (normal / lit).
    /// </summary>
    internal static class ToolbarGlyphs
    {
        public const string Asw = "asw", Cap = "cap";
        public static readonly string[] Keys = { Asw, Cap };
        public const int TexPx = 64;

        /// <summary>RGBA 0xRRGGBBAA. Placeholders (Q2), calibrated in the live run L-T3.</summary>
        public static uint Normal = 0xC8D0D6FF, Lit = 0x8FD8FFFF;

        /// <summary>Key -> absolute path data (M L H V A C Z) in the 22.5 grid; every point lies in [2.25, 20.25] (30 px field, 3 px margin).</summary>
        public static readonly Dictionary<string, string> Paths = new Dictionary<string, string>
        {
            // submarine side view (hull capsule, sail, rudder fin) low, three sonar arcs up and to the right
            { Asw, "M 5 14.5 L 11.5 14.5 A 2.25 2.25 0 0 1 11.5 19 L 5 19 A 2.25 2.25 0 0 1 5 14.5 Z M 6.5 14.5 L 6.5 12.5 L 9 12.5 L 9 14.5 M 3.25 15 L 2.5 13 M 11.5 9 A 3 3 0 0 1 14.5 12 M 11.5 6.25 A 5.75 5.75 0 0 1 17.25 12 M 11.5 3.5 A 8.5 8.5 0 0 1 20 12" },
            // jet in plan view (swept wing, tail) on top, a dome shield open downwards with a vertical axis below
            { Cap, "M 11.25 2.5 L 11.25 10 M 5.5 8.25 L 11.25 4.75 L 17 8.25 M 8.5 10.25 L 11.25 8.75 L 14 10.25 M 3.25 20 A 8 8 0 0 1 19.25 20 M 11.25 13 V 20" },
        };

        private static readonly Dictionary<string, List<double[]>> Parsed = new Dictionary<string, List<double[]>>();
        private static readonly Dictionary<string, byte[]> Masks = new Dictionary<string, byte[]>();
        private static List<string> _errors;
        private static readonly object Gate = new object();

        /// <summary>Parses all paths once; failed keys are reported ("key: error"). Idempotent.</summary>
        public static List<string> Load()
        {
            lock (Gate)
            {
                if (_errors != null) return new List<string>(_errors);
                _errors = new List<string>();
                foreach (var kv in Paths)
                {
                    string err;
                    var p = NtdsIcons.ParsePath(kv.Value, out err);
                    if (p == null) _errors.Add(kv.Key + ": " + err); else Parsed[kv.Key] = p;
                }
                return new List<string>(_errors);
            }
        }

        public static bool Has(string key)
        {
            if (key == null) return false;
            Load();
            lock (Gate) return Parsed.ContainsKey(key);
        }

        public static double StrokePx(int px) { return 2.2 * px / 30.0; }

        /// <summary>px*px alpha (row 0 = top), cached per (key, px); null if no glyph or px &lt; 4.</summary>
        public static byte[] Mask(string key, int px)
        {
            if (!Has(key) || px < 4) return null;
            string ck = key + ":" + px.ToString(CultureInfo.InvariantCulture);
            lock (Gate)
            {
                byte[] m;
                if (Masks.TryGetValue(ck, out m)) return m;
                m = NtdsIcons.RasterMask(Parsed[key], false, px, StrokePx(px));
                Masks[ck] = m;
                return m;
            }
        }

        /// <summary>px*px*4 bytes for a Texture2D (RGBA32). flipY: Unity texture row 0 is the bottom, so the rows are written bottom-up.</summary>
        public static byte[] Rgba(byte[] mask, uint rgba, bool flipY)
        {
            if (mask == null) return null;
            int px = (int)Math.Round(Math.Sqrt(mask.Length));
            if (px * px != mask.Length) return null;
            byte r = (byte)(rgba >> 24), g = (byte)(rgba >> 16), b = (byte)(rgba >> 8), a = (byte)rgba;
            var o = new byte[mask.Length * 4];
            for (int y = 0; y < px; y++)
            {
                int sy = flipY ? px - 1 - y : y;
                for (int x = 0; x < px; x++)
                {
                    int i = (y * px + x) * 4;
                    o[i] = r; o[i + 1] = g; o[i + 2] = b;
                    o[i + 3] = (byte)(mask[sy * px + x] * a / 255);
                }
            }
            return o;
        }

        public static double Coverage(byte[] mask)
        {
            if (mask == null || mask.Length == 0) return 0;
            int n = 0;
            foreach (byte b in mask) if (b >= 128) n++;
            return (double)n / mask.Length;
        }

        public static double Difference(byte[] a, byte[] b)
        {
            int diff = 0, any = 0;
            for (int i = 0; i < a.Length; i++)
            {
                bool x = a[i] >= 128, y = b[i] >= 128;
                if (x || y) any++;
                if (x != y) diff++;
            }
            return any == 0 ? 0 : (double)diff / any;
        }

        private static string _fail;
        private static void Eq(string what, bool ok) { if (!ok && _fail == null) _fail = what; }

        public static string SelfTest()
        {
            _fail = null;
            Eq("two keys", Keys.Length == 2 && Paths.Count == 2 && Keys.All(k => Paths.ContainsKey(k)));
            var errs = Load();
            if (errs.Count > 0) return "parse: " + string.Join("; ", errs);
            foreach (var kv in Paths)
                if (kv.Value.Any(c => char.IsLower(c))) return kv.Key + ": relative command in the path";
            foreach (var k in Keys)
            {
                foreach (int px in new[] { 24, 30, 64 })
                {
                    double cov = Coverage(Mask(k, px));
                    Eq(k + " " + px + " px coverage " + cov.ToString("0.000", CultureInfo.InvariantCulture), cov >= 0.03 && cov <= 0.45);
                }
                lock (Gate)
                    foreach (var pl in Parsed[k])
                        for (int i = 0; i + 1 < pl.Length; i++)
                            Eq(k + " point outside the frame " + pl[i].ToString("0.##", CultureInfo.InvariantCulture), pl[i] >= 2.25 - 0.01 && pl[i] <= 20.25 + 0.01);
            }
            Eq("cache returns the same array", ReferenceEquals(Mask(Asw, 64), Mask(Asw, 64)));
            Eq("unknown key / tiny size", Mask("nope", 64) == null && !Has("nope") && !Has(null) && Mask(Asw, 3) == null);
            Eq("ASW differs from CAP", Difference(Mask(Asw, 64), Mask(Cap, 64)) >= 0.10);
            Eq("StrokePx(30)", Math.Abs(StrokePx(30) - 2.2) < 1e-9);
            var m2 = new byte[] { 255, 0, 0, 0 };
            var up = Rgba(m2, 0x112233FF, false); var fl = Rgba(m2, 0x112233FF, true);
            Eq("Rgba top row", up != null && up[0] == 0x11 && up[1] == 0x22 && up[2] == 0x33 && up[3] == 255 && up[7] == 0);
            Eq("Rgba flipY puts the top row last", fl != null && fl[3] == 0 && fl[(2 * 1) * 4 + 3] == 255);
            Eq("Rgba bad length", Rgba(new byte[3], 0, false) == null);
            return _fail;
        }
    }
}
