using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoiceBotBridge
{
    internal enum GlyphState { Normal, Hot, Disabled }

    /// <summary>
    /// Own stroke glyphs of the radial ring (T-140, ADR-0047). Pure C# (BCL only): path data in the 22.5 grid, rasterised by
    /// <see cref="NtdsIcons.RasterMask"/>; Radial.cs turns the alpha masks into textures. The drawings are ours (GPL-3.0 of the mod).
    /// </summary>
    internal static class RadialGlyphs
    {
        public const int RingBasePx = 36, ArcBasePx = 28, MinPx = 16;
        public const double HotGrow = 1.15, ShadowAlpha = 0.6;

        /// <summary>Glyph keys in the order of ADR-0047 section 3 (also the row order of the preview sheet).</summary>
        public static readonly string[] Keys =
        {
            "torpedo_alert", "break_contact", "cease_all", "release_control", "sub_smart_attack", "auto_evade", "veto_neutral",
            "radar_coverage", "overview", "post_launch", "air_defense_up", "silent_run", "decoy_salvo", "evade_attack", "more", "group",
            "formation_return", "air_rtb",
            // T-328 (ADR-0127): "+" / "-" sectors and the group kinds of the "+" arc
            "group_add", "group_remove", "grp_cap", "grp_asw", "grp_strike", "grp_sar"
        };

        private static string N(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }

        /// <summary>Full circle as two arcs (absolute commands only).</summary>
        private static string Circ(double cx, double cy, double r)
        {
            return "M " + N(cx - r) + " " + N(cy) + " A " + N(r) + " " + N(r) + " 0 1 1 " + N(cx + r) + " " + N(cy)
                 + " A " + N(r) + " " + N(r) + " 0 1 1 " + N(cx - r) + " " + N(cy);
        }

        /// <summary>Eight gear teeth: radial strokes between r0 and r1 every 45 degrees around (11.25, 11.25).</summary>
        private static string Teeth(double r0, double r1)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 8; i++)
            {
                double a = i * Math.PI / 4;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append("M ").Append(N(11.25 + r0 * Math.Cos(a))).Append(' ').Append(N(11.25 + r0 * Math.Sin(a)))
                  .Append(" L ").Append(N(11.25 + r1 * Math.Cos(a))).Append(' ').Append(N(11.25 + r1 * Math.Sin(a)));
            }
            return sb.ToString();
        }

        /// <summary>Key -> absolute path data (M L H V A C Z) in the 22.5 grid, field 2..20.5.</summary>
        public static readonly Dictionary<string, string> Paths = new Dictionary<string, string>
        {
            // torpedo (capsule + tail fin) with two alarm waves
            { "torpedo_alert", "M 2.5 8.75 L 10.5 8.75 A 2.5 2.5 0 0 1 10.5 13.75 L 2.5 13.75 Z M 2.5 6.5 L 2.5 16 M 14.5 7 A 5 5 0 0 1 14.5 15.5 M 17.5 4 A 8.5 8.5 0 0 1 17.5 18.5" },
            // from the dot: a long arrow away
            { "break_contact", Circ(5, 16.5, 1.5) + " M 8.5 13.5 L 19 3.5 M 13.5 3.5 L 19 3.5 L 19 9" },
            // crosshair crossed out
            { "cease_all", Circ(11.25, 11.25, 5.5) + " M 11.25 2 V 6.5 M 11.25 16 V 20.5 M 2 11.25 H 6.5 M 16 11.25 H 20.5 M 3.5 19.5 L 19.5 3.5" },
            // wheel hub with a 270-degree return arrow
            { "release_control", "M 11.25 3.5 A 7.75 7.75 0 1 1 3.5 11.25 M 1 13.75 L 3.5 11.25 L 6 13.75 " + Circ(11.25, 11.25, 2) },
            // dog-leg path ending in a diamond target
            { "sub_smart_attack", "M 3 19 L 8 10 L 14 10 M 11.5 7.5 L 14 10 L 11.5 12.5 M 17.5 6.5 L 20.25 9.25 L 17.5 12 L 14.75 9.25 Z" },
            // arrow-hull with a curved wake to the side
            { "auto_evade", "M 15 3 L 18.5 9 L 16.5 9 L 16.5 12.5 L 13.5 12.5 L 13.5 9 L 11.5 9 Z M 15 12.5 C 15 17 8.5 15.5 7.5 20.25" },
            // shield with the NTDS neutral square
            { "veto_neutral", "M 11.25 2.5 L 18.5 5 L 18.5 11 C 18.5 15.5 15 18.5 11.25 20.25 C 7.5 18.5 4 15.5 4 11 L 4 5 Z M 8.25 8.25 H 14.25 V 14.25 H 8.25 Z" },
            // mast and two concentric half circles
            { "radar_coverage", "M 11.25 19.5 V 13.5 M 5 13.5 A 6.25 6.25 0 0 1 17.5 13.5 M 2.25 13.5 A 9 9 0 0 1 20.25 13.5" },
            // list: bullets and three lines
            { "overview", Circ(4.5, 5.5, 1.25) + " " + Circ(4.5, 11.25, 1.25) + " " + Circ(4.5, 17, 1.25) + " M 8.5 5.5 H 20 M 8.5 11.25 H 20 M 8.5 17 H 20" },
            // missile that turns back
            { "post_launch", "M 3.5 18 L 12.5 9 M 8.5 8.5 L 13 8.5 L 13 13 M 13 8.5 A 5.5 5.5 0 0 1 17 18 M 14.5 15.5 L 17 18.5 L 19.5 15.5" },
            // NTDS air half circle over an up arrow
            { "air_defense_up", "M 4.5 10 A 6.75 6.75 0 0 1 18 10 M 4.5 10 H 18 M 11.25 20.25 V 13.5 M 8.5 16 L 11.25 13.25 L 14 16" },
            // sound wave, crossed out
            { "silent_run", "M 2.5 11.25 C 4.25 7 6.25 7 8 11.25 C 9.75 15.5 11.75 15.5 13.5 11.25 C 15.25 7 17.25 7 19 11.25 M 3 18.5 L 19.5 4" },
            // fan of three strokes from a dot
            { "decoy_salvo", Circ(4.5, 17.5, 1.5) + " M 7 15 L 12.5 5.5 M 8 16 L 18.5 10 M 8.5 18.5 L 20 17.5" },
            // aircraft chevron and a turn-away arc
            { "evade_attack", "M 4 20 L 7.5 12 L 11 20 L 7.5 17.5 Z M 9.5 9.5 A 8 8 0 0 1 19 4 M 14.5 3 L 19.5 4 L 18 8.5" },
            // three dots
            { "more", Circ(4.5, 11.25, 1.5) + " " + Circ(11.25, 11.25, 1.5) + " " + Circ(18, 11.25, 1.5) },
            // gear: rim, eight teeth
            { "group", Circ(11.25, 11.25, 5.25) + " " + Teeth(5.25, 8.75) + " " + Circ(11.25, 11.25, 1.75) },
            // T-243: wedge of two ships, an empty slot beside them and an arrow into it
            { "formation_return", Circ(11.25, 4.5, 1.5) + " " + Circ(5, 12, 1.5) + " " + Circ(17.5, 12, 1.6) + " M 17.5 20.25 V 14.2 M 15.3 16.4 L 17.5 14.2 L 19.7 16.4" },
            // T-243: aircraft chevron, an arc with an arrow to a short deck line
            { "air_rtb", "M 3.5 11 L 6.5 3.5 L 9.5 11 L 6.5 9 Z M 11 5 A 8 8 0 0 1 17 14 M 14.5 12.5 L 17 14.5 L 19 11.8 M 11.5 19.5 H 20.5" },
            // T-328: a row of three dots (the formation) above a plus
            { "group_add", Circ(4.5, 6, 1.5) + " " + Circ(11.25, 6, 1.5) + " " + Circ(18, 6, 1.5) + " M 11.25 11.5 V 20 M 6.75 15.75 H 15.75" },
            // T-328: a minus above a row of three dots
            { "group_remove", "M 6.25 6 H 16.25 " + Circ(4.5, 16.5, 1.5) + " " + Circ(11.25, 16.5, 1.5) + " " + Circ(18, 16.5, 1.5) },
            // T-328: swept-wing fighter, top view (CAP group)
            { "grp_cap", "M 11.25 2.5 L 12.75 8 L 20 13.5 L 20 15.5 L 12.5 14 L 12 18 L 14.5 19.5 L 14.5 20.5 L 11.25 19.8 L 8 20.5 L 8 19.5 L 10.5 18 L 10 14 L 2.5 15.5 L 2.5 13.5 L 9.75 8 Z" },
            // T-328: periscope over a wave (ASW group)
            { "grp_asw", "M 8.5 14.5 V 4.5 H 14.5 V 8 M 2.5 16.5 C 5 14 7 14 9.5 16.5 C 12 19 14 19 16.5 16.5 C 18 15 19.5 15 20.5 16.5" },
            // T-328: torpedo head down with a strike arrow (Strike group)
            { "grp_strike", "M 9 3 H 13.5 V 10 A 2.25 2.25 0 0 1 9 10 Z M 7 3 H 15.5 M 11.25 15 V 20.5 M 8.5 17.8 L 11.25 20.5 L 14 17.8" },
            // T-328: lifebuoy (SAR group)
            { "grp_sar", Circ(11.25, 11.25, 8) + " " + Circ(11.25, 11.25, 3.75) + " M 5.6 5.6 L 8.6 8.6 M 16.9 5.6 L 13.9 8.6 M 5.6 16.9 L 8.6 13.9 M 16.9 16.9 L 13.9 13.9" },
        };

        private static readonly Dictionary<string, List<double[]>> Parsed = new Dictionary<string, List<double[]>>();
        private static readonly Dictionary<string, byte[]> Masks = new Dictionary<string, byte[]>();
        private static List<string> _errors;
        private static readonly object Gate = new object();

        /// <summary>Parses all paths once; failed keys are dropped and reported ("key: error"). Idempotent.</summary>
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

        /// <summary>icon -> id -> "more" for Id=="more" / "group" for other groups -> null (draw the short text). Value nodes -> null.</summary>
        public static string KeyFor(MenuNode n)
        {
            if (n == null || n.Kind == "value") return null;
            if (n.Icon != null && Has(n.Icon)) return n.Icon;
            if (n.Id != null && Has(n.Id)) return n.Id;
            if (n.Kind == "group") return n.Id == "more" ? "more" : "group";
            return null;
        }

        public static int RingPx(double scale) { return Math.Max(MinPx, (int)Math.Round(RingBasePx * scale)); }
        public static int ArcPx(double scale) { return Math.Max(MinPx, (int)Math.Round(ArcBasePx * scale)); }
        public static int HotPx(int px) { return (int)Math.Round(px * HotGrow); }
        public static double StrokePx(int px) { return Math.Max(1.5, 2.0 * px / 36.0); }

        /// <summary>RGBA 0..1 of the glyph colour (GUI.color): Normal (0.88,0.92,0.98,1), Hot (1,1,1,1), Disabled (0.6,0.6,0.6,0.55).</summary>
        public static float[] Tint(GlyphState s)
        {
            switch (s)
            {
                case GlyphState.Hot: return new[] { 1f, 1f, 1f, 1f };
                case GlyphState.Disabled: return new[] { 0.6f, 0.6f, 0.6f, 0.55f };
                default: return new[] { 0.88f, 0.92f, 0.98f, 1f };
            }
        }

        public static GlyphState StateOf(MenuNode n, bool hot)
        {
            if (n != null && !n.Enabled) return GlyphState.Disabled;
            return hot ? GlyphState.Hot : GlyphState.Normal;
        }

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

        /// <summary>Share of pixels with alpha >= 128 (0..1).</summary>
        public static double Coverage(byte[] mask)
        {
            if (mask == null || mask.Length == 0) return 0;
            int n = 0;
            foreach (byte b in mask) if (b >= 128) n++;
            return (double)n / mask.Length;
        }

        /// <summary>Share of differing pixels (alpha >= 128 in exactly one mask) among the pixels set in either mask.</summary>
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
            Eq("24 keys", Keys.Length == 24 && Paths.Count == 24 && Keys.All(k => Paths.ContainsKey(k)));
            var errs = Load();
            if (errs.Count > 0) return "parse: " + string.Join("; ", errs);
            foreach (var kv in Paths)
                if (kv.Value.Any(c => char.IsLower(c))) return kv.Key + ": relative command in the path";
            foreach (var k in Keys)
            {
                foreach (int px in new[] { 36, 28 })
                {
                    double cov = Coverage(Mask(k, px));
                    Eq(k + " " + px + " px coverage " + cov.ToString("0.000", CultureInfo.InvariantCulture), cov >= 0.03 && cov <= 0.40);
                }
            }
            Eq("cache returns the same array", ReferenceEquals(Mask("more", 36), Mask("more", 36)));
            Eq("unknown key", Mask("nope", 36) == null && !Has("nope") && !Has(null));
            Eq("tiny size", Mask("more", 3) == null);
            Eq("RingPx(1)", RingPx(1) == 36); Eq("RingPx(1.5)", RingPx(1.5) == 54); Eq("ArcPx(0.4) min", ArcPx(0.4) == 16);
            Eq("HotPx(36)", HotPx(36) == 41); Eq("StrokePx(36)", Math.Abs(StrokePx(36) - 2) < 1e-9); Eq("StrokePx(18)", Math.Abs(StrokePx(18) - 1.5) < 1e-9);
            Eq("Tint", Tint(GlyphState.Normal)[0] == 0.88f && Tint(GlyphState.Hot)[3] == 1f && Tint(GlyphState.Disabled)[3] == 0.55f);
            Eq("StateOf", StateOf(new MenuNode { Enabled = false }, true) == GlyphState.Disabled && StateOf(new MenuNode(), true) == GlyphState.Hot && StateOf(new MenuNode(), false) == GlyphState.Normal);
            for (int i = 0; i < Keys.Length; i++)
                for (int j = i + 1; j < Keys.Length; j++)
                    Eq("glyphs differ " + Keys[i] + "/" + Keys[j], Difference(Mask(Keys[i], 36), Mask(Keys[j], 36)) >= 0.10);
            return _fail;
        }
    }
}
