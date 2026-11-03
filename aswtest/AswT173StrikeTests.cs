using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VoiceBotBridge
{
    // T-173 (ADR-0074): the strike group: the order, the targets, the assignment, the claims, the deck, skip, the cards, the save.
    internal static class AswT173StrikeTests
    {
        private static Dictionary<string, object> P(string json) { return AswT173AttackTests.P(json); }
        private static void Run(AswController c, AswFakeGame f, int s) { AswT173AttackTests.Run(c, f, s); }

        private static AswContact Sub(AswFakeGame f, int id, double eastNm, string side = "hostile", double northNm = 0)
        {
            var p = AswMath.Offset(AswMath.Offset(new GeoPt(f.Tgt.Lat, f.Tgt.Lon), 90, eastNm), 0, northNm);
            var s = new AswContact { Id = id, Name = "Sub #" + id, Lat = p.Lat, Lon = p.Lon, Source = "live", Side = side, Live = true, ClassName = "Kilo", Classified = true, MapPosture = side == "hostile" ? "Enemy" : "Unknown" };
            f.Subs.Add(s);
            return s;
        }

        private static AswMember Helo(AswFakeGame f, int uid, double lonShift, int torps = 2)
        {
            var m = AswFakeGame.Helo(uid, "air"); m.Torpedoes = torps; m.Lat = f.Tgt.Lat; m.Lon = f.Tgt.Lon + lonShift;
            f.Mem.Add(m); f.FixPos.Add(uid);
            return m;
        }

        /// <summary>A strike group "G" of the helicopters in f.Mem, the order given.</summary>
        private static AswController Strike(AswFakeGame f, Action<AswConfig> cfg = null, string order = "")
        {
            var c = AswT173AttackTests.Make(f, cfg);
            string uids = string.Join(",", f.Mem.Select(m => m.Uid.ToString()).ToArray());
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[" + uids + "]}"));
            string r = c.Order(P("{\"group\":\"G\",\"order\":\"strike\"" + order + "}"));
            if (AswController.IsErr(r)) throw new InvalidOperationException(r);
            return c;
        }

        private static bool Log(AswFakeGame f, string needle) { return AswT173AttackTests.Log(f, needle); }

        public static string Run()
        {
            string e;
            if ((e = OrderCmd()) != null) return "order: " + e;
            if ((e = Eligible()) != null) return "strike-eligible: " + e;
            if ((e = Assign()) != null) return "strike-assign: " + e;
            if ((e = TwoGroups()) != null) return "strike-two-groups: " + e;
            if ((e = Skip()) != null) return "strike-skip: " + e;
            if ((e = Switch()) != null) return "strike-switch: " + e;
            if ((e = Player()) != null) return "strike-player: " + e;
            if ((e = Deck()) != null) return "strike-deck: " + e;
            if ((e = Alerts()) != null) return "strike-alerts: " + e;
            if ((e = IdleLand()) != null) return "strike-idle: " + e;
            if ((e = IdleNoHome()) != null) return "strike-idle-no-home: " + e;
            if ((e = MembersListed()) != null) return "strike-members-listed: " + e;
            if ((e = SaveLoad()) != null) return "strike-save: " + e;
            return null;
        }

        // asw_order order=strike: the group, its order, the members without a torpedo leave; bad arguments
        private static string OrderCmd()
        {
            var f = new AswFakeGame();
            Helo(f, 1, 0); Helo(f, 2, 0); Helo(f, 3, 0, 0);
            var c = AswT173AttackTests.Make(f);
            c.Group(P("{\"op\":\"create\",\"uids\":[1,2,3]}"));
            string name = c.View().Groups[0].Name;
            string r = c.Order(P("{\"group\":\"" + name + "\",\"order\":\"strike\",\"radius_nm\":25,\"targets\":\"hostile\",\"airborne\":1,\"ref_uid\":500}"));
            if (AswController.IsErr(r)) return r;
            var g = c.View().Groups[0];
            if (g.Order != "strike" || g.Members.Count != 2 || g.Members.Contains(3)) return "group " + g.Order + " " + g.Members.Count;
            if (g.RefUid != 500 || g.StrikeRadiusNm != 25 || g.Targets != "hostile" || g.Airborne != 1) return "fields";
            if (!c.View().ExcludedStrike.Exists(x => x.Row.Uid == 3 && x.Reason == "no_torpedo")) return "no_torpedo not excluded";
            var stj = Json.Parse(c.StateJson()) as Dictionary<string, object>;
            var grp = ((List<object>)stj["groups"])[0] as Dictionary<string, object>;
            if ((string)grp["order"] != "strike" || !grp.ContainsKey("strike") || grp["strike"] == null) return "state json";
            if (!g.Name.StartsWith(UiText.Get("asw.group.strike_prefix"), StringComparison.Ordinal)) return "automatic name is not renamed: " + g.Name;
            if (!AswController.IsErr(c.Order(P("{\"group\":\"nope\",\"order\":\"strike\"}")))) return "unknown group accepted";
            if (c.Order(P("{\"group\":\"" + g.Name + "\",\"order\":\"strike\",\"radius_nm\":-3}")) != "!bad_args") return "bad radius";
            return null;
        }

        // red -> a target; grey classified 60 s -> a target, 30 s -> not yet; grey without class, neutral posture, whale, decoy -> never
        private static string Eligible()
        {
            var f = new AswFakeGame { TruthTrap = true };
            Helo(f, 1, 0); Helo(f, 2, 0); Helo(f, 3, 0); Helo(f, 4, 0); Helo(f, 5, 0);
            var red = Sub(f, 11, 5);
            var grey = Sub(f, 12, 6, "unknown");
            var noClass = Sub(f, 13, 7, "unknown"); noClass.ClassName = null; noClass.Classified = null;
            var neutralPosture = Sub(f, 14, 8, "unknown"); neutralPosture.MapPosture = "Neutral";
            var whale = Sub(f, 15, 9, "unknown"); whale.Biologic = "whale";
            var decoy = Sub(f, 16, 10, "hostile"); decoy.Decoy = "Noisemaker";
            var c = Strike(f);
            Run(c, f, 30);
            if (c.RunState(11) == null) return "red sub is not a target";
            if (c.RunState(12) != null) return "grey sub taken after 30 s";
            Run(c, f, 40);
            if (c.RunState(12) == null) return "grey classified sub not taken after 70 s";
            foreach (int id in new[] { 13, 14, 15, 16 }) if (c.RunState(id) != null) return "target " + id + " must not be attacked";
            if (!Log(f, "target contact=13 ok=0 reason=unclassified")) return "no unclassified line";
            if (!Log(f, "target contact=14 ok=0 reason=neutral")) return "no neutral line";
            if (!Log(f, "target contact=15 ok=0 reason=biologic")) return "no biologic line";
            if (!Log(f, "target contact=16 ok=0 reason=decoy")) return "no decoy line";
            if (!Log(f, "target contact=12 ok=0 reason=unknown_young")) return "no young line";
            // hostile only
            var f2 = new AswFakeGame();
            Helo(f2, 1, 0);
            var g2 = Sub(f2, 12, 6, "unknown");
            var c2 = Strike(f2, null, ",\"targets\":\"hostile\"");
            Run(c2, f2, 100);
            if (c2.RunState(12) != null) return "grey sub attacked in hostile-only mode";
            // the zone
            var f3 = new AswFakeGame();
            Helo(f3, 1, 0);
            Sub(f3, 11, 40);
            var c3 = Strike(f3, null, ",\"radius_nm\":20,\"ref_uid\":500");
            Run(c3, f3, 20);
            if (c3.RunState(11) != null || !Log(f3, "reason=out_of_zone")) return "out of zone taken";
            // an own sub on the track
            var f4 = new AswFakeGame();
            Helo(f4, 1, 0);
            var s4 = Sub(f4, 11, 5);
            f4.OwnSubsList.Add(new AswOwnSub { Uid = 900, Name = "Own", Lat = s4.Lat, Lon = s4.Lon });
            var c4 = Strike(f4);
            Run(c4, f4, 20);
            if (c4.RunState(11) != null || !Log(f4, "reason=own_track")) return "own track attacked";
            return null;
        }

        private static List<string> AssignLines(AswFakeGame f) { return f.Logs.FindAll(l => l.StartsWith("asw strike G assign ", StringComparison.Ordinal)); }

        // 3 targets, 2 airborne shooters, one on the deck: by priority; the extra aircraft is a second number
        private static string Assign()
        {
            var f = new AswFakeGame();
            f.ShipsList.Add(new AswShipPos { Uid = 500, Name = "Yorktown", Lat = f.Tgt.Lat, Lon = f.Tgt.Lon });
            Helo(f, 1, 0); Helo(f, 2, 0.02);
            Sub(f, 21, 6); Sub(f, 22, 12); Sub(f, 23, 20);
            var c = Strike(f, null, ",\"ref_uid\":500");
            Run(c, f, 5);
            var lines = AssignLines(f);
            if (lines.Count != 2) return "assign lines " + lines.Count + ": " + string.Join(" | ", lines.ToArray());
            if (!lines[0].Contains("contact=21") || !lines[1].Contains("contact=22")) return "priority order: " + string.Join(" | ", lines.ToArray());
            if (c.RunState(23) != "idle") return "the third target should wait: " + c.RunState(23);
            if (!f.HasEvent("asw_strike_assign")) return "no assign event";
            // a third aircraft: second number of the nearest target
            var f2 = new AswFakeGame();
            f2.ShipsList.Add(new AswShipPos { Uid = 500, Name = "Yorktown", Lat = f2.Tgt.Lat, Lon = f2.Tgt.Lon });
            Helo(f2, 1, 0); Helo(f2, 2, 0.02); Helo(f2, 3, 0.04);
            Sub(f2, 21, 6);
            var c2 = Strike(f2, null, ",\"ref_uid\":500");
            Run(c2, f2, 5);
            var root = (Dictionary<string, object>)Json.Parse(c2.StateJson());
            var grp = (Dictionary<string, object>)((List<object>)root["groups"])[0];
            var st = (List<object>)((Dictionary<string, object>)grp["strike"])["targets_list"];
            var t0 = st == null || st.Count == 0 ? null : st[0] as Dictionary<string, object>;
            if (t0 == null || (double)t0["shooter"] == 0 || (double)t0["second"] == 0) return "no shooter + second: " + c2.StateJson();
            if (Math.Abs((double)t0["shooter"] - (double)t0["second"]) < 0.5) return "the same aircraft twice";
            // a shooter in the water is not moved when a better one appears
            return null;
        }

        // two strike groups, one target: one group has it
        private static string TwoGroups()
        {
            var f = new AswFakeGame();
            Helo(f, 1, 0); Helo(f, 2, 0.02);
            Sub(f, 21, 6);
            var c = AswT173AttackTests.Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"A\",\"uids\":[1]}"));
            c.Group(P("{\"op\":\"create\",\"group\":\"B\",\"uids\":[2]}"));
            c.Order(P("{\"group\":\"A\",\"order\":\"strike\"}"));
            c.Order(P("{\"group\":\"B\",\"order\":\"strike\"}"));
            Run(c, f, 10);
            int a = f.Logs.FindAll(l => l.StartsWith("asw strike A assign ", StringComparison.Ordinal)).Count, b = f.Logs.FindAll(l => l.StartsWith("asw strike B assign ", StringComparison.Ordinal)).Count;
            if (a + b != 1) return "assigned by both groups: " + a + "/" + b;
            return null;
        }

        // "do not attack": out of the assignment; "attack": back
        private static string Skip()
        {
            var f = new AswFakeGame();
            Helo(f, 1, 0);
            Sub(f, 21, 6);
            var c = Strike(f);
            Run(c, f, 5);
            if (c.RunState(21) == null) return "no run";
            if (AswController.IsErr(c.StrikeSkip(P("{\"contact\":21}"), true))) return "skip";
            if (c.RunDone(21) != "skipped") return "not skipped: " + c.RunState(21);
            Run(c, f, 20);
            int n = AssignLines(f).Count;
            Run(c, f, 20);
            if (AssignLines(f).Count != n || c.RunDone(21) != "skipped") return "assigned while skipped";
            if (AswController.IsErr(c.StrikeSkip(P("{\"contact\":21}"), false))) return "resume";
            Run(c, f, 10);
            if (c.RunState(21) == null || c.RunState(21) == "done") return "not back in the assignment: " + c.RunState(21);
            if (c.StrikeSkip(P("{}"), true) != "!bad_args") return "bad args";
            return null;
        }

        // the shooter has no torpedo left: the second number takes the target; nobody: the contact stays, nobody attacks
        private static string Switch()
        {
            var f = new AswFakeGame();
            Helo(f, 1, 0); Helo(f, 2, 0.02);
            Sub(f, 21, 6);
            var c = Strike(f);
            Run(c, f, 5);
            int first = 0;
            foreach (var l in AssignLines(f)) { int i = l.IndexOf(" uid="); first = int.Parse(l.Substring(i + 5, 1)); }
            if (first == 0) return "no shooter";
            f.Mem.Find(m => m.Uid == first).Torpedoes = 0;
            Run(c, f, 5);
            var second = f.Mem.Find(m => m.Uid != first);
            string st = c.StateJson();
            if (!st.Contains("\"shooter\":" + second.Uid)) return "the second number did not take over: " + st;
            second.Torpedoes = 0;
            Run(c, f, 10);
            if (c.RunState(21) != "idle") return "state without shooters " + c.RunState(21);
            if (!f.Logs.Exists(l => l.Contains("no armed") || l.Contains("asw warn no_armed"))) { if (c.View().SubAlerts.Count == 0) return "no armed line"; }
            return null;
        }

        // the player takes the shooter: out of the group, the target goes to the other one
        private static string Player()
        {
            var f = new AswFakeGame();
            Helo(f, 1, 0); Helo(f, 2, 0.02);
            Sub(f, 21, 6);
            var c = Strike(f);
            Run(c, f, 5);
            var shooterLine = AssignLines(f).Last();
            int su = int.Parse(shooterLine.Substring(shooterLine.IndexOf(" uid=") + 5, 1));
            f.Mem.Find(m => m.Uid == su).Where = "player";
            Run(c, f, 6);
            if (c.View().Groups[0].Members.Contains(su)) return "the player's aircraft stays in the group";
            int other = su == 1 ? 2 : 1;
            if (!c.StateJson().Contains("\"shooter\":" + other)) return "the target was not given to the other aircraft: " + c.StateJson();
            return null;
        }

        // no air shooter, an armed machine on the deck: the broker is asked to launch it; the launch is held under stage 2
        private static string Deck()
        {
            var f = new AswFakeGame();
            var d = AswFakeGame.Helo(-11, "deck_ready"); d.DeckKey = "task:g1"; d.DeckUid = 77; d.Torpedoes = 2;
            f.Mem.Add(d);
            f.Decks[77] = new AswTarget { Uid = 77, Name = "Yorktown", Lat = f.Tgt.Lat, Lon = f.Tgt.Lon };
            Sub(f, 21, 6);
            var c = Strike(f);
            Run(c, f, 4);
            if (!Log(f, "deck uid=-11 ship=77 reason=no_air_shooter")) return "no deck line: " + string.Join(" | ", f.Logs.FindAll(l => l.Contains("strike")).ToArray());
            if (f.Allows.Count != 1 || f.Allows[0] != "77:task:g1") return "no launch request: " + string.Join(";", f.Allows.ToArray());
            // stage 2 of the air threat: no launch
            var f2 = new AswFakeGame();
            var d2 = AswFakeGame.Helo(-11, "deck_ready"); d2.DeckKey = "task:g1"; d2.DeckUid = 77; d2.Torpedoes = 2;
            f2.Mem.Add(d2);
            Sub(f2, 21, 6);
            var c2 = Strike(f2);
            c2.AirGuard = new AirGuardStub();
            Run(c2, f2, 5);
            if (f2.Allows.Count != 0) return "launched under stage 2";
            return null;
        }

        private sealed class AirGuardStub : IAirThreats
        {
            private readonly AirGuardParams _p = new AirGuardParams();
            public AirGuardParams P { get { return _p; } }
            public int Revision { get { return 0; } }
            public IList<AirTrack> Tracks() { return new List<AirTrack>(); }
            public AirUnitThreat ForUnit(int uid) { return null; }
            public AirUnitThreat AtPoint(GeoPt p, bool helo) { return null; }
            public AirStage GroupStage(string window, string group) { return AirStage.Retreat; }
            public AirCover CoverFor(int uid) { return null; }
            public bool Ignored(string window, string group) { return false; }
            public void Ignore(string window, string group, double now) { }
        }

        // the card: once per track; a new serial on hold-by-own-sub, killed; the line says what happens
        private static string Alerts()
        {
            var f = new AswFakeGame();
            f.ShipsList.Add(new AswShipPos { Uid = 500, Name = "Yorktown", Lat = f.Tgt.Lat, Lon = f.Tgt.Lon });
            var m = AswT173AttackTests.Shooter(f, 1, 1.0, 3, Sub(f, 21, 6));
            var sub = f.Subs[0];
            var c = Strike(f, null, ",\"ref_uid\":500");
            f.OwnSubsList.Add(new AswOwnSub { Uid = 900, Name = "Own", Lat = sub.Lat + 0.03, Lon = sub.Lon });
            Run(c, f, 25);
            if (f.Events.FindAll(e => e.StartsWith("asw_sub_detected ", StringComparison.Ordinal)).Count != 1) return "detected events";
            if (!Log(f, "asw sub detected contact=21")) return "no detected line";
            var a = c.View().SubAlerts.Find(x => x.ContactId == 21);
            if (a == null) return "no alert";
            if (a.Line != "hold_ownsub" || a.Episode != "hold_own_sub" || a.Serial != 2) return "hold alert " + a.Line + " " + a.Episode + " " + a.Serial;
            if (a.RefName != "Yorktown" || a.DistNm < 5) return "reference ship " + a.RefName + " " + a.DistNm;
            f.OwnSubsList.Clear();
            Run(c, f, 60);
            a = c.View().SubAlerts.Find(x => x.ContactId == 21);
            if (a.Line != "strike") return "line after the hold " + a.Line;
            f.Subs[0].Killed = true;
            Run(c, f, 3);
            a = c.View().SubAlerts.Find(x => x.ContactId == 21);
            if (a == null || a.Episode != "killed" || a.Serial < 3) return "killed alert " + (a == null ? "-" : a.Episode + " " + a.Serial);
            // without a strike group: the line says so
            var f2 = new AswFakeGame();
            Sub(f2, 21, 6);
            var c2 = AswT173AttackTests.Make(f2);
            Run(c2, f2, 10);
            var a2 = c2.View().SubAlerts.Find(x => x.ContactId == 21);
            if (a2 == null || a2.Line != "none") return "no group line " + (a2 == null ? "-" : a2.Line);
            return null;
        }

        // without a target: Airborne = 0 -> the aircraft lands after IdleS
        private static string IdleLand()
        {
            var f = new AswFakeGame();
            Helo(f, 1, 0);
            var c = Strike(f, cf => cf.Strike.IdleS = 30);
            Run(c, f, 20);
            if (f.Calls.Contains("rtb:1")) return "landed too early";
            Run(c, f, 30);
            if (!f.Calls.Contains("rtb:1")) return "idle aircraft did not land";
            return null;
        }

        /// <summary>A land-based P-3C of a mission without an air base (live 2026-10-22: "idle uid=159 lands after 600 s -> no_home_base").</summary>
        private static AswMember P3c(AswFakeGame f, int uid)
        {
            var m = Helo(f, uid, 0, 8);
            m.Name = "P-3C VP-5 'Mad Foxes'"; m.Type = "P-3C"; m.Base = null; m.ShipUid = 0; m.DeckUid = 0; m.Helicopter = false;
            return m;
        }

        // 2026-10-22: an idle machine without a home base is never sent to land: it orbits the reference (or its standby point); one log line, no repeat
        private static string IdleNoHome()
        {
            foreach (bool withRef in new[] { false, true })
            {
                var f = new AswFakeGame();
                P3c(f, 159);
                var c = Strike(f, cf => cf.Strike.IdleS = 30, withRef ? ",\"ref_uid\":" + f.Tgt.Uid : "");
                Run(c, f, 20);
                Run(c, f, 30);
                if (f.Calls.Contains("rtb:159")) return "sent to land without a home base (ref " + withRef + ")";
                int n = f.Logs.Count(l => l.Contains("idle uid=159") && l.Contains("no landing (no_home_base)"));
                if (n != 1) return "no-landing line " + n + " (ref " + withRef + ")";
                if (!f.Calls.Contains("goto:159")) return "no orbit order (ref " + withRef + ")";
                if (!Log(f, withRef ? "standby uid=159 over ref" : "standby uid=159 over the standby point")) return "orbit line (ref " + withRef + ")";
                Run(c, f, 900);
                n = f.Logs.Count(l => l.Contains("idle uid=159") && l.Contains("no_home_base"));
                if (n != 1 || f.Calls.Contains("rtb:159")) return "repeated: " + n;
            }
            // a machine with a home (ship) still lands after IdleS
            var g = new AswFakeGame();
            Helo(g, 2, 0);
            var c2 = Strike(g, cf => cf.Strike.IdleS = 30);
            Run(c2, g, 50);
            if (!g.Calls.Contains("rtb:2") || !Log(g, "idle uid=2 lands after")) return "a helicopter with a ship must land";
            // the game refuses the landing anyway: orbit, one line
            var h = new AswFakeGame();
            Helo(h, 3, 0);
            var c3 = Strike(h, cf => cf.Strike.IdleS = 30);
            h.RtbError = "no_home_base";
            Run(c3, h, 50);
            Run(c3, h, 600);
            if (h.Calls.Count(x => x == "rtb:3") != 1 || h.Logs.Count(l => l.Contains("idle uid=3") && l.Contains("no landing (no_home_base)")) != 1) return "refused landing: " + h.Calls.Count(x => x == "rtb:3");
            return null;
        }

        // 2026-10-22 (customer: "P-3C is not in the ASW list"): a strike-group member is listed as "in group STRIKE-n" in the ASW, SAR and strike lists
        private static string MembersListed()
        {
            var f = new AswFakeGame();
            P3c(f, 159);
            var c = Strike(f);
            var h2 = AswFakeGame.Helo(2, "air"); f.Mem.Add(h2);
            Run(c, f, 2);
            var v = c.View();
            string grp = v.Groups[0].Name;
            if (v.Groups[0].Order != "strike" || !v.Groups[0].MemberRows.Exists(r => r.Uid == 159)) return "the strike group's members table has no P-3C";
            string why = "in_group:" + grp;
            if (!v.Excluded.Exists(x => x.Row.Uid == 159 && x.Reason == why)) return "asw excluded: " + string.Join(",", v.Excluded.Select(x => x.Row.Uid + "=" + x.Reason).ToArray());
            if (!v.ExcludedSar.Exists(x => x.Row.Uid == 159 && x.Reason == why)) return "sar excluded";
            if (!v.ExcludedStrike.Exists(x => x.Row.Uid == 159 && x.Reason == why)) return "strike excluded";
            if (v.Available.Exists(r => r.Uid == 159) || v.AvailableStrike.Exists(r => r.Uid == 159)) return "a member is available";
            if (!v.Available.Exists(r => r.Uid == 2)) return "the free helicopter is not available";
            if (AswWindowModel.ExcludedSeenBy(v.Excluded, grp).Exists(x => x.Row.Uid == 159)) return "its own group must not list it as excluded";
            if (!AswWindowModel.ExcludedSeenBy(v.Excluded, "ASW-9").Exists(x => x.Row.Uid == 159)) return "another group must see it";
            var sj = c.StateJson();
            if (!sj.Contains("\"uid\":159") || !sj.Contains("\"reason\":\"" + why + "\"")) return "state json";
            return null;
        }

        private static string SaveLoad()
        {
            string dir = Path.Combine(Path.GetTempPath(), "aswtest-" + Guid.NewGuid().ToString("N"));
            try
            {
                var f = new AswFakeGame { SaveDir = dir, LastSavePath = "/s/a.sav" };
                Helo(f, 1, 0);
                Sub(f, 21, 6);
                var c = Strike(f, null, ",\"radius_nm\":0,\"targets\":\"hostile\",\"airborne\":1,\"ref_uid\":500");
                Run(c, f, 5);
                c.StrikeSkip(P("{\"contact\":21}"), true);
                f.LastSavePath = "/s/B.sav";
                Run(c, f, 1);
                var g = new AswFakeGame { SaveDir = dir, Now = f.Now };
                var x = AswFakeGame.Helo(11, "air"); x.Name = "Helo 1"; x.Torpedoes = 2; g.Mem.Add(x);
                Sub(g, 21, 6);
                var c2 = AswT173AttackTests.Make(g, cf => cf.RestoreOnLoad = "auto");
                c2.OnMissionChanged("/s/b.sav", f.Now + 2);
                var v = c2.View();
                if (v.Groups.Count != 1 || v.Groups[0].Order != "strike") return "not restored as a strike group";
                if (v.Groups[0].RefUid != 500 || v.Groups[0].Targets != "hostile" || v.Groups[0].Airborne != 1) return "order fields lost";
                Run(c2, g, 5);
                if (c2.RunState(21) != null && c2.RunState(21) != "done") return "the skip list was lost";
                // one summary card for the live tracks, no card of its own for the track
                var al = c2.View().SubAlerts;
                if (al.Count != 1 || al[0].Episode != "summary" || al[0].LineArgs[0] != "1") return "summary card " + al.Count;
                return null;
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
