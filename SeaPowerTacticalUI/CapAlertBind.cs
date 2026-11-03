// T-219 / ADR-0090: the alert binding of a member (state, raid, AirGuard key, last target point) and the raid place memory of a group - JSON write/read.
// Times in the file are ages relative to mission_time_s, so a clock shift on load (ADR-0086) does not matter. No Unity, selftest.
using System;
using System.Collections.Generic;
using System.Text;

namespace VoiceBotBridge
{
    internal sealed class CapAlertBindDto
    {
        public string State;            // scramble | return
        public string Raid, Key;        // AlertRaid (mod cluster key), AirGuard key at the scramble (null = none)
        public List<int> RaidIds;       // null = barrage without a raid
        public bool Barrage, Handed;
        public RaidMemo Tgt;            // last target point; in the file as age_s from mission_time_s
    }

    internal static class CapAlertBind
    {
        private static void Memo(StringBuilder sb, RaidMemo m, double t, bool withKeyIds)
        {
            sb.Append('{');
            if (withKeyIds)
            {
                sb.Append("\"key\":").Append(Json.Str(m.Key)).Append(",\"ids\":[");
                for (int i = 0; i < m.Ids.Count; i++) { if (i > 0) sb.Append(','); sb.Append(m.Ids[i]); }
                sb.Append("],");
            }
            sb.Append("\"lat\":").Append(Json.Num(m.Lat)).Append(",\"lon\":").Append(Json.Num(m.Lon))
              .Append(",\"course\":").Append(Json.Num(m.CourseDeg)).Append(",\"kn\":").Append(Json.Num(m.Kn))
              .Append(",\"age_s\":").Append(Json.Num(Math.Max(0, t - m.At))).Append('}');
        }

        public static void Write(StringBuilder sb, CapAlertBindDto b, double missionTimeS)
        {
            if (b == null || (b.State != "scramble" && b.State != "return")) return;
            sb.Append(",\"alert_bind\":{\"state\":").Append(Json.Str(b.State));
            if (b.Raid != null) sb.Append(",\"raid\":").Append(Json.Str(b.Raid));
            if (b.Key != null) sb.Append(",\"key\":").Append(Json.Str(b.Key));
            if (b.RaidIds != null)
            {
                sb.Append(",\"ids\":[");
                for (int i = 0; i < b.RaidIds.Count; i++) { if (i > 0) sb.Append(','); sb.Append(b.RaidIds[i]); }
                sb.Append(']');
            }
            sb.Append(",\"barrage\":").Append(Json.Bool(b.Barrage)).Append(",\"handed\":").Append(Json.Bool(b.Handed));
            if (b.Tgt != null) { sb.Append(",\"tgt\":"); Memo(sb, b.Tgt, missionTimeS, false); }
            sb.Append('}');
        }

        private static object Val(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) ? v : null; }
        private static double Num(Dictionary<string, object> d, string k, double def) { object v = Val(d, k); return v is double ? (double)v : def; }

        private static RaidMemo ReadMemo(Dictionary<string, object> d, double t, bool withKeyIds)
        {
            if (d == null || !(Val(d, "lat") is double) || !(Val(d, "lon") is double)) return null;
            var m = new RaidMemo { Lat = Num(d, "lat", 0), Lon = Num(d, "lon", 0), CourseDeg = Num(d, "course", 0), Kn = Num(d, "kn", 0), At = t - Math.Max(0, Num(d, "age_s", 0)) };
            if (withKeyIds)
            {
                m.Key = Val(d, "key") as string;
                var l = Val(d, "ids") as List<object>;
                if (l != null) foreach (var o in l) if (o is double) m.Ids.Add((int)(double)o);
            }
            return m;
        }

        public static CapAlertBindDto Read(Dictionary<string, object> member, double missionTimeS)
        {
            var d = Val(member, "alert_bind") as Dictionary<string, object>;
            if (d == null) return null;
            string st = Val(d, "state") as string;
            if (st != "scramble" && st != "return") return null;
            var b = new CapAlertBindDto { State = st, Raid = Val(d, "raid") as string, Key = Val(d, "key") as string };
            var l = Val(d, "ids") as List<object>;
            if (l != null) { b.RaidIds = new List<int>(); foreach (var o in l) if (o is double) b.RaidIds.Add((int)(double)o); }
            b.Barrage = Val(d, "barrage") is bool && (bool)Val(d, "barrage");
            b.Handed = Val(d, "handed") is bool && (bool)Val(d, "handed");
            b.Tgt = ReadMemo(Val(d, "tgt") as Dictionary<string, object>, missionTimeS, false);
            return b;
        }

