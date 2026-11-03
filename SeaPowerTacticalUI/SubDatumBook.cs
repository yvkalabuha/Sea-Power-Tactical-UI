using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VoiceBotBridge
{
    // T-288 / ADR-0115: the journal of "sub launch area" sectors (pure, no game; time = mission seconds).

    internal sealed class SubDatumTrackIn
    {
        public int TrackId; public double Lat, Lon, NowS;
        public bool Globe;                              // T-352: the world of the game (false = flat, stable); set by SubDatumGame = AswMath.WorldGlobe
        public string Relation;                         // hostile | unknown (others are filtered by the game layer, the book checks again)
        public bool Identified; public string Ammo;     // null = "?"
        public double RunNm;                            // SubDatumMath.RunNm result
        public SubDatumAxisIn Axis = new SubDatumAxisIn();
        public double NearestOwnNm = double.PositiveInfinity;   // P0 -> nearest own unit
        public bool AirNear;                            // hostile/unknown air track within AirSkipNm of P0 in the last AirSkipS
        public List<DxY> OwnShipsFromP0 = new List<DxY>();
    }

    internal sealed class SubDatum
    {
        public int Id, TrackId; public List<int> Tracks = new List<int>();
        public double Lat0, Lon0, T0S, ThetaDeg, SigmaDeg, RmaxNm, CoreLenNm, UpdatedS, ClosedAtS = -1;
        public string Src, Ammo, State = "live";        // live | found | expired | dismissed
        public bool Frozen; public int Merges;
        public bool Globe;                              // T-352: the frame of Core/Far, fixed at creation (= t.Globe in Offer)
        public List<DxY> Core = new List<DxY>(), Far = new List<DxY>();   // from (Lat0, Lon0)
        public string SearchGroup; public double[] SearchRect;
    }

    internal sealed class SubDatumBook
    {
        private readonly SubDatumParams _p;
        private readonly Action<string> _log;
        private readonly List<SubDatum> _list = new List<SubDatum>();
        private readonly HashSet<int> _dead = new HashSet<int>();                  // tracks of removed sectors
        private readonly Dictionary<int, string> _skipped = new Dictionary<int, string>();
        private readonly List<SubDatum> _recentFound = new List<SubDatum>();   // T-294: found sectors removed by Tick while now - T0S <= MergeS
        private int _nextId = 1;

        public SubDatumBook(SubDatumParams p, Action<string> log) { _p = p ?? new SubDatumParams(); _log = log ?? (s => { }); }

        public SubDatumParams Params { get { return _p; } }
        public IList<SubDatum> All { get { return _list; } }
        public int Revision { get; private set; }

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static string N(double v, string f = "0.##") { return v.ToString(f, Inv); }
        private void Log(string s) { _log("[SubDatum] " + s); }

        private static int Rank(string src) { return src == "vel" ? 2 : src == "fixes" ? 1 : 0; }

        private SubDatum ByTrack(int trk)
        {
            foreach (SubDatum d in _list) if (d.Tracks.Contains(trk)) return d;
            return null;
        }

        private string Skip(SubDatumTrackIn t, string reason)
        {
            string last;
            if (_skipped.TryGetValue(t.TrackId, out last) && last == reason) return null;
            _skipped[t.TrackId] = reason;
            Log("skip trk=" + t.TrackId + " reason=" + reason);
            return "skip:" + reason;
        }

        private void Build(SubDatum d, double theta, double sigma, string src, SubDatumTrackIn t)
        {
            double dtgt = SubDatumMath.DTgtNm(Normalize(theta + 180), t.OwnShipsFromP0);
            d.ThetaDeg = theta; d.SigmaDeg = sigma; d.Src = src;
            d.RmaxNm = SubDatumMath.Rmax(t.RunNm, dtgt, _p.MaxLenNm);
            d.CoreLenNm = Math.Min(d.RmaxNm, _p.CoreNm);
            d.Core = SubDatumMath.Sector(theta, sigma, 0, d.CoreLenNm);
            d.Far = SubDatumMath.Sector(theta, sigma, 0, d.RmaxNm);
        }

        private static double Normalize(double a) { a %= 360; return a < 0 ? a + 360 : a; }

        /// <summary>First sight of a track or a new fix: new | refine | merge | skip:&lt;own|far|air|relation|axis&gt; | null (nothing to do).</summary>
        public string Offer(SubDatumTrackIn t)
        {
            if (t == null) return null;
            SubDatum known = ByTrack(t.TrackId);
            if (known != null) return Refine(known, t);
            if (_dead.Contains(t.TrackId)) return null;
            if (t.Relation == "own") return Skip(t, "own");
            if (t.Relation != "hostile" && t.Relation != "unknown") return Skip(t, "relation");
            if (t.NearestOwnNm > _p.TriggerNm) return Skip(t, "far");
            if (t.AirNear) return Skip(t, "air");
            double theta, sigma; string src;
            if (!SubDatumMath.Axis(t.Axis, out theta, out sigma, out src)) return Skip(t, "axis");
            _skipped.Remove(t.TrackId);

            var d = new SubDatum { Id = _nextId, TrackId = t.TrackId, Lat0 = t.Lat, Lon0 = t.Lon, T0S = t.NowS, UpdatedS = t.NowS, Ammo = t.Ammo, Globe = t.Globe };
            d.Tracks.Add(t.TrackId);
            Build(d, theta, sigma, src, t);
            string merged = TryMerge(d, t);
            if (merged != null) return merged;

            int live = 0; SubDatum oldest = null;
            foreach (SubDatum x in _list) if (x.State == "live") { live++; if (oldest == null || x.T0S < oldest.T0S) oldest = x; }
            if (live >= _p.MaxLive && oldest != null) Close(oldest, "expired", t.NowS);

            _nextId++;
            _list.Add(d);
            Revision++;
            Log("new id=" + d.Id + " trk=" + d.TrackId + " p0=" + N(d.Lat0, "0.#####") + "," + N(d.Lon0, "0.#####") + " brg=" + N(d.ThetaDeg, "0.#")
                + " sigma=" + N(d.SigmaDeg, "0.#") + " src=" + d.Src + " r=0.." + N(d.RmaxNm, "0.#") + " core=" + N(d.CoreLenNm, "0.#") + " ammo=" + (d.Ammo ?? "?"));
            return "new";
        }

        private string TryMerge(SubDatum nd, SubDatumTrackIn t)
        {
            for (int i = _list.Count - 1; i >= 0; i--)
            {
                SubDatum o = _list[i];
                if (o.State != "live" || t.NowS - o.T0S > _p.MergeS || o.Globe != nd.Globe) continue;   // T-352: sectors of another world do not merge
                DxY off = SubDatumMath.ToLocal(o.Lat0, o.Lon0, nd.Lat0, nd.Lon0, o.Globe);
                var far = SubDatumMath.Clip(Shift(nd.Far, off), o.Far);
                if (far.Count < 3) continue;
                var core = SubDatumMath.Clip(Shift(nd.Core, off), o.Core);
                if (core.Count < 3) continue;
                o.Far = far; o.Core = core;
                o.Tracks.Add(t.TrackId); o.Merges++; o.UpdatedS = t.NowS;
                o.CoreLenNm = Math.Min(o.CoreLenNm, nd.CoreLenNm);
                Revision++;
                Log("merge id=" + o.Id + " trk=" + t.TrackId + " area_nm2=" + N(SubDatumMath.Area(far), "0.0"));
                return "merge";
            }
            // T-294: a found sector (listed or recently removed) absorbs the track without a new card; polygons stay as they are
            for (int pass = 0; pass < 2; pass++)
            {
                List<SubDatum> src = pass == 0 ? _list : _recentFound;
                for (int i = src.Count - 1; i >= 0; i--)
                {
                    SubDatum o = src[i];
                    if (o.State != "found" || t.NowS - o.T0S > _p.MergeS || o.Globe != nd.Globe) continue;
                    DxY off = SubDatumMath.ToLocal(o.Lat0, o.Lon0, nd.Lat0, nd.Lon0, o.Globe);
                    if (SubDatumMath.Clip(Shift(nd.Far, off), o.Far).Count < 3) continue;
                    if (SubDatumMath.Clip(Shift(nd.Core, off), o.Core).Count < 3) continue;
                    o.Tracks.Add(t.TrackId); o.Merges++; o.UpdatedS = t.NowS;
                    if (pass == 1) _dead.Add(t.TrackId);
                    Revision++;
                    Log("merge id=" + o.Id + " trk=" + t.TrackId + " state=found");
                    return "merge";
                }
            }
            return null;
        }

        private static List<DxY> Shift(IList<DxY> poly, DxY off)
        {
            var r = new List<DxY>(poly.Count);
            foreach (DxY p in poly) r.Add(new DxY(p.X + off.X, p.Y + off.Y));
            return r;
        }

        private string Refine(SubDatum d, SubDatumTrackIn t)
        {
            if (d.State != "live" || d.Frozen || d.TrackId != t.TrackId || d.Merges > 0) return null;
            if (t.NowS - d.T0S > _p.RefineS) { d.Frozen = true; return null; }
            double theta, sigma; string src;
            if (!SubDatumMath.Axis(t.Axis, out theta, out sigma, out src) || Rank(src) <= Rank(d.Src)) return null;
            Build(d, theta, sigma, src, t);
            d.UpdatedS = t.NowS;
            Revision++;
            Log("refine id=" + d.Id + " brg=" + N(d.ThetaDeg, "0.#") + " sigma=" + N(d.SigmaDeg, "0.#") + " src=" + d.Src);
            return "refine";
        }

        private double EffNow(SubDatum d, double nowS) { return d.State == "live" || d.ClosedAtS < 0 ? nowS : Math.Min(nowS, d.ClosedAtS); }

        /// <summary>Core/Far plus a circle of SubKn x (now - T0S).</summary>
        public List<DxY> Inflated(SubDatum d, bool core, double nowS)
        {
            double b = _p.SubKn * Math.Max(0, EffNow(d, nowS) - d.T0S) / 3600.0;
            return SubDatumMath.Inflate(core ? d.Core : d.Far, b);
        }

        /// <summary>The 8 numbers lat/lon of the order: the box of the inflated core, a side at most AreaMaxNm.</summary>
        public double[] OrderRect(SubDatum d, double nowS)
        {
            double x0, y0, x1, y1;
            SubDatumMath.AreaRect(Inflated(d, true, nowS), _p.AreaMaxNm, out x0, out y0, out x1, out y1);
            return SubDatumMath.RectGeo(d.Lat0, d.Lon0, x0, y0, x1, y1, d.Globe);
        }

        /// <summary>A sub contact/track inside an inflated sector: the sector is found.</summary>
        public void NoteSub(double lat, double lon, double nowS)
        {
            foreach (SubDatum d in new List<SubDatum>(_list))
            {
                if (d.State != "live") continue;
                DxY p = SubDatumMath.ToLocal(d.Lat0, d.Lon0, lat, lon, d.Globe);
                if (!SubDatumMath.Contains(Inflated(d, false, nowS), p)) continue;
                Close(d, "found", nowS);
                Log("found id=" + d.Id + " at=" + N(lat, "0.#####") + "," + N(lon, "0.#####"));
            }
        }

        private void Close(SubDatum d, string state, double nowS)
        {
            d.State = state; d.ClosedAtS = nowS;
            Revision++;
            if (state == "expired") Log("expired id=" + d.Id);
        }

        /// <summary>T-356: the world of the game changed (the decision came after the datum was made): the live sectors get the new frame (the core is in physical nm, only the nm to degrees conversion changes).</summary>
        public void SetWorld(bool globe)
        {
            bool any = false;
            foreach (SubDatum d in _list)
                if (d.State == "live" && d.Globe != globe) { d.Globe = globe; any = true; }
            if (any) Revision++;
        }

        /// <summary>Freeze after RefineS, expire after TtlS, drop grey sectors after FoundCloseS.</summary>
        public void Tick(double nowS)
        {
            foreach (SubDatum d in new List<SubDatum>(_list))
            {
                if (d.State == "live")
                {
                    if (!d.Frozen && nowS - d.T0S > _p.RefineS) d.Frozen = true;
                    if (nowS - d.T0S > _p.TtlS) Close(d, "expired", nowS);
                }
                else if (nowS - d.ClosedAtS >= _p.FoundCloseS)
                {
                    foreach (int t in d.Tracks) _dead.Add(t);
                    _list.Remove(d);
                    if (d.State == "found" && nowS - d.T0S <= _p.MergeS) _recentFound.Add(d);
                    Revision++;
                }
            }
            _recentFound.RemoveAll(x => nowS - x.T0S > _p.MergeS);
        }

        public bool Dismiss(int id, double nowS)
        {
            SubDatum d = Find(id);
            if (d == null) return false;
            d.State = "dismissed"; d.ClosedAtS = nowS;
            foreach (int t in d.Tracks) _dead.Add(t);
            _list.Remove(d);
            Revision++;
            Log("dismissed id=" + id);
            return true;
        }

        public void NoteSearch(int id, string group, double[] rect)
        {
            SubDatum d = Find(id);
            if (d == null) return;
            d.SearchGroup = group; d.SearchRect = rect;
            Revision++;
            var sb = new StringBuilder();
            if (rect != null) for (int i = 0; i < rect.Length; i++) { if (i > 0) sb.Append(i % 2 == 0 ? ";" : ","); sb.Append(N(rect[i], "0.#####")); }
            Log("search id=" + id + " group=" + group + " rect=" + sb);
        }

        public SubDatum Find(int id)
        {
            foreach (SubDatum d in _list) if (d.Id == id) return d;
            return null;
        }

        public void Clear()
        {
            _list.Clear(); _recentFound.Clear(); _dead.Clear(); _skipped.Clear();
            Revision++;
        }

        /// <summary>[{id,trk,state,p0:[lat,lon],brg,sigma,src,r,core,ammo,age_s,merges,group,rect}], at most limit entries.</summary>
        public string Json(int limit, double nowS)
        {
            var sb = new StringBuilder("[");
            int n = 0;
            foreach (SubDatum d in _list)
            {
                if (n >= limit) break;
                if (n++ > 0) sb.Append(',');
                sb.Append("{\"id\":").Append(d.Id).Append(",\"trk\":").Append(d.TrackId).Append(",\"state\":").Append(VoiceBotBridge.Json.Str(d.State))
                    .Append(",\"p0\":[").Append(VoiceBotBridge.Json.Num(d.Lat0)).Append(',').Append(VoiceBotBridge.Json.Num(d.Lon0)).Append(']')
                    .Append(",\"brg\":").Append(VoiceBotBridge.Json.Num(Math.Round(d.ThetaDeg, 1))).Append(",\"sigma\":").Append(VoiceBotBridge.Json.Num(d.SigmaDeg))
                    .Append(",\"src\":").Append(VoiceBotBridge.Json.Str(d.Src)).Append(",\"r\":").Append(VoiceBotBridge.Json.Num(Math.Round(d.RmaxNm, 2)))
                    .Append(",\"core\":").Append(VoiceBotBridge.Json.Num(Math.Round(d.CoreLenNm, 2))).Append(",\"ammo\":").Append(VoiceBotBridge.Json.Str(d.Ammo))
                    .Append(",\"age_s\":").Append(VoiceBotBridge.Json.Num(Math.Round(Math.Max(0, EffNow(d, nowS) - d.T0S), 1)))
                    .Append(",\"merges\":").Append(d.Merges).Append(",\"group\":").Append(VoiceBotBridge.Json.Str(d.SearchGroup)).Append(",\"rect\":");
                if (d.SearchRect == null) sb.Append("null");
                else
                {
                    sb.Append('[');
                    for (int i = 0; i < d.SearchRect.Length; i++) { if (i > 0) sb.Append(','); sb.Append(VoiceBotBridge.Json.Num(d.SearchRect[i])); }
                    sb.Append(']');
                }
                sb.Append('}');
            }
            return sb.Append(']').ToString();
        }
    }
}
