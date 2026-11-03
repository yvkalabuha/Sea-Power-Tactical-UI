using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VoiceBotBridge
{
    // T-256 (ADR-0086 pattern): the player's route before an own torpedo evasion, kept next to the game save. Pure: no UnityEngine, no Harmony.

    internal sealed class EvMemRecord
    {
        public int Uid; public string Name;
        public List<double[]> Route = new List<double[]>();   // {lat, lon} = ControlEntry.SavedRoute
        public double? CourseDeg;                              // ControlEntry.SavedCourseDeg
        public double[] PlacedByMod;                           // {lat, lon}, may be null
        public string TgSaved, TgSetByMod;                     // Fields["telegraph"], null = none
    }

    internal sealed class EvSaveData
    {
        public int Version = EvasionSave.Version;
        public double MissionT;
        public SavStamp Sav;
        public List<EvMemRecord> Units = new List<EvMemRecord>();
    }

    internal static class EvasionSave
    {
        public const int Version = 1;
        public const double BindTimeoutS = 60;    // pending records wait this long (mission s) for their units
        public const double ResumeGraceS = 30;    // a bound unit without a restarted evasion gets EvasionFinish after this

        public static string Key(string savPath) { return SaveSlotStore.Key(savPath); }

        private static string Pt(double[] p)
        {
            return "[" + Json.Num(p[0]) + "," + Json.Num(p[1]) + "]";
        }

        public static string ToJson(EvSaveData d)
        {
            var sb = new StringBuilder("{");
            sb.Append("\"version\":").Append(d.Version.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"mission_t\":").Append(Json.Num(d.MissionT));
            if (d.Sav != null) sb.Append(",\"sav\":").Append(d.Sav.ToJson());
            sb.Append(",\"units\":[");
            bool first = true;
            foreach (var r in d.Units)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"uid\":").Append(r.Uid.ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"name\":").Append(Json.Str(r.Name));
                sb.Append(",\"route\":[");
                for (int i = 0; i < r.Route.Count; i++) { if (i > 0) sb.Append(','); sb.Append(Pt(r.Route[i])); }
                sb.Append(']');
                if (r.CourseDeg.HasValue) sb.Append(",\"course\":").Append(Json.Num(r.CourseDeg.Value));
                if (r.PlacedByMod != null) sb.Append(",\"placed\":").Append(Pt(r.PlacedByMod));
                if (r.TgSaved != null) sb.Append(",\"tg_saved\":").Append(Json.Str(r.TgSaved));
                if (r.TgSetByMod != null) sb.Append(",\"tg_set\":").Append(Json.Str(r.TgSetByMod));
                sb.Append('}');
            }
            return sb.Append("]}").ToString();
        }

        private static double[] ParsePt(object o)
        {
            var l = o as List<object>;
            if (l == null || l.Count != 2 || !(l[0] is double) || !(l[1] is double)) return null;
            return new[] { (double)l[0], (double)l[1] };
        }

        /// <summary>Null + err on a bad file or an unsupported version.</summary>
        public static EvSaveData Parse(string json, out string err)
        {
            err = null;
            try
            {
                var root = Json.Parse(json) as Dictionary<string, object>;
                if (root == null) { err = "not_an_object"; return null; }
                object v;
                if (!root.TryGetValue("version", out v) || !(v is double) || (int)(double)v != Version) { err = "unsupported_version"; return null; }
                var d = new EvSaveData();
                if (root.TryGetValue("mission_t", out v) && v is double) d.MissionT = (double)v;
                d.Sav = root.TryGetValue("sav", out v) ? SavStamp.FromJson(v) : null;
                var units = root.TryGetValue("units", out v) ? v as List<object> : null;
                foreach (object o in units ?? new List<object>())
                {
                    var m = o as Dictionary<string, object>;
                    if (m == null) continue;
                    object x;
                    if (!m.TryGetValue("uid", out x) || !(x is double)) continue;
                    var r = new EvMemRecord { Uid = (int)(double)x };
                    r.Name = m.TryGetValue("name", out x) ? x as string : null;
                    var route = m.TryGetValue("route", out x) ? x as List<object> : null;
                    foreach (object p in route ?? new List<object>())
                    {
                        var pt = ParsePt(p);
                        if (pt != null) r.Route.Add(pt);
                    }
                    if (m.TryGetValue("course", out x) && x is double) r.CourseDeg = (double)x;
                    r.PlacedByMod = m.TryGetValue("placed", out x) ? ParsePt(x) : null;
                    r.TgSaved = m.TryGetValue("tg_saved", out x) ? x as string : null;
                    r.TgSetByMod = m.TryGetValue("tg_set", out x) ? x as string : null;
                    d.Units.Add(r);
                }
                return d;
            }
            catch (Exception e)
            {
                err = "parse: " + e.Message;
                return null;
            }
        }

        /// <summary>Pairs (record index, live index): exact uid first; then a unique equal name whose record uid is not live. Each side once.</summary>
        public static List<int[]> Bind(IList<EvMemRecord> recs, IList<int> liveUid, IList<string> liveName, out List<string> by)
        {
            var res = new List<int[]>();
            by = new List<string>();
            var recUsed = new bool[recs.Count];
            var liveUsed = new bool[liveUid.Count];
            for (int i = 0; i < recs.Count; i++)
                for (int j = 0; j < liveUid.Count; j++)
                    if (!liveUsed[j] && liveUid[j] == recs[i].Uid)
                    {
                        recUsed[i] = true; liveUsed[j] = true;
                        res.Add(new[] { i, j }); by.Add("uid");
                        break;
                    }
            for (int i = 0; i < recs.Count; i++)
            {
                if (recUsed[i] || string.IsNullOrEmpty(recs[i].Name)) continue;
                int sameRec = 0;
                for (int k = 0; k < recs.Count; k++) if (recs[k].Name == recs[i].Name) sameRec++;
                if (sameRec != 1) continue;
                int found = -1, count = 0;
                for (int j = 0; j < liveUid.Count; j++)
                    if (liveName[j] == recs[i].Name) { count++; found = j; }
                if (count != 1 || liveUsed[found]) continue;
                bool uidLive = false;   // the record uid belongs to some other live unit: it is that unit's record, not ours
                for (int j = 0; j < liveUid.Count; j++) if (liveUid[j] == recs[i].Uid) uidLive = true;
                if (uidLive) continue;
                recUsed[i] = true; liveUsed[found] = true;
                res.Add(new[] { i, found }); by.Add("name");
            }
            return res;
        }

        // ---- self-test --------------------------------------------------------------------------------------------------

        private static EvMemRecord Rec(int uid, string name, int wps)
        {
            var r = new EvMemRecord { Uid = uid, Name = name };
            for (int i = 0; i < wps; i++) r.Route.Add(new[] { 10.0 + i * 0.5, 20.25 + i });
            return r;
        }

        public static string SelfTestJson()
        {
            var d = new EvSaveData { MissionT = 812.5, Sav = new SavStamp { ElapsedS = 812.4, Created = "2026-10-23T02:07:37Z", Src = "hook" } };
            var a = Rec(7, "DDG \"Kidd\"", 3); a.CourseDeg = 45.5; a.PlacedByMod = new[] { 11.5, 22.25 }; a.TgSaved = "2"; a.TgSetByMod = "5";
            d.Units.Add(a);
            d.Units.Add(Rec(9, "FFG", 0));
            d.Units.Add(Rec(11, null, 1));
            string err;
            var back = Parse(ToJson(d), out err);
            if (back == null || err != null) return "roundtrip parse: " + err;
            if (back.Units.Count != 3 || Math.Abs(back.MissionT - 812.5) > 1e-9) return "roundtrip header";
            if (back.Sav == null || Math.Abs(back.Sav.ElapsedS - 812.4) > 1e-9 || back.Sav.Created != "2026-10-23T02:07:37Z") return "roundtrip stamp";
            var x = back.Units[0];
            if (x.Uid != 7 || x.Name != "DDG \"Kidd\"" || x.Route.Count != 3 || x.Route[2][0] != 11.0 || x.Route[2][1] != 22.25) return "roundtrip route 3";
            if (x.CourseDeg != 45.5 || x.PlacedByMod == null || x.PlacedByMod[1] != 22.25 || x.TgSaved != "2" || x.TgSetByMod != "5") return "roundtrip fields";
            var y = back.Units[1];
            if (y.Route.Count != 0 || y.CourseDeg.HasValue || y.PlacedByMod != null || y.TgSaved != null || y.TgSetByMod != null) return "roundtrip empty route";
            if (back.Units[2].Route.Count != 1 || back.Units[2].Name != null) return "roundtrip route 1, null name";
            var noSav = Parse(ToJson(new EvSaveData()), out err);
            if (noSav == null || noSav.Sav != null || noSav.Units.Count != 0) return "roundtrip no stamp";
            if (Parse("{\"version\":2,\"units\":[]}", out err) != null || err != "unsupported_version") return "wrong version";
            if (Parse("{\"units\":[]}", out err) != null || err == null) return "no version";
            if (Parse("[1]", out err) != null || err == null) return "not an object";
            if (Parse("{broken", out err) != null || err == null) return "broken json";
            var bad = Parse("{\"version\":1,\"units\":[1,{\"name\":\"x\"},{\"uid\":3,\"route\":[[1],[2,3]]}]}", out err);
            if (bad == null || bad.Units.Count != 1 || bad.Units[0].Route.Count != 1) return "bad entries skipped";
            return null;
        }

        private static string Pairs(List<int[]> p, List<string> by)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < p.Count; i++) sb.Append(p[i][0]).Append('>').Append(p[i][1]).Append(by[i]).Append(';');
            return sb.ToString();
        }

        public static string SelfTestBind()
        {
            List<string> by;
            var recs = new List<EvMemRecord> { Rec(1, "A", 1), Rec(2, "B", 1) };
            string s = Pairs(Bind(recs, new[] { 2, 1 }, new[] { "B", "A" }, out by), by);
            if (s != "0>1uid;1>0uid;") return "exact uid: " + s;
            s = Pairs(Bind(recs, new[] { 5, 6 }, new[] { "A", "B" }, out by), by);
            if (s != "0>0name;1>1name;") return "by name: " + s;
            s = Pairs(Bind(recs, new[] { 5, 6 }, new[] { "A", "A" }, out by), by);
            if (s != "") return "two equal live names: " + s;
            s = Pairs(Bind(new List<EvMemRecord> { Rec(1, "A", 1), Rec(2, "A", 1) }, new[] { 5 }, new[] { "A" }, out by), by);
            if (s != "") return "two equal record names: " + s;
            // a record taken by uid is not taken a second time by name; live unit 1 carries record 0's name
            s = Pairs(Bind(new List<EvMemRecord> { Rec(1, "A", 1), Rec(2, "A", 1) }, new[] { 1, 9 }, new[] { "A", "Z" }, out by), by);
            if (s != "0>0uid;") return "uid taken once: " + s;
            // the record uid is live for another unit: no name binding
            s = Pairs(Bind(new List<EvMemRecord> { Rec(1, "A", 1) }, new[] { 1, 5 }, new[] { "Z", "A" }, out by), by);
            if (s != "0>0uid;" && s != "") return "other live uid: " + s;
            s = Pairs(Bind(new List<EvMemRecord> { Rec(3, "A", 1) }, new[] { 3, 5 }, new[] { "Z", "A" }, out by), by);
            if (s != "0>0uid;") return "uid wins over name: " + s;
            s = Pairs(Bind(new List<EvMemRecord> { Rec(4, "A", 1) }, new[] { 5, 4 }, new[] { "A", "Q" }, out by), by);
            if (s != "0>1uid;") return "live uid of another unit: " + s;
            s = Pairs(Bind(new List<EvMemRecord> { Rec(1, null, 1) }, new[] { 5 }, new[] { null as string }, out by), by);
            if (s != "") return "null name: " + s;
            s = Pairs(Bind(new List<EvMemRecord>(), new[] { 5 }, new[] { "A" }, out by), by);
            if (s != "") return "no records";
            return null;
        }

        public static string SelfTestMemory()
        {
            Func<double[], double[], double> dist = (p, q) => Math.Abs(p[0] - q[0]) * 60.0;
            var m = new ControlMemory(); var u1 = new object();
            m.RememberRoute(u1, new List<double[]> { new[] { 10.0, 20.0 }, new[] { 11.0, 21.0 } }, 90.0);
            m.RememberField(u1, "telegraph", "3");
            m.NoteRoutePlaced(u1, 30.0, 40.0); m.NoteFieldSet(u1, "telegraph", "5");
            var rec = m.ExportRoute(u1, 7, "Ship");
            if (rec == null || rec.Uid != 7 || rec.Name != "Ship" || rec.Route.Count != 2 || rec.CourseDeg != 90.0 || rec.PlacedByMod == null || rec.TgSaved != "3" || rec.TgSetByMod != "5") return "export";
            string err;
            var back = Parse(ToJson(new EvSaveData { Units = { rec } }), out err);
            // (a) into a fresh memory on another object
            var m2 = new ControlMemory(); var u2 = new object();
            m2.ImportRoute(u2, back.Units[0]);
            var e2 = m2.Peek(u2);
            if (e2 == null || !e2.HasRoute || e2.SavedRoute.Count != 2 || e2.SavedCourseDeg != 90.0) return "import route";
            if (ControlMemory.DecideRoute(e2, new[] { 30.0, 40.0 }, dist) != "restore") return "import: DecideRoute restore";
            if (ControlMemory.DecideRoute(e2, new[] { 35.0, 40.0 }, dist) != "player_changed") return "import: DecideRoute player_changed";
            if (ControlMemory.DecideField(e2, "telegraph", "5") != "restore") return "import: DecideField restore";
            // (b) over an entry made after the load: evasion point remembered, a new live placement
            var m3 = new ControlMemory(); var u3 = new object();
            m3.RememberRoute(u3, new List<double[]> { new[] { 30.0, 40.0 } }, 270.0);
            m3.NoteRoutePlaced(u3, 31.0, 41.0);
            m3.RememberField(u3, "telegraph", "5"); m3.NoteFieldSet(u3, "telegraph", "5");
            m3.ImportRoute(u3, back.Units[0]);
            var e3 = m3.Peek(u3);
            if (e3.SavedRoute.Count != 2 || e3.SavedRoute[0][0] != 10.0 || e3.SavedCourseDeg != 90.0) return "merge: route from the file";
            if (e3.PlacedByMod[0] != 31.0) return "merge: live PlacedByMod kept";
            if (e3.Fields["telegraph"].Saved != "3" || e3.Fields["telegraph"].SetByMod != "5") return "merge: telegraph";
            // (c) ROE and other fields untouched
            var m4 = new ControlMemory(); var u4 = new object();
            m4.RememberRoe(u4, "tight"); m4.NoteRoeSet(u4, "hold");
            m4.RememberField(u4, "emcon", "false"); m4.NoteFieldSet(u4, "emcon", "true");
            m4.ImportRoute(u4, back.Units[0]);
            var e4 = m4.Peek(u4);
            if (e4.SavedRoe != "tight" || e4.RoeSetByMod != "hold" || !e4.HasRoe) return "roe untouched";
            if (e4.Fields["emcon"].Saved != "false" || e4.Fields["emcon"].SetByMod != "true") return "emcon untouched";
            // the file has no placed point: a live one stays; the file's telegraph is absent: nothing added
            var bare = new EvMemRecord { Uid = 1, Name = "X" };
            var m5 = new ControlMemory(); var u5 = new object();
            m5.ImportRoute(u5, bare);
            if (m5.Peek(u5).Fields.ContainsKey("telegraph") || m5.Peek(u5).PlacedByMod != null) return "bare import";
            // (d) no route -> null
            var m6 = new ControlMemory(); var u6 = new object();
            m6.RememberRoe(u6, "hold");
            if (m6.ExportRoute(u6, 1, "x") != null || m6.ExportRoute(new object(), 1, "x") != null || m6.ExportRoute(null, 1, "x") != null) return "export without a route";
            m6.ImportRoute(null, rec);
            if (m6.Count != 1) return "import to null unit";
            return null;
        }

        /// <summary>null = ok.</summary>
        public static string SelfTest()
        {
            return SelfTestJson() ?? SelfTestBind() ?? SelfTestMemory();
        }
    }
}
