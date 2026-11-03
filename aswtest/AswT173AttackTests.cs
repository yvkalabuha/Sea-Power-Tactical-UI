using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    // T-173 (ADR-0074 / ADR-0072 s.1.2): the attack automaton: approach at the drop range, the drop, the water, the assessment, the repeat, the holds.
    internal static class AswT173AttackTests
    {
        internal static Dictionary<string, object> P(string json) { return (Dictionary<string, object>)Json.Parse(json); }

        internal static AswController Make(AswFakeGame f, Action<AswConfig> cfg = null)
        {
            var c = new AswConfig { RestoreOnLoad = "off" };
            if (cfg != null) cfg(c);
            return new AswController(f, new AswParams(), c);
        }

        internal static void Run(AswController c, AswFakeGame f, int seconds) { for (int i = 0; i < seconds; i++) { f.Advance(1); f.Tick(c); } }

        internal static GeoPt SubPos(AswFakeGame f) { return AswMath.Offset(new GeoPt(f.Tgt.Lat, f.Tgt.Lon), 90, 10); }

        internal static AswContact AddSub(AswFakeGame f, int id = 7, string side = "hostile")
        {
            var p = SubPos(f);
            var s = new AswContact { Id = id, Name = "Sub #" + id, Lat = p.Lat, Lon = p.Lon, Source = "live", Side = side, Live = true, ClassName = "Kilo", Classified = true,
                                     MapPosture = side == "hostile" ? "Enemy" : "Unknown" };
            f.Subs.Add(s);
            return s;
        }

        /// <summary>A helicopter with torpedoes kept still, away from the track by <paramref name="nm"/> to the west.</summary>
        internal static AswMember Shooter(AswFakeGame f, int uid, double nm, int torps = 2, AswContact sub = null)
        {
            var m = AswFakeGame.Helo(uid, "air", dip: false); m.Torpedoes = torps;
            var p = AswMath.Offset(sub == null ? SubPos(f) : new GeoPt(sub.Lat, sub.Lon), 270, nm);
            m.Lat = p.Lat; m.Lon = p.Lon;
            f.Mem.Add(m); f.FixPos.Add(uid);
            return m;
        }

        /// <summary>A controller whose group "S" holds the helicopters; the attack is started by hand (the strike group is tested in t173-strike).</summary>
        internal static AswController Group(AswFakeGame f, Action<AswConfig> cfg = null)
        {
            var c = Make(f, cfg);
            var uids = string.Join(",", f.Mem.Select(m => m.Uid.ToString()).ToArray());
            c.Group(P("{\"op\":\"create\",\"group\":\"S\",\"uids\":[" + uids + "]}"));
            return c;
        }

        internal static string Dump(AswFakeGame f) { var l = f.Logs.FindAll(x => x.StartsWith("asw attack") || x.StartsWith("asw strike") || x.StartsWith("asw mad")); return string.Join(" | ", l.Skip(Math.Max(0, l.Count - 8)).ToArray()); }

        internal static bool Log(AswFakeGame f, string needle) { return f.Logs.Exists(l => l.Contains(needle)); }

        public static string Run()
        {
            string e;
            if ((e = Legacy()) != null) return "legacy-attack: " + e;
            if ((e = DropRange()) != null) return "drop-range: " + e;
            if ((e = Kill()) != null) return "strike-kill: " + e;
            if ((e = Ledger()) != null) return "attack-ledger-off: " + e;
            if ((e = MissStreak()) != null) return "strike-miss-streak: " + e;
            if ((e = Limit()) != null) return "strike-limit: " + e;
            if ((e = OwnSub()) != null) return "strike-own-sub: " + e;
            if ((e = Neutral()) != null) return "strike-neutral: " + e;
            if ((e = AirHold()) != null) return "hold-air: " + e;
            if ((e = OneInWater()) != null) return "strike-one-in-water: " + e;
            if ((e = Lost()) != null) return "strike-lost: " + e;
            if ((e = Verify()) != null) return "mad-verify: " + e;
            if ((e = Charge()) != null) return "depth-charge: " + e;
            return null;
        }

        // asw_attack and asw_attack_cancel answer with codes; a search group with an armed helicopter never attacks
        private static string Legacy()
        {
            var f = new AswFakeGame();
            var m = AswFakeGame.Helo(1, "air", dip: true); f.Mem.Add(m);
            var c = Make(f);
            c.SearchCourse(P("{\"tactics\":[\"dip\",\"rows\"]}"));
            Run(c, f, 5);
            AddSub(f);
            Run(c, f, 900);
            if (f.HasEvent("asw_attack_proposed") || f.Attacks.Count != 0) return "a search group attacked or asked";
            if (c.Attack(P("{\"proposal_id\":\"p1\",\"confirm\":true}")) != "!no_confirm_needed") return "asw_attack";
            if (c.AttackCancel(P("{}")) != "!use_asw_strike_skip") return "asw_attack_cancel";
            return null;
        }

        // the aircraft is sent to the point at DropRangeNm from the estimate (not to the target); the attack goes out only within DropNm
        private static string DropRange()
        {
            var f = new AswFakeGame { ReleaseDelayS = 1 };
            var sub = AddSub(f);
            f.DefaultDrop = new AswDropSpec { MinNm = 0.5, MaxNm = 5, MinAltFt = 100, MaxAltFt = 1000, Ammo = "Mk46" };
            Shooter(f, 1, 6, 2, sub);
            var c = Group(f);
            if (!c.StartStrikeRun("S", sub.Id, 1, "shooter")) return "no run";
            f.Calls.Clear(); f.GotoAlts.Clear();
            Run(c, f, 3);
            if (f.AttackTrackCalls.Count != 0) return "attacked from 6 nm: " + string.Join(",", f.AttackTrackCalls.ToArray());
            if (!f.Calls.Contains("goto:1")) return "no approach: " + c.RunState(sub.Id) + " " + Dump(f);
            if (f.GotoAlts.Count == 0 || f.GotoAlts[0] != 150) return "drop altitude " + (f.GotoAlts.Count == 0 ? "-" : f.GotoAlts[0].ToString());
            var pt = f.Route[1][0];
            double d = AswMath.DistNm(pt, new GeoPt(sub.Lat, sub.Lon));
            if (Math.Abs(d - 1.0) > 0.05) return "approach point at " + d + " nm";
            // arrive: now within the drop range
            var near = AswMath.Offset(new GeoPt(sub.Lat, sub.Lon), 270, 1.0);
            f.Mem[0].Lat = near.Lat; f.Mem[0].Lon = near.Lon;
            Run(c, f, 3);
            if (f.AttackTrackCalls.Count != 1) return "attack calls " + f.AttackTrackCalls.Count;
            return null;
        }

        // drop -> torpedo in the water -> assessment -> hit; killed ends it with the counts
        private static string Kill()
        {
            var f = new AswFakeGame { ReleaseDelayS = 1, SimTorps = true, TorpRunS = 40 };
            var sub = AddSub(f);
            Shooter(f, 1, 1.0, 3, sub);
            var c = Group(f);
            c.StartStrikeRun("S", sub.Id, 1, "shooter");
            Run(c, f, 8);
            if (c.RunState(sub.Id) != "in_water") return "not in the water: " + c.RunState(sub.Id);
            if (!f.HasEvent("asw_attack_drop")) return "no drop event";
            if (!Log(f, " drop contact=7 uid=1 n=1 ")) return "no drop line";
            Run(c, f, 50);
            if (!Log(f, "result contact=7 n=1 result=miss")) return "no miss result: " + string.Join(" | ", f.Logs.FindAll(l => l.Contains("asw attack")).ToArray());
            // repeat after the gap, without a question, by the same shooter
            Run(c, f, 25);
            if (f.AttackTrackCalls.Count < 2) return "no repeat: " + f.AttackTrackCalls.Count;
            f.Hits[sub.Id] = 1;
            Run(c, f, 60);
            if (!Log(f, "result contact=7 n=2 result=hit")) return "no hit result";
            sub.Killed = true;
            Run(c, f, 3);
            if (c.RunDone(sub.Id) != "killed") return "not killed: " + c.RunState(sub.Id);
            string ev = f.EventData("asw_contact_killed");
            if (ev == null || !ev.Contains("\"torpedoes\":") || !ev.Contains("\"hits\":1")) return "kill event " + ev;
            if (!Log(f, "done contact=7 reason=killed torpedoes=")) return "no done line";
            return null;
        }

        // HitsOn = -1: the result is "unknown", the repeat goes on by the live track
        private static string Ledger()
        {
            var f = new AswFakeGame { ReleaseDelayS = 1, SimTorps = true, TorpRunS = 20, HitsOff = true };
            var sub = AddSub(f);
            Shooter(f, 1, 1.0, 3, sub);
            var c = Group(f);
            c.StartStrikeRun("S", sub.Id, 1, "shooter");
            Run(c, f, 40);
            if (!Log(f, "result contact=7 n=1 result=unknown")) return "no unknown result";
            Run(c, f, 40);
            if (f.AttackTrackCalls.Count < 2) return "no repeat by the live track";
            return null;
        }

        // two misses: the next drop needs a fresh fix (<= 90 s); the holder is asked, the drop waits
        private static string MissStreak()
        {
            var f = new AswFakeGame { ReleaseDelayS = 1, SimTorps = true, TorpRunS = 10 };
            var sub = AddSub(f); sub.FixS = f.Now;
            Shooter(f, 1, 1.0, 6, sub);
            var c = Group(f);
            c.StartStrikeRun("S", sub.Id, 1, "shooter");
            for (int i = 0; i < 120 && f.AttackTrackCalls.Count < 2; i++) { sub.FixS = f.Now; Run(c, f, 1); }
            if (f.AttackTrackCalls.Count < 2) return "two drops not reached";
            // the fix goes stale (the track stays live, the sensor does not refresh it)
            Run(c, f, 20);
            sub.FixS = f.Now - 300;
            int n = f.AttackTrackCalls.Count;
            Run(c, f, 80);
            if (c.RunHold(sub.Id) != "stale_fix") return "no stale_fix hold: " + c.RunState(sub.Id);
            if (f.AttackTrackCalls.Count != n) return "dropped on a stale fix";
            sub.FixS = f.Now;
            Run(c, f, 12);
            if (f.AttackTrackCalls.Count != n + 1) return "no drop after the fresh fix: " + f.AttackTrackCalls.Count;
            return null;
        }

        // MaxTorpedoes = 2 -> done limit; "attack again" starts over
        private static string Limit()
        {
            var f = new AswFakeGame { ReleaseDelayS = 1, SimTorps = true, TorpRunS = 5 };
            var sub = AddSub(f);
            Shooter(f, 1, 1.0, 6, sub);
            var c = Group(f, cf => cf.Strike.MaxTorpedoes = 2);
            c.StartStrikeRun("S", sub.Id, 1, "shooter");
            for (int i = 0; i < 200 && c.RunDone(sub.Id) == null; i++) Run(c, f, 1);
            if (c.RunDone(sub.Id) != "limit") return "no limit: " + c.RunState(sub.Id);
            if (f.AttackTrackCalls.Count != 2) return "drops " + f.AttackTrackCalls.Count;
            return null;
        }

        // an own submarine 3 nm from the datum: hold, no attack; 4.6 nm for 20 s: go; 4.2 nm: still held
        private static string OwnSub()
        {
            var f = new AswFakeGame { ReleaseDelayS = 1 };
            var sub = AddSub(f);
            Shooter(f, 1, 1.0, 3, sub);
            var own = new AswOwnSub { Uid = 900, Name = "Own", Lat = 0, Lon = 0 };
            var op = AswMath.Offset(new GeoPt(sub.Lat, sub.Lon), 0, 3.0); own.Lat = op.Lat; own.Lon = op.Lon;
            f.OwnSubsList.Add(own);
            var c = Group(f);
            c.StartStrikeRun("S", sub.Id, 1, "shooter");
            Run(c, f, 20);
            if (c.RunHold(sub.Id) != "own_sub") return "no hold: " + c.RunState(sub.Id);
            if (f.AttackTrackCalls.Count != 0) return "attacked with an own sub near";
            if (!f.Events.Exists(e => e.StartsWith("asw_strike_hold", StringComparison.Ordinal))) return "no hold event";
            int holds = f.Events.FindAll(e => e.StartsWith("asw_strike_hold", StringComparison.Ordinal)).Count;
            op = AswMath.Offset(new GeoPt(sub.Lat, sub.Lon), 0, 4.2); own.Lat = op.Lat; own.Lon = op.Lon;
            Run(c, f, 40);
            if (c.RunHold(sub.Id) != "own_sub") return "released at 4.2 nm";
            op = AswMath.Offset(new GeoPt(sub.Lat, sub.Lon), 0, 4.6); own.Lat = op.Lat; own.Lon = op.Lon;
            Run(c, f, 15);
            if (c.RunHold(sub.Id) != "own_sub") return "released before 20 s";
            Run(c, f, 15);
            if (f.AttackTrackCalls.Count != 1) return "no attack after the clearance: " + f.AttackTrackCalls.Count + " " + c.RunState(sub.Id);
            if (f.Events.FindAll(e => e.StartsWith("asw_strike_hold", StringComparison.Ordinal)).Count != holds) return "hold event repeated";
            if (!f.HasEvent("asw_strike_resume")) return "no resume event";
            // the position of own subs is unreadable: held for everybody
            var f2 = new AswFakeGame { OwnSubsUnreadable = true };
            var s2 = AddSub(f2); Shooter(f2, 1, 1.0, 3, s2);
            var c2 = Group(f2);
            c2.StartStrikeRun("S", s2.Id, 1, "shooter");
            Run(c2, f2, 20);
            if (c2.RunHold(s2.Id) != "own_sub" || f2.AttackTrackCalls.Count != 0) return "unreadable own subs did not hold";
            return null;
        }

        // neutral_near: hold without a question, retried every 5 s, goes when clear
        private static string Neutral()
        {
            var f = new AswFakeGame { ReleaseDelayS = 1, AttackTrackError = "neutral_near" };
            var sub = AddSub(f);
            Shooter(f, 1, 1.0, 3, sub);
            var c = Group(f);
            c.StartStrikeRun("S", sub.Id, 1, "shooter");
            Run(c, f, 21);
            if (c.RunHold(sub.Id) != "neutral") return "no neutral hold: " + c.RunState(sub.Id);
            if (f.AttackTrackCalls.Count < 3) return "no retries " + f.AttackTrackCalls.Count;
            f.AttackTrackError = null;
            Run(c, f, 8);
            if (c.RunState(sub.Id) != "engaged" && c.RunState(sub.Id) != "in_water") return "no attack after the corridor cleared: " + c.RunState(sub.Id);
            return null;
        }

        private sealed class FakeAir : IAirThreats
        {
            public AirStage Stage;
            private readonly AirGuardParams _p = new AirGuardParams();
            public AirGuardParams P { get { return _p; } }
            public int Revision { get { return 0; } }
            public IList<AirTrack> Tracks() { return new List<AirTrack>(); }
            public AirUnitThreat ForUnit(int uid) { return Stage == AirStage.None ? null : new AirUnitThreat { Uid = uid, Stage = Stage, Nm = 30 }; }
            public AirUnitThreat AtPoint(GeoPt p, bool helo) { return null; }
            public AirStage GroupStage(string window, string group) { return Stage; }
            public AirCover CoverFor(int uid) { return null; }
            public bool Ignored(string window, string group) { return false; }
            public void Ignore(string window, string group, double now) { }
        }

        // stage 2 of the air threat over the group: fire is held (air_threat); it goes on after the threat
        private static string AirHold()
        {
            var f = new AswFakeGame { ReleaseDelayS = 1 };
            var sub = AddSub(f);
            Shooter(f, 1, 1.0, 3, sub);
            var c = Group(f);
            var air = new FakeAir { Stage = AirStage.Retreat };
            c.AirGuard = air;
            c.StartStrikeRun("S", sub.Id, 1, "shooter");
            Run(c, f, 12);
            if (c.RunHold(sub.Id) != "air_threat") return "no air_threat hold: " + c.RunState(sub.Id);
            if (f.AttackTrackCalls.Count != 0) return "attacked under the threat";
            air.Stage = AirStage.None;
            Run(c, f, 12);
            if (f.AttackTrackCalls.Count != 1) return "no attack after the threat: " + c.RunState(sub.Id);
            return null;
        }

        // a torpedo of the player (an own sub's) already runs at the track: no second one
        private static string OneInWater()
        {
            var f = new AswFakeGame { ReleaseDelayS = 1 };
            var sub = AddSub(f);
            Shooter(f, 1, 1.0, 3, sub);
            f.Torps.Add(new AswTorpObs { Id = 1, LauncherUid = 900, TargetId = sub.Id });
            var c = Group(f);
            c.StartStrikeRun("S", sub.Id, 1, "shooter");
            Run(c, f, 20);
            if (f.AttackTrackCalls.Count != 0) return "second torpedo with one in the water";
            f.Torps.Clear();
            Run(c, f, 10);
            if (f.AttackTrackCalls.Count != 1) return "no attack once the water is clear";
            // unreadable torpedoes: only the group's own count
            var f2 = new AswFakeGame { ReleaseDelayS = 1, TorpsUnreadable = true };
            var s2 = AddSub(f2); Shooter(f2, 1, 1.0, 3, s2);
            var c2 = Group(f2);
            c2.StartStrikeRun("S", s2.Id, 1, "shooter");
            Run(c2, f2, 10);
            if (f2.AttackTrackCalls.Count != 1) return "unreadable torpedoes blocked the attack";
            return null;
        }

        // the track is not live: wait (reacquire); live again -> on; LostHoldS -> done lost; surfaced -> done surfaced
        private static string Lost()
        {
            var f = new AswFakeGame { ReleaseDelayS = 1 };
            var sub = AddSub(f);
            Shooter(f, 1, 6, 3, sub);
            var c = Group(f);
            c.StartStrikeRun("S", sub.Id, 1, "shooter");
            Run(c, f, 5);
            sub.Live = false;
            Run(c, f, 20);
            if (c.RunState(sub.Id) != "reacquire") return "no reacquire: " + c.RunState(sub.Id);
            sub.Live = true;
            Run(c, f, 5);
            if (c.RunState(sub.Id) != "approach") return "not back to the approach: " + c.RunState(sub.Id);
            sub.Live = false;
            Run(c, f, 620);
            if (c.RunDone(sub.Id) != "lost") return "not lost: " + c.RunState(sub.Id);
            var f2 = new AswFakeGame();
            var s2 = AddSub(f2); Shooter(f2, 1, 6, 3, s2);
            var c2 = Group(f2);
            c2.StartStrikeRun("S", s2.Id, 1, "shooter");
            Run(c2, f2, 3);
            s2.Surfaced = true;
            Run(c2, f2, 3);
            if (c2.RunDone(s2.Id) != "surfaced") return "not surfaced";
            return null;
        }

        // a MAD pass before the first drop: legs at the pass altitude; a MAD fix ends it early
        private static string Verify()
        {
            var f = new AswFakeGame { ReleaseDelayS = 1 };
            var sub = AddSub(f); sub.MadFixS = -1;
            var m = Shooter(f, 1, 1.0, 3, sub); m.Mad = true;
            var c = Group(f, cfg => cfg.Loc.MadVerify = true);
            f.GotoAlts.Clear();
            c.StartStrikeRun("S", sub.Id, 1, "shooter");
            Run(c, f, 2);
            if (c.RunState(sub.Id) != "verify") return "no verify: " + c.RunState(sub.Id);
            if (f.GotoAlts.Count == 0 || f.GotoAlts[0] != 100) return "pass altitude";
            if (f.Route[1].Count != 8) return "legs " + f.Route[1].Count;
            sub.MadFixS = f.Now + 1;
            Run(c, f, 4);
            if (!Log(f, "pass result=fix")) return "no fix result";
            if (!Log(f, "elapsed_s=")) return "no elapsed_s";
            if (c.RunState(sub.Id) == "verify") return "still verifying";
            // no mark: 240 s, then the approach with the old point
            var f2 = new AswFakeGame { ReleaseDelayS = 1 };
            var s2 = AddSub(f2); s2.MadFixS = -1;
            var m2 = Shooter(f2, 1, 1.0, 3, s2); m2.Mad = true;
            var c2 = Group(f2, cfg => cfg.Loc.MadVerify = true);
            c2.StartStrikeRun("S", s2.Id, 1, "shooter");
            Run(c2, f2, 245);
            if (!Log(f2, "pass result=none") || !Log(f2, "limit_s=240") || !Log(f2, "elapsed_s=24")) return "no none result with limit_s/elapsed_s";
            if (f2.AttackTrackCalls.Count != 1) return "no attack after the pass";
            // the game does not report MAD detections (-2): the pass is skipped
            var f3 = new AswFakeGame { ReleaseDelayS = 1 };
            var s3 = AddSub(f3); s3.MadFixS = -2;
            var m3 = Shooter(f3, 1, 1.0, 3, s3); m3.Mad = true;
            var c3 = Group(f3, cfg => cfg.Loc.MadVerify = true);
            c3.StartStrikeRun("S", s3.Id, 1, "shooter");
            Run(c3, f3, 4);
            if (!Log(f3, "pass result=skipped") || f3.AttackTrackCalls.Count != 1) return "pass not skipped";
            return null;
        }

        // only a depth charge aboard: no shot without a MAD fix
        private static string Charge()
        {
            var f = new AswFakeGame { ReleaseDelayS = 1 };
            var sub = AddSub(f); sub.DepthFt = 200; sub.MadFixS = -1;
            Shooter(f, 1, 1.0, 3, sub);
            f.DefaultDrop = new AswDropSpec { Charge = true, MaxDepthFt = 600 };
            var c = Group(f);
            c.StartStrikeRun("S", sub.Id, 1, "shooter");
            Run(c, f, 5);
            if (f.AttackTrackCalls.Count != 0) return "a depth charge without a MAD fix";
            if (c.RunDone(sub.Id) != "no_torpedo" || !Log(f, "done contact=7 reason=no_torpedo")) return "no done no_torpedo";
            var f2 = new AswFakeGame { ReleaseDelayS = 1 };
            var s2 = AddSub(f2); s2.DepthFt = 200; s2.MadFixS = f2.Now - 10;
            Shooter(f2, 1, 1.0, 3, s2);
            f2.DefaultDrop = new AswDropSpec { Charge = true, MaxDepthFt = 600 };
            var c2 = Group(f2);
            c2.StartStrikeRun("S", s2.Id, 1, "shooter");
            Run(c2, f2, 5);
            if (f2.AttackTrackCalls.Count != 1) return "no charge on a fresh MAD fix";
            return null;
        }
    }
}
