using System;
using System.Collections.Generic;
using System.IO;

namespace VoiceBotBridge
{
    internal static class AswControllerTests
    {
        private static Dictionary<string, object> P(string json) { return (Dictionary<string, object>)Json.Parse(json); }

        private static AswController Make(AswFakeGame f, string restore = "auto")
        {
            return new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = restore });
        }

        private static void Run(AswController c, AswFakeGame f, int seconds)
        {
            for (int i = 0; i < seconds; i++) { f.Advance(1); f.Tick(c); }
        }

        private static string Err(string r) { return AswController.IsErr(r) ? r : null; }

        public static string Run()
        {
            string e;
            if ((e = Course()) != null) return "course: " + e;
            if ((e = Blocked()) != null) return "blocked: " + e;
            if ((e = AddAirborne()) != null) return "add airborne: " + e;
            if ((e = Relief3()) != null) return "relief3: " + e;
            if ((e = Relief3(true)) != null) return "relief3 (new uid on launch): " + e;
            if ((e = Relief2()) != null) return "relief2: " + e;
            if ((e = Contact()) != null) return "contact: " + e;
            if ((e = Attack()) != null) return "attack: " + e;
            if ((e = SaveLoad()) != null) return "saveload: " + e;
            if ((e = SaveSlot()) != null) return "saveslot: " + e;
            if ((e = Misc()) != null) return "misc: " + e;
            if ((e = EvadingMember()) != null) return "evading: " + e;
            if ((e = Area()) != null) return "area: " + e;
            if ((e = Tactics()) != null) return "tactics: " + e;
            if ((e = Groups()) != null) return "groups: " + e;
            if ((e = Lists()) != null) return "lists: " + e;
            if ((e = PreBuoys()) != null) return "pre buoys: " + e;
            if ((e = Hotkeys()) != null) return "hotkeys: " + e;
            if ((e = Preview()) != null) return "preview: " + e;
            if ((e = DeckAllow()) != null) return "deck allow: " + e;
            if ((e = DeckCreate()) != null) return "deck create: " + e;
            if ((e = ReassignNow()) != null) return "reassign now: " + e;
            if ((e = WarnClear()) != null) return "warn clear: " + e;
            if ((e = ForeignOwned()) != null) return "foreign owned: " + e;
            return null;
        }

        private static string Course()
        {
            var f = new AswFakeGame();
            var h1 = AswFakeGame.Helo(1, "air"); h1.SpeedKn = 250;   // T-334: fast enough that the ETA filter keeps the whole package
            f.Mem.Add(h1);
            var c = Make(f);
            string r = c.SearchCourse(P("{\"tactics\":[\"corridor\"]}"));   // T-176: 2+2 rows became the corridor X (3 rows per side at X = 20)
            if (Err(r) != null) return r;
            Run(c, f, 5);
            if (f.Drops.Count != 1) return "first drop count " + f.Drops.Count;
            var d = f.Drops[0];
            if (d.Ammo != "usn_ssq-53" || d.Pts.Count != 24) return "ammo/pts " + d.Ammo + " " + d.Pts.Count;
            int inner = 0, mid = 0, outer = 0;
            var origin = new GeoPt(f.Tgt.Lat, f.Tgt.Lon);
            foreach (var p in d.Pts)
            {
                double y = Math.Abs(AswMath.ToLocal(origin, 90, p).Y);
                if (Math.Abs(y - 2.16) < 0.15) inner++;
                if (Math.Abs(y - 5.0) < 0.15) mid++;
                if (Math.Abs(y - 7.84) < 0.15) outer++;
            }
            if (inner != 8 || mid != 8 || outer != 8) return "corridor rows inner=" + inner + " mid=" + mid + " outer=" + outer;
            if (!f.Calls.Contains("roe:1") || !f.Calls.Contains("depth:1:below")) return "roe/depth not set (auto = below the layer for a corridor)";
            Run(c, f, 3600);
            if (f.Drops.Count < 2) return "no second segment";
            double dt = f.Drops[1].At - f.Drops[0].At;
            if (dt < 30 || dt > 3000) return "second segment after " + dt;   // the carpet rolls: a new column comes into the window as the group goes on
            if (!f.HasEvent("asw_barrier_laid")) return "no barrier event";
            // turn by 60 degrees -> the anchor of the pattern is moved (T-176: 30 degrees held for 60 s)
            f.Tgt.CourseDeg = 150;
            Run(c, f, 900);
            bool replan = false; foreach (var l in f.Logs) if (l.Contains("reanchor reason=turn")) replan = true;
            if (!replan) return "no re-anchor after a 60 degree turn";
            string st = c.StateJson();
            if (!(Json.Parse(st) is Dictionary<string, object>)) return "state json invalid";
            if (!st.Contains("\"groups\"") || !st.Contains("ПЛО")) return "state json " + st;
            return null;
        }

        private static string Blocked()
        {
            var f = new AswFakeGame { TimeScale = 4, BlockDrops = true };
            f.Mem.Add(AswFakeGame.Helo(1, "air"));
            var c = Make(f);
            if (Err(c.SearchCourse(P("{\"tactics\":[\"rows\"]}"))) != null) return "order";
            // ADR-0063: not while the aircraft flies to the first point (3 nm at 100 kn + 60 s), only after it
            Run(c, f, 6);
            if (f.HasEvent("asw_warn", "tc_blocked") || f.Drops.Count != 1) return "tc_blocked during the transit, drops " + f.Drops.Count;
            Run(c, f, 480);   // T-334: the ladder starts at the end of the nearest column (8.4 nm: 5 min + 60 s)
            if (!f.HasEvent("asw_warn", "tc_blocked")) return "no tc_blocked";
            if (f.Drops.Count < 2) return "no retry " + f.Drops.Count;
            // the points that never fell do not stay in the group's buoy list
            int listed = 0; foreach (var g in c.View().Groups) listed += g.Buoys.Count;
            if (listed > f.Drops[f.Drops.Count - 1].Pts.Count) return "buoy list " + listed + " after " + f.Drops.Count + " packages that never fell";
            return null;
        }

        private static string Preview()
        {
            var f = new AswFakeGame();
            f.Mem.Add(AswFakeGame.Helo(1, "air", true));
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"uids\":[1]}"));
            int before = f.Drops.Count + f.Dips.Count + f.Calls.Count + f.Attacks.Count;
            var v = c.Preview(false, P("{\"tactics\":[\"dip\",\"rows\",\"mad\"]}"));
            if (v.Preview.Count < 3) return "course preview lines " + v.Preview.Count;
            foreach (var l in v.Preview) if (l.Kind != "preview" || l.Points.Count == 0 || l.Tactic == null) return "bad preview line";
            if (f.Drops.Count + f.Dips.Count + f.Calls.Count + f.Attacks.Count != before) return "preview touched the game";
            if (v.Groups.Count != 1 || v.Groups[0].Order != "none") return "preview created an order";
            if (v.PreviewUntilS <= 0) return "no deadline";
            var va = c.Preview(true, P("{\"bearing\":0,\"dist_nm\":15,\"tactics\":[\"rows\",\"mad\"]}"));
            if (va.Preview.Count < 2) return "area preview lines " + va.Preview.Count;
            if (f.Drops.Count + f.Dips.Count + f.Calls.Count != before) return "area preview touched the game";
            // a real order clears the preview
            if (Err(c.SearchCourse(P("{\"tactics\":[\"dip\"]}"))) != null) return "order";
            if (c.View().Preview.Count != 0) return "preview stays after an order";
            c.Preview(false, P("{\"tactics\":[\"dip\"]}"));
            if (c.View().Preview.Count == 0) return "no preview";
            Run(c, f, 40);
            if (c.View().Preview.Count != 0) return "preview does not expire";
            var bad = c.Preview(false, P("{\"tactics\":[\"zig\"]}"));
            if (bad.Preview.Count != 0) return "bad args gave a preview";
            return null;
        }

        private static bool Logged(AswFakeGame f, string prefix, string sub)
        {
            foreach (var l in f.Logs) if (l.StartsWith(prefix, StringComparison.Ordinal) && l.Contains(sub)) return true;
            return false;
        }

        private static AswMember DeckHelo(int uid, string where, string key)
        {
            var m = AswFakeGame.Helo(uid, where);
            m.DeckKey = key; m.Uid = uid; m.DeckUid = 77; m.Dipping = true;
            return m;
        }

        private static string DeckAllow()
        {
            var f = new AswFakeGame();
            f.Mem.Add(DeckHelo(-11, "deck_ready", "task:g1"));
            var c = Make(f);
            if (Err(c.SearchCourse(P("{\"tactics\":[\"dip\"]}"))) != null) return "order";
            Run(c, f, 3);
            if (f.Allows.Count != 1 || f.Allows[0] != "77:task:g1") return "allow calls " + string.Join(";", f.Allows.ToArray()) + " logs: " + string.Join(" | ", f.Logs.ToArray()) + " ev: " + string.Join(" | ", f.Events.ToArray());
            if (f.Calls.Contains("launch:-11") || f.Calls.Exists(x => x.StartsWith("launch:", StringComparison.Ordinal))) return "Launch used for a ready task";
            if (!Logged(f, "asw launch ", "route=allow -> ok")) return "no launch log: " + string.Join(" | ", f.Logs.ToArray());
            Run(c, f, 20);
            if (f.Allows.Count != 1) return "allow repeated " + f.Allows.Count;
            bool adopted = false;
            foreach (var r in c.View().Aircraft) if (r.State == "air" && r.Group != null && r.Tactic == "dip") adopted = true;
            if (!adopted) return "launched aircraft not adopted into the group with dip";
            // error path is logged too
            var f2 = new AswFakeGame { AllowError = "launch_blocked" };
            f2.Mem.Add(DeckHelo(-12, "deck_ready", "task:g2"));
            var c2 = Make(f2);
            c2.SearchCourse(P("{\"tactics\":[\"dip\"]}"));
            Run(c2, f2, 3);
            if (!Logged(f2, "asw launch ", "route=allow -> launch_blocked")) return "no error log";
            if (!Logged(f2, "asw warn launch_failed", "")) return "no warn log: " + string.Join(" | ", f2.Logs.ToArray());
            return null;
        }

        private static string DeckCreate()
        {
            var f = new AswFakeGame();
            f.Mem.Add(DeckHelo(5, "deck_ready", "hangar:SH-3H"));
            var c = Make(f);
            c.SearchCourse(P("{\"tactics\":[\"dip\"]}"));
            Run(c, f, 3);
            if (f.Allows.Count != 0) return "allow used for hangar";
            if (!f.Calls.Contains("launch:5")) return "Launch not called";
            if (!Logged(f, "asw launch ", "route=create -> ok")) return "no create log";
            return null;
        }

        private static string ReassignNow()
        {
            var f = new AswFakeGame();
            f.Mem.Add(AswFakeGame.Helo(1, "air", true));
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"uids\":[1]}"));
            c.SetTactics(P("{\"tactics\":[\"dip\"]}"));
            string tac = null;
            foreach (var r in c.View().Aircraft) if (r.Uid == 1) tac = r.Tactic;
            if (tac != "dip") return "tactic before tick = " + tac;
            int assigns = 0; foreach (var l in f.Logs) if (l.StartsWith("asw assign", StringComparison.Ordinal)) assigns++;
            c.SetTactics(P("{\"tactics\":[\"dip\"]}"));
            int assigns2 = 0; foreach (var l in f.Logs) if (l.StartsWith("asw assign", StringComparison.Ordinal)) assigns2++;
            if (assigns2 != assigns) return "assign logged although nothing changed";
            return null;
        }

        private static string WarnClear()
        {
            var f = new AswFakeGame();
            f.Mem.Add(AswFakeGame.Helo(1, "air"));
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"uids\":[1]}"));
            c.SearchCourse(P("{\"tactics\":[\"dip\"]}"));
            f.Tgt.Uid = 501; f.Tgt.Name = "Other";   // target vanishes -> target_lost warning
            Run(c, f, 3);
            bool has = false; foreach (var w in c.View().Warnings) if (w.Contains("(ПЛО-1)")) has = true;
            if (!has) return "no warning to clear";
            c.Group(P("{\"op\":\"disband\",\"group\":\"ПЛО-1\"}"));
            foreach (var w in c.View().Warnings) if (w.Contains("(ПЛО-1)")) return "warning stays after disband: " + w;
            return null;
        }

        // T-150 R5: an airborne machine added to a working group gets a route within two ticks (a tactic, or a waiting point when all are held)
        private static string AddAirborne()
        {
            var f = new AswFakeGame();
            f.Mem.Add(AswFakeGame.Helo(1, "air", dip: true));
            f.Mem.Add(AswFakeGame.Helo(2, "air"));
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            if (Err(c.SearchCourse(P("{\"tactics\":[\"dip\"]}"))) != null) return "order";
            Run(c, f, 5);
            if (f.Route.ContainsKey(2)) return "route before the add";
            c.Group(P("{\"op\":\"add\",\"group\":\"G\",\"uids\":[2]}"));
            Run(c, f, 2);
            if (!f.Route.ContainsKey(2) || f.Route[2].Count == 0) return "no route for the added machine";
            int gotos = 0; foreach (var cl in f.Calls) if (cl == "goto:2") gotos++;
            if (gotos != 1) return "gotos " + gotos;
            Run(c, f, 200);
            gotos = 0; foreach (var cl in f.Calls) if (cl == "goto:2") gotos++;
            if (gotos != 1) return "standby repeated " + gotos;
            return null;
        }

        private static string Relief3(bool rename = false)
        {
            var f = new AswFakeGame { RenameOnLaunch = rename };
            var a = AswFakeGame.Helo(1, "air"); a.FuelFrac = 0.5;
            var b = AswFakeGame.Helo(2, "deck_ready");
            var d = AswFakeGame.Helo(3, "deck_service"); d.ReadyInS = 600;
            f.Mem.Add(a); f.Mem.Add(b); f.Mem.Add(d);
            var c = Make(f);
            if (Err(c.Group(P("{\"op\":\"create\",\"uids\":[1,2,3]}"))) != null) return "group";
            if (Err(c.SearchCourse(P("{\"tactics\":[\"rows\"]}"))) != null) return "order";
            int gap = 0;
            for (int i = 0; i < 8 * 3600; i++)
            {
                f.Advance(1); f.Tick(c);
                bool on = false; foreach (var m in f.Mem) if (f.OnStation(m)) on = true;
                if (!on) gap++;
            }
            if (gap > 0) return "coverage gap " + gap + " s";
            if (f.HasEvent("asw_warn", "continuity_gap")) return "unexpected continuity warning";
            if (!f.HasEvent("asw_relief")) return "no relief";
            int launches = 0; foreach (var cl in f.Calls) if (cl.StartsWith("launch:", StringComparison.Ordinal)) launches++;
            if (launches < 3) return "launches " + launches;
            return null;
        }

        private static string Relief2()
        {
            var f = new AswFakeGame();
            var a = AswFakeGame.Helo(1, "air"); a.EnduranceS = 4000; a.FuelFrac = 0.5;
            var b = AswFakeGame.Helo(2, "deck_service"); b.ReadyInS = 1500; b.EnduranceS = 4000;
            f.Mem.Add(a); f.Mem.Add(b);
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"uids\":[1,2]}"));
            c.SearchCourse(P("{\"tactics\":[\"rows\"]}"));
            Run(c, f, 5);
            if (!f.HasEvent("asw_warn", "continuity_gap")) return "no continuity_gap";
            return null;
        }

        private static string Contact()
        {
            var f = new AswFakeGame();
            f.Mem.Add(AswFakeGame.Helo(1, "air", dip: true));
            f.Mem.Add(AswFakeGame.Helo(2, "air"));
            var c = Make(f);
            if (Err(c.SearchCourse(P("{\"tactics\":[\"dip\",\"rows\"]}"))) != null) return "order";
            Run(c, f, 10);
            var ahead = AswMath.Offset(new GeoPt(f.Tgt.Lat, f.Tgt.Lon), 90, 10);
            var sub = new AswContact { Id = 77, Name = "Sub 77", Lat = ahead.Lat, Lon = ahead.Lon, Source = "buoy" };
            f.Subs.Add(sub);
            int before = f.Drops.Count, dipsBefore = f.Dips.Count;
            Run(c, f, 2);
            if (!f.HasEvent("asw_contact", "\"contact\":77")) return "no contact event";
            // ADR-0063: the cross is planned with 4 buoys; inside the barrier the ones next to a barrier buoy (< 0.79 R) are left out
            if (!f.Logs.Exists(l => l.Contains(" contact uid=") && l.Contains("planned=4"))) return "no localization cross planned";
            DropCall cross = null;
            var cg = new GeoPt(sub.Lat, sub.Lon);
            for (int i = before; i < f.Drops.Count; i++) if (f.Drops[i].Ammo == "usn_ssq-53" && f.Drops[i].Pts.TrueForAll(q => AswMath.DistNm(cg, q) < 2.45)) cross = f.Drops[i];
            if (cross == null) return "no localization cross";
            foreach (var p in cross.Pts)
            {
                double dist = AswMath.DistNm(cg, p);
                if (Math.Abs(dist - 2.3) > 0.05) return "cross radius " + dist + " (the spook radius 2.3)";   // T-176 C11
            }
            if (f.Dips.Count <= dipsBefore) return "no dip at contact";
            // contact lost
            f.Subs.Clear();
            int dropsBefore = f.Drops.Count;
            Run(c, f, 310);
            if (!f.HasEvent("asw_contact_lost")) return "no lost event";
            if (!f.Logs.Exists(l => l.Contains(" ring r="))) return "no lost-contact ring";   // T-176 C11: the ring replaced the expanding square
            return null;
        }

        private static string Attack()
        {
            // T-173 (ADR-0074, legacy-attack): a search group never asks and never attacks; asw_attack answers "no_confirm_needed"
            var f = new AswFakeGame();
            f.Mem.Add(AswFakeGame.Helo(1, "air", dip: true));
            var c = Make(f);
            c.SearchCourse(P("{\"tactics\":[\"dip\",\"rows\"]}"));
            Run(c, f, 5);
            var ahead = AswMath.Offset(new GeoPt(f.Tgt.Lat, f.Tgt.Lon), 90, 10);
            f.Subs.Add(new AswContact { Id = 5, Name = "Sub 5", Lat = ahead.Lat, Lon = ahead.Lon, Source = "buoy", Side = "hostile" });
            Run(c, f, 900);
            if (f.HasEvent("asw_attack_proposed")) return "a proposal was made";
            if (f.Attacks.Count != 0) return "a search group attacked";
            if (!f.HasEvent("asw_contact")) return "no contact";
            if (c.Attack(P("{\"proposal_id\":\"p1\",\"confirm\":true}")) != "!no_confirm_needed") return "asw_attack: " + c.Attack(P("{}"));
            if (c.AttackCancel(P("{}")) != "!use_asw_strike_skip") return "asw_attack_cancel";
            var stj = (Json.Parse(c.StateJson()) as Dictionary<string, object>)["proposals"] as List<object>;
            if (stj == null || stj.Count != 0) return "proposals stay an empty list";
            // attack not allowed: the contact is still taken
            var f3 = new AswFakeGame { AllowAttack = false };
            f3.Mem.Add(AswFakeGame.Helo(1, "air", dip: true));
            var c3 = Make(f3);
            c3.SearchCourse(P("{\"tactics\":[\"dip\",\"rows\"]}"));
            Run(c3, f3, 5);
            var a3 = AswMath.Offset(new GeoPt(f3.Tgt.Lat, f3.Tgt.Lon), 90, 10);
            f3.Subs.Add(new AswContact { Id = 7, Name = "Sub 7", Lat = a3.Lat, Lon = a3.Lon });
            Run(c3, f3, 40);
            if (!f3.HasEvent("asw_contact")) return "contact missing without AllowAttack";
            var hs = AttackHostility.SelfTest();
            if (hs != null) return "hostility rule: " + hs;
            if (AttackHostility.AttackOk("unknown", "Unknown", null) || AttackHostility.Route("unknown", false) != "not_hostile") return "grey attackable";
            if (AttackHostility.AttackOk("neutral", "Enemy", "Enemy")) return "neutral attackable";
            // 0.27.10 (live): a noisemaker next to the boat ("Sub #7047") is no contact; reclassified -> dropped and logged
            {
                var f8 = new AswFakeGame();
                f8.Mem.Add(AswFakeGame.Helo(1, "air", dip: true));
                var c8 = Make(f8);
                c8.SearchCourse(P("{\"tactics\":[\"dip\",\"rows\"]}"));
                Run(c8, f8, 5);
                var a8 = AswMath.Offset(new GeoPt(f8.Tgt.Lat, f8.Tgt.Lon), 90, 10);
                f8.Subs.Add(new AswContact { Id = 7047, Name = "Sub #7047", Lat = a8.Lat, Lon = a8.Lon, Side = "unknown", Decoy = "Noisemaker", AttackOk = false });
                Run(c8, f8, 40);
                if (f8.HasEvent("asw_contact")) return "decoy taken as contact";
                if (f8.Logs.FindAll(l => l.Contains("id=7047 ignored: decoy (Noisemaker)")).Count != 1) return "decoy ignore log";
                f8.Subs.Clear();
                f8.Subs.Add(new AswContact { Id = 7050, Name = "Sub #7050", Lat = a8.Lat, Lon = a8.Lon, Side = "hostile" });
                Run(c8, f8, 5);
                if (!f8.HasEvent("asw_contact")) return "sub not reported";
                f8.Subs[0].Decoy = "usn_noisemaker";
                Run(c8, f8, 60);
                if (!f8.Logs.Exists(l => l.Contains("id=7050 reclassified decoy (usn_noisemaker): dropped"))) return "reclassified decoy not logged";
                // a whale is never a contact (T-173)
                f8.Subs.Clear();
                f8.Subs.Add(new AswContact { Id = 7060, Name = "Sub #7060", Lat = a8.Lat, Lon = a8.Lon, Side = "unknown", Biologic = "whale" });
                int before = f8.Events.FindAll(e => e.StartsWith("asw_contact ", StringComparison.Ordinal)).Count;
                Run(c8, f8, 30);
                if (f8.Events.FindAll(e => e.StartsWith("asw_contact ", StringComparison.Ordinal)).Count != before) return "a whale became a contact";
            }
            return null;
        }

        private static string SaveLoad()
        {
            string dir = Path.Combine(Path.GetTempPath(), "aswtest-" + Guid.NewGuid().ToString("N"));
            try
            {
                var f = new AswFakeGame { SaveDir = dir, LastSavePath = "/s/a.sav" };
                f.Mem.Add(AswFakeGame.Helo(1, "air"));
                f.Mem.Add(AswFakeGame.Helo(2, "deck_ready"));
                var c = Make(f);
                c.Group(P("{\"op\":\"create\",\"uids\":[1,2]}"));
                c.SearchCourse(P("{\"tactics\":[\"rows\"]}"));
                Run(c, f, 20);
                f.LastSavePath = "/s/B.sav";
                Run(c, f, 1);
                string file = Path.Combine(dir, AswSave.Key("/s/B.sav") + ".json");
                if (!File.Exists(file)) return "save file missing";
                if (File.Exists(file + ".tmp")) return "tmp left";
                // new mission from the save: uids changed, names/types the same
                var g = new AswFakeGame { SaveDir = dir, Now = f.Now };
                var x = AswFakeGame.Helo(11, "air"); x.Name = "Helo 1";
                var y = AswFakeGame.Helo(12, "deck_ready"); y.Name = "Helo 2";
                g.Mem.Add(x); g.Mem.Add(y);
                var z = AswFakeGame.Helo(13, "deck_ready"); z.Name = "Helo 3";
                g.Mem.Add(z);
                var c2 = Make(g);
                c2.OnMissionChanged("/s/b.sav", f.Now + 2);
                var v = c2.View();
                if (v.Groups.Count != 1) return "not restored: " + v.Groups.Count;
                if (v.Groups[0].Members.Count != 2 || !v.Groups[0].Members.Contains(11) || !v.Groups[0].Members.Contains(12)) return "members not matched by name+type";
                if (v.Groups[0].Order != "course" || v.Groups[0].Buoys.Count != 12) return "order/buoys lost " + v.Groups[0].Order + " " + v.Groups[0].Buoys.Count;
                if (!g.HasEvent("asw_warn", "restored")) return "no restored warning";
                // T-103 A4: the counter continues after the restored "ПЛО-1" (any prefix), a new group takes the prefix of the UI language
                if (AswController.GroupNo("ПЛО-3") != 3 || AswController.GroupNo("ASW-5") != 5 || AswController.GroupNo("ПЛО") != 0 || AswController.GroupNo("A-") != 0 || AswController.GroupNo(null) != 0 || AswController.GroupNo("X-1-7") != 7) return "GroupNo";
                using (UiText.Push("en"))
                {
                    if (Err(c2.Group(P("{\"op\":\"create\",\"uids\":[13]}"))) != null) return "create en group";
                    var names = new List<string>(); foreach (var gv in c2.View().Groups) names.Add(gv.Name);
                    if (!names.Contains("ASW-2")) return "new group name en: " + string.Join(",", names);
                }
                Run(c2, g, 2);
                // stale file ignored
                var h = new AswFakeGame { SaveDir = dir, Now = f.Now };
                h.Mem.Add(AswFakeGame.Helo(1, "air"));
                var c3 = Make(h);
                c3.OnMissionChanged("/s/b.sav", f.Now + 100);
                if (c3.View().Groups.Count != 0) return "stale file restored";
                // new mission clears
                c2.OnMissionChanged(null, 0);
                if (c2.View().Groups.Count != 0) return "new mission did not clear";
                // ask mode
                var k = new AswFakeGame { SaveDir = dir, Now = f.Now };
                k.Mem.Add(x);
                var c4 = Make(k, "ask");
                c4.OnMissionChanged("/s/b.sav", f.Now);
                if (c4.View().RestorePrompt == null || c4.View().Groups.Count != 0) return "ask mode";
                if (Err(c4.Restore(P("{}"))) != null || c4.View().Groups.Count != 1) return "ask restore";
                // off
                var c5 = Make(k, "off");
                c5.OnMissionChanged("/s/b.sav", f.Now);
                if (c5.View().Groups.Count != 0) return "off mode restored";
            }
            finally { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception) { } }
            return null;
        }

        // T-122: a broken main file falls back to .bak; R8: a new mark without groups removes the slot file
        private static string SaveSlot()
        {
            string dir = Path.Combine(Path.GetTempPath(), "aswtest-slot-" + Guid.NewGuid().ToString("N"));
            try
            {
                var f = new AswFakeGame { SaveDir = dir, LastSavePath = "/s/a.sav" };
                f.Mem.Add(AswFakeGame.Helo(1, "air")); f.Mem.Add(AswFakeGame.Helo(2, "deck_ready"));
                var c = Make(f);
                c.Group(P("{\"op\":\"create\",\"uids\":[1,2]}"));
                c.SearchCourse(P("{\"tactics\":[\"rows\"]}"));
                Run(c, f, 20);
                f.LastSavePath = "/s/B.sav"; Run(c, f, 1);
                f.LastSavePath = "/s/B.sav"; 
                string file = Path.Combine(dir, AswSave.Key("/s/B.sav") + ".json");
                if (!File.Exists(file)) return "first save missing";
                // second write into the same slot leaves a backup
                var st = new SaveSlotStore(dir, null);
                string good = File.ReadAllText(file);
                if (!st.Write("/s/B.sav", good)) return "rewrite";
                if (!File.Exists(file + ".bak")) return "no .bak after the second write";
                File.WriteAllText(file, "{broken");
                var g = new AswFakeGame { SaveDir = dir, Now = f.Now };
                var x = AswFakeGame.Helo(11, "air"); x.Name = "Helo 1";
                var y = AswFakeGame.Helo(12, "deck_ready"); y.Name = "Helo 2";
                g.Mem.Add(x); g.Mem.Add(y);
                var c2 = Make(g);
                c2.OnMissionChanged("/s/b.sav", f.Now + 2);
                if (c2.View().Groups.Count != 1) return "not restored from .bak: " + c2.View().Groups.Count;
                // groups gone, a new mark for the same path: the slot file goes
                foreach (var gv in new List<AswGroupView>(c.View().Groups)) c.Group(P("{\"op\":\"disband\",\"group\":\"" + gv.Name + "\"}"));
                if (c.View().Groups.Count != 0) return "groups not removed: " + c.View().Groups.Count;
                f.LastSavePath = "/s/c.sav"; Run(c, f, 1);
                f.LastSavePath = "/s/B.sav"; Run(c, f, 1);
                if (!File.Exists(file)) return "slot file must stay inside the grace (T-130)";
                Run(c, f, 4);
                if (File.Exists(file)) return "slot file not removed";
                if (!f.Logs.Exists(l => l.StartsWith("asw save slot cleared " + AswSave.Key("/s/B.sav")))) return "no 'slot cleared' log";
            }
            finally { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception) { } }
            return null;
        }

        // T-100: a machine in an evasion turn stays in the group, gets no tactics and is not "taken by the player"
        private static string EvadingMember()
        {
            var f = new AswFakeGame();
            f.Mem.Add(AswFakeGame.Helo(1, "air")); f.Mem.Add(AswFakeGame.Helo(2, "air"));
            var c = Make(f);
            if (Err(c.SearchCourse(P("{\"tactics\":[\"rows\"]}"))) != null) return "group";
            Run(c, f, 3);
            AswMember m1 = null; foreach (var m in f.Mem) if (m.Uid == 1) m1 = m;
            m1.Evading = true; f.Changed.Add(1);
            f.Calls.Clear();
            Run(c, f, 6);
            if (f.HasEvent("asw_warn", "player_took")) return "player_took while evading";
            if (!c.View().Groups[0].Members.Contains(1)) return "evading member dropped";
            foreach (var call in f.Calls)
                if (call == "goto:1" || call == "clear:1" || call.StartsWith("drop:1") || call.StartsWith("dip:1")) return "tactic issued to the evading machine: " + call;
            m1.Evading = false; f.Changed.Clear();
            f.Calls.Clear();
            Run(c, f, 30);
            if (!c.View().Groups[0].Members.Contains(1)) return "member lost after the turn";
            bool again = false; foreach (var call in f.Calls) if (call.EndsWith(":1") || call.Contains(":1:")) again = true;
            if (!again && f.Drops.Count == 0 && f.Dips.Count == 0 && !f.Route.ContainsKey(1)) return "no tactic after the turn";
            return null;
        }

        private static string Misc()
        {
            var f = new AswFakeGame();
            f.Mem.Add(AswFakeGame.Helo(1, "air")); f.Mem.Add(AswFakeGame.Helo(2, "air")); f.Mem.Add(AswFakeGame.Helo(3, "player"));
            var c = Make(f);
            if (c.Group(P("{\"op\":\"add\",\"group\":\"nope\",\"uids\":[1]}")) != "!no_group") return "no_group";
            if (c.Stop(P("{\"group\":\"nope\"}")) != "!no_group") return "stop no_group";
            var empty = new AswFakeGame();
            if (Make(empty).SearchCourse(P("{}")) != "!no_aircraft") return "course without aircraft";
            // autogroup takes capable non-player aircraft
            c = Make(f);
            if (Err(c.SearchCourse(P("{\"tactics\":[\"rows\"]}"))) != null) return "auto group";
            if (c.View().Groups.Count != 1 || c.View().Groups[0].Members.Count != 2) return "auto group members";
            Run(c, f, 3);
            // player takes over aircraft 1
            f.Changed.Add(1);
            Run(c, f, 2);
            if (!f.HasEvent("asw_warn", "player_took")) return "no player_took";
            if (c.View().Groups[0].Members.Contains(1)) return "taken aircraft stays";
            if (!f.Calls.Contains("restore_roe:1:weapons_free")) return "roe not restored on release";
            // stop restores the others
            if (Err(c.Stop(P("{\"all\":true}"))) != null) return "stop";
            var f2 = new AswFakeGame();
            f2.Mem.Add(AswFakeGame.Helo(1, "air"));
            var c2 = Make(f2);
            c2.SearchCourse(P("{\"tactics\":[\"rows\"]}"));
            Run(c2, f2, 3);
            c2.Stop(P("{\"group\":\"all\"}"));
            if (!f2.Calls.Contains("restore_roe:1:weapons_free") || !f2.Calls.Contains("depth:1:below") || f2.Calls.FindAll(x => x == "depth:1:alternate").Count != 1) return "stop did not restore roe/depth";
            if (c.View().Active) return "still active";
            // group ops
            if (Err(c.Group(P("{\"op\":\"create\",\"group\":\"X\",\"uids\":[2]}"))) != null) return "create";
            if (c.View().Groups.Count != 2) return "two groups " + c.View().Groups.Count;
            if (Err(c.Group(P("{\"op\":\"disband\",\"group\":\"X\"}"))) != null) return "disband";
            if (c.Group(P("{\"op\":\"create\",\"uids\":[\"x\"]}")) != "!bad_args") return "bad args";
            c.ReleaseUid(2);
            return null;
        }

        private static string Area()
        {
            var f = new AswFakeGame();
            f.Mem.Add(AswFakeGame.Helo(1, "air"));
            var c = Make(f);
            string r = c.SearchArea(P("{\"bearing\":0,\"dist_nm\":20,\"size_nm\":10}"));
            if (Err(r) != null) return r;
            Run(c, f, 5);
            if (f.Drops.Count < 1) return "no area drop";
            if (c.View().Groups[0].Rect == null || c.View().Groups[0].Rect.Length != 8) return "rect";
            // every point lies inside the area (20 nm north of the target)
            var ctr = AswMath.Offset(new GeoPt(35, 20), 0, 20);
            foreach (var p in f.Drops[0].Pts)
            {
                var l = AswMath.ToLocal(ctr, 0, p);
                if (Math.Abs(l.X) > 5.2 || Math.Abs(l.Y) > 5.2) return "point outside area";
            }
            // the centre is covered, and the route is no longer than the naive order
            bool centre = false;
            foreach (var p in f.Drops[0].Pts) { var l = AswMath.ToLocal(ctr, 0, p); if (Math.Abs(l.X) < 0.3 && Math.Abs(l.Y) < 0.3) centre = true; }
            if (!centre) return "centre not covered";
            string bad = c.SearchArea(P("{\"bearing\":10}"));
            if (bad != "!bad_args") return "area args";
            return null;
        }

        private static string Tactics()
        {
            var f = new AswFakeGame();
            f.Mem.Add(AswFakeGame.Helo(1, "air", dip: true));
            var h2 = AswFakeGame.Helo(2, "air"); h2.Mad = true; f.Mem.Add(h2);
            var c = Make(f);
            if (Err(c.SearchCourse(P("{\"tactics\":[\"dip\",\"rows\"]}"))) != null) return "order";
            Run(c, f, 3);
            var rows = c.View().Groups[0].MemberRows;
            if (rows.Count != 2 || rows[0].Tactic != "dip") return "initial tactics " + rows[0].Tactic + "/" + rows[1].Tactic;
            // switch to the barrier only (T-176: the MAD snake by course is gone): everybody who held a gone tactic is re-made
            string r = c.SetTactics(P("{\"tactics\":[\"barrier\"]}"));
            if (Err(r) != null) return "set_tactics " + r;
            Run(c, f, 3);
            rows = c.View().Groups[0].MemberRows;
            int mad = 0, other = 0;
            foreach (var m in rows) { if (m.Tactic == "barrier") mad++; else if (m.Tactic != "reserve") other++; }
            if (mad != 2 || other != 0) return "after switch barrier=" + mad + " other=" + other;   // two aircraft with buoys: both fly the barrier (lanes)
            if (c.View().Groups[0].Tactics.Count != 1 || c.View().Groups[0].Tactics[0] != "barrier") return "group tactics";
            if (c.SetTactics(P("{\"group\":\"zz\",\"tactics\":[]}")) != "!no_group") return "unknown group";
            if (c.SetTactics(P("{}")) != "!bad_args") return "bad args";
            // area takes the tactics of the command
            var g = new AswFakeGame();
            g.Mem.Add(AswFakeGame.Helo(1, "air", dip: true));
            var c2 = Make(g);
            if (Err(c2.SearchArea(P("{\"bearing\":0,\"dist_nm\":20,\"tactics\":[\"dip\"]}"))) != null) return "area order";
            if (c2.View().Groups[0].Tactics.Count != 1 || c2.View().Groups[0].Tactics[0] != "dip") return "area tactics " + c2.View().Groups[0].Tactics.Count;
            Run(c2, g, 3);
            if (g.Drops.Count != 0 || g.Dips.Count == 0) return "area dip: drops=" + g.Drops.Count + " dips=" + g.Dips.Count;
            // a new search order resets the assignments
            c2.SearchArea(P("{\"bearing\":0,\"dist_nm\":20,\"tactics\":[\"rows\"]}"));
            Run(c2, g, 3);
            if (c2.View().Groups[0].MemberRows[0].Tactic != "grid") return "area reset " + c2.View().Groups[0].MemberRows[0].Tactic;
            return null;
        }

        private static string Groups()
        {
            var f = new AswFakeGame();
            f.Mem.Add(AswFakeGame.Helo(1, "air")); f.Mem.Add(AswFakeGame.Helo(2, "air"));
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1,2]}"));
            if (c.View().Available.Count != 0) return "grouped aircraft are available";
            c.Group(P("{\"op\":\"disband\",\"group\":\"G\"}"));
            if (c.View().Groups.Count != 0 || c.View().Available.Count != 2) return "disband -> available " + c.View().Available.Count;
            // an empty group stays until the player disbands it (T-142)
            c.Group(P("{\"op\":\"create\",\"group\":\"E\"}"));
            Run(c, f, 60);
            if (c.View().Groups.Count != 1) return "empty group removed";
            if (f.HasEvent("asw_warn", "group_removed_empty")) return "empty group removal event";
            c.Group(P("{\"op\":\"disband\",\"group\":\"E\"}"));
            if (c.View().Groups.Count != 0) return "disband of an empty group";
            // a vanished member gives a warning with the reason
            c.Group(P("{\"op\":\"create\",\"group\":\"G2\",\"uids\":[1]}"));
            Run(c, f, 2);
            f.Mem.RemoveAll(m => m.Uid == 1);
            Run(c, f, 2);
            if (f.HasEvent("asw_warn", "member_lost")) return "member_lost at once (a landed machine may be coming back)";
            Run(c, f, 310);   // T-150: the member is given up only after the adoption gap
            if (!f.HasEvent("asw_warn", "member_lost") || !f.HasEvent("asw_warn", "left_air")) return "no member_lost with reason";
            return null;
        }

        private static string ForeignOwned()
        {
            var f = new AswFakeGame();
            f.Mem.Add(AswFakeGame.Helo(1, "air"));
            f.Mem.Add(AswFakeGame.Helo(2, "air"));
            var own = new AirOwnership();
            own.Claim(2, "cap", "ПВО-1");
            var c = Make(f);
            c.Ownership = own;
            Run(c, f, 2);
            c.Group(P("{\"op\":\"create\",\"group\":\"ПЛО-9\",\"uids\":[1,2]}"));
            Run(c, f, 2);
            var v = c.View();
            bool in2 = false;
            foreach (var g in v.Groups) if (g.Members.Contains(2)) in2 = true;
            if (in2) return "cap-owned machine joined an ASW group";
            string reason = null;
            foreach (var x in v.Excluded) if (x.Row.Uid == 2) reason = x.Reason;
            if (reason != "in_group:ПВО-1") return "excluded reason " + reason;
            foreach (var x in v.ExcludedSar) if (x.Row.Uid == 2) reason = x.Reason;
            if (reason != "in_group:ПВО-1") return "excluded sar reason " + reason;
            foreach (var r in v.Available) if (r.Uid == 2) return "still available";
            // without Ownership: as before
            var f2 = new AswFakeGame();
            f2.Mem.Add(AswFakeGame.Helo(2, "air"));
            var c2 = Make(f2);
            Run(c2, f2, 2);
            bool avail = false;
            foreach (var r in c2.View().Available) if (r.Uid == 2) avail = true;
            if (!avail) return "no Ownership: must stay available";
            return null;
        }

        private static string Lists()
        {
            var f = new AswFakeGame();
            f.Mem.Add(AswFakeGame.Helo(1, "air"));
            var s1 = AswFakeGame.Helo(2, "deck_ready"); s1.Type = "SH-2F"; f.Mem.Add(s1);
            var s2 = AswFakeGame.Helo(3, "deck_prep"); s2.Type = "SH-2F"; s2.ReadyInS = 200; f.Mem.Add(s2);
            f.Mem.Add(new AswMember { Uid = 4, Name = "Hawkeye", Type = "E-2C", Where = "air", FuelFrac = 1, EnduranceS = 9000 });
            var bare = AswFakeGame.Helo(5, "air", passive: 0); bare.ActiveBuoys = 0; bare.Vlad = 0; bare.Torpedoes = 0; f.Mem.Add(bare);
            f.Mem.Add(new AswMember { Uid = 6, Name = "SAR", Type = "SH-3H", Where = "sar", Passive = 12 });
            f.Mem.Add(new AswMember { Uid = 7, Name = "Patrol", Type = "SH-3H", Where = "game_patrol", Passive = 12 });
            var c = Make(f);
            Run(c, f, 2);
            var v = c.View();
            if (v.Available.Count != 3) return "available " + v.Available.Count;
            bool deck2 = false, deck3 = false;
            foreach (var r in v.Available) { if (r.Uid == 2) deck2 = true; if (r.Uid == 3) deck3 = true; }
            if (!deck2 || !deck3) return "both deck SH-2F must be available";
            var reasons = new Dictionary<int, string>();
            foreach (var x in v.Excluded) reasons[x.Row.Uid] = x.Reason;
            if (reasons[4] != "aew" || reasons[5] != "no_asw_load" || reasons[6] != "ext_sar" || reasons[7] != "game_patrol") return "reasons " + reasons.Count;
            if (!c.StateJson().Contains("\"excluded\"") || !c.StateJson().Contains("\"available\"") || !(Json.Parse(c.StateJson()) is Dictionary<string, object>)) return "state json lists";
            // autogroup never takes the excluded ones
            c.SearchCourse(P("{\"tactics\":[\"rows\"]}"));
            if (c.View().Groups[0].Members.Contains(4) || c.View().Groups[0].Members.Contains(5)) return "excluded in autogroup";
            // a free aircraft that vanished stays under excluded
            var g = new AswFakeGame();
            g.Mem.Add(AswFakeGame.Helo(1, "air")); g.Mem.Add(AswFakeGame.Helo(2, "air"));
            var c2 = Make(g);
            Run(c2, g, 2);
            g.Mem.RemoveAll(m => m.Uid == 2);
            Run(c2, g, 2);
            string lost = null; foreach (var x in c2.View().Excluded) if (x.Row.Uid == 2) lost = x.Reason;
            if (lost != "lost") return "gone reason " + lost;
            return null;
        }

        private static string PreBuoys()
        {
            var f = new AswFakeGame();
            f.Mem.Add(AswFakeGame.Helo(1, "air"));
            var inside = AswMath.Offset(new GeoPt(35, 20), 0, 20);
            var outside = AswMath.Offset(new GeoPt(35, 20), 180, 60);
            f.Buoys.Add(new AswBuoyObs { Id = 1, OwnerUid = 1, Lat = inside.Lat, Lon = inside.Lon, Ammo = "usn_ssq-53" });
            f.Buoys.Add(new AswBuoyObs { Id = 2, OwnerUid = 1, Lat = outside.Lat, Lon = outside.Lon, Ammo = "usn_ssq-53" });
            f.Buoys.Add(new AswBuoyObs { Id = 3, OwnerUid = 99, Lat = inside.Lat, Lon = inside.Lon, Ammo = "usn_ssq-53" });
            var c = Make(f);
            if (Err(c.SearchArea(P("{\"bearing\":0,\"dist_nm\":20,\"size_nm\":10,\"tactics\":[\"dip\"]}"))) != null) return "order";
            Run(c, f, 3);
            var gv = c.View().Groups[0];
            int pre = 0; foreach (var b in gv.Buoys) if (b.Pre) pre++;
            if (pre != 1) return "pre buoys counted " + pre;
            return null;
        }

        private static string Hotkeys()
        {
            if (new AswConfig().Hotkey != "LeftAlt+U|LeftAlt+J|F7") return "default hotkey";
            return null;
        }
    }
}
