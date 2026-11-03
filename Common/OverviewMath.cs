using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace VoiceBotBridge
{
    /// <summary>Raw snapshot of one track (filled by GameApi.Snapshot on the main thread). Velocities are unity units per second. T-070.</summary>
    internal sealed class OvTrack
    {
        public int Id;
        public string ClassName;   // display class name of the game, null = unknown (T-080)
        public string Side, Kind, Class, VisualType, Posture, ObjType;   // Kind = Vehicle.GetMapType() (the main map's icon, player knowledge);
                                                                  // ObjType = true ObjectBase._type: ASW only (decoy/biologic names), never for the Overview display (T-210)
        public bool Identified, Live, Fan, Gone, Visible;
        public bool HasId = true, IdentifiedKnown;   // contacts json: "id":null / "identified":null when the game does not tell
        public int Sensors;                  // T-206: Vehicle.DetectingSensors (SensorTypeSet flags), 0 = none/unknown
        public double? PosSigmaM;            // T-206: PositionEstimate.Item2.MajorStdDev in metres, null = unknown
        public bool? Classified;                      // contacts json: Vehicle.IsClassified when present
        public double? Lat, Lon, Vx, Vz, AltFt, FanBearing;
        public uint Colour;                                                // 0xAARRGGBB from UnitColorBrush, 0 = none
        // T-092
        public bool Dormant;                 // Vehicle.IsDormant
        public string SideSrc;               // "track" | "override" | "none"
        public string ColourSrc;             // "map_visuals" | "fallback"
        public string MapPosture;            // MapContactVisuals.Resolve().Posture, null = not called (debug only)
        public string VmPosture; public bool? VmMain;   // Vehicle.ViewModel.VisualPosture / is it the main map's VM (debug only)
    }

    /// <summary>One ready-to-draw line of the Overview window.</summary>
    internal sealed class OvRow
    {
        public int Id;
        public string Icon /* "VisualType/Posture" */, Type, Dist, Brg, Spd, Alt, Crs, Sensor, Age, Closing;
        public string Identify = "";   // T-111: ready cell text of the identify-request status, "" = not asked
        public int SortIdentify;       // None 0 < NoLink 1 < Silent 2 < Refused 3 < Answered 4
        public int Arrow;      // -1 opening, 0 neutral, +1 closing, +2 closing fast
        public bool Stale, Hostile;
        public bool DistApprox;   // T-206: Dist is an estimate ("≈N"), excluded from the nearest-contact summary
        public double SortDist = double.NaN, SortClosing = double.NaN, SortSpd = double.NaN, SortAlt = double.NaN, SortBrg = double.NaN, SortCrs = double.NaN;
        public uint Colour;
    }

    internal sealed class OverviewFrame
    {
        public List<OvRow> Rows = new List<OvRow>();
        public int Hostile, Unknown;
        public string RefName;
        public double BuildMs;
        public long Version = 0;
    }

    /// <summary>Closing-speed smoothing per track: EMA, dead band with hysteresis. Fallback without velocity vectors: range difference.</summary>
    internal sealed class ClosingFilter
    {
        private sealed class St
        {
            public double Ema, LastT, Range, RangeT, EmaF, LastF;
            public bool Has, HasF, HasRange, On;
        }

        private readonly double _tau, _dead, _hyst, _fast;
        private readonly Dictionary<int, St> _s = new Dictionary<int, St>();
        private const double FallbackTau = 5.0;

        public ClosingFilter(double tauS, double deadbandKn, double hysteresisKn, double fastKn)
        {
            _tau = Math.Max(0.1, tauS); _dead = deadbandKn; _hyst = hysteresisKn; _fast = fastKn;
        }

        /// <summary>Smoothed closing speed in knots (NaN = nothing computable) and the arrow state.</summary>
        public double Update(int id, double? vClosingKn, double? rangeNm, double tS, out int arrow)
        {
            St s;
            if (!_s.TryGetValue(id, out s)) { s = new St(); _s[id] = s; }
            double v = double.NaN;
            if (vClosingKn.HasValue && !double.IsNaN(vClosingKn.Value))
            {
                double dt = tS - s.LastT;
                if (!s.Has || dt > 30 || dt < 0) s.Ema = vClosingKn.Value;
                else s.Ema += (vClosingKn.Value - s.Ema) * (1 - Math.Exp(-dt / _tau));
                s.Has = true; s.LastT = tS;
                v = s.Ema;
                if (rangeNm.HasValue) { s.Range = rangeNm.Value; s.RangeT = tS; s.HasRange = true; }
            }
            else if (rangeNm.HasValue)
            {
                if (s.HasRange && tS > s.RangeT)
                {
                    double dt = tS - s.RangeT;
                    double raw = (s.Range - rangeNm.Value) / (dt / 3600.0);
                    double dtf = tS - s.LastF;
                    if (!s.HasF || dtf > 30 || dtf < 0) s.EmaF = raw;
                    else s.EmaF += (raw - s.EmaF) * (1 - Math.Exp(-dtf / FallbackTau));
                    s.HasF = true; s.LastF = tS;
                }
                if (!s.HasRange || tS > s.RangeT) { s.Range = rangeNm.Value; s.RangeT = tS; s.HasRange = true; }
                if (s.HasF) v = s.EmaF;
            }
            arrow = 0;
            if (double.IsNaN(v)) { s.On = false; return v; }
            double a = Math.Abs(v);
            if (s.On) { if (a < _dead - _hyst) s.On = false; }
            else if (a >= _dead) s.On = true;
            if (s.On) arrow = (v > 0 ? 1 : -1) * (a >= _fast && v > 0 ? 2 : 1);
            return v;
        }

        public void Forget(IEnumerable<int> aliveIds)
        {
            var alive = new HashSet<int>(aliveIds);
            foreach (int k in _s.Keys.ToList()) if (!alive.Contains(k)) _s.Remove(k);
        }

        public int Count { get { return _s.Count; } }
    }

    /// <summary>Pure rules of the Overview panel (no Unity, no game): filter, formulas, rows, formatting, sort, config parsing.</summary>
    internal static class OverviewMath
    {
        public const double KnPerUnity = 67.200066 * 1.94384;
        public static readonly string[] AllColumns = { "icon", "type", "dist", "brg", "spd", "alt", "crs", "id", "sensor", "age", "vcl", "idn" };
        private const string Dash = "—";
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static bool Include(OvTrack t, bool showWeapons)
        {
            if (t == null || !t.Visible || t.Gone) return false;
            if (t.Side != "hostile" && t.Side != "unknown") return false;
            if (!showWeapons && (t.Kind == "Missile" || t.Kind == "Torpedo")) return false;
            return true;
        }

        /// <summary>Closing speed (kn, positive = approaching) from the bearing own->contact and both velocity vectors (x east, z north).</summary>
        public static double? ClosingKn(double brgDeg, double vcx, double vcz, double vox, double voz)
        {
            double b = brgDeg * Math.PI / 180.0;
            double ux = Math.Sin(b), uz = Math.Cos(b);
            double vr = ((vcx - vox) * ux + (vcz - voz) * uz) * KnPerUnity;
            return -vr + 0.0;
        }

        public static double SpeedKn(double vx, double vz) { return Math.Sqrt(vx * vx + vz * vz) * KnPerUnity; }

        public static double CourseDeg(double vx, double vz)
        {
            double d = Math.Atan2(vx, vz) * 180.0 / Math.PI;
            d = ((d % 360.0) + 360.0) % 360.0;
            return d >= 359.9999 ? 0 : d;
        }

        // ---------------------------------------------------------------- T-206 range knowledge

        public const int RangeNone = 0, RangeApprox = 1, RangeKnown = 2;
        /// <summary>SensorTypeSet flags that measure range: Self, Visual, TVSeeker, SearchRadar, FCR, ARS, ActiveSonar, MAD.</summary>
        public const int SensorRanging = 0x1 | 0x2 | 0x4 | 0x8 | 0x10 | 0x20 | 0x40 | 0x400;
        /// <summary>PassiveSonar, ActiveIntercept, ESM: bearing-only sources.</summary>
        public const int SensorPassive = 0x80 | 0x100 | 0x200;
        public const double RangeTightMinM = 3704;   // 2 nm
        public const double RangeTightFrac = 0.10;

        /// <summary>Does the player know the range to this track? fan -> None; a ranging sensor -> Known; passive only -> Known with a tight error ellipse, else Approx;
        /// no sensors (stale track) -> Approx with a wide ellipse, else Known (as before). distM NaN -> threshold RangeTightMinM.</summary>
        public static int RangeOf(bool fan, int sensors, double? majorSigmaM, double distM)
        {
            if (fan) return RangeNone;
            if ((sensors & SensorRanging) != 0) return RangeKnown;
            double tight = RangeTightMinM;
            if (!double.IsNaN(distM) && !double.IsInfinity(distM)) tight = Math.Max(RangeTightMinM, RangeTightFrac * distM);
            bool narrow = majorSigmaM.HasValue && 2 * majorSigmaM.Value <= tight;
            if ((sensors & SensorPassive) != 0) return narrow ? RangeKnown : RangeApprox;
            if (!majorSigmaM.HasValue) return RangeKnown;
            return narrow ? RangeKnown : RangeApprox;
        }

        public const double FtPerUnity = 220.47266;   // the game's own Unity-height -> feet factor (MapUnitViewModel)

        /// <summary>Track altitude estimate (Unity units, Vehicle.Altitude.Value.Estimate) -> feet; null/NaN/Inf -> null (T-209).</summary>
        public static double? TrackAltFt(double? estimateUnity)
        {
            if (!estimateUnity.HasValue || double.IsNaN(estimateUnity.Value) || double.IsInfinity(estimateUnity.Value)) return null;
            return estimateUnity.Value * FtPerUnity;
        }

        /// <summary>Does the Overview show an altitude for this map type (Vehicle.GetMapType, the icon the main map draws for every track)? Air, Helicopter, Missile -> true. T-210.</summary>
        public static bool KindShowsAltitude(string kind)
        {
            switch (kind) { case "Air": case "Helicopter": case "Missile": return true; }
            return false;
        }

        // ---------------------------------------------------------------- formatting

        private static string Group(long v, string lang)
        {
            string s = Math.Abs(v).ToString(Inv);
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (i > 0 && (s.Length - i) % 3 == 0) sb.Append(lang == "en" ? ',' : ' ');
                sb.Append(s[i]);
            }
            return (v < 0 ? "-" : "") + sb;
        }

        public static string FmtNm(double nm, string lang = "ru")
        {
            if (double.IsNaN(nm) || double.IsInfinity(nm)) return Dash;
            string s = nm < 100 ? nm.ToString("0.0", Inv) : Math.Round(nm).ToString("0", Inv);
            return lang == "en" ? s : s.Replace('.', ',');
        }

        public static string FmtDeg(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return Dash;
            int v = (int)Math.Round(((d % 360.0) + 360.0) % 360.0) % 360;
            return v.ToString("000", Inv) + "°";
        }

        /// <summary>T-103: UI text hook (key -> text or null). null hook / null result = built-in ru/en below (voice mod keeps its old behaviour).</summary>
        public static Func<string, string> Text;

        private static string L(string key, string lang, string ru, string en)
        {
            string t = null;
            var h = Text;
            if (h != null) { try { t = h(key); } catch { t = null; } }
            return t ?? (lang == "en" ? en : ru);
        }

        public static string FmtKn(double kn, string lang = "ru")
        {
            if (double.IsNaN(kn) || double.IsInfinity(kn)) return Dash;
            return Math.Round(kn).ToString("0", Inv) + " " + L("ov.kn", lang, "уз", "kn");
        }

        public static string FmtFt(double ft, string lang = "ru")
        {
            if (double.IsNaN(ft) || double.IsInfinity(ft)) return Dash;
            return Group((long)Math.Round(ft), lang) + " " + L("ov.ft", lang, "фт", "ft");
        }

        private static string FmtSigned(double kn, string lang)
        {
            long r = (long)Math.Round(kn);
            return (r > 0 ? "+" : r < 0 ? "-" : "") + Math.Abs(r).ToString(Inv) + " " + L("ov.kn", lang, "уз", "kn");
        }

        private static string FmtAge(double? s, string lang)
        {
            if (!s.HasValue) return Dash;
            long v = (long)Math.Max(0, s.Value);
            if (v < 60) return v + " " + L("ov.s", lang, "с", "s");
            return (v / 60) + " " + L("ov.min", lang, "мин", "min");
        }

        public static string TypeText(OvTrack t, string lang)
        {
            string s;
            if (!string.IsNullOrEmpty(t.ClassName)) s = t.ClassName;
            else if (!string.IsNullOrEmpty(t.Class)) s = t.Class;
            else s = KindLabel(t.Fan ? null : t.Kind, lang) + t.Id;
            if (s.Length > 22) s = s.Substring(0, 21) + "…";
            return s;
        }

        /// <summary>Caption prefix for a track without a class, by map type. null/unknown kind -> "Контакт #".</summary>
        public static string KindLabel(string kind, string lang)
        {
            switch (kind)
            {
                case "Surface": return L("ov.kind.surface", lang, "Надв. #", "Surface #");
                case "Air": return L("ov.kind.air", lang, "Воздушн. #", "Air #");
                case "Helicopter": return L("ov.kind.helo", lang, "Верт. #", "Helo #");
                case "Submarine": return L("ov.kind.sub", lang, "Подв. #", "Sub #");
                case "Missile": return L("ov.kind.missile", lang, "Ракета #", "Missile #");
                case "Torpedo": return L("ov.kind.torpedo", lang, "Торпеда #", "Torpedo #");
                default: return L("ov.kind.contact", lang, "Контакт #", "Contact #");
            }
        }

        /// <summary>Icon posture from the player's knowledge of the side (same source as the main map).</summary>
        public static string PostureOf(string side)
        {
            switch (side)
            {
                case "hostile": return "Enemy";
                case "friendly": return "Allied";
                case "neutral": return "Neutral";
                default: return "Unknown";
            }
        }

        /// <summary>Default palette of Settings_UI_Tactical.ini; dormant/destroyed win over the posture.</summary>
        public static uint FallbackColour(string posture, bool dormant, bool destroyed)
        {
            if (destroyed) return 0xFF000000u;
            if (dormant) return 0xFF979899u;
            switch (posture)
            {
                case "Enemy": return 0xFFFF0000u;
                case "Neutral": return 0xFF009900u;
                case "Allied": return 0xFFB35900u;
                default: return 0xFF666600u;
            }
        }

        /// <summary>Stale track: same RGB, alpha x0.6 (never grey).</summary>
        public static uint StaleTint(uint argb)
        {
            uint a = (argb >> 24) & 255;
            if (a == 0) a = 255;
            a = (uint)Math.Round(a * 0.6);
            return (a << 24) | (argb & 0x00FFFFFFu);
        }

        private static string Q(string s) { return string.IsNullOrEmpty(s) ? "-" : s; }

        /// <summary>One Debug log line per Overview row (T-092 section 5).</summary>
        public static string DebugLine(OvTrack t, OvRow r)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "overview track {0}: side={1} src={2} ident={3} class={4} kind={5} posture={6} map_posture={7} vm_posture={8} vm_main={9} colour=#{10:X8} src={11} live={12} dormant={13} stale={14} icon={15} type=\"{16}\" sens=0x{17:X} sig_m={18} alt_ft={19}",
                t.Id, Q(t.Side), Q(t.SideSrc), t.Identified ? "true" : "false", Q(t.Class), Q(t.Kind), Q(t.Posture), Q(t.MapPosture), Q(t.VmPosture),
                t.VmMain.HasValue ? (t.VmMain.Value ? "true" : "false") : "-", t.Colour, Q(t.ColourSrc),
                t.Live ? "true" : "false", t.Dormant ? "true" : "false", r != null && r.Stale ? "true" : "false", r == null ? "-" : r.Icon, r == null ? "" : r.Type, t.Sensors, t.PosSigmaM.HasValue ? t.PosSigmaM.Value.ToString("0", Inv) : "-", t.AltFt.HasValue ? t.AltFt.Value.ToString("0", Inv) : "-");
        }

        // ---------------------------------------------------------------- rows

        public static OvRow MakeRow(OvTrack t, double? refLat, double? refLon, double? vox, double? voz, ClosingFilter f, double tS, double? ageS, string lang, string identifyText = "", int identifyRank = 0)
        {
            var r = new OvRow
            {
                Id = t.Id, Icon = (t.VisualType ?? "") + "/" + (t.Posture ?? ""), Type = TypeText(t, lang), Stale = !t.Live,
                Hostile = t.Side == "hostile", Colour = t.Colour, Closing = Dash, Dist = Dash, Brg = Dash, Spd = Dash, Crs = Dash, Alt = Dash,
                Sensor = t.Fan ? "ESM" : "", Age = FmtAge(ageS, lang), Arrow = 0,
                Identify = identifyText ?? "", SortIdentify = identifyRank
            };
            bool haveRef = refLat.HasValue && refLon.HasValue && t.Lat.HasValue && t.Lon.HasValue;
            double dist = double.NaN, brg = double.NaN;
            if (haveRef)
            {
                brg = GameApi.Bearing(refLat.Value, refLon.Value, t.Lat.Value, t.Lon.Value);
                if (!t.Fan) dist = GameApi.DistanceNm(refLat.Value, refLon.Value, t.Lat.Value, t.Lon.Value);
            }
            bool approx = !t.Fan && RangeOf(false, t.Sensors, t.PosSigmaM, double.IsNaN(dist) ? double.NaN : dist * 1852.0) == RangeApprox;
            if (approx)
            {
                r.DistApprox = true;
                if (t.Sensors == 0 || (t.Sensors & (0x100 | 0x200)) != 0) r.Sensor = "ESM";
            }
            if (t.Fan)
            {
                if (t.FanBearing.HasValue) brg = t.FanBearing.Value;
                r.Alt = Dash;
            }
            else
            {
                if (!double.IsNaN(dist)) { r.Dist = (approx ? "≈" : "") + FmtNm(dist, lang); r.SortDist = dist; }
                if (t.Vx.HasValue && t.Vz.HasValue)
                {
                    double sp = SpeedKn(t.Vx.Value, t.Vz.Value);
                    r.Spd = FmtKn(sp, lang); r.SortSpd = sp;
                    if (sp >= 0.5) { double cr = CourseDeg(t.Vx.Value, t.Vz.Value); r.Crs = FmtDeg(cr); r.SortCrs = cr; }
                }
                if (KindShowsAltitude(t.Kind)) { if (t.AltFt.HasValue) { r.Alt = FmtFt(t.AltFt.Value, lang); r.SortAlt = t.AltFt.Value; } }
                else r.Alt = t.Kind == "Submarine" ? Dash : "";
                if (approx) r.Alt = Dash;   // the altitude of a bearing-derived track is not known to the player
                if (f != null)
                {
                    double? vcl = null;
                    if (haveRef && t.Vx.HasValue && t.Vz.HasValue && vox.HasValue && voz.HasValue)
                        vcl = ClosingKn(brg, t.Vx.Value, t.Vz.Value, vox.Value, voz.Value);
                    int arrow;
                    double v = f.Update(t.Id, vcl, double.IsNaN(dist) || approx ? (double?)null : dist, tS, out arrow);
                    if (!double.IsNaN(v)) { r.Closing = FmtSigned(v, lang); r.SortClosing = v; r.Arrow = arrow; }
                }
            }
            if (!double.IsNaN(brg)) { r.Brg = FmtDeg(brg); r.SortBrg = brg; }
            return r;
        }

        // ---------------------------------------------------------------- sort / frozen row

        private static double Key(OvRow r, string col)
        {
            switch (col)
            {
                case "dist": return r.SortDist;
                case "brg": return r.SortBrg;
                case "spd": return r.SortSpd;
                case "alt": return r.SortAlt;
                case "crs": return r.SortCrs;
                case "vcl": return r.SortClosing;
                case "id": return r.Id;
                case "idn": return r.SortIdentify;
            }
            return 0;
        }

        public static void Sort(List<OvRow> rows, string column, bool asc)
        {
            bool text = column == "type" || column == "icon" || column == "sensor" || column == "age";
            Comparison<OvRow> cmp = (a, b) =>
            {
                int c;
                if (text)
                {
                    string x = column == "type" ? a.Type : column == "icon" ? a.Icon : column == "sensor" ? a.Sensor : a.Age;
                    string y = column == "type" ? b.Type : column == "icon" ? b.Icon : column == "sensor" ? b.Sensor : b.Age;
                    c = string.Compare(x ?? "", y ?? "", StringComparison.OrdinalIgnoreCase);
                    if (!asc) c = -c;
                }
                else
                {
                    double x = Key(a, column), y = Key(b, column);
                    bool nx = double.IsNaN(x), ny = double.IsNaN(y);
                    if (nx || ny) return nx && ny ? a.Id.CompareTo(b.Id) : nx ? 1 : -1;   // NaN last in both directions
                    c = x.CompareTo(y);
                    if (!asc) c = -c;
                }
                return c != 0 ? c : a.Id.CompareTo(b.Id);
            };
            var sorted = rows.OrderBy(r => r, Comparer<OvRow>.Create(cmp)).ToList();
            rows.Clear(); rows.AddRange(sorted);
        }

        /// <summary>The row under the cursor keeps its index; the others follow the fresh order.</summary>
        public static List<OvRow> ApplyFrozen(List<OvRow> fresh, List<OvRow> shown, int frozenId, int frozenIndex)
        {
            var res = new List<OvRow>(fresh);
            if (frozenId < 0 || frozenIndex < 0) return res;
            int at = res.FindIndex(r => r.Id == frozenId);
            if (at < 0) return res;
            var row = res[at];
            res.RemoveAt(at);
            res.Insert(Math.Min(frozenIndex, res.Count), row);
            return res;
        }

        public static string Summary(OverviewFrame f, string lang)
        {
            var sb = new StringBuilder(L("ov.sum.title", lang, "Обзор", "Overview")).Append(": ");
            sb.Append(f.Hostile).Append(' ').Append(L("ov.sum.hostile", lang, "враг", "hostile")).Append(" · ").Append(f.Unknown).Append(' ').Append(L("ov.sum.unknown", lang, "неопозн.", "unk."));
            OvRow near = null;
            foreach (var r in f.Rows) if (!r.DistApprox && !double.IsNaN(r.SortDist) && (near == null || r.SortDist < near.SortDist)) near = r;
            if (near != null)
                sb.Append(" · ").Append(L("ov.sum.nearest", lang, "ближ.", "nearest")).Append(' ').Append(FmtNm(near.SortDist, lang)).Append(' ').Append(near.Arrow > 0 ? "▼" : near.Arrow < 0 ? "▲" : "•");
            return sb.ToString();
        }

        // ---------------------------------------------------------------- config parsing

        public static bool ParseRect(string spec, out double x, out double y, out double w, out double h)
        {
            x = y = w = h = 0;
            if (string.IsNullOrWhiteSpace(spec)) return false;
            var p = spec.Split(',');
            if (p.Length != 4) return false;
            var v = new double[4];
            for (int i = 0; i < 4; i++)
                if (!double.TryParse(p[i].Trim(), NumberStyles.Float, Inv, out v[i]) || double.IsNaN(v[i]) || v[i] < 0 || v[i] > 1) return false;
            if (v[2] <= 0 || v[3] <= 0) return false;
            x = v[0]; y = v[1]; w = v[2]; h = v[3];
            return true;
        }

        public static void ClampRect(ref double x, ref double y, ref double w, ref double h, double minW, double minH, double screenW, double screenH)
        {
            w = Math.Min(Math.Max(w, minW), screenW);
            h = Math.Min(Math.Max(h, minH), screenH);
            x = Math.Max(0, Math.Min(x, screenW - w));
            y = Math.Max(0, Math.Min(y, screenH - h));
        }

        public static bool ParseSort(string spec, out string column, out bool asc)
        {
            column = "dist"; asc = true;
            if (string.IsNullOrWhiteSpace(spec)) return false;
            var p = spec.Trim().ToLowerInvariant().Split(':');
            if (p.Length > 2 || Array.IndexOf(AllColumns, p[0]) < 0) return false;
            if (p.Length == 2) { if (p[1] != "asc" && p[1] != "desc") return false; asc = p[1] == "asc"; }
            column = p[0];
            return true;
        }

        public static string[] ParseColumns(string spec, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(spec)) { error = "columns: empty"; return null; }
            var cols = spec.Split(',').Select(c => c.Trim().ToLowerInvariant()).Where(c => c.Length > 0).ToArray();
            foreach (var c in cols) if (Array.IndexOf(AllColumns, c) < 0) { error = "columns: unknown '" + c + "'"; return null; }
            if (Array.IndexOf(cols, "icon") < 0 || Array.IndexOf(cols, "type") < 0) { error = "columns: icon and type are required"; return null; }
            if (cols.Distinct().Count() != cols.Length) { error = "columns: duplicate"; return null; }
            return cols;
        }

        public const double HeaderH = 20, ColHeadH = 18, RowH = 18, MinH = 120;

        /// <summary>"free" | "taken" | "unknown" (no bindings known yet). The hotkey is taken when a game binding's key set is a subset of ours and contains our main (last) key.</summary>
        public static string HotkeyVerdict(string[] combo, IEnumerable<string[]> bindings, IList<string> names, out string conflict)
        {
            conflict = null;
            if (combo == null || combo.Length == 0) return "taken";
            if (bindings == null) return "unknown";
            var ours = new HashSet<string>(combo.Select(c => c.ToLowerInvariant()));
            string main = combo[combo.Length - 1].ToLowerInvariant();
            int i = 0, n = 0;
            foreach (var b in bindings)
            {
                int idx = i++;
                if (b == null || b.Length == 0) continue;
                n++;
                var set = b.Select(c => c.ToLowerInvariant()).ToList();
                if (set.Contains(main) && set.All(k => ours.Contains(k)))
                {
                    string nm = names != null && idx < names.Count ? names[idx] : null;
                    conflict = (nm ?? "?") + " (" + string.Join("+", b) + ")";
                    return "taken";
                }
            }
            return n == 0 ? "unknown" : "free";
        }

        public static bool HotkeyFree(string[] combo, IEnumerable<string[]> bindings)
        {
            string c;
            return HotkeyVerdict(combo, bindings, null, out c) != "taken";
        }

        /// <summary>Window height that fits the rows: header + column heads + rows + margin, at least MinH.</summary>
        public static double FitHeight(int rows, double scale)
        {
            return Math.Max(MinH, HeaderH + ColHeadH + Math.Max(1, rows) * RowH + 4) * scale;
        }

        /// <summary>none | map | resources | own | mixed (no strict majority among several sources).</summary>
        public static string IconSourceOf(int map, int resources, int own)
        {
            int sum = map + resources + own;
            if (sum <= 0) return "none";
            if (map * 2 > sum) return "map";
            if (resources * 2 > sum) return "resources";
            if (own * 2 > sum) return "own";
            return "mixed";
        }

        /// <summary>Which unit a command without uid goes to: the selected own unit, else the last own one that is still alive.</summary>
        public static object PickCommandUnit(object current, bool currentIsOwnControllable, object lastOwn, bool lastOwnAlive, out string source)
        {
            if (current != null && currentIsOwnControllable) { source = "render_position"; return current; }
            if (lastOwn != null && lastOwnAlive) { source = "last_own"; return lastOwn; }
            source = null;
            return null;
        }

        // ---------------------------------------------------------------- self test

        private static string _fail;
        private static void Eq(string name, bool ok) { if (!ok && _fail == null) _fail = name; }
        private static bool Near(double a, double b, double tol) { return Math.Abs(a - b) <= tol; }

        private static OvTrack Tr(int id, string side, string kind = "Surface")
        {
            return new OvTrack { Id = id, Side = side, Kind = kind, Visible = true, Live = true, VisualType = kind, Posture = side == "hostile" ? "Enemy" : "Unknown", ObjType = "Vessel" };
        }

        public static string SelfTest()
        {
            _fail = null;
            var savedText = Text; Text = null;   // built-in ru/en must behave as before; the hook is checked below
            // --- O1 filter
            Eq("hostile in", Include(Tr(1, "hostile"), true));
            Eq("unknown in", Include(Tr(1, "unknown"), true));
            Eq("friendly/neutral out", !Include(Tr(1, "friendly"), true) && !Include(Tr(1, "neutral"), true));
            var gone = Tr(1, "hostile"); gone.Gone = true;
            var invis = Tr(1, "hostile"); invis.Visible = false;
            Eq("gone / invisible out", !Include(gone, true) && !Include(invis, true));
            Eq("missile follows ShowWeapons", Include(Tr(1, "hostile", "Missile"), true) && !Include(Tr(1, "hostile", "Missile"), false) && !Include(Tr(1, "hostile", "Torpedo"), false) && Include(Tr(1, "hostile", "Air"), false));
            // --- formulas
            Eq("SpeedKn(1,0)", Near(SpeedKn(1, 0), 130.62, 0.01));
            Eq("CourseDeg", Near(CourseDeg(0, 1), 0, 1e-9) && Near(CourseDeg(1, 0), 90, 1e-9) && Near(CourseDeg(0, -1), 180, 1e-9) && Near(CourseDeg(-1, 0), 270, 1e-9));
            Eq("closing: contact N going S", Near(ClosingKn(0, 0, -0.1, 0, 0).Value, 13.06, 0.01));
            Eq("closing: contact N going N", Near(ClosingKn(0, 0, 0.1, 0, 0).Value, -13.06, 0.01));
            Eq("closing: crossing", Near(ClosingKn(0, 0.1, 0, 0, 0).Value, 0, 1e-6));
            Eq("closing: own speed counts", Near(ClosingKn(90, 0, 0, 0.1, 0).Value, 13.06, 0.01));
            Eq("altitude types", KindShowsAltitude("Air") && KindShowsAltitude("Helicopter") && KindShowsAltitude("Missile") &&
                                 !KindShowsAltitude("Surface") && !KindShowsAltitude("Submarine") && !KindShowsAltitude("Torpedo") && !KindShowsAltitude("Invisible") && !KindShowsAltitude("Airbase") && !KindShowsAltitude(null));
            // --- formatting
            Eq("FmtNm", FmtNm(12.44) == "12,4" && FmtNm(120.4) == "120" && FmtNm(12.44, "en") == "12.4" && FmtNm(double.NaN) == Dash);
            Eq("FmtFt", FmtFt(1500) == "1 500 фт" && FmtFt(250) == "250 фт" && FmtFt(1500, "en") == "1,500 ft" && FmtFt(30000) == "30 000 фт");
            Eq("FmtDeg", FmtDeg(45) == "045°" && FmtDeg(0) == "000°" && FmtDeg(359.6) == "000°" && FmtDeg(-90) == "270°");
            Text = k => "<" + k + ">";
            Eq("text hook", FmtKn(24, "ru") == "24 <ov.kn>" && FmtFt(10, "en") == "10 <ov.ft>" && KindLabel("Air", "en") == "<ov.kind.air>" && Summary(new OverviewFrame(), "ru").StartsWith("<ov.sum.title>: 0 <ov.sum.hostile>"));
            Text = k => null;
            Eq("text hook null result", FmtKn(24, "en") == "24 kn");
            Text = null;
            Eq("FmtKn", FmtKn(24.2) == "24 уз" && FmtKn(24.2, "en") == "24 kn");
            // --- TypeText
            var tt = Tr(12, "hostile"); Eq("type: surface", TypeText(tt, "ru") == "Надв. #12" && TypeText(tt, "en") == "Surface #12");
            tt.Kind = "Air"; Eq("type: air", TypeText(tt, "ru") == "Воздушн. #12" && TypeText(tt, "en") == "Air #12");
            tt.Fan = true; Eq("type: fan", TypeText(tt, "ru") == "Контакт #12" && TypeText(tt, "en") == "Contact #12"); tt.Fan = false;
            Eq("kind labels", KindLabel("Surface", "ru") == "Надв. #" && KindLabel("Helicopter", "ru") == "Верт. #" && KindLabel("Submarine", "ru") == "Подв. #" && KindLabel("Missile", "ru") == "Ракета #" &&
                              KindLabel("Torpedo", "ru") == "Торпеда #" && KindLabel(null, "ru") == "Контакт #" && KindLabel("Surface", "en") == "Surface #" && KindLabel("Helicopter", "en") == "Helo #" &&
                              KindLabel("Submarine", "en") == "Sub #" && KindLabel("Missile", "en") == "Missile #" && KindLabel("Torpedo", "en") == "Torpedo #" && KindLabel("Air", "en") == "Air #");
            tt.Class = "Sovremenny"; Eq("type: class", TypeText(tt, "ru") == "Sovremenny");
            tt.Class = "A very long class name that does not fit"; Eq("type: truncated", TypeText(tt, "ru").Length == 22 && TypeText(tt, "ru").EndsWith("…"));
            // --- MakeRow
            var own = new { Lat = 15.0, Lon = 42.0 };
            var tr = Tr(7, "hostile"); tr.Lat = 15.5; tr.Lon = 42.0; tr.Vx = 0; tr.Vz = -0.1; tr.Colour = 0xFFFF0000u;
            var f = new ClosingFilter(3, 3, 1, 300);
            var row = MakeRow(tr, own.Lat, own.Lon, 0.0, 0.0, f, 0, 12, "ru");
            Eq("row dist 30,0", row.Dist == "30,0" && Near(row.SortDist, 30, 0.01));
            Eq("row brg 000", row.Brg == "000°");
            Eq("row spd/crs", row.Spd == "13 уз" && row.Crs == "180°");
            Eq("row closing arrow", row.Arrow == 1 && row.Closing == "+13 уз");
            Eq("row surface alt empty", row.Alt == "");
            Eq("row colour/hostile/age", row.Colour == 0xFFFF0000u && row.Hostile && !row.Stale && row.Age == "12 с");
            var fan = Tr(8, "unknown"); fan.Fan = true; fan.FanBearing = 123.4; fan.Lat = 15.5; fan.Lon = 42.0; fan.Vx = 1; fan.Vz = 1;
            row = MakeRow(fan, own.Lat, own.Lon, 0.0, 0.0, f, 0, null, "ru");
            Eq("fan row: only bearing", row.Brg == "123°" && row.Dist == Dash && row.Spd == Dash && row.Crs == Dash && row.Closing == Dash && row.Alt == Dash && row.Arrow == 0 && row.Sensor == "ESM");
            row = MakeRow(tr, null, null, null, null, f, 1, null, "ru");
            Eq("no reference: dist and bearing dash", row.Dist == Dash && row.Brg == Dash && row.Spd == "13 уз" && row.Closing == Dash);
            var dead = Tr(9, "hostile"); dead.Live = false;
            var idRow = MakeRow(tr, null, null, null, null, f, 1, null, "ru", "ответил", 4);
            Eq("row identify", idRow.Identify == "ответил" && idRow.SortIdentify == 4 && MakeRow(tr, null, null, null, null, f, 1, null, "ru").Identify == "");
            string idErr;
            Eq("AllColumns idn", Array.IndexOf(AllColumns, "idn") >= 0 && ParseColumns("icon,type,dist,brg,spd,alt,crs,idn", out idErr) != null && ParseColumns("icon,type,dist", out idErr) != null);
            {
                var ir = new List<OvRow> { new OvRow { Id = 1, SortIdentify = 4 }, new OvRow { Id = 2, SortIdentify = 0 }, new OvRow { Id = 3, SortIdentify = 2 }, new OvRow { Id = 4, SortIdentify = 0 }, new OvRow { Id = 5, SortIdentify = 3 }, new OvRow { Id = 6, SortIdentify = 1 } };
                Sort(ir, "idn", true);
                Eq("sort idn asc", string.Join(",", ir.Select(x => x.Id)) == "2,4,6,3,5,1");
                Sort(ir, "idn", false);
                Eq("sort idn desc", string.Join(",", ir.Select(x => x.Id)) == "1,5,3,6,2,4");
            }
            Eq("not live -> stale", MakeRow(dead, null, null, null, null, f, 0, null, "ru").Stale);
            var sub = Tr(10, "hostile", "Submarine"); sub.ObjType = "Submarine";
            Eq("submarine altitude dash", MakeRow(sub, null, null, null, null, f, 0, null, "ru").Alt == Dash);
            var air = Tr(11, "hostile", "Air"); air.ObjType = "Aircraft"; air.AltFt = 1500;
            Eq("aircraft altitude", MakeRow(air, null, null, null, null, f, 0, null, "ru").Alt == "1 500 фт");
            air.AltFt = null; Eq("aircraft altitude unknown -> dash", MakeRow(air, null, null, null, null, f, 0, null, "ru").Alt == Dash);

            {   // T-210: the Overview altitude column follows the map type (Kind), never the true ObjType (Tr() defaults to ObjType "Vessel")
                var k1 = Tr(30, "unknown", "Air"); k1.AltFt = 1500;
                Eq("t210 kind air opens altitude", MakeRow(k1, null, null, null, null, f, 0, null, "ru").Alt == "1 500 фт");
                var k2 = Tr(31, "unknown", "Surface"); k2.ObjType = "Aircraft"; k2.AltFt = 1500;
                var r2 = MakeRow(k2, null, null, null, null, f, 0, null, "ru");
                Eq("t210 true aircraft on surface icon: no altitude", r2.Alt == "" && double.IsNaN(r2.SortAlt));
                var k3 = Tr(32, "unknown", "Submarine"); k3.ObjType = "Biologic";
                Eq("t210 submarine icon dash", MakeRow(k3, null, null, null, null, f, 0, null, "ru").Alt == Dash);
                k3.ObjType = null; Eq("t210 submarine icon dash without type", MakeRow(k3, null, null, null, null, f, 0, null, "ru").Alt == Dash);
                var k4 = Tr(33, "unknown", "Invisible"); k4.ObjType = "Bomb"; k4.AltFt = 500;
                Eq("t210 invisible bomb: no altitude", MakeRow(k4, null, null, null, null, f, 0, null, "ru").Alt == "");
                k4.Kind = null; Eq("t210 null kind: no altitude", MakeRow(k4, null, null, null, null, f, 0, null, "ru").Alt == "");
                var k5 = Tr(34, "unknown", "Missile"); k5.ObjType = "ASROC"; k5.AltFt = 800;
                Eq("t210 missile icon altitude", MakeRow(k5, null, null, null, null, f, 0, null, "ru").Alt == "800 фт");
            }

            // --- T-206 range knowledge
            Eq("RangeOf fan", RangeOf(true, 0x8, 0, 1000) == RangeNone);
            Eq("RangeOf search radar", RangeOf(false, 0x8, null, 500000) == RangeKnown);
            Eq("RangeOf esm no sigma", RangeOf(false, 0x200, null, 500000) == RangeApprox);
            Eq("RangeOf esm wide", RangeOf(false, 0x200, 50000, 500000) == RangeApprox);
            Eq("RangeOf esm narrow", RangeOf(false, 0x200, 1000, 500000) == RangeKnown);
            Eq("RangeOf esm + radar", RangeOf(false, 0x200 | 0x8, null, 500000) == RangeKnown);
            Eq("RangeOf no sensors sigma 0", RangeOf(false, 0, 0, 500000) == RangeKnown);
            Eq("RangeOf no sensors wide", RangeOf(false, 0, 80000, 500000) == RangeApprox);
            Eq("RangeOf NaN dist -> 2 nm", RangeOf(false, 0x200, 1800, double.NaN) == RangeKnown && RangeOf(false, 0x200, 2000, double.NaN) == RangeApprox);
            {
                var es = Tr(21, "unknown", "Air"); es.ObjType = "Aircraft"; es.Lat = 15.0 + 394.0 / 60.0; es.Lon = 42.0; es.Vx = 0; es.Vz = -0.1; es.AltFt = 9000; es.Sensors = 0x200;
                var fe = new ClosingFilter(3, 3, 1, 300);
                var er = MakeRow(es, 15.0, 42.0, 0.0, 0.0, fe, 0, null, "ru");
                var ee = MakeRow(es, 15.0, 42.0, 0.0, 0.0, fe, 0, null, "en");
                Eq("esm row approx", er.Dist == "≈394" && ee.Dist == "≈394" && Near(er.SortDist, 394, 0.01) && er.DistApprox && er.Sensor == "ESM" && er.Alt == Dash);
                Eq("esm row spd/crs as before", er.Spd == "13 уз" && er.Crs == "180°");
                var kr = new OvRow { SortDist = 50, DistApprox = true };
                var sf = new OverviewFrame { Hostile = 1 }; sf.Rows.Add(kr);
                Eq("summary: only approx -> no nearest", Summary(sf, "ru") == "Обзор: 1 враг · 0 неопозн.");
                sf.Rows.Add(new OvRow { SortDist = 80 });
                Eq("summary: nearest skips approx", Summary(sf, "ru") == "Обзор: 1 враг · 0 неопозн. · ближ. 80,0 •");
                es.Sensors = 0x8;
                var kn = MakeRow(es, 15.0, 42.0, 0.0, 0.0, fe, 0, null, "ru");
                Eq("search radar row exact", kn.Dist == "394" && !kn.DistApprox && kn.Alt == "9 000 фт");
            }

            // --- O2 closing filter
            var cf = new ClosingFilter(3, 3, 1, 300);
            int ar = 0; double ev = 0;
            for (int i = 0; i <= 60; i++) ev = cf.Update(1, 10, null, i * 0.5, out ar);
            Eq("EMA steady 10 kn", ev > 9.5 && ar == 1);
            cf = new ClosingFilter(3, 3, 1, 300);
            for (int i = 0; i <= 60; i++) cf.Update(2, 0, null, i * 0.5, out ar);
            for (int i = 61; i <= 160; i++) cf.Update(2, 2.5, null, i * 0.5, out ar);
            Eq("2.5 kn does not switch the arrow on", ar == 0);
            for (int i = 161; i <= 260; i++) cf.Update(2, 3.2, null, i * 0.5, out ar);
            Eq("3.2 kn switches on", ar == 1);
            for (int i = 261; i <= 360; i++) cf.Update(2, 2.4, null, i * 0.5, out ar);
            Eq("2.4 kn keeps it (hysteresis)", ar == 1);
            for (int i = 361; i <= 460; i++) cf.Update(2, 1.9, null, i * 0.5, out ar);
            Eq("1.9 kn switches off", ar == 0);
            cf = new ClosingFilter(3, 3, 1, 300);
            for (int i = 0; i <= 60; i++) cf.Update(3, 400, null, i * 0.5, out ar);
            Eq("fast closing -> +2", ar == 2);
            cf = new ClosingFilter(3, 3, 1, 300);
            for (int i = 0; i <= 60; i++) cf.Update(4, -5, null, i * 0.5, out ar);
            Eq("opening -> -1", ar == -1);
            cf = new ClosingFilter(3, 3, 1, 300);
            cf.Update(5, null, 10.0, 0, out ar);
            ev = cf.Update(5, null, 9.9, 6, out ar);
            Eq("fallback by ranges ~60 kn", Near(ev, 60, 0.5) && ar == 1);
            cf.Update(6, 1, null, 0, out ar); cf.Update(7, 1, null, 0, out ar);
            cf.Forget(new[] { 6 });
            Eq("Forget drops vanished ids", cf.Count == 1);

            // --- O3 sort / frozen / config
            var rows = new List<OvRow>
            {
                new OvRow { Id = 3, Type = "b", SortDist = 5, SortClosing = 10 }, new OvRow { Id = 1, Type = "a", SortDist = 5, SortClosing = 50 },
                new OvRow { Id = 2, Type = "c", SortDist = double.NaN, SortClosing = double.NaN }, new OvRow { Id = 4, Type = "d", SortDist = 2, SortClosing = -4 }
            };
            Sort(rows, "dist", true);
            Eq("sort dist asc: ties by Id, NaN last", string.Join(",", rows.Select(r => r.Id)) == "4,1,3,2");
            Sort(rows, "dist", false);
            Eq("sort dist desc: NaN still last", string.Join(",", rows.Select(r => r.Id)) == "1,3,4,2");
            Sort(rows, "vcl", false);
            Eq("sort vcl desc: fast closing on top", string.Join(",", rows.Select(r => r.Id)) == "1,3,4,2");
            Sort(rows, "type", false);
            Eq("sort type desc", string.Join(",", rows.Select(r => r.Id)) == "4,2,3,1");
            Sort(rows, "dist", true);
            var fresh = new List<OvRow> { rows[0], rows[1], rows[2], rows[3] };  // 4,1,3,2
            var fz = ApplyFrozen(fresh, rows, 2, 0);
            Eq("frozen row keeps its index", string.Join(",", fz.Select(r => r.Id)) == "2,4,1,3");
            fz = ApplyFrozen(fresh, rows, 99, 1);
            Eq("vanished frozen row: no exception", string.Join(",", fz.Select(r => r.Id)) == "4,1,3,2");
            double rx, ry, rw, rh;
            Eq("ParseRect ok", ParseRect("0.70,0.08,0.28,0.40", out rx, out ry, out rw, out rh) && Near(rx, 0.7, 1e-9) && Near(rh, 0.4, 1e-9));
            Eq("ParseRect bad", !ParseRect("0.7,0.1,0.3", out rx, out ry, out rw, out rh) && !ParseRect("0.7,0.1,1.3,0.4", out rx, out ry, out rw, out rh) && !ParseRect("-0.1,0.1,0.3,0.4", out rx, out ry, out rw, out rh) && !ParseRect("a,b,c,d", out rx, out ry, out rw, out rh) && !ParseRect("", out rx, out ry, out rw, out rh));
            double cx = 1900, cy = 1070, cw = 500, ch = 300;
            ClampRect(ref cx, ref cy, ref cw, ref ch, 320, 120, 1920, 1080);
            Eq("ClampRect at the edge 1920x1080", cx == 1420 && cy == 780 && cw == 500 && ch == 300);
            cx = -50; cy = -5; cw = 100; ch = 50;
            ClampRect(ref cx, ref cy, ref cw, ref ch, 320, 120, 1280, 720);
            Eq("ClampRect minimum size, 1280x720", cx == 0 && cy == 0 && cw == 320 && ch == 120);
            string col; bool asc;
            Eq("ParseSort", ParseSort("dist:asc", out col, out asc) && col == "dist" && asc && ParseSort("vcl:desc", out col, out asc) && col == "vcl" && !asc && ParseSort("spd", out col, out asc) && asc &&
                            !ParseSort("foo:asc", out col, out asc) && !ParseSort("dist:up", out col, out asc) && !ParseSort("", out col, out asc));
            string err;
            Eq("ParseColumns ok", ParseColumns("icon,type,dist,brg,spd,alt,crs", out err).Length == 7 && ParseColumns("icon,type,id,vcl", out err).Length == 4);
            Eq("ParseColumns bad", ParseColumns("icon,type,foo", out err) == null && err != null && ParseColumns("type,dist", out err) == null && ParseColumns("icon,dist", out err) == null);
            Eq("hotkey taken by O", !HotkeyFree(new[] { "LeftAlt", "O" }, new List<string[]> { new[] { "O" } }));
            Eq("hotkey free against LeftAlt+W", HotkeyFree(new[] { "LeftAlt", "O" }, new List<string[]> { new[] { "LeftAlt", "W" } }));
            Eq("hotkey taken by LeftAlt+O", !HotkeyFree(new[] { "LeftAlt", "O" }, new List<string[]> { new[] { "LeftAlt", "O" } }));
            Eq("hotkey free against Ctrl+O", HotkeyFree(new[] { "LeftAlt", "O" }, new List<string[]> { new[] { "LeftControl", "O" } }));
            string src;
            var cur = new object(); var last = new object();
            Eq("pick: own selected", ReferenceEquals(PickCommandUnit(cur, true, last, true, out src), cur) && src == "render_position");
            Eq("pick: contact selected -> last own", ReferenceEquals(PickCommandUnit(cur, false, last, true, out src), last) && src == "last_own");
            Eq("pick: dead last own -> null", PickCommandUnit(cur, false, last, false, out src) == null && src == null);
            var fr = new OverviewFrame { Hostile = 3, Unknown = 2 };
            fr.Rows.Add(new OvRow { SortDist = 8.2, Arrow = 1 }); fr.Rows.Add(new OvRow { SortDist = 30 });
            Eq("summary ru", Summary(fr, "ru") == "Обзор: 3 враг · 2 неопозн. · ближ. 8,2 ▼");
            Eq("summary en", Summary(fr, "en") == "Overview: 3 hostile · 2 unk. · nearest 8.2 ▼");
            Eq("summary empty", Summary(new OverviewFrame(), "ru") == "Обзор: 0 враг · 0 неопозн.");
            // --- T-080
            string hc;
            Eq("verdict: null bindings unknown", HotkeyVerdict(new[] { "LeftAlt", "O" }, null, null, out hc) == "unknown");
            Eq("verdict: empty list unknown", HotkeyVerdict(new[] { "LeftAlt", "O" }, new List<string[]>(), null, out hc) == "unknown");
            Eq("verdict: LeftShift+O free", HotkeyVerdict(new[] { "LeftAlt", "O" }, new List<string[]> { new[] { "LeftShift", "O" } }, null, out hc) == "free");
            Eq("verdict: O taken + conflict", HotkeyVerdict(new[] { "LeftAlt", "O" }, new List<string[]> { new[] { "O" } }, new[] { "X" }, out hc) == "taken" && hc == "X (O)");
            Eq("verdict: LeftAlt+O taken", HotkeyVerdict(new[] { "LeftAlt", "O" }, new List<string[]> { new[] { "LeftAlt", "O" } }, null, out hc) == "taken");
            Eq("FitHeight 6 rows", Near(FitHeight(6, 1), 20 + 18 + 108 + 4, 1e-9));
            Eq("FitHeight 0 rows -> MinH", Near(FitHeight(0, 1), 120, 1e-9));
            Eq("FitHeight 30 rows x2", Near(FitHeight(30, 2), (20 + 18 + 540 + 4) * 2, 1e-9));
            Eq("icon source none", IconSourceOf(0, 0, 0) == "none");
            Eq("icon source map", IconSourceOf(5, 0, 0) == "map" && IconSourceOf(5, 0, 1) == "map");
            Eq("icon source mixed", IconSourceOf(2, 0, 2) == "mixed");
            Eq("icon source own", IconSourceOf(0, 0, 3) == "own");
            var tn = Tr(12, "hostile"); tn.ClassName = "P-6"; tn.Class = "wp_pt_p6";
            Eq("type: ClassName wins", TypeText(tn, "ru") == "P-6");
            tn.ClassName = null; Eq("type: code without ClassName", TypeText(tn, "ru") == "wp_pt_p6");
            tn.Class = null; Eq("type: fallback surface", TypeText(tn, "ru") == "Надв. #12");
            // --- T-092
            Eq("PostureOf", PostureOf("hostile") == "Enemy" && PostureOf("friendly") == "Allied" && PostureOf("neutral") == "Neutral" && PostureOf("unknown") == "Unknown" && PostureOf(null) == "Unknown");
            var tu = Tr(7017, "unknown", "Air"); tu.Posture = PostureOf(tu.Side); tu.Colour = 0;
            var ru = MakeRow(tu, null, null, null, null, new ClosingFilter(3, 3, 1, 300), 0, null, "ru");
            Eq("accept: unknown air", Include(tu, true) && !ru.Hostile && ru.Icon == "Air/Unknown" && ru.Type == "Воздушн. #7017");
            Eq("fallback colour", FallbackColour("Enemy", false, false) == 0xFFFF0000u && FallbackColour("Unknown", false, false) == 0xFF666600u && FallbackColour("Neutral", false, false) == 0xFF009900u &&
                                  FallbackColour("Allied", false, false) == 0xFFB35900u && FallbackColour("Enemy", true, false) == 0xFF979899u && FallbackColour("Enemy", true, true) == 0xFF000000u);
            Eq("stale tint", StaleTint(0xFFFF0000u) == 0x99FF0000u);
            tu.SideSrc = "none"; tu.ColourSrc = "fallback"; tu.Colour = 0xFF666600u; tu.VmMain = false; tu.VmPosture = "Enemy";
            string dl = DebugLine(tu, ru);
            Eq("debug line", dl.Contains("track 7017") && dl.Contains("side=unknown src=none") && dl.Contains("posture=Unknown") && dl.Contains("colour=#FF666600 src=fallback") && dl.Contains("live=true") && dl.Contains("dormant=false") && dl.Contains("vm_main=false"));
            // --- T-209
            Eq("track alt: null", TrackAltFt(null) == null && TrackAltFt(double.NaN) == null && TrackAltFt(double.PositiveInfinity) == null);
            Eq("track alt: values", Near(TrackAltFt(40.8213).Value, 9000, 1) && TrackAltFt(0).Value == 0 && Near(TrackAltFt(-0.5).Value, -110, 1));
            Eq("debug line alt dash", dl.Contains("alt_ft=-"));
            tu.AltFt = 9000; Eq("debug line alt", DebugLine(tu, ru).Contains("alt_ft=9000"));
            Text = savedText;
            return _fail;
        }
    }
}
