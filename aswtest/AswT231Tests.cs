using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    // T-231: the strike and the flight deck after the 0.30.11 run: a knocked-out deck takes no orders, the machine that is already attacking is not "no shooter",
    // the hits of the ledger at a kill in the water. `aswtest t231 <name>`.
    internal static class AswT231Tests
    {
        private static readonly string[] Names = { "deck-down-strike", "deck-down-race", "deck-down-shooter", "kept-shooter", "killed-in-water", "killed-no-ledger", "deck-down-relief", "deck-down-text" };
        private const string Sh3 = "usn_sh-3h";

        private static Dictionary<string, object> P(string json) { return AswT173AttackTests.P(json); }
        private static void Run(AswController c, AswFakeGame f, int s) { AswT173AttackTests.Run(c, f, s); }
        private static int Count(AswFakeGame f, string needle) { return f.Logs.Count(l => l.Contains(needle)); }

        public static string Run(string only)
        {
            foreach (string n in Names)
            {
                if (only != null && only != n) continue;
                string e;
                switch (n)
                {
                    case "deck-down-strike": e = DeckDownStrike(); break;
                    case "deck-down-race": e = DeckDownRace(); break;
                    case "deck-down-shooter": e = DeckDownShooter(); break;
                    case "kept-shooter": e = KeptShooter(); break;
                    case "killed-in-water": e = KilledInWater(); break;
                    case "deck-down-relief": e = DeckDownRelief(); break;
                    case "deck-down-text": e = DeckDownText(); break;
                    default: e = KilledNoLedger(); break;
                }
                if (e != null) return n + ": " + e;
            }
            if (only != null && Array.IndexOf(Names, only) < 0) return "unknown case " + only;
            return null;
        }

        private static AswContact Sub(AswFakeGame f, int id, double eastNm)
        {
            var p = AswMath.Offset(new GeoPt(f.Tgt.Lat, f.Tgt.Lon), 90, eastNm);
            var s = new AswContact { Id = id, Name = "Sub #" + id, Lat = p.Lat, Lon = p.Lon, Source = "live", Side = "hostile", Live = true, FixS = f.Now, ClassName = "Kilo", Classified = true, MapPosture = "Enemy" };
            f.Subs.Add(s);
            return s;
        }

        /// <summary>A strike group G of the hangar rows of deck 77 (two SH-3H), a torpedo loadout, a hostile track 12 nm east, airborne = <paramref name="airborne"/>.</summary>
        private static AswController DeckStrike(AswFakeGame f, int airborne)
        {
            var d = new AswFakeDeck { HangarReady = true, HangarPrepS = 0 };
            d.AddHangar(Sh3, "SH-3H", 2);
            f.RealDeck = d;
            f.Decks[77] = new AswTarget { Uid = 77, Name = d.ShipName, Lat = f.Tgt.Lat, Lon = f.Tgt.Lon };
            f.LoadoutList.Add(new LoadoutOption { Name = "ASWHunter", Display = "ASWHunter", Capacity = 2, ReadyUpMin = 0, Available = true, Weapons = new Dictionary<string, int> { { "usn_mk46", 2 } } });
            Sub(f, 7, 12);
            var c = AswT173AttackTests.Make(f);
            c.Tick(f.Now);
            string uids = AswMath.HangarSlotUid(77, Sh3, 1) + "," + AswMath.HangarSlotUid(77, Sh3, 2);
            string r = c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[" + uids + "]}"));
            if (AswController.IsErr(r)) throw new InvalidOperationException(r);
            r = c.Order(P("{\"group\":\"G\",\"order\":\"strike\",\"airborne\":" + airborne + "}"));
            if (AswController.IsErr(r)) throw new InvalidOperationException(r);
            return c;
        }

        private static string DeckDownStrike()
        {
            var f = new AswFakeGame { DeckOut = true };
            var c = DeckStrike(f, 2);
            Run(c, f, 600);
            if (Count(f, "deck_inoperable") != 0) return "deck_inoperable lines: " + Count(f, "deck_inoperable");
            if (Count(f, "launch_failed") != 0) return "launch_failed";
            if (Count(f, "asw deck down G deck=77") != 1) return "deck down lines: " + Count(f, "asw deck down G deck=77") + " " + AswT173AttackTests.Dump(f);
            if (!c.View().Warnings.Exists(w => w.StartsWith("deck_down", StringComparison.Ordinal))) return "no deck_down warning: " + string.Join("|", c.View().Warnings.ToArray());
            if (f.RealDeck.Orders.Count != 0) return "orders on a dead deck: " + f.RealDeck.Orders.Count;
            f.DeckOut = false;
            Run(c, f, 4);
            if (Count(f, "asw deck up G deck=77") != 1) return "no deck up line";
            if (c.View().Warnings.Exists(w => w.StartsWith("deck_down", StringComparison.Ordinal))) return "warning stays";
            if (f.RealDeck.Orders.Count < 1) return "no order after the repair: " + AswT173AttackTests.Dump(f);
            return null;
        }

        private sealed class DownDeck : IAirDeck
        {
            public bool Down;
            public int Reqs;
            public void Declare(string window, AirGroupPrio prio, IEnumerable<int> deckUids) { }
            public int Submit(DeckRequest r, Action<DeckResult> done) { Reqs++; Down = true; if (done != null) done(new DeckResult { Ticket = Reqs, Err = "deck_inoperable" }); return Reqs; }
            public double HeldS(string window, string group) { return -1; }
            public bool DeckDown(int d) { return Down; }
        }

        /// <summary>The deck is knocked out between the check and the order: the broker says deck_inoperable.</summary>
        private static string DeckDownRace()
        {
            var f = new AswFakeGame();
            var c = DeckStrike(f, 2);
            var deck = new DownDeck();
            c.Ownership = new AirOwnership();
            c.Deck = deck;
            for (int i = 0; i < 30; i++) { f.Advance(1); c.Tick(f.Now); }
            if (deck.Reqs != 1) return "requests " + deck.Reqs;
            if (Count(f, "launch_failed") != 0) return "launch_failed";
            if (Count(f, "asw deck down G deck=77") != 1) return "deck down lines: " + Count(f, "asw deck down G deck=77") + " " + AswT173AttackTests.Dump(f);
            return null;
        }

        private static string DeckDownShooter()
        {
            var f = new AswFakeGame { DeckOut = true };
            var c = DeckStrike(f, 0);
            Run(c, f, 60);
            if (Count(f, "no shooter contact=7 why=") != 1 || Count(f, ":deck_down") < 1) return "no shooter lines: " + Count(f, "no shooter") + " " + AswT173AttackTests.Dump(f);
            if (f.RealDeck.Orders.Count != 0) return "orders " + f.RealDeck.Orders.Count;
            return null;
        }

        private static AswMember Helo(AswFakeGame f, int uid, double fuel)
        {
            var m = AswFakeGame.Helo(uid, "air"); m.Torpedoes = 2; m.FuelFrac = fuel; m.Lat = f.Tgt.Lat; m.Lon = f.Tgt.Lon;
            f.Mem.Add(m); f.FixPos.Add(uid);
            return m;
        }

        private static AswController Strike(AswFakeGame f)
        {
            var c = AswT173AttackTests.Make(f);
            string uids = string.Join(",", f.Mem.Select(m => m.Uid.ToString()).ToArray());
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[" + uids + "]}"));
            string r = c.Order(P("{\"group\":\"G\",\"order\":\"strike\"}"));
            if (AswController.IsErr(r)) throw new InvalidOperationException(r);
            return c;
        }

        /// <summary>S9: a shooter that is already on its way fails the fresh fuel check: no "no shooter", one "shooter kept".</summary>
        private static string KeptShooter()
        {
            var f = new AswFakeGame();
            var m = Helo(f, 1, 0.9);
            Sub(f, 7, 12);
            var c = Strike(f);
            Run(c, f, 5);
            if (c.RunShooter(7) != 1) return "not assigned: " + AswT173AttackTests.Dump(f);
            string st = c.RunState(7);
            m.FuelFrac = 0.1;
            Run(c, f, 60);
            if (Count(f, "no shooter") != 0) return "no shooter lines: " + Count(f, "no shooter") + " " + AswT173AttackTests.Dump(f);
            if (Count(f, "shooter kept contact=7 uid=1 why=fuel(") != 1) return "kept lines: " + Count(f, "shooter kept") + " state " + st + " " + AswT173AttackTests.Dump(f);
            var a = c.View().SubAlerts.FirstOrDefault(x => x.ContactId == 7);
            if (a != null && a.Line == "no_shooter") return "card says no_shooter";
            if (c.RunShooter(7) != 1) return "the run was dropped";
            // control: nobody assigned, the old line
            var f2 = new AswFakeGame();
            Helo(f2, 1, 0.1);
            Sub(f2, 7, 23);
            var c2 = Strike(f2);
            Run(c2, f2, 5);
            if (Count(f2, "no shooter contact=7 why=1:fuel(") != 1 || Count(f2, "shooter kept") != 0) return "control: " + Count(f2, "no shooter") + "/" + Count(f2, "shooter kept");
            return null;
        }

        private static AswController KillRun(AswFakeGame f, out AswContact sub)
        {
            sub = AswT173AttackTests.AddSub(f);
            AswT173AttackTests.Shooter(f, 1, 1.0, 3, sub);
            var c = AswT173AttackTests.Group(f);
            c.StartStrikeRun("S", sub.Id, 1, "shooter");
            Run(c, f, 8);
            return c;
        }

        private static string KilledInWater()
        {
            var f = new AswFakeGame { ReleaseDelayS = 1, SimTorps = true, TorpRunS = 40 };
            AswContact sub;
            var c = KillRun(f, out sub);
            if (c.RunState(7) != "in_water") return "not in the water: " + c.RunState(7);
            f.Hits[7] = 1;
            sub.Killed = true;
            Run(c, f, 3);
            if (c.RunDone(7) != "killed") return "not killed: " + c.RunState(7);
            if (!AswT173AttackTests.Log(f, "killed contact=7 track=Sub #7 torpedoes=1 hits=1 ledger=1")) return "killed line: " + string.Join(" | ", f.Logs.Where(l => l.Contains("killed")).ToArray());
            if (!AswT173AttackTests.Log(f, "done contact=7 reason=killed torpedoes=1 hits=1")) return "done line: " + string.Join(" | ", f.Logs.Where(l => l.Contains("done")).ToArray());
            string ev = f.EventData("asw_contact_killed");
            if (ev == null || !ev.Contains("\"hits\":1")) return "event " + ev;
            return null;
        }

        private static string KilledNoLedger()
        {
            var f = new AswFakeGame { ReleaseDelayS = 1, SimTorps = true, TorpRunS = 40, HitsOff = true };
            AswContact sub;
            var c = KillRun(f, out sub);
            sub.Killed = true;
            Run(c, f, 3);
            if (!AswT173AttackTests.Log(f, "killed contact=7 track=Sub #7 torpedoes=1 hits=0 ledger=-1")) return "killed line: " + string.Join(" | ", f.Logs.Where(l => l.Contains("killed")).ToArray());
            return null;
        }

        /// <summary>D1: a search group on a course: the only ready reserve sits on the knocked-out deck 77, so the relief plan must not count it: the gap is
        /// reported (continuity_gap); with the deck repaired the reserve is counted and no gap is reported.</summary>
        private static string DeckDownRelief()
        {
            string e;
            if ((e = ReliefCase(true)) != null) return "dead deck: " + e;
            if ((e = ReliefCase(false)) != null) return "live deck: " + e;
            return null;
        }

        private static string ReliefCase(bool dead)
        {
            var f = new AswFakeGame();
            if (dead) f.DeckOutUids.Add(77);
            var a = AswFakeGame.Helo(1, "air"); a.EnduranceS = 4000; a.FuelFrac = 0.5;
            var b = AswFakeGame.Helo(2, "deck_ready"); b.DeckUid = 77; b.EnduranceS = 4000;
            var d = AswFakeGame.Helo(3, "deck_service"); d.DeckUid = 8; d.ReadyInS = 5000; d.EnduranceS = 4000;
            f.Mem.Add(a); f.Mem.Add(b); f.Mem.Add(d);
            var c = AswT173AttackTests.Make(f);
            if (AswController.IsErr(c.Group(P("{\"op\":\"create\",\"group\":\"S\",\"uids\":[1,2,3]}")))) return "group";
            if (AswController.IsErr(c.SearchCourse(P("{\"tactics\":[\"rows\"]}")))) return "order";
            Run(c, f, 5);
            bool gap = f.HasEvent("asw_warn", "continuity_gap");
            if (dead && !gap) return "the dead deck was counted as a reserve";
            if (!dead && gap) return "the live reserve was not counted";
            if (dead && f.Calls.Contains("launch:2")) return "launched from the dead deck";
            return null;
        }

        /// <summary>D2: the deck_down warning has a text (not the raw code), with the deck kept as the tail.</summary>
        private static string DeckDownText()
        {
            string t = AswWindowModel.WarningText("deck_down deck 77");
            string key = UiText.Get("asw.warn.deck_down");
            if (t.StartsWith("deck_down", StringComparison.Ordinal) || key.StartsWith("asw.warn.", StringComparison.Ordinal)) return "raw: " + t;
            if (!t.StartsWith(key, StringComparison.Ordinal) || !t.EndsWith("deck 77", StringComparison.Ordinal)) return "text: " + t;
            return null;
        }
    }
}
