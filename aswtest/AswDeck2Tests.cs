using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>T-150 A1: adoption, hand-over and landing on the realistic deck model (order of events as in the game, 0.19.1 log).</summary>
    internal static class AswFakeDeckTests
    {
        private const string Sh3 = "usn_sh-3h";
        private static Dictionary<string, object> P(string json) { return (Dictionary<string, object>)Json.Parse(json); }

        private static AswFakeGame Game(out AswFakeDeck d)
        {
            var f = new AswFakeGame();
            d = new AswFakeDeck();
            f.RealDeck = d;
            f.Decks[77] = new AswTarget { Uid = 77, Name = d.ShipName, Lat = 35, Lon = 20 };
            return f;
        }

        private static AswController Make(AswFakeGame f) { return new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "off" }); }

        private static void Run(AswController c, AswFakeGame f, int seconds)
        {
            for (int i = 0; i < seconds; i++) { f.Advance(1); f.Tick(c); }
        }

        private static bool Logged(AswFakeGame f, string sub) { foreach (var l in f.Logs) if (l.Contains(sub)) return true; return false; }

        private static string Bad(AswFakeGame f) { foreach (var l in f.Logs) if (l.Contains("tick error")) return l; return null; }

        private static int Air(AswController c)
        {
            int n = 0;
            foreach (var g in c.View().Groups) foreach (var r in g.MemberRows) if (r.State == "air") n++;
            return n;
        }

        private static string Create(AswController c, params int[] uids)
        {
            var sb = new System.Text.StringBuilder("{\"op\":\"create\",\"group\":\"ASW-3\",\"uids\":[");
            for (int i = 0; i < uids.Length; i++) { if (i > 0) sb.Append(','); sb.Append(uids[i]); }
            string r = c.Group(P(sb.Append("]}").ToString()));
            return AswController.IsErr(r) ? r : null;
        }

        public static string Run()
        {
            string e;
            if ((e = Live0191()) != null) return "Live0191: " + e;
            if ((e = HangarEmpty()) != null) return "HangarEmpty: " + e;
            if ((e = NoDoubleCreate()) != null) return "NoDoubleCreate: " + e;
            if ((e = ForeignLaunch()) != null) return "ForeignLaunch: " + e;
            if ((e = Landing()) != null) return "Landing: " + e;
            if ((e = Gap()) != null) return "Gap: " + e;
            return null;
        }

        // the 0.19.1 log: a ready task, two machines in the hangar and a later task; all of them end up in the air in the group
        // T-172 R10: a buoy holder that has used its passive/VLAD buoys is replaced (active buoys do not search), so a 4th machine may fly: at least 3, not exactly 3
        private static string Live0191()
        {
            AswFakeDeck d; var f = Game(out d);
            d.AddHangar(Sh3, "SH-3H", 2);
            string ka = d.AddTask(Sh3, "SH-3H", 0), kb = d.AddTask(Sh3, "SH-3H", 1800);
            var c = Make(f);
            f.Advance(1); f.Tick(c);
            string err = Create(c, d.UidOfTask(ka), AswMath.HangarUid(77, Sh3), d.UidOfTask(kb));
            if (err != null) return err;
            if (AswController.IsErr(c.SearchCourse(P("{\"tactics\":[\"dip\",\"rows\"]}")))) return "order";
            Run(c, f, 3600);
            if (Bad(f) != null) return Bad(f);
            if (f.HasEvent("asw_warn", "member_lost")) return "member_lost: " + string.Join(" | ", f.Events.ToArray());
            if (Air(c) < 3) return "air members " + Air(c) + " rows " + Dump(c);
            foreach (var r in c.View().Available) if (r.Type != null && r.Type.Contains("SH-3H")) return "SH-3H still available: " + r.Name;
            if (!Logged(f, "asw handover")) return "no handover log";
            if (!Logged(f, "asw adopt")) return "no adopt log";
            return null;
        }

        private static string Dump(AswController c)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var g in c.View().Groups) foreach (var r in g.MemberRows) sb.Append(r.Uid).Append(':').Append(r.State).Append('/').Append(r.Tactic).Append(' ');
            return sb.ToString();
        }

        // the last machine of the hangar goes into a task: the row disappears, nobody is lost
        private static string HangarEmpty()
        {
            AswFakeDeck d; var f = Game(out d);
            d.AddHangar(Sh3, "SH-3H", 1);
            var c = Make(f);
            f.Advance(1); f.Tick(c);
            string err = Create(c, AswMath.HangarUid(77, Sh3));
            if (err != null) return err;
            if (AswController.IsErr(c.SearchCourse(P("{\"tactics\":[\"dip\"]}")))) return "order";
            Run(c, f, 120);
            if (Bad(f) != null) return Bad(f);
            if (f.HasEvent("asw_warn", "member_lost")) return "member_lost";
            // T-181 (ADR-0080): the last machine goes into a task: the member simply becomes that order, the row going away is no event
            if (Logged(f, "asw member hangar_empty") || Logged(f, "asw member lost") || Logged(f, "hangar_taken")) return "the member was reported lost";
            if (!Logged(f, "asw handover")) return "no handover";
            if (d.Orders.Count != 1) return "orders " + d.Orders.Count;
            var g = c.View().Groups[0];
            if (g.Members.Count != 1) return "members " + g.Members.Count + " " + Dump(c);
            return null;
        }

        // T-181: three slots in the group, one tactic: no slot is ordered twice in 600 s (the order moves the member to the task, there is no source row any more)
        private static string NoDoubleCreate()
        {
            AswFakeDeck d; var f = Game(out d);
            d.PrepS = 1200;
            d.AddHangar(Sh3, "SH-3H", 3);
            var c = Make(f);
            f.Advance(1); f.Tick(c);
            string err = Create(c, AswMath.HangarUid(77, Sh3));
            if (err != null) return err;
            if (AswController.IsErr(c.SearchCourse(P("{\"tactics\":[\"dip\"]}")))) return "order";
            Run(c, f, 600);
            if (f.HasEvent("asw_warn", "member_lost")) return "member_lost";
            var slots = new HashSet<string>();
            foreach (var l in f.Logs)
            {
                int i = l.IndexOf("asw handover ", StringComparison.Ordinal), j = l.IndexOf(" slot=", StringComparison.Ordinal);
                if (i < 0 || j < 0) continue;
                string sl = l.Substring(j + 6, l.IndexOf(' ', j + 6) - j - 6);
                if (!slots.Add(sl)) return "slot ordered twice: " + sl;
            }
            if (d.Orders.Count < 1 || d.Orders.Count != slots.Count) return "orders " + d.Orders.Count + " handovers " + slots.Count;
            return null;
        }

        // an F-14 of the same deck launches while our task is spawning: it is not adopted
        private static string ForeignLaunch()
        {
            AswFakeDeck d; var f = Game(out d);
            string ka = d.AddTask(Sh3, "SH-3H", 5);
            var c = Make(f);
            f.Advance(1); f.Tick(c);
            string err = Create(c, d.UidOfTask(ka));
            if (err != null) return err;
            if (AswController.IsErr(c.SearchCourse(P("{\"tactics\":[\"dip\"]}")))) return "order";
            int f14 = 0;
            for (int i = 0; i < 200; i++)
            {
                f.Advance(1);
                if (i == 3) f14 = d.LaunchForeign("usn_f-14", "F-14A");
                f.Tick(c);
            }
            foreach (var g in c.View().Groups) foreach (int u in g.Members) if (u == f14) return "the F-14 was adopted";
            if (Air(c) != 1) return "air " + Air(c) + " " + Dump(c);
            if (f.HasEvent("asw_warn", "member_lost")) return "member_lost";
            return null;
        }

        // fuel runs out: air -> unit: recovering -> cool: -> hangar; the machine stays in the group all the time and can fly again
        private static string Landing()
        {
            AswFakeDeck d; var f = Game(out d);
            d.CoolS = 300; d.PrepS = 300;
            string ka = d.AddTask(Sh3, "SH-3H", 30, true);
            var c = Make(f);
            f.Advance(1); f.Tick(c);
            string err = Create(c, d.UidOfTask(ka));
            if (err != null) return err;
            if (AswController.IsErr(c.SearchCourse(P("{\"tactics\":[\"dip\"]}")))) return "order";
            Run(c, f, 100);
            if (Air(c) != 1) return "not airborne " + Dump(c);
            int uid = d.Air[0].Uid;
            d.ForceRecover(uid);
            int seenNonAir = 0;
            for (int i = 0; i < 1500; i++)
            {
                f.Advance(1); f.Tick(c);
                var g = c.View().Groups[0];
                if (g.MemberRows.Count + 0 != g.Members.Count && g.Members.Count != 1) return "members " + g.Members.Count;
                if (g.Members.Count != 1) return "lost from the group at " + i + ": " + g.Members.Count;
                if (g.MemberRows.Count == 1 && g.MemberRows[0].State != "air") seenNonAir++;
            }
            if (Bad(f) != null) return Bad(f);
            if (f.HasEvent("asw_warn", "member_lost")) return "member_lost";
            if (seenNonAir == 0) return "never on the deck";
            if (!Logged(f, "via=cool")) return "the cool-down row was not followed";
            if (!Logged(f, "via=hangar")) return "the hangar row was not followed";
            // after the cool-down it is a hangar row of the group again, and the plan flew it once more
            if (Air(c) != 1 && !HasHangarMember(c)) return "neither flying nor in the hangar: " + Dump(c);
            return null;
        }

        private static bool HasHangarMember(AswController c)
        {
            foreach (var r in c.View().Groups[0].MemberRows) if (r.State != "air") return true;
            return false;
        }

        // one frame with neither the task nor the unit row: the member stays
        private static string Gap()
        {
            AswFakeDeck d; var f = Game(out d);
            d.GapFrame = true;
            string ka = d.AddTask(Sh3, "SH-3H", 3, true);
            var c = Make(f);
            f.Advance(1); f.Tick(c);
            string err = Create(c, d.UidOfTask(ka));
            if (err != null) return err;
            if (AswController.IsErr(c.SearchCourse(P("{\"tactics\":[\"dip\"]}")))) return "order";
            Run(c, f, 120);
            if (f.HasEvent("asw_warn", "member_lost")) return "member_lost";
            if (Air(c) != 1) return "air " + Air(c) + " " + Dump(c);
            return null;
        }
    }

    /// <summary>T-126: lazy RefreshLive (idle controller does not ask the game) and the air tick clock at x16 on the realistic deck.</summary>
    internal static class AswTickTests
    {
        private const string Sh3 = "usn_sh-3h";
        private static Dictionary<string, object> P(string json) { return (Dictionary<string, object>)Json.Parse(json); }
        private static AswController Make(AswFakeGame f) { return new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "off" }); }

        public static string Run()
        {
            string e;
            if ((e = Idle()) != null) return "Idle: " + e;
            if ((e = Opens()) != null) return "Opens: " + e;
            if ((e = Command()) != null) return "Command: " + e;
            if ((e = WithGroup()) != null) return "WithGroup: " + e;
            if ((e = Swap16()) != null) return "Swap16: " + e;
            return null;
        }

        private static string Idle()
        {
            var f = new AswFakeGame();
            f.Mem.Add(AswFakeGame.Helo(1, "air"));
            var c = Make(f);
            c.ViewWanted = false;
            for (int i = 0; i < 100; i++) { f.Advance(1); f.Tick(c); if (i == 50) f.LastSavePath = "b.sav"; }
            if (f.MembersCalls != 0) return "Members calls " + f.MembersCalls;
            if (c.SkippedTicks != 100) return "skipped " + c.SkippedTicks;
            if (f.SavePathReads < 100) return "save marker reads " + f.SavePathReads;
            return null;
        }

        private static string Opens()
        {
            var f = new AswFakeGame();
            f.Mem.Add(AswFakeGame.Helo(1, "air"));
            var c = Make(f);
            c.ViewWanted = false;
            for (int i = 0; i < 5; i++) { f.Advance(1); f.Tick(c); }
            if (f.MembersCalls != 0) return "closed: " + f.MembersCalls;
            c.ViewWanted = true;
            if (f.MembersCalls != 1) return "opened (same frame): " + f.MembersCalls;
            f.Advance(1); f.Tick(c);
            if (f.MembersCalls != 2) return "next tick: " + f.MembersCalls;
            if (c.View().Available.Count == 0) return "view not rebuilt";
            if (f.HasEvent("asw_warn", "member_lost")) return "member_lost";
            return null;
        }

        // a command while the window is closed: EnsureLive (the single entry of GameApi.AswCmd) brings the rows in
        private static string Command()
        {
            var f = new AswFakeGame();
            var c = Make(f);
            c.ViewWanted = false;
            for (int i = 0; i < 10; i++) { f.Advance(1); f.Tick(c); }
            f.Mem.Add(AswFakeGame.Helo(1, "air"));
            c.EnsureLive();
            string r = c.Group(P("{\"op\":\"create\",\"uids\":[1]}"));
            if (AswController.IsErr(r)) return "create after EnsureLive: " + r;
            // the reference: the same scenario with the window open
            var f2 = new AswFakeGame();
            var c2 = Make(f2);
            for (int i = 0; i < 10; i++) { f2.Advance(1); f2.Tick(c2); }
            f2.Mem.Add(AswFakeGame.Helo(1, "air"));
            f2.Advance(1); f2.Tick(c2);
            string r2 = c2.Group(P("{\"op\":\"create\",\"uids\":[1]}"));
            if (AswController.IsErr(r2)) return "reference: " + r2;
            if (c.View().Groups.Count != 1) return "groups " + c.View().Groups.Count;
            // EnsureLive twice asks the game once
            int before = f.MembersCalls;
            c.EnsureLive();
            if (f.MembersCalls != before) return "EnsureLive when fresh";
            return null;
        }

        private static string WithGroup()
        {
            var f = new AswFakeGame();
            f.Mem.Add(AswFakeGame.Helo(1, "air", true));
            var c = Make(f);
            c.ViewWanted = false;
            f.Advance(1); f.Tick(c);
            c.EnsureLive();
            if (AswController.IsErr(c.Group(P("{\"op\":\"create\",\"uids\":[1]}")))) return "create";
            int before = f.MembersCalls, skipped = c.SkippedTicks;
            for (int i = 0; i < 10; i++) { f.Advance(1); f.Tick(c); }
            if (f.MembersCalls - before != 10) return "Members calls " + (f.MembersCalls - before);
            if (c.SkippedTicks != skipped) return "skipped with a group";
            return null;
        }

        // x16: game +0.53 s per frame, real +0.033 s; the clock gives the steps. The deck swap works, no more deck orders than at x1.
        private static string Swap16()
        {
            int orders1, orders16; string e;
            if ((e = Flight(1.0, 1.0, out orders1)) != null) return "x1 " + e;
            if ((e = Flight(0.53, 0.033, out orders16)) != null) return "x16 " + e;
            if (orders16 > orders1) return "orders x16 " + orders16 + " > x1 " + orders1;
            return null;
        }

        private static string Flight(double gameStep, double realStep, out int orders)
        {
            orders = 0;
            var f = new AswFakeGame();
            var d = new AswFakeDeck();
            f.RealDeck = d;
            f.Decks[77] = new AswTarget { Uid = 77, Name = d.ShipName, Lat = 35, Lon = 20 };
            string ka = d.AddTask(Sh3, "SH-3H", 5);
            var c = Make(f);
            var clock = new AirTickClock();
            double real = 0, endGame = f.Now + 318;
            bool first = true;
            while (f.Now < endGame)
            {
                f.Advance(gameStep); real += realStep;
                if (first || clock.Due(f.Now, real))
                {
                    f.Tick(c);
                    if (first)
                    {
                        string r = c.Group(P("{\"op\":\"create\",\"group\":\"ASW-3\",\"uids\":[" + d.UidOfTask(ka) + "]}"));
                        if (AswController.IsErr(r)) return r;
                        if (AswController.IsErr(c.SearchCourse(P("{\"tactics\":[\"dip\"]}")))) return "order";
                        first = false;
                    }
                }
                f.Flush();
            }
            foreach (var l in f.Logs) if (l.Contains("tick error")) return l;
            if (f.HasEvent("asw_warn", "member_lost")) return "member_lost";
            int air = 0;
            foreach (var g in c.View().Groups) foreach (var r in g.MemberRows) if (r.State == "air") air++;
            if (air != 1) return "air " + air;
            orders = d.Orders.Count;
            return null;
        }
    }
}
