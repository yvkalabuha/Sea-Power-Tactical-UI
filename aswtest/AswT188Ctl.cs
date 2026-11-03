using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>T-188 C: the game refuses the dip order (no_dipping_sonar) -> the aircraft leaves the dipping tactic; the type is shown in ui_state.</summary>
    internal static class AswT188Ctl
    {
        private static Dictionary<string, object> P(string json) { return (Dictionary<string, object>)Json.Parse(json); }
        private static AswController Make(AswFakeGame f) { return new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "off" }); }
        private static void Run(AswController c, AswFakeGame f, int seconds) { for (int i = 0; i < seconds; i++) { f.Advance(1); f.Tick(c); } }
        private static bool Logged(AswFakeGame f, string sub) { foreach (var l in f.Logs) if (l.Contains(sub)) return true; return false; }

        private static int Warns(AswFakeGame f, string code, string sub)
        {
            int n = 0;
            foreach (var e in f.Events) if (e.StartsWith("asw_warn ", StringComparison.Ordinal) && e.Contains("\"" + code + "\"") && (sub == null || e.Contains(sub))) n++;
            return n;
        }

        private static string TacticOf(AswController c, int uid)
        {
            foreach (var g in c.View().Groups) foreach (var r in g.MemberRows) if (r.Uid == uid) return r.Tactic;
            return "?";
        }

        private static AswMember Heli(AswFakeGame f, int uid, bool dip, int passive)
        {
            var h = AswFakeGame.Helo(uid, "air", dip: dip, passive: passive); h.EnduranceS = 1e7; h.FuelFrac = 1; f.Mem.Add(h); f.FixPos.Add(uid);
            return h;
        }

        private static AswController Course(AswFakeGame f, string order)
        {
            var c = Make(f);
            var ids = new List<string>(); foreach (var m in f.Mem) ids.Add(m.Uid.ToString());
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[" + string.Join(",", ids.ToArray()) + "]}"));
            return AswController.IsErr(c.SearchCourse(P(order))) ? null : c;
        }

        private static AswController Area(AswFakeGame f, string tactics)
        {
            var c = Make(f);
            var ids = new List<string>(); foreach (var m in f.Mem) ids.Add(m.Uid.ToString());
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[" + string.Join(",", ids.ToArray()) + "]}"));
            var ctr = new GeoPt(f.Tgt.Lat + 0.3, f.Tgt.Lon);
            var r = AswController.RectAround(ctr, 21.6, 12.96);
            var sb = new System.Text.StringBuilder("[");
            for (int i = 0; i < r.Length; i++) { if (i > 0) sb.Append(','); sb.Append(r[i].ToString("R", System.Globalization.CultureInfo.InvariantCulture)); }
            return AswController.IsErr(c.SearchArea(P("{\"group\":\"G\",\"rect\":" + sb.Append(']') + ",\"tactics\":" + tactics + "}"))) ? null : c;
        }

        public static string Run(string name)
        {
            switch (name)
            {
                case "refused": return Refused();
                case "refused_buoys": return RefusedBuoys();
                case "refused_type": return RefusedType();
                case "other_error": return OtherError();
                case "area_refused": return AreaRefused();
                case "ui_state": return UiState();
                default: return "unknown case " + name;
            }
        }

        private static string Refused()
        {
            var f = new AswFakeGame { DropDelayS = 0, DipRange = 2.1, DipError = "no_dipping_sonar" };
            Heli(f, 1, true, 0);
            var c = Course(f, "{\"tactics\":[\"dip\"]}");
            if (c == null) return "order refused";
            Run(c, f, 1);
            if (f.Dips.Count != 1) return "dip orders after 1 tick " + f.Dips.Count;
            if (TacticOf(c, 1) == "dip") return "still on dip";
            for (int i = 0; i < 5; i++) Run(c, f, 40);
            if (f.Dips.Count != 1) return "dip orders " + f.Dips.Count;
            if (TacticOf(c, 1) == "dip") return "back on dip";
            int n = Warns(f, "no_dipping_sonar", "uid=1");
            if (n != 1) return "warnings " + n;
            if (Warns(f, "dip_failed", null) != 0) return "dip_failed";
            return null;
        }

        private static string RefusedBuoys()
        {
            var f = new AswFakeGame { DropDelayS = 0, DipRange = 2.1, DipError = "no_dipping_sonar" };
            Heli(f, 1, true, 40);
            Heli(f, 2, false, 0);
            var c = Area(f, "[\"dip\",\"grid\"]");
            if (c == null) return "order refused";
            if (TacticOf(c, 1) != "dip") return "A starts on " + TacticOf(c, 1);
            for (int i = 0; i < 6; i++) Run(c, f, 20);
            if (TacticOf(c, 1) != "grid") return "A on " + TacticOf(c, 1);
            if (Warns(f, "no_dipping_sonar", "uid=1") != 1) return "warnings " + Warns(f, "no_dipping_sonar", "uid=1");
            return null;
        }

        private static string RefusedType()
        {
            var f = new AswFakeGame { DropDelayS = 0, DipRange = 2.1, DipError = "no_dipping_sonar" };
            Heli(f, 1, true, 0);
            var d = AswFakeGame.Helo(2, "deck_ready", dip: true, passive: 40); d.DeckKey = "task:x"; d.Uid = 2;
            f.Mem.Add(d);
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            if (AswController.IsErr(c.SearchCourse(P("{\"tactics\":[\"dip\"]}")))) return "order refused";
            Run(c, f, 3);
            foreach (var r in c.View().Available)
                if (r.Uid == 2) return r.Dipping || r.DipSrc != "refused" ? "row dipping=" + r.Dipping + " src=" + r.DipSrc : null;
            return "row 2 not available";
        }

        private static string OtherError()
        {
            var f = new AswFakeGame { DropDelayS = 0, DipRange = 2.1, DipError = "dip_failed" };
            Heli(f, 1, true, 0);
            var c = Course(f, "{\"tactics\":[\"dip\"]}");
            if (c == null) return "order refused";
            Run(c, f, 1);
            if (Warns(f, "dip_failed", null) != 1) return "no dip_failed";
            if (Warns(f, "no_dipping_sonar", null) != 0) return "no_dipping_sonar";
            if (TacticOf(c, 1) != "dip") return "left dip: " + TacticOf(c, 1);
            int before = f.Dips.Count;
            Run(c, f, 20);
            if (f.Dips.Count != before) return "no pause: " + before + " -> " + f.Dips.Count;
            return null;
        }

        private static string AreaRefused()
        {
            var f = new AswFakeGame { DropDelayS = 0, DipRange = 2.1, DipError = "no_dipping_sonar" };
            Heli(f, 1, true, 0);
            var c = Area(f, "[\"dip\"]");
            if (c == null) return "order refused";
            for (int i = 0; i < 5; i++) Run(c, f, 40);
            if (f.Dips.Count != 1) return "dip orders " + f.Dips.Count;
            if (TacticOf(c, 1) == "dip") return "still on dip";
            if (Warns(f, "no_dipping_sonar", "uid=1") != 1) return "warnings " + Warns(f, "no_dipping_sonar", "uid=1");
            return null;
        }

        private static string UiState()
        {
            const string note = "beyond_count:SensorSystem3=VGS-3 n=2";
            var f = new AswFakeGame();
            var d = new AswFakeDeck { HangarReady = true };
            d.AddHangar("wp_ka-27", "Ka-27PL", 2, false, note);
            f.RealDeck = d;
            f.Decks[77] = new AswTarget { Uid = 77, Name = d.ShipName, Lat = 35, Lon = 20 };
            var c = Make(f);
            Run(c, f, 1);
            string js = c.StateJson();
            if (!js.Contains("\"dip_src\":\"type\"") || !js.Contains("\"dip_note\":\"" + note + "\"") || !js.Contains("\"dipping\":false")) return "available row: " + js;
            int slot = AswMath.HangarSlotUid(77, "wp_ka-27", 1);
            if (AswController.IsErr(c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[" + slot + "]}")))) return "create";
            js = c.StateJson();
            int at = js.IndexOf("\"member_dip\"", StringComparison.Ordinal);
            if (at < 0 || !js.Substring(at).Contains("\"src\":\"type\"") || !js.Substring(at).Contains(note)) return "member_dip: " + js;
            return null;
        }
    }
}
