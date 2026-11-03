using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>T-288 A4: the sector book (new / refine / merge / skip / ttl / found / dismiss / json / revision).</summary>
    internal static class SubDatumBookTests
    {
        private sealed class Env
        {
            public SubDatumBook Book; public List<string> Log = new List<string>(); public SubDatumParams P = new SubDatumParams();
            public Env(Action<SubDatumParams> tune = null) { if (tune != null) tune(P); Book = new SubDatumBook(P, Log.Add); }
            public string Joined() { return string.Join("\n", Log); }
        }

        private static SubDatumTrackIn T(int id, double now, double lat = 35, double lon = 20, double velCourse = 170, double velKn = 30)
        {
            return new SubDatumTrackIn
            {
                TrackId = id, Lat = lat, Lon = lon, NowS = now, Relation = "hostile", Identified = true, Ammo = "71M", RunNm = 12, NearestOwnNm = 4,
                Axis = new SubDatumAxisIn { VelKn = velKn, VelCourseDeg = velCourse },
            };
        }

        public static string New()
        {
            var e = new Env();
            string r = e.Book.Offer(T(1, 100));
            if (r != "new") return "offer " + r;
            if (e.Book.All.Count != 1) return "count";
            SubDatum d = e.Book.All[0];
            if (d.Id < 1 || d.TrackId != 1 || d.Tracks.Count != 1 || d.State != "live") return "fields";
            if (d.Lat0 != 35 || d.Lon0 != 20 || d.T0S != 100) return "p0/t0";
            if (Math.Abs(d.ThetaDeg - 350) > 1e-6 || d.SigmaDeg != 15 || d.Src != "vel" || d.Ammo != "71M") return "axis fields " + d.ThetaDeg + "/" + d.SigmaDeg + "/" + d.Src;
            if (Math.Abs(d.RmaxNm - 12) > 1e-9 || Math.Abs(d.CoreLenNm - 6) > 1e-9) return "lengths " + d.RmaxNm + "/" + d.CoreLenNm;
            if (d.Core.Count < 3 || d.Far.Count < 3 || SubDatumMath.Area(d.Far) <= SubDatumMath.Area(d.Core)) return "polygons";
            string j = e.Joined();
            foreach (string s in new[] { "[SubDatum] new id=1 trk=1", "brg=350", "sigma=15", "src=vel", "r=0..12", "core=6", "ammo=71M", "p0=35,20" })
                if (j.IndexOf(s, StringComparison.Ordinal) < 0) return "log lacks '" + s + "': " + j;
            var e2 = new Env();
            var t = T(2, 10); t.RunNm = 4; t.Ammo = null;
            e2.Book.Offer(t);
            if (Math.Abs(e2.Book.All[0].CoreLenNm - 4) > 1e-9) return "core = min(Rmax, CoreNm)";
            if (e2.Joined().IndexOf("ammo=?", StringComparison.Ordinal) < 0) return "unknown ammo must log ?";
            // the nearest own ship ahead of the torpedo shortens the run
            var e3 = new Env();
            var t3 = T(3, 10); t3.OwnShipsFromP0.Add(new DxY(0, 3.2 * -1)); t3.Axis = new SubDatumAxisIn { VelKn = 30, VelCourseDeg = 180 };
            e3.Book.Offer(t3);
            if (Math.Abs(e3.Book.All[0].RmaxNm - (12 - 3.2)) > 1e-6) return "d_tgt not applied: " + e3.Book.All[0].RmaxNm;
            return null;
        }

        /// <summary>T-352: the datum remembers the world of its frame; a point 14 nm east on the flat map (7 nm on the 60N globe) is outside the 12 nm sector on stable and inside on the beta.</summary>
        public static string WorldFlat()
        {
            foreach (bool globe in new[] { false, true })
            {
                var e = new Env();
                var t = T(1, 100, 60, 20, 270); t.Globe = globe;   // moving west: the sector points east
                if (e.Book.Offer(t) != "new" || e.Book.All[0].Globe != globe) return "datum frame globe=" + globe;
                e.Book.NoteSub(60, 20 + 14 / 60.0, 100);
                string want = globe ? "found" : "live";
                if (e.Book.All[0].State != want) return "NoteSub 14 nm east of the flat map globe=" + globe + ": " + e.Book.All[0].State;
                if (globe) continue;
                e.Book.NoteSub(60, 20 + 7 / 60.0, 100);
                if (e.Book.All[0].State != "found") return "NoteSub 7 nm east on the flat map must find it";
            }
            // sectors of two worlds never merge
            var m = new Env();
            var a = T(1, 100, 60, 20, 270); a.Globe = false;
            var b = T(2, 101, 60, 20, 270); b.Globe = true;
            m.Book.Offer(a);
            if (m.Book.Offer(b) != "new" || m.Book.All.Count != 2) return "sectors of two worlds must not merge";
            return null;
        }

        public static string Skip()
        {
            var e = new Env();
            var own = T(1, 10); own.Relation = "own";
            if (e.Book.Offer(own) != "skip:own") return "own";
            var far = T(2, 10); far.NearestOwnNm = 13;
            if (e.Book.Offer(far) != "skip:far") return "far";
            var air = T(3, 10); air.AirNear = true;
            if (e.Book.Offer(air) != "skip:air") return "air";
            var fr = T(4, 10); fr.Relation = "friendly";
            if (e.Book.Offer(fr) != "skip:relation") return "relation";
            if (e.Book.All.Count != 0) return "skipped tracks must not create sectors";
            string j = e.Joined();
            if (j.IndexOf("skip trk=2 reason=far", StringComparison.Ordinal) < 0) return "skip log: " + j;
            int n = e.Log.Count;
            e.Book.Offer(far);
            if (e.Log.Count != n) return "the same skip must be logged once";
            var near = T(2, 20); near.NearestOwnNm = 10;
            if (e.Book.Offer(near) != "new") return "far track that came closer must create a sector";
            var unk = T(5, 20); unk.Relation = "unknown"; unk.Axis = new SubDatumAxisIn { LosBrgDeg = 100 };
            if (e.Book.Offer(unk) == null) return "unknown relation is allowed";
            return null;
        }

        private static Env FoundEnv(Action<SubDatumParams> tune = null)
        {
            var e = new Env(tune);
            e.Book.Offer(T(1, 100, 35, 20, 180));
            e.Book.NoteSub(35 + 3 / 60.0, 20, 100);
            return e;
        }

        /// <summary>T-295: a track that moves away from every own ship (7056, Yorktown ~107 deg off course) still builds a sector.</summary>
        public static string Receding()
        {
            var e = new Env();
            SubDatumTrackIn t = T(7056, 100, 43.30056, 12.77362, 133.3);
            t.OwnShipsFromP0.Add(new DxY(0.1, 0.21));
            t.NearestOwnNm = 0.23;
            if (e.Book.Offer(t) != "new") return "sector expected";
            if (e.Book.All.Count != 1 || e.Book.All[0].State != "live") return "one live sector";
            if (e.Joined().IndexOf("skip", StringComparison.Ordinal) >= 0) return "no skip logged";
            return null;
        }

        public static string MergeFound()
        {
            var e = FoundEnv();
            if (e.Book.All[0].State != "found") return "found";
            double a0 = SubDatumMath.Area(e.Book.All[0].Far), c0 = SubDatumMath.Area(e.Book.All[0].Core);
            if (e.Book.Offer(T(2, 102, 35, 20, 180)) != "merge") return "t2";
            if (e.Book.Offer(T(3, 108, 35.0004, 20.0002, 180, 18)) != "merge") return "t3";
            if (e.Book.Offer(T(4, 110, 35.0004, 20.0002, 180, 18)) != "merge") return "t4";
            if (e.Book.All.Count != 1) return "one sector " + e.Book.All.Count;
            SubDatum d = e.Book.All[0];
            if (d.Tracks.Count != 4 || d.State != "found") return "fields " + d.Tracks.Count + d.State;
            if (Math.Abs(SubDatumMath.Area(d.Far) - a0) > 1e-9 || Math.Abs(SubDatumMath.Area(d.Core) - c0) > 1e-9) return "polygons changed";
            if (e.Joined().IndexOf("merge id=1 trk=2 state=found", StringComparison.Ordinal) < 0) return "log";
            return null;
        }

        public static string MergePrefersLive()
        {
            var e = new Env();
            e.Book.Offer(T(1, 100, 35, 20, 180));
            e.Book.NoteSub(35 + 3 / 60.0, 20, 100);
            e.Book.Offer(T(2, 110, 37, 20, 180));   // disjoint live sector
            e.Book.Offer(T(3, 120, 37.0004, 20, 180));
            SubDatum live = null; foreach (SubDatum d in e.Book.All) if (d.TrackId == 2) live = d;
            if (live == null || live.Tracks.Count != 2 || e.Book.All.Count != 2) return "merge into live";
            // overlapping live + found: live first
            var o = new Env();
            o.Book.Offer(T(1, 100, 35, 20, 180));
            o.Book.NoteSub(35 + 3 / 60.0, 20, 100);
            o.Book.Offer(T(2, 105, 35.3, 20, 90));
            SubDatum lv = null; foreach (SubDatum d in o.Book.All) if (d.TrackId == 2) lv = d;
            if (lv == null) return "live sector expected (does not overlap found)";
            // T-294 review: a live and a found sector both overlap the new track -> the live one takes it
            var w = new Env();
            var tl = T(1, 100, 35, 20, 170); tl.RunNm = 2;
            var tf = T(2, 101, 35.1, 20, 0); tf.RunNm = 2;
            w.Book.Offer(tl);
            if (w.Book.Offer(tf) != "new") return "setup: disjoint cores expected";
            w.Book.NoteSub(35.09, 20, 102);   // inside the second sector only
            SubDatum L = null, F = null; foreach (SubDatum d in w.Book.All) { if (d.TrackId == 1) L = d; if (d.TrackId == 2) F = d; }
            if (L == null || F == null || L.State != "live" || F.State != "found") return "setup states " + (L == null ? "-" : L.State) + "/" + (F == null ? "-" : F.State);
            var tn = T(3, 110, 35.02, 20, 170);
            if (w.Book.Offer(tn) != "merge") return "overlap merge";
            if (L.Tracks.Count != 2 || F.Tracks.Count != 1) return "live must take the track first: " + L.Tracks.Count + "/" + F.Tracks.Count;
            return null;
        }

        public static string MergeFoundWindow()
        {
            var e = FoundEnv();
            if (e.Book.Offer(T(2, 100 + 601, 35, 20, 180)) != "new") return "window";
            return null;
        }

        public static string NoMergeDismissed()
        {
            var e = FoundEnv();
            if (!e.Book.Dismiss(e.Book.All[0].Id, 110)) return "dismiss";
            if (e.Book.Offer(T(2, 120, 35, 20, 180)) != "new") return "new after dismiss";
            return null;
        }

        public static string MergeRecent()
        {
            var e = FoundEnv();
            e.Book.Tick(100 + 120);
            if (e.Book.All.Count != 0) return "removed";
            if (e.Book.Offer(T(5, 640, 35, 20, 180)) != "merge") return "recent merge";
            if (e.Book.All.Count != 0) return "no new sector";
            if (e.Book.Offer(T(5, 650, 35, 20, 180)) != null) return "track dead";
            if (e.Book.Offer(T(6, 701, 35, 20, 180)) != "new") return "window over";
            var c = FoundEnv();
            c.Book.Tick(220); c.Book.Clear();
            if (c.Book.Offer(T(7, 300, 35, 20, 180)) != "new") return "clear";
            return null;
        }

        public static string Refine()
        {
            var e = new Env();
            var t = T(1, 100); t.Axis = new SubDatumAxisIn { LosBrgDeg = 350 };
            if (e.Book.Offer(t) != "new") return "new";
            SubDatum d = e.Book.All[0];
            if (d.SigmaDeg != 30 || d.Src != "los") return "los first " + d.SigmaDeg + d.Src;
            int rev = e.Book.Revision;
            var t2 = T(1, 130); t2.Axis = new SubDatumAxisIn { VelKn = 2, FixDtS = 30, FixDistNm = 0.4, FixCourseDeg = 170, LosBrgDeg = 350 };
            if (e.Book.Offer(t2) != "refine") return "refine";
            if (d.SigmaDeg != 15 || d.Src != "fixes" || Math.Abs(d.ThetaDeg - 350) > 1e-6) return "refined fields";
            if (e.Book.Revision <= rev) return "revision after refine";
            if (e.Joined().IndexOf("refine id=" + d.Id, StringComparison.Ordinal) < 0) return "refine log";
            rev = e.Book.Revision;
            var t3 = T(1, 140); t3.Axis = new SubDatumAxisIn { VelKn = 2, FixDtS = 40, FixDistNm = 0.5, FixCourseDeg = 170 };
            if (e.Book.Offer(t3) != null) return "no better axis -> null";
            if (e.Book.Revision != rev) return "no change must not bump revision";
            var t4 = T(1, 161);
            if (e.Book.Offer(t4) != null) return "after RefineS -> null";
            if (!d.Frozen) return "must be frozen";
            return null;
        }

        public static string Merge()
        {
            var e = new Env();
            e.Book.Offer(T(1, 100));
            double a0 = SubDatumMath.Area(e.Book.All[0].Far);
            string r = e.Book.Offer(T(2, 400, 35.02, 20.01));
            if (r != "merge") return "offer " + r;
            if (e.Book.All.Count != 1) return "one sector";
            SubDatum d = e.Book.All[0];
            if (d.Merges != 1 || d.Tracks.Count != 2 || !d.Tracks.Contains(2)) return "merge fields";
            double a1 = SubDatumMath.Area(d.Far);
            if (!(a1 < a0)) return "area must shrink " + a0 + " -> " + a1;
            if (d.Core.Count < 3) return "core after merge";
            if (e.Joined().IndexOf("merge id=" + d.Id + " trk=2", StringComparison.Ordinal) < 0) return "merge log";
            if (e.Book.Offer(T(2, 410, 35.02, 20.01)) != null) return "merged track is known";
            var late = new Env();
            late.Book.Offer(T(1, 100));
            if (late.Book.Offer(T(2, 100 + 601, 35.02, 20.01)) != "new") return "older than MergeS must create a new sector";
            return null;
        }

        public static string NoMerge()
        {
            var e = new Env();
            e.Book.Offer(T(1, 100));
            if (e.Book.Offer(T(2, 150, 36, 20)) != "new") return "disjoint -> new";
            if (e.Book.All.Count != 2) return "count 2";
            var o = new Env();
            o.Book.Offer(T(1, 100));
            if (o.Book.Offer(T(2, 150, 35, 20.4, 10)) != "new") return "opposite axes, apart -> new";
            return null;
        }

        public static string MaxLive()
        {
            var e = new Env();
            for (int i = 1; i <= 4; i++) e.Book.Offer(T(i, 100 + i * 10, 35 + i * 1.0, 20));
            int live = 0;
            foreach (var d in e.Book.All) if (d.State == "live") live++;
            if (live != 3) return "live " + live;
            SubDatum first = null;
            foreach (var d in e.Book.All) if (d.TrackId == 1) first = d;
            if (first == null || first.State != "expired") return "oldest must be taken off (grey)";
            return null;
        }

        public static string Ttl()
        {
            var e = new Env();
            e.Book.Offer(T(1, 100));
            e.Book.Tick(100 + 1799);
            if (e.Book.All[0].State != "live") return "still live";
            e.Book.Tick(100 + 1801);
            if (e.Book.All.Count != 1 || e.Book.All[0].State != "expired") return "expired";
            if (e.Joined().IndexOf("expired id=", StringComparison.Ordinal) < 0) return "expired log";
            e.Book.Tick(100 + 1801 + 119);
            if (e.Book.All.Count != 1) return "grey for 120 s";
            e.Book.Tick(100 + 1801 + 121);
            if (e.Book.All.Count != 0) return "removed after grey";
            return null;
        }

        public static string Found()
        {
            var e = new Env();
            e.Book.Offer(T(1, 100, 35, 20, 180));            // theta 0: the sub is north of P0
            e.Book.NoteSub(35, 20.5, 110);
            if (e.Book.All[0].State != "live") return "sub far off the axis";
            e.Book.NoteSub(35 + 3 / 60.0, 20, 110);
            SubDatum d = e.Book.All[0];
            if (d.State != "found" || d.ClosedAtS < 0) return "found " + d.State;
            if (e.Joined().IndexOf("found id=" + d.Id + " at=", StringComparison.Ordinal) < 0) return "found log";
            // buffer: 12.4 nm north is outside the raw sector (Rmax 12) but inside the 600 s buffer (0.83 nm)
            var raw = new Env(); raw.Book.Offer(T(1, 100, 35, 20, 180));
            raw.Book.NoteSub(35 + 12.4 / 60.0, 20, 100);
            if (raw.Book.All[0].State != "live") return "outside without buffer";
            var buf = new Env(); buf.Book.Offer(T(1, 100, 35, 20, 180));
            buf.Book.NoteSub(35 + 12.4 / 60.0, 20, 700);
            if (buf.Book.All[0].State != "found") return "inside the buffer";
            return null;
        }

        public static string Dismiss()
        {
            var e = new Env();
            e.Book.Offer(T(1, 100));
            int id = e.Book.All[0].Id;
            if (!e.Book.Dismiss(id, 110)) return "dismiss";
            if (e.Book.All.Count != 0) return "dismissed disappears";
            if (e.Book.Dismiss(id, 111)) return "second dismiss";
            if (e.Book.Dismiss(999, 111)) return "unknown id";
            if (e.Book.Offer(T(1, 120)) == "new") return "dismissed track must not come back";
            if (e.Joined().IndexOf("dismissed id=" + id, StringComparison.Ordinal) < 0) return "dismissed log";
            return null;
        }

        public static string InflatedGrows()
        {
            var e = new Env();
            e.Book.Offer(T(1, 100));
            SubDatum d = e.Book.All[0];
            double a0 = SubDatumMath.Area(e.Book.Inflated(d, true, 100));
            double a1 = SubDatumMath.Area(e.Book.Inflated(d, true, 700));
            double a2 = SubDatumMath.Area(e.Book.Inflated(d, true, 1900));
            if (Math.Abs(a0 - SubDatumMath.Area(d.Core)) > 1e-6) return "no age -> core";
            if (!(a1 > a0 && a2 > a1)) return "must grow " + a0 + "," + a1 + "," + a2;
            // b = 5 kn x 600 s = 0.833 nm: the farthest point of the core moves out by about that much
            DxY far = new DxY(0, 0); double best = 0;
            foreach (var p in e.Book.Inflated(d, true, 700)) { double r = Math.Sqrt(p.X * p.X + p.Y * p.Y); if (r > best) { best = r; far = p; } }
            if (Math.Abs(best - (6 + 5 * 600 / 3600.0)) > 0.1) return "buffer width " + best;
            var f = e.Book.Inflated(d, false, 700);
            if (SubDatumMath.Area(f) <= SubDatumMath.Area(e.Book.Inflated(d, true, 700))) return "far bigger than core";
            return null;
        }

        public static string OrderRect()
        {
            var e = new Env();
            e.Book.Offer(T(1, 100));
            SubDatum d = e.Book.All[0];
            foreach (double now in new[] { 100.0, 700.0, 1900.0 })
            {
                double[] r = e.Book.OrderRect(d, now);
                if (r == null || r.Length != 8) return "length";
                double dLat = (r[0] - r[4]) * 60, dLon = (r[3] - r[1]) * 60 * Math.Cos(35 * Math.PI / 180);
                if (dLat > 10.01 || dLon > 10.01 || dLat <= 0 || dLon <= 0) return "sides " + dLat + "x" + dLon + " at " + now;
            }
            return null;
        }

        public static string JsonOut()
        {
            var e = new Env();
            for (int i = 1; i <= 4; i++) e.Book.Offer(T(i, 100 + i, 35 + i * 1.0, 20));
            var all = Json.Parse(e.Book.Json(10, 200)) as List<object>;
            if (all == null || all.Count != 4) return "list";
            var one = Json.Parse(e.Book.Json(2, 200)) as List<object>;
            if (one == null || one.Count != 2) return "limit";
            var o = all[3] as Dictionary<string, object>;
            if (o == null) return "object";
            foreach (string k in new[] { "id", "trk", "state", "p0", "brg", "sigma", "src", "r", "core", "ammo", "age_s", "merges", "group", "rect" })
                if (!o.ContainsKey(k)) return "key " + k;
            var p0 = o["p0"] as List<object>;
            if (p0 == null || p0.Count != 2) return "p0";
            if (Math.Abs((double)o["age_s"] - 96) > 1e-6) return "age_s " + o["age_s"];
            if (o["group"] != null || o["rect"] != null) return "group/rect must be null before a search";
            var rect = new double[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            e.Book.NoteSearch(e.Book.All[0].Id, "ASW-1", rect);
            var o2 = (Json.Parse(e.Book.Json(1, 200)) as List<object>)[0] as Dictionary<string, object>;
            if ((string)o2["group"] != "ASW-1" || ((List<object>)o2["rect"]).Count != 8) return "search fields";
            if (e.Joined().IndexOf("search id=" + e.Book.All[0].Id + " group=ASW-1", StringComparison.Ordinal) < 0) return "search log";
            return null;
        }

        public static string Revision()
        {
            var e = new Env();
            int r = e.Book.Revision;
            e.Book.Offer(T(1, 100));
            if (e.Book.Revision <= r) return "new";
            r = e.Book.Revision;
            e.Book.Tick(110);
            if (e.Book.Revision != r) return "quiet tick";
            e.Book.NoteSearch(e.Book.All[0].Id, "G", new double[8]);
            if (e.Book.Revision <= r) return "search";
            r = e.Book.Revision;
            e.Book.Offer(T(2, 300, 35.02, 20.01));
            if (e.Book.Revision <= r) return "merge";
            r = e.Book.Revision;
            e.Book.Tick(100 + 1801);
            if (e.Book.Revision <= r) return "expire";
            r = e.Book.Revision;
            e.Book.Clear();
            if (e.Book.Revision <= r || e.Book.All.Count != 0) return "clear";
            return null;
        }
    }
}
