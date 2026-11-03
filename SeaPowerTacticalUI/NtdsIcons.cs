using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace VoiceBotBridge
{
    /// <summary>Contact-icon geometries of the game's NTDS theme, flattened to polylines in the 22.5 x 22.5 icon grid (y down). T-070.</summary>
    internal sealed class NtdsSet
    {
        public Dictionary<string, List<double[]>> Paths = new Dictionary<string, List<double[]>>();   // "NTDS.Enemy.Air" -> [x0,y0,x1,y1,...] polylines
        public Dictionary<string, uint> Colours = new Dictionary<string, uint>();                       // "NTDS.HostileColour" -> 0xAARRGGBB
        public List<string> Errors = new List<string>();                                                // keys whose path could not be parsed (+ reason)
    }

    /// <summary>
    /// Pure (no Unity) reader of the game's vector contact icons. The game stores SeaPower.Theme.NTDS.xaml as plain text in
    /// globalgamemanagers.assets (Unity string = int32 LE length + UTF-8); the mod only reads that file, nothing is copied.
    /// </summary>
    internal static class NtdsIcons
    {
        public const double Grid = 22.5;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private const string GeoMark = "<Geometry x:Key=\"NTDS.";
        private const string DictOpen = "<ResourceDictionary";
        private const string DictClose = "</ResourceDictionary>";

        // ---------------------------------------------------------------- file / dictionary

        private static int IndexOf(byte[] hay, byte[] needle, int from, int to)
        {
            int last = Math.Min(to, hay.Length) - needle.Length;
            for (int i = Math.Max(0, from); i <= last; i++)
            {
                if (hay[i] != needle[0]) continue;
                int j = 1;
                while (j < needle.Length && hay[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }

        private static int LastIndexOf(byte[] hay, byte[] needle, int before)
        {
            for (int i = Math.Min(before, hay.Length - needle.Length); i >= 0; i--)
            {
                if (hay[i] != needle[0]) continue;
                int j = 1;
                while (j < needle.Length && hay[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }

        /// <summary>Text of the &lt;ResourceDictionary&gt; that holds the NTDS geometries, or null (error says why).</summary>
        public static string ExtractDictionary(byte[] assets, out string error)
        {
            error = null;
            if (assets == null || assets.Length < 64) { error = "assets empty"; return null; }
            var geo = Encoding.ASCII.GetBytes(GeoMark);
            int gi = IndexOf(assets, geo, 0, assets.Length);
            if (gi < 0) { error = "no NTDS geometry found"; return null; }
            int start = LastIndexOf(assets, Encoding.ASCII.GetBytes(DictOpen), gi);
            if (start < 0) { error = "no <ResourceDictionary before the geometry"; return null; }
            var close = Encoding.ASCII.GetBytes(DictClose);
            // normal path: the int32 length that precedes the text
            if (start >= 4)
            {
                int n = assets[start - 4] | (assets[start - 3] << 8) | (assets[start - 2] << 16) | (assets[start - 1] << 24);
                if (n > geo.Length && start + n <= assets.Length && n < 4 * 1024 * 1024)
                {
                    string text = Encoding.UTF8.GetString(assets, start, n);
                    string tr = text.TrimEnd();
                    if (tr.EndsWith(DictClose, StringComparison.Ordinal) && text.IndexOf(GeoMark, StringComparison.Ordinal) >= 0) return text;
                }
            }
            // fallback: up to the closing tag
            int end = IndexOf(assets, close, gi, assets.Length);
            if (end < 0) { error = "no closing </ResourceDictionary>"; return null; }
            return Encoding.UTF8.GetString(assets, start, end + close.Length - start);
        }

        public static NtdsSet ParseDictionary(string xaml)
        {
            var set = new NtdsSet();
            if (string.IsNullOrEmpty(xaml)) { set.Errors.Add("empty dictionary"); return set; }
            string clean = Regex.Replace(xaml, "<!--.*?-->", "", RegexOptions.Singleline);
            foreach (Match m in Regex.Matches(clean, "<SolidColorBrush\\s+x:Key=\"([^\"]+)\"\\s+Color=\"([^\"]+)\""))
            {
                uint c = ParseColour(m.Groups[2].Value);
                if (c != 0) set.Colours[m.Groups[1].Value] = c;
            }
            foreach (Match m in Regex.Matches(clean, "<Geometry\\s+x:Key=\"([^\"]+)\"[^>]*>(.*?)</Geometry>", RegexOptions.Singleline))
            {
                string key = m.Groups[1].Value, err;
                var path = ParsePath(m.Groups[2].Value, out err);
                if (path == null || path.Count == 0) set.Errors.Add(key + ": " + (err ?? "empty"));
                else set.Paths[key] = path;
            }
            return set;
        }

        public static NtdsSet LoadFromFile(string path, out string error)
        {
            error = null;
            try
            {
                byte[] data;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    data = new byte[fs.Length];
                    int got = 0;
                    while (got < data.Length) { int r = fs.Read(data, got, data.Length - got); if (r <= 0) break; got += r; }
                }
                string text = ExtractDictionary(data, out error);
                if (text == null) return null;
                var set = ParseDictionary(text);
                if (set.Paths.Count == 0) { error = "no geometry parsed" + (set.Errors.Count > 0 ? ": " + set.Errors[0] : ""); return null; }
                return set;
            }
            catch (Exception e) { error = e.GetType().Name + ": " + e.Message; return null; }
        }

        // ---------------------------------------------------------------- path data

        private sealed class Lexer
        {
            private readonly string _s; private int _i;
            public Lexer(string s) { _s = s; }
            private void Skip() { while (_i < _s.Length && (char.IsWhiteSpace(_s[_i]) || _s[_i] == ',')) _i++; }
            public bool End { get { Skip(); return _i >= _s.Length; } }
            public bool PeekIsLetter { get { Skip(); return _i < _s.Length && char.IsLetter(_s[_i]); } }
            public char Letter() { Skip(); return _s[_i++]; }
            public bool Number(out double v)
            {
                Skip(); v = 0;
                int st = _i;
                if (_i < _s.Length && (_s[_i] == '-' || _s[_i] == '+')) _i++;
                bool digits = false;
                while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) { digits = true; _i++; }
                if (digits && _i < _s.Length && (_s[_i] == 'e' || _s[_i] == 'E'))
                {
                    int save = _i; _i++;
                    if (_i < _s.Length && (_s[_i] == '-' || _s[_i] == '+')) _i++;
                    bool ed = false;
                    while (_i < _s.Length && char.IsDigit(_s[_i])) { ed = true; _i++; }
                    if (!ed) _i = save;
                }
                if (!digits) { _i = st; return false; }
                return double.TryParse(_s.Substring(st, _i - st), NumberStyles.Float, Inv, out v);
            }
        }

        private static int Args(char c)
        {
            switch (c) { case 'M': case 'L': return 2; case 'H': case 'V': return 1; case 'A': return 7; case 'C': return 6; case 'Z': return 0; }
            return -1;
        }

        /// <summary>M L H V A C Z (absolute; a coordinate pair after M/L repeats as L). Polylines are flattened [x0,y0,x1,y1,...]; null on error.</summary>
        public static List<double[]> ParsePath(string data, out string error)
        {
            error = null;
            if (data == null) { error = "null"; return null; }
            var lx = new Lexer(data);
            var res = new List<double[]>();
            var cur = new List<double>();
            double x = 0, y = 0, sx = 0, sy = 0;
            bool haveStart = false, closed = false;
            char cmd = ' ';
            Action flush = () => { if (cur.Count >= 4) res.Add(cur.ToArray()); cur = new List<double>(); };
            while (!lx.End)
            {
                if (lx.PeekIsLetter)
                {
                    char c = lx.Letter();
                    if (c == 'z') c = 'Z';
                    if (c != 'Z' && Args(char.ToUpperInvariant(c)) < 0) { error = "bad command '" + c + "'"; return null; }
                    if (char.IsLower(c)) { error = "unsupported command '" + c + "'"; return null; }
                    if (Args(c) < 0) { error = "unsupported command '" + c + "'"; return null; }
                    cmd = c;
                    if (c == 'Z')
                    {
                        if (!haveStart || cur.Count < 2) { error = "Z without a path"; return null; }
                        cur.Add(sx); cur.Add(sy);
                        flush();
                        x = sx; y = sy; closed = true;
                        continue;
                    }
                }
                else if (cmd == ' ' || cmd == 'Z') { error = "number without a command"; return null; }
                int n = Args(cmd);
                var a = new double[n];
                for (int k = 0; k < n; k++) if (!lx.Number(out a[k])) { error = "bad number for '" + cmd + "'"; return null; }
                if (cmd != 'M' && !haveStart) { error = "path does not start with M"; return null; }
                if (closed && cmd != 'M') { cur.Add(x); cur.Add(y); closed = false; }   // a drawing command after Z starts at the subpath start
                switch (cmd)
                {
                    case 'M':
                        flush();
                        x = a[0]; y = a[1]; sx = x; sy = y; haveStart = true; closed = false;
                        cur.Add(x); cur.Add(y);
                        cmd = 'L';   // implicit lineto for further pairs
                        break;
                    case 'L': x = a[0]; y = a[1]; cur.Add(x); cur.Add(y); break;
                    case 'H': x = a[0]; cur.Add(x); cur.Add(y); break;
                    case 'V': y = a[0]; cur.Add(x); cur.Add(y); break;
                    case 'A': Arc(cur, x, y, a[0], a[1], a[2], a[3] != 0, a[4] != 0, a[5], a[6]); x = a[5]; y = a[6]; break;
                    case 'C':
                        for (int s = 1; s <= 8; s++)
                        {
                            double t = s / 8.0, u = 1 - t;
                            cur.Add(u * u * u * x + 3 * u * u * t * a[0] + 3 * u * t * t * a[2] + t * t * t * a[4]);
                            cur.Add(u * u * u * y + 3 * u * u * t * a[1] + 3 * u * t * t * a[3] + t * t * t * a[5]);
                        }
                        x = a[4]; y = a[5];
                        break;
                }
            }
            flush();
            if (res.Count == 0) { error = "no segments"; return null; }
            return res;
        }

        /// <summary>SVG 1.1 F.6.5 endpoint -> centre parameterisation, F.6.6 radius correction; appends points (start point already in cur).</summary>
        private static void Arc(List<double> cur, double x1, double y1, double rx, double ry, double phiDeg, bool large, bool sweep, double x2, double y2)
        {
            rx = Math.Abs(rx); ry = Math.Abs(ry);
            if ((x1 == x2 && y1 == y2)) return;
            if (rx == 0 || ry == 0) { cur.Add(x2); cur.Add(y2); return; }
            double phi = phiDeg * Math.PI / 180.0, cp = Math.Cos(phi), sp = Math.Sin(phi);
            double dx = (x1 - x2) / 2, dy = (y1 - y2) / 2;
            double x1p = cp * dx + sp * dy, y1p = -sp * dx + cp * dy;
            double lam = x1p * x1p / (rx * rx) + y1p * y1p / (ry * ry);
            if (lam > 1) { double s = Math.Sqrt(lam); rx *= s; ry *= s; }
            double num = rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p;
            double den = rx * rx * y1p * y1p + ry * ry * x1p * x1p;
            double co = den == 0 ? 0 : Math.Sqrt(Math.Max(0, num / den));
            if (large == sweep) co = -co;
            double cxp = co * rx * y1p / ry, cyp = -co * ry * x1p / rx;
            double cx = cp * cxp - sp * cyp + (x1 + x2) / 2, cy = sp * cxp + cp * cyp + (y1 + y2) / 2;
            double th1 = Math.Atan2((y1p - cyp) / ry, (x1p - cxp) / rx);
            double th2 = Math.Atan2((-y1p - cyp) / ry, (-x1p - cxp) / rx);
            double dth = th2 - th1;
            if (!sweep && dth > 0) dth -= 2 * Math.PI;
            else if (sweep && dth < 0) dth += 2 * Math.PI;
            int steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(dth) / (10.0 * Math.PI / 180.0)));
            for (int i = 1; i <= steps; i++)
            {
                double th = i == steps ? th2 : th1 + dth * i / steps;
                if (i == steps) { cur.Add(x2); cur.Add(y2); continue; }
                double ex = rx * Math.Cos(th), ey = ry * Math.Sin(th);
                cur.Add(cp * ex - sp * ey + cx);
                cur.Add(sp * ex + cp * ey + cy);
            }
        }

        // ---------------------------------------------------------------- pair -> geometry key

        private static readonly HashSet<string> WithDot = new HashSet<string> { "Air", "Helicopter", "Surface", "Submarine", "Airbase", "Installation", "MobileUnit", "AAA", "MobileAAA", "SAM", "Radar", "Port", "Bridge" };
        private static readonly HashSet<string> NoDot = new HashSet<string> { "Missile", "Torpedo", "ActiveSonobouy", "PassiveSonobouy" };
        private static readonly HashSet<string> Structures = new HashSet<string> { "Airbase", "Installation", "MobileUnit", "AAA", "MobileAAA", "SAM", "Radar", "Port", "Bridge" };

        private static string TypeKey(string visualType)
        {
            if (visualType == null) return null;
            if (visualType == "Biologic") return "Submarine";
            return WithDot.Contains(visualType) || NoDot.Contains(visualType) ? visualType : null;
        }

        private static string PostureKey(string posture)
        {
            switch (posture) { case "Enemy": case "Unknown": case "Neutral": case "Allied": return posture; }
            return null;
        }

        /// <summary>"NTDS.&lt;Posture&gt;.&lt;Type&gt;" (Biologic uses Submarine); null = the game has no template for the pair.</summary>
        public static string KeyFor(string visualType, string posture, out bool centreDot)
        {
            centreDot = false;
            string t = TypeKey(visualType), p = PostureKey(posture);
            if (t == null || p == null) return null;
            centreDot = WithDot.Contains(t);
            return "NTDS." + p + "." + t;
        }

        /// <summary>Fallback texture name under Resources ui/minimap/minimap_icons/ (minimap.ini [NTDS]), or null.</summary>
        public static string ResourceName(string visualType, string posture)
        {
            string t = TypeKey(visualType), p = PostureKey(posture);
            if (t == null || p == null) return null;
            string side = p == "Enemy" ? "enemy" : p == "Unknown" ? "unknown" : p == "Neutral" ? "neutral" : "friendly";
            if (t == "Air") return side + "_air";
            if (t == "Helicopter") return "helo";
            if (t == "Surface") return side + "_surface";
            if (t == "Submarine") return side + "_sub";
            if (t == "Missile") return "enemy_missile";
            if (t == "Torpedo") return "enemy_torpedo";
            if (Structures.Contains(t)) return "structure";
            return null;
        }

        // ---------------------------------------------------------------- colours

        /// <summary>"#FFC000", "#80FFC000", "Red", "Green" (WPF = #008000), "Blue"...; 0 = not understood.</summary>
        public static uint ParseColour(string spec)
        {
            if (string.IsNullOrEmpty(spec)) return 0;
            spec = spec.Trim();
            if (spec[0] == '#')
            {
                uint v;
                if (!uint.TryParse(spec.Substring(1), NumberStyles.AllowHexSpecifier, Inv, out v)) return 0;
                if (spec.Length == 7) return 0xFF000000u | v;
                if (spec.Length == 9) return v;
                return 0;
            }
            switch (spec.ToLowerInvariant())
            {
                case "red": return 0xFFFF0000u;
                case "green": return 0xFF008000u;
                case "blue": return 0xFF0000FFu;
                case "yellow": return 0xFFFFFF00u;
                case "white": return 0xFFFFFFFFu;
                case "black": return 0xFF000000u;
                case "orange": return 0xFFFFA500u;
                case "gray": case "grey": return 0xFF808080u;
            }
            return 0;
        }

        public static uint PostureColour(NtdsSet set, string posture)
        {
            string key; uint fallback;
            switch (posture)
            {
                case "Enemy": key = "NTDS.HostileColour"; fallback = 0xFFFF0000u; break;
                case "Unknown": key = "NTDS.UnknownColour"; fallback = 0xFFFFC000u; break;
                case "Neutral": key = "NTDS.NeutralColour"; fallback = 0xFF008000u; break;
                case "Allied": key = "NTDS.FriendColour"; fallback = 0xFF0000FFu; break;
                default: return 0xFFFFC000u;
            }
            uint c;
            return set != null && set.Colours.TryGetValue(key, out c) ? c : fallback;
        }

        // ---------------------------------------------------------------- own symbols (fallback, grid 22.5)

        private static double[] Poly(params double[] p) { return p; }

        /// <summary>Own NTDS-like symbols used when neither the game's geometry nor a texture is available.</summary>
        public static List<double[]> OwnSymbol(string visualType, string posture)
        {
            var r = new List<double[]>();
            string t = TypeKey(visualType);
            switch (t)
            {
                case "Air": r.Add(Poly(2, 11.25, 11.25, 2, 20.5, 11.25)); r.Add(Poly(9.5, 11.25, 13, 11.25)); break;
                case "Helicopter": r.Add(Poly(2, 11.25, 11.25, 2, 20.5, 11.25)); r.Add(Poly(5, 15, 17.5, 15)); break;
                case "Surface": r.Add(Poly(2, 11.25, 11.25, 2, 20.5, 11.25, 11.25, 20.5, 2, 11.25)); break;
                case "Submarine": r.Add(Poly(2, 11.25, 11.25, 20.5, 20.5, 11.25)); r.Add(Poly(9.5, 11.25, 13, 11.25)); break;
                case "Missile": r.Add(Poly(11.25, 3, 17, 18, 11.25, 14.5, 5.5, 18, 11.25, 3)); break;
                case "Torpedo": r.Add(Poly(11.25, 3, 11.25, 19)); r.Add(Poly(7.5, 7.5, 11.25, 3, 15, 7.5)); break;
                case "ActiveSonobouy": case "PassiveSonobouy": r.Add(Poly(11.25, 3, 11.25, 19)); r.Add(Poly(6, 11.25, 16.5, 11.25)); break;
                case null:
                    r.Add(Poly(2, 2, 20.5, 2, 20.5, 20.5, 2, 20.5, 2, 2));
                    r.Add(Poly(8, 8, 8, 6.5, 14.5, 6.5, 14.5, 10.5, 11.25, 12.5, 11.25, 14.5));
                    r.Add(Poly(11.25, 17, 11.25, 17.5));
                    break;
                default: r.Add(Poly(2, 20.5, 2, 6, 11.25, 2, 20.5, 6, 20.5, 20.5, 2, 20.5)); break;   // structures
            }
            return r;
        }

        // ---------------------------------------------------------------- rasterising (T-080: textures instead of GL)

        private static double SegDist(double px, double py, double x0, double y0, double x1, double y1)
        {
            double dx = x1 - x0, dy = y1 - y0, l2 = dx * dx + dy * dy;
            double t = l2 < 1e-12 ? 0 : ((px - x0) * dx + (py - y0) * dy) / l2;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            double qx = x0 + t * dx - px, qy = y0 + t * dy - py;
            return Math.Sqrt(qx * qx + qy * qy);
        }

        private static byte Alpha(double cover) { return (byte)Math.Round(255 * Math.Max(0, Math.Min(1, cover))); }

        /// <summary>px*px alpha 0..255, row 0 = top; polylines in the 22.5 grid scaled to px; null when px &lt; 4 or paths == null.</summary>
        public static byte[] RasterMask(List<double[]> paths, bool centreDot, int px, double strokePx)
        {
            if (paths == null || px < 4) return null;
            double k = px / Grid, half = Math.Max(0.5, strokePx) / 2;
            var m = new byte[px * px];
            for (int y = 0; y < px; y++)
                for (int x = 0; x < px; x++)
                {
                    double cx = x + 0.5, cy = y + 0.5, best = double.MaxValue;
                    foreach (var pl in paths)
                        for (int i = 0; i + 3 < pl.Length; i += 2)
                            best = Math.Min(best, SegDist(cx, cy, pl[i] * k, pl[i + 1] * k, pl[i + 2] * k, pl[i + 3] * k));
                    double cover = best == double.MaxValue ? 0 : half + 0.5 - best;
                    if (centreDot)
                    {
                        double dd = Math.Sqrt((cx - 11.25 * k) * (cx - 11.25 * k) + (cy - 11.25 * k) * (cy - 11.25 * k));
                        cover = Math.Max(cover, 1.5 * k + 0.5 - dd);
                    }
                    m[y * px + x] = Alpha(cover);
                }
            return m;
        }

        private static double TriCover(double x, double y, double ax, double ay, double bx, double by, double cx, double cy)
        {
            int n = 0;
            for (int sy = 0; sy < 2; sy++)
                for (int sx = 0; sx < 2; sx++)
                {
                    double qx = x + 0.25 + 0.5 * sx, qy = y + 0.25 + 0.5 * sy;
                    double d1 = (qx - bx) * (ay - by) - (ax - bx) * (qy - by);
                    double d2 = (qx - cx) * (by - cy) - (bx - cx) * (qy - cy);
                    double d3 = (qx - ax) * (cy - ay) - (cx - ax) * (qy - ay);
                    bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
                    if (!(neg && pos)) n++;
                }
            return n / 4.0;
        }

        /// <summary>Filled triangles: +1 one down, +2 two down (stacked), -1 one up, 0 small square; other values -> null.</summary>
        public static byte[] ArrowMask(int arrow, int px)
        {
            if (px < 4 || arrow < -1 || arrow > 2) return null;
            var m = new byte[px * px];
            double p = px;
            for (int y = 0; y < px; y++)
                for (int x = 0; x < px; x++)
                {
                    double c = 0;
                    switch (arrow)
                    {
                        case 1: c = TriCover(x, y, 0, 0, p, 0, p / 2, p); break;
                        case 2:
                            c = Math.Max(TriCover(x, y, 0, 0, p, 0, p / 2, 0.6 * p), TriCover(x, y, 0, 0.4 * p, p, 0.4 * p, p / 2, p));
                            break;
                        case -1: c = TriCover(x, y, 0, p, p, p, p / 2, 0); break;
                        case 0:
                            {
                                double a = p / 2 - Math.Max(1, p * 0.12), b = p / 2 + Math.Max(1, p * 0.12);
                                c = (x + 1 > a && x < b && y + 1 > a && y < b) ? 1 : 0;
                                break;
                            }
                    }
                    m[y * px + x] = Alpha(c);
                }
            return m;
        }

        // ---------------------------------------------------------------- self test

        private static string _fail;
        private static void Eq(string name, bool ok) { if (!ok && _fail == null) _fail = name; }
        private static bool Near(double a, double b, double tol) { return Math.Abs(a - b) <= tol; }

        public static string SelfTest()
        {
            _fail = null;
            string err;
            // --- I1 path parser
            var p = ParsePath("M 2 11.25 L 11.25 2 L 20.5 11.25", out err);
            Eq("polyline 1 path / 3 points", p != null && p.Count == 1 && p[0].Length == 6);
            p = ParsePath("M 2 2 H 10 V 8 L 2 8 Z", out err);
            Eq("H/V relative to current point", p != null && p.Count == 1 && p[0].Length == 10 && p[0][2] == 10 && p[0][3] == 2 && p[0][4] == 10 && p[0][5] == 8);
            Eq("Z closes the polyline", p != null && p[0][8] == p[0][0] && p[0][9] == p[0][1]);
            p = ParsePath("M 0 0 L 4 0 4 4 M 10 10 20 10", out err);
            Eq("pair repetition after L and M", p != null && p.Count == 2 && p[0].Length == 6 && p[1].Length == 4 && p[1][2] == 20);
            p = ParsePath("M 0 0 L 4 0 z M 10 10 L 12 10", out err);
            Eq("lower z closes, next M starts a new polyline", p != null && p.Count == 2 && p[0][4] == 0 && p[1][0] == 10);
            p = ParsePath("M-2,-3.5L12.418,.5", out err);
            Eq("commas, minus, fractions", p != null && p[0].Length == 4 && p[0][0] == -2 && p[0][1] == -3.5 && Near(p[0][2], 12.418, 1e-9) && p[0][3] == 0.5);
            p = ParsePath("M 3 11 A 8,8 0 0 1 19,11", out err);
            bool onCircle = p != null; double minY = 99, maxY = -99;
            if (p != null) for (int i = 0; i < p[0].Length; i += 2)
                {
                    double dd = Math.Sqrt((p[0][i] - 11) * (p[0][i] - 11) + (p[0][i + 1] - 11) * (p[0][i + 1] - 11));
                    if (Math.Abs(dd - 8) > 0.05) onCircle = false;
                    minY = Math.Min(minY, p[0][i + 1]); maxY = Math.Max(maxY, p[0][i + 1]);
                }
            Eq("arc sweep 1 on the circle", onCircle);
            Eq("arc sweep 1 goes over the top", Near(minY, 3, 0.05) && maxY <= 11.001);
            p = ParsePath("M 3 11 A 8,8 0 0 0 19,11", out err);
            maxY = -99; if (p != null) for (int i = 1; i < p[0].Length; i += 2) maxY = Math.Max(maxY, p[0][i]);
            Eq("arc sweep 0 goes under", p != null && Near(maxY, 19, 0.05));
            p = ParsePath("M 20 11.5 A 1 1 0 0 0 3 11.5", out err);
            double cxx = 0, cyy = 0; int cn = 0;
            if (p != null) for (int i = 0; i < p[0].Length; i += 2) { cxx += p[0][i]; cyy += p[0][i + 1]; cn++; }
            double minRad = 99, maxRad = -99;
            if (p != null) for (int i = 0; i < p[0].Length; i += 2) { double dd = Math.Sqrt((p[0][i] - 11.5) * (p[0][i] - 11.5) + (p[0][i + 1] - 11.5) * (p[0][i + 1] - 11.5)); minRad = Math.Min(minRad, dd); maxRad = Math.Max(maxRad, dd); }
            Eq("small-radius arc is lifted to 8.5 (half circle)", p != null && Near(minRad, 8.5, 0.05) && Near(maxRad, 8.5, 0.05));
            p = ParsePath("M 0 0 C 0 10 10 10 10 0", out err);
            Eq("cubic: exact ends, midpoint on the Bezier", p != null && p[0][0] == 0 && p[0][p[0].Length - 2] == 10 && p[0][p[0].Length - 1] == 0 && Near(p[0][1 + 2 * 4], 7.5, 1e-9) && Near(p[0][2 * 4], 5, 1e-9));
            Eq("relative command is an error", ParsePath("m 1 1 l 2 2", out err) == null && err != null && err.Contains("unsupported command"));
            Eq("garbage is an error, no exception", ParsePath("hello world", out err) == null && err != null);
            Eq("number without command", ParsePath("1 2 3", out err) == null);
            Eq("missing numbers", ParsePath("M 1", out err) == null);

            // --- I2 dictionary
            string xaml = "<ResourceDictionary xmlns=\"x\">\n <SolidColorBrush x:Key=\"NTDS.UnknownColour\" Color=\"#FFC000\"/>\n <SolidColorBrush x:Key=\"NTDS.HostileColour\" Color=\"Red\"/>\n" +
                          " <Geometry x:Key=\"NTDS.Enemy.Air\">M 2 11.25 L 11.25 2 L 20.5 11.25</Geometry>\n" +
                          " <Geometry x:Key=\"NTDS.Enemy.Bridge\">\n <!-- M 0 0 L 9 9 -->\n M 1 1 L 5 5\n <!-- note --> </Geometry>\n" +
                          " <Geometry x:Key=\"NTDS.Bad.Thing\">m 1 1</Geometry>\n</ResourceDictionary>";
            var set = ParseDictionary(xaml);
            Eq("dictionary: 2 colours", set.Colours.Count == 2 && set.Colours["NTDS.UnknownColour"] == 0xFFFFC000u && set.Colours["NTDS.HostileColour"] == 0xFFFF0000u);
            Eq("dictionary: 2 geometries, comments cut", set.Paths.Count == 2 && set.Paths["NTDS.Enemy.Bridge"][0].Length == 4 && set.Paths["NTDS.Enemy.Bridge"][0][2] == 5);
            Eq("dictionary: bad path reported", set.Errors.Count == 1 && set.Errors[0].StartsWith("NTDS.Bad.Thing"));
            byte[] body = Encoding.UTF8.GetBytes(xaml);
            Func<byte[], byte[], byte[], byte[]> cat = (a, b, c) => { var o = new byte[a.Length + b.Length + c.Length]; Buffer.BlockCopy(a, 0, o, 0, a.Length); Buffer.BlockCopy(b, 0, o, a.Length, b.Length); Buffer.BlockCopy(c, 0, o, a.Length + b.Length, c.Length); return o; };
            var junk = new byte[100]; for (int i = 0; i < junk.Length; i++) junk[i] = (byte)(i * 7 + 3);
            var len = BitConverter.GetBytes(body.Length);
            string ex = ExtractDictionary(cat(cat(junk, len, new byte[0]), body, junk), out err);
            Eq("extract: length-prefixed text", ex == xaml);
            var badLen = BitConverter.GetBytes(5);
            ex = ExtractDictionary(cat(cat(junk, badLen, new byte[0]), body, junk), out err);
            Eq("extract: bad length -> up to the closing tag", ex == xaml);
            Eq("extract: no NTDS geometry -> error", ExtractDictionary(cat(junk, junk, junk), out err) == null && err != null);
            Eq("colour #FFC000", ParseColour("#FFC000") == 0xFFFFC000u);
            Eq("colour #80FFC000", ParseColour("#80FFC000") == 0x80FFC000u);
            Eq("colour names", ParseColour("Red") == 0xFFFF0000u && ParseColour("Green") == 0xFF008000u && ParseColour("Blue") == 0xFF0000FFu);
            Eq("colour unknown -> 0", ParseColour("Mauve") == 0 && ParseColour("#12") == 0 && ParseColour(null) == 0);

            // --- key / resource / colour tables
            bool dot;
            Eq("Air key + dot", KeyFor("Air", "Enemy", out dot) == "NTDS.Enemy.Air" && dot);
            Eq("Biologic -> Submarine", KeyFor("Biologic", "Enemy", out dot) == "NTDS.Enemy.Submarine" && dot);
            Eq("Missile has no dot", KeyFor("Missile", "Enemy", out dot) == "NTDS.Enemy.Missile" && !dot);
            Eq("Torpedo/sonobuoy no dot", KeyFor("Torpedo", "Unknown", out dot) == "NTDS.Unknown.Torpedo" && !dot && KeyFor("PassiveSonobouy", "Enemy", out dot) == "NTDS.Enemy.PassiveSonobouy" && !dot);
            Eq("Invisible/decoys -> null", KeyFor("Invisible", "Enemy", out dot) == null && KeyFor("AirDecoy", "Enemy", out dot) == null && KeyFor("SubDecoy", "Unknown", out dot) == null);
            Eq("Unknown/Surface key", KeyFor("Surface", "Unknown", out dot) == "NTDS.Unknown.Surface");
            Eq("structure key + dot", KeyFor("SAM", "Enemy", out dot) == "NTDS.Enemy.SAM" && dot);
            Eq("bad posture -> null", KeyFor("Air", "Weird", out dot) == null && KeyFor(null, "Enemy", out dot) == null);
            Eq("resource names", ResourceName("Air", "Enemy") == "enemy_air" && ResourceName("Air", "Allied") == "friendly_air" && ResourceName("Helicopter", "Unknown") == "helo" &&
                                 ResourceName("Surface", "Unknown") == "unknown_surface" && ResourceName("Submarine", "Enemy") == "enemy_sub" && ResourceName("Biologic", "Unknown") == "unknown_sub" &&
                                 ResourceName("Missile", "Enemy") == "enemy_missile" && ResourceName("Torpedo", "Enemy") == "enemy_torpedo" && ResourceName("Radar", "Enemy") == "structure" &&
                                 ResourceName("ActiveSonobouy", "Enemy") == null && ResourceName("Invisible", "Enemy") == null);
            Eq("posture colours (set)", PostureColour(set, "Enemy") == 0xFFFF0000u && PostureColour(set, "Unknown") == 0xFFFFC000u);
            Eq("posture colours (fallback)", PostureColour(null, "Enemy") == 0xFFFF0000u && PostureColour(null, "Unknown") == 0xFFFFC000u && PostureColour(null, "Neutral") == 0xFF008000u && PostureColour(null, "Allied") == 0xFF0000FFu);
            foreach (string t in new[] { "Air", "Helicopter", "Surface", "Submarine", "Missile", "Torpedo", "Airbase", "ActiveSonobouy", "Invisible", null })
            {
                var os = OwnSymbol(t, "Enemy");
                bool ok = os != null && os.Count > 0;
                if (ok) foreach (var pl in os) if (pl.Length < 4 || pl.Length % 2 != 0) ok = false;
                Eq("own symbol " + (t ?? "null"), ok);
            }

            // --- T-080 rasterising
            var hl = new List<double[]> { new[] { 2, 11.25, 20.5, 11.25 } };
            var rm = RasterMask(hl, false, 22, 2);
            Eq("raster: size", rm != null && rm.Length == 22 * 22);
            Eq("raster: line centre solid, edge rows empty", rm != null && rm[11 * 22 + 11] == 255 && rm[0 * 22 + 11] == 0 && rm[21 * 22 + 11] == 0);
            var dm = RasterMask(new List<double[]>(), true, 22, 2);
            Eq("raster: centre dot with no paths", dm != null && dm[11 * 22 + 11] > 0 && dm[0] == 0);
            Eq("raster: px<4 / null paths -> null", RasterMask(hl, false, 3, 2) == null && RasterMask(null, false, 22, 2) == null);
            var om = RasterMask(OwnSymbol("Surface", "Enemy"), false, 14, 1.5);
            int onz = 0; if (om != null) foreach (var b in om) if (b > 0) onz++;
            Eq("raster: own Surface partially filled", om != null && onz >= 20 && onz < om.Length);
            var a1 = ArrowMask(1, 12); var am1 = ArrowMask(-1, 12); var a2 = ArrowMask(2, 12); var a0 = ArrowMask(0, 12);
            Eq("arrow +1: apex bottom, base top, bottom corners empty", a1 != null && a1[11 * 12 + 6] > 0 && a1[0] > 0 && a1[11 * 12 + 11] > 0 == false && a1[11 * 12] == 0 && a1[11] > 0);
            Eq("arrow -1 mirrored", am1 != null && am1[6] > 0 && am1[11 * 12] > 0 && am1[0] == 0 && am1[11] == 0);
            int n1 = 0, n2 = 0; if (a1 != null) foreach (var b in a1) if (b > 0) n1++; if (a2 != null) foreach (var b in a2) if (b > 0) n2++;
            Eq("arrow +2 has more pixels than +1", n2 > n1);
            int n0 = 0; if (a0 != null) foreach (var b in a0) if (b > 0) n0++;
            Eq("arrow 0: small centre square only", a0 != null && a0[6 * 12 + 6] > 0 && a0[0] == 0 && n0 < 20);
            Eq("arrow 5 -> null", ArrowMask(5, 12) == null);
            return _fail;
        }
    }
}