        public static void WriteMemos(StringBuilder sb, IList<RaidMemo> memos, double missionTimeS)
        {
            if (memos == null || memos.Count == 0) return;
            sb.Append(",\"served_memo\":[");
            for (int i = 0; i < memos.Count; i++) { if (i > 0) sb.Append(','); Memo(sb, memos[i], missionTimeS, true); }
            sb.Append(']');
        }

        public static List<RaidMemo> ReadMemos(Dictionary<string, object> group, double missionTimeS)
        {
            var l = Val(group, "served_memo") as List<object>;
            if (l == null) return null;
            var res = new List<RaidMemo>();
            foreach (var o in l) { var m = ReadMemo(o as Dictionary<string, object>, missionTimeS, true); if (m != null) res.Add(m); }
            return res;
        }

        public static string SelfTest()
        {
            var tgt = new RaidMemo { Lat = 12.5, Lon = 34.25, CourseDeg = 180, Kn = 480, At = 987.5 };
            var b = new CapAlertBindDto { State = "scramble", Raid = "7028", Key = "r7028", RaidIds = new List<int> { 7028, 7030 }, Barrage = false, Handed = true, Tgt = tgt };
            var sb = new StringBuilder(); Write(sb, b, 1000);
            string j = "{\"uid\":1" + sb + "}";
            var back = Read(Json.Parse(j) as Dictionary<string, object>, 5000);
            if (back == null || back.State != "scramble" || back.Raid != "7028" || back.Key != "r7028" || back.RaidIds == null || back.RaidIds.Count != 2 || back.RaidIds[1] != 7030 || back.Barrage || !back.Handed) return "bind round trip";
            if (back.Tgt == null || Math.Abs(back.Tgt.At - (5000 - 12.5)) > 1e-9 || back.Tgt.Lat != 12.5 || back.Tgt.Lon != 34.25 || back.Tgt.CourseDeg != 180 || back.Tgt.Kn != 480) return "bind tgt age -> At = now - age";
            var sb2 = new StringBuilder(); Write(sb2, back, 5000);
            if (sb2.ToString() != sb.ToString()) return "bind re-write is stable";
            var bar = new CapAlertBindDto { State = "return", Barrage = true };
            var sb3 = new StringBuilder(); Write(sb3, bar, 10);
            var b3 = Read(Json.Parse("{" + sb3.ToString().Substring(1) + "}") as Dictionary<string, object>, 10);
            if (b3 == null || b3.State != "return" || !b3.Barrage || b3.RaidIds != null || b3.Tgt != null || b3.Raid != null) return "barrage bind";
            var none = new StringBuilder(); Write(none, null, 1);
            if (none.Length != 0) return "null writes nothing";
            if (Read(Json.Parse("{\"uid\":1}") as Dictionary<string, object>, 1) != null) return "no field -> null";
            if (Read(Json.Parse("{\"alert_bind\":{\"state\":\"zzz\"}}") as Dictionary<string, object>, 1) != null) return "garbage state -> null";
            if (Read(Json.Parse("{\"alert_bind\":5}") as Dictionary<string, object>, 1) != null) return "garbage field -> null";
            var memos = new List<RaidMemo> { new RaidMemo { Key = "r7028", Ids = new List<int> { 7028 }, Lat = 1, Lon = 2, CourseDeg = 90, Kn = 450, At = 970 } };
            var sm = new StringBuilder(); WriteMemos(sm, memos, 1000);
            var gm = ReadMemos(Json.Parse("{\"group\":\"G\"" + sm + "}") as Dictionary<string, object>, 2000);
            if (gm == null || gm.Count != 1 || gm[0].Key != "r7028" || gm[0].Ids.Count != 1 || gm[0].Ids[0] != 7028 || gm[0].Lat != 1 || gm[0].Lon != 2 || gm[0].CourseDeg != 90 || gm[0].Kn != 450 || Math.Abs(gm[0].At - 1970) > 1e-9) return "memos round trip";
            if (ReadMemos(Json.Parse("{\"group\":\"G\"}") as Dictionary<string, object>, 1) != null) return "no memos -> null";
            var se = new StringBuilder(); WriteMemos(se, null, 1); WriteMemos(se, new List<RaidMemo>(), 1);
            if (se.Length != 0) return "empty memos write nothing";
            return null;
        }
    }
}
