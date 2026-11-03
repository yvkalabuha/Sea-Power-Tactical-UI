using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoiceBotBridge
{
    // T-342 phase D stand sections "t342 ewf-*": the jam of hostile fighters by the jammer aircraft (the controller over the fake game). STRIKE_TRACE=1 prints the journal.
    internal static class StrikeT342EwfTests
    {
        private static Dictionary<string, object> C(params object[] kv) { return StrikeT341CtlTests.C(kv); }
        private static Dictionary<string, object> Obj(string j) { return StrikeT341CtlTests.Obj(j); }
        private static List<object> Arr(object o) { return StrikeT341CtlTests.Arr(o); }
        private static readonly bool Trace = Environment.GetEnvironmentVariable("STRIKE_TRACE") != null;

        public static readonly string[] Names = { "ewf-threat-ctl", "ewf-on", "ewf-prio", "ewf-standoff", "ewf-reserve", "ewf-view", "ewf-save", "ewf-budget" };

        public static string Dispatch(string n)
        {
            string saved = StrikeController.DefaultGoal;
            StrikeController.DefaultGoal = "breach";
            try
            {
                switch (n)
                {
                    case "ewf-threat-ctl": return ThreatCtl();
                    case "ewf-on": return On();
                    case "ewf-prio": return Prio();
                    case "ewf-standoff": return Standoff();
                    case "ewf-reserve": return Reserve();
                    case "ewf-view": return View();
                    case "ewf-save": return Save();
                    case "ewf-budget": return Budget();
                }
            }
            finally { StrikeController.DefaultGoal = saved; }
            return "unknown section " + n;
        }

        private sealed class W
        {
            public StrikeFakeGame Game; public StrikeController Ctl; public List<string> Log = new List<string>();
            public Func<int, string, bool> Retreat = (u, g) => false;
            public int Steers;
            public List<StrikeFighter> Movers = new List<StrikeFighter>();
            public StrikeGroup Grp { get { return Ctl.Groups[0]; } }
            public StrikeEwState Ew(int uid = 5) { return Grp.EwExec.FirstOrDefault(x => x.Uid == uid); }
            public string St(int uid = 5) { var s = Ew(uid); return s == null ? "none" : s.State; }
            public GeoPt Center()
            {
                var st = Game.Air.Where(a => a.Ew == null && a.Airborne).ToList();
                return new GeoPt(st.Average(a => a.Lat), st.Average(a => a.Lon));
            }
            public GeoPt PosOf(int uid) { var a = Game.Air.First(x => x.Uid == uid); return new GeoPt(a.Lat, a.Lon); }
            public void Step(int secs = 1, Func<bool> until = null, Action each = null)
            {
                for (int s = 0; s < secs; s++)
                {
                    Game.Advance(1);
                    foreach (var f in Movers) { var p = AswMath.Offset(new GeoPt(f.Lat, f.Lon), f.CourseDeg, f.SpeedKn / 3600.0); f.Lat = p.Lat; f.Lon = p.Lon; }
                    Ctl.Tick(Game.Time);
                    if (each != null) each();
                    if (until != null && until()) return;
                }
            }
            public StrikeFighter Add(int id, GeoPt at, double course, double kn = 300, double r = 38, string cls = "F-14A", bool move = true)
            {
                var f = new StrikeFighter { TrackId = id, ClassIni = "usn_f-14a", ClassName = cls, Lat = at.Lat, Lon = at.Lon, CourseDeg = course, SpeedKn = kn, RNm = r, RSrc = "class", Identified = true, Hostile = true, IsFighter = true, FighterSrc = "role" };
                Game.FighterList.Add(f); if (move) Movers.Add(f);
                return f;
            }
            public void Remove(StrikeFighter f) { Game.FighterList.Remove(f); Movers.Remove(f); }
            public int Calls(string what) { return Game.JamObjCalls.Count(x => x.Contains(what)); }
        }

        private static W Start(int ews = 1, StrikeParams p = null, int planes = 2)
        {
            var w = new W { Game = new StrikeFakeGame() };
            var g = w.Game;
            for (int i = 1; i <= planes; i++) g.AddPlane(i, "usn_a-6e", "usn_agm-84c", 4, 39.5 - 0.02 * i, 10 + 0.02 * i);
            g.AddKidd(100, 41.0, 10.0);
            for (int e = 0; e < ews; e++) g.AddEw(5 + e, 38.9, 10.2 + 0.1 * e);
            w.Ctl = new StrikeController(g, p ?? new StrikeParams(), l => { w.Log.Add(l); if (Trace) Console.WriteLine("  t=" + g.Time.ToString("0", CultureInfo.InvariantCulture) + " " + l); });
            var c = w.Ctl;
            c.Group(C("op", "create", "group", "A", "uids", g.Air.Select(a => a.Uid).ToArray()));
            c.Target(C("group", "A", "target_id", 100));
            c.SetGoal(C("group", "A", "goal", "breach"));
            c.AirRetreatNow = (u, gn) => w.Retreat(u, gn);
            c.AirThreatNow = (u, gn) => w.Retreat(u, gn);
            c.AirSteerEw = (u, r, m) => { w.Steers++; return null; };
            c.AirReleaseEw = u => { };
            return w;
        }

        private static string Launch(W w)
        {
            if (StrikeController.IsErr(w.Ctl.Plan(C("group", "A")))) return "plan";
            if (StrikeController.IsErr(w.Ctl.Launch(C("group", "A")))) return "launch";
            return null;
        }

        /// <summary>A fighter that flies at the group centre from 'nm' away on the side opposite to the jammer.</summary>
        private static StrikeFighter Inbound(W w, int id, double nm, double kn = 300, double sideDeg = 0)
        {
            var c = w.Center();
            double brg = AswMath.BearingDeg(w.PosOf(5), c) + sideDeg;
            var at = AswMath.Offset(c, brg, nm);
            return w.Add(id, at, AswMath.BearingDeg(at, c), kn);
        }

        private static string ThreatCtl()
        {
            var w = Start();
            var f = Inbound(w, 4021, 85);
            w.Step(5);
            if (w.Grp.FighterThreats.Count != 0 || w.Log.Any(l => l.Contains("fighter threat"))) return "a threat before the launch";
            string e = Launch(w); if (e != null) return e;
            w.Game.TofCalls = 0;
            w.Step(3);
            if (!w.Log.Any(l => l.Contains("strike ew fighter threat grp=A tgt=4021") && l.Contains("eta_s="))) return "threat log " + string.Join("|", w.Log.Where(l => l.Contains("fighter")));
            if (w.Grp.FighterThreats.Count != 1) return "view threats " + w.Grp.FighterThreats.Count;
            // two groups: the table is read at most once per second of the game
            w.Game.FightersCalls = 0;
            w.Step(20);
            if (w.Game.FightersCalls > 21) return "Fighters() " + w.Game.FightersCalls + " calls in 20 s";
            w.Remove(f); w.Step(2);
            if (!w.Log.Any(l => l.Contains("strike ew fighter gone grp=A tgt=4021"))) return "gone log";
            // identification is required: an unidentified track is never a threat
            var u = Inbound(w, 4022, 60); u.Identified = false; u.ClassName = null;
            w.Step(3);
            if (w.Grp.FighterThreats.Count != 0 || w.Calls(" on 4022") != 0) return "an unidentified track";
            if (w.Game.TofCalls != 0) return "no EstimateShot in the tick";
            return null;
        }

        private static string OnCase(StrikeParams p, bool strict)
        {
            var w = Start(1, p);
            string e = Launch(w); if (e != null) return e;
            w.Step(400, () => w.St() == "to_station" || w.St() == "on_station");
            if (w.St() != "to_station" && w.St() != "on_station") return "state " + w.St();
            var f = Inbound(w, 4021, 70, 300);
            w.Step(5, () => w.Calls(" on 4021") > 0);
            if (w.Calls(" on 4021") != 1 || w.Ew().JamKind != "object" || !w.Ew().On) return "object jam on " + string.Join("|", w.Game.JamObjCalls) + " " + w.Ew().JamKind;
            if (!w.Log.Any(l => l.Contains("strike ew fighter on uid=5 tgt=4021") && l.EndsWith("-> ok"))) return "on log";
            // T-346 D3: the fighter turns away; the threat is held (EwFighterGoneS) and the jam stays EwFighterClearS after it is gone
            f.CourseDeg = (f.CourseDeg + 180) % 360;
            double turn = w.Game.Time; double offAt = -1;
            w.Step(150, () => { if (w.Calls(" off 4021") > 0) { offAt = w.Game.Time; return true; } return false; });
            if (offAt < 0) return "never switched off";
            if (strict && (offAt - turn < StrikeCalib.EwFighterGoneS + StrikeCalib.EwFighterClearS - 2 || offAt - turn > StrikeCalib.EwFighterGoneS + StrikeCalib.EwFighterClearS + 5)) return "off after " + (offAt - turn) + " s (gone + clear expected)";
            if (!strict && offAt - turn < 29) return "off after " + (offAt - turn) + " s (not less than 30 s)";
            if (strict)
            {
                if (!w.Log.Any(l => l.Contains("strike ew fighter off uid=5") && l.Contains("why=clear"))) return "off clear log " + string.Join("|", w.Log.Where(l => l.Contains("fighter off") || l.Contains("fighter gone")));
                if (w.Log.Any(l => l.Contains("why=attack_ships"))) return "attack_ships in the strict case";
                if (w.Ew().JamKind == "object") return "state after off";
            }
            else
            {
                if (!w.Log.Any(l => l.Contains("strike ew fighter off uid=5") && (l.Contains("why=clear") || l.Contains("why=attack_ships")))) return "off log " + string.Join("|", w.Log.Where(l => l.Contains("fighter off") || l.Contains("fighter gone")));
                if (w.Ew().JamKind == "object") return "state after off";
            }
            return null;
        }

        private static string On()
        {
            // strict case: a long settle time keeps the group out of the attack for minutes, so the only way off is why=clear
            string e = OnCase(new StrikeParams { EwSettleS = 150 }, true);
            if (e != null) return "strict: " + e;
            // default timing: the group may start its attack meanwhile (why=attack_ships is the same cease)
            e = OnCase(null, false);
            if (e != null) return "default: " + e;
            // a refused order: three tries (EwRetryS apart), then jam_refused and no more
            var w2 = Start();
            if ((e = Launch(w2)) != null) return e;
            w2.Step(400, () => w2.St() == "to_station" || w2.St() == "on_station");
            w2.Game.JamObjErr = "failed:x";
            Inbound(w2, 4021, 70, 300);
            w2.Step(60);
            int ons = w2.Game.JamObjCalls.Count(x => x.Contains(" on 4021"));
            if (ons != 3 || w2.Ew().FighterWhy != "jam_refused") return "refused: " + ons + " tries, why " + w2.Ew().FighterWhy;
            w2.Step(60);
            if (w2.Game.JamObjCalls.Count(x => x.Contains(" on 4021")) != 3) return "more tries after jam_refused";
            if (w2.Ew().JamKind == "object") return "controller thinks the object jam is on after the refusal";
            if (!w2.Log.Any(l => l.Contains("fighter refused uid=5"))) return "refused log";
            // EwFighterEtaS = 0 switches the function off
            var w3 = Start(1, new StrikeParams { EwFighterEtaS = 0 });
            if ((e = Launch(w3)) != null) return e;
            Inbound(w3, 4021, 70, 300); w3.Step(300);
            if (w3.Game.JamObjCalls.Count != 0 || w3.Grp.FighterThreats.Count != 0) return "EwFighterEtaS 0";
            return null;
        }

        private static string Prio()
        {
            var w = Start();
            string e = Launch(w); if (e != null) return e;
            w.Step(400, () => w.St() == "to_station" || w.St() == "on_station");
            // a slow fighter far from the jammer: it stays a threat (inside R_t of the strikers soon) for the whole approach to the ships
            var f = Inbound(w, 4021, 40, 20);
            w.Step(5, () => w.Calls(" on 4021") > 0);
            if (w.Calls(" on 4021") != 1) return "no object jam on the way " + string.Join("|", w.Log.Where(l => l.Contains("fighter") || l.Contains("standoff")));
            int steps = 0;
            w.Step(3000, () => w.St() == "jamming" || w.St() == "back", () => steps++);
            if (w.St() != "jamming") return "never in jamming " + w.St();
            if (w.Ew().JamKind != "point") return "jamming must be the point jam of the ships: " + w.Ew().JamKind;
            if (!w.Log.Any(l => l.Contains("strike ew fighter off uid=5") && l.Contains("why=attack_ships"))) return "off attack_ships log";
            // order of the calls: object off, then the point on
            int iOff = w.Log.FindIndex(l => l.Contains("fighter off uid=5") && l.Contains("attack_ships"));
            int iOn = w.Log.FindIndex(l => l.Contains("strike ew on uid=5"));
            if (iOff < 0 || iOn < iOff) return "object off before point on " + iOff + " " + iOn;
            w.Step(60);
            if (w.Calls(" on 4021") != 1) return "a new object jam during the attack: " + w.Calls(" on 4021");
            if (w.Ew().Mode != "attack") return "mode " + w.Ew().Mode;
            // the end of the strike: everything is off
            w.Step(1500, () => w.St() == "back" || w.St() == "none");
            if (w.Game.JamOn.ContainsKey(5) && w.Game.JamOn[5]) return "jam left on after the strike";
            // cancel while jamming a fighter
            var w2 = Start();
            if ((e = Launch(w2)) != null) return e;
            w2.Step(400, () => w2.St() == "to_station" || w2.St() == "on_station");
            Inbound(w2, 4021, 40, 20);
            w2.Step(5, () => w2.Calls(" on 4021") > 0);
            w2.Ctl.Cancel(C("group", "A"));
            w2.Step(2);
            if (w2.Game.JamObjOn.ContainsKey(5) && w2.Game.JamObjOn[5] != 0) return "cancel left the object jam on";
            if (w2.Ew() != null && w2.Ew().JamKind != null) return "kind after cancel";
            // a retreat (AirGuard) switches the object jam off
            var w3 = Start();
            if ((e = Launch(w3)) != null) return e;
            w3.Step(400, () => w3.St() == "to_station" || w3.St() == "on_station");
            Inbound(w3, 4021, 40, 20);
            w3.Step(5, () => w3.Calls(" on 4021") > 0);
            w3.Retreat = (u, g) => u == 5;
            w3.Step(3);
            if (w3.St() != "retreat" || w3.Calls(" off 4021") == 0 || !w3.Log.Any(l => l.Contains("fighter off uid=5") && l.Contains("why=air_threat"))) return "retreat switch off " + w3.St();
            return null;
        }

        private static string Standoff()
        {
            // 1. the jammer is nearer than R_t + 10 to a fighter, a point behind the group exists: it goes there and does not switch on before it is safe
            var w = Start();
            string e = Launch(w); if (e != null) return e;
            w.Step(1500, () => w.St() == "on_station");
            if (w.St() != "on_station") return "on_station expected, got " + w.St();
            w.Game.Fleet[0].SamMaxNm = 20;   // a small zone: the point behind the group exists (the real Kidd zone covers the whole axis)
            var jam = w.PosOf(5); var c = w.Center();
            var at = AswMath.Offset(jam, 90, 46);   // 46 nm east of the jammer on its station: inside R_t + 10
            var f = w.Add(4021, at, AswMath.BearingDeg(at, c), 200, 38, "F-14A", false);
            w.Step(3);
            if (!w.Log.Any(l => l.Contains("strike ew fighter standoff uid=5 pt="))) return "standoff log " + string.Join("|", w.Log.Where(l => l.Contains("fighter")));
            if (w.Calls(" on 4021") != 0) return "switched on while unsafe";
            if (w.Ew().Sent != "standoff") return "sent " + w.Ew().Sent;
            int gotos = w.Log.Count(l => l.Contains("fighter standoff uid=5"));
            w.Step(8);
            if (w.Log.Count(l => l.Contains("fighter standoff uid=5")) != gotos) return "the point is re-sent too often";
            // the threat is gone: the route to the station is sent again
            w.Remove(f); w.Step(3);
            int routes = w.Log.Count(l => l.Contains("strike ew route uid=5"));
            if (w.Ew().Sent == "standoff") return "standoff not cleared";
            // 2. the group is already inside R_t of the fighter: no point, retreat with the message
            var w2 = Start();
            if ((e = Launch(w2)) != null) return e;
            w2.Step(400, () => w2.St() == "to_station" || w2.St() == "on_station");
            var c2 = w2.Center();
            var at2 = AswMath.Offset(c2, 180, 30);   // between the group and the jammer: the group is inside R_t, the jammer within R_t + 10
            w2.Add(4030, at2, 180, 0, 38, "MiG-25PD", false);
            w2.Step(3);
            if (w2.St() != "retreat" || w2.Ew().FighterWhy == null || !w2.Ew().FighterWhy.StartsWith("no_cover:4030")) return "no_cover retreat " + w2.St() + " " + w2.Ew().FighterWhy;
            if (w2.Steers == 0) return "AirSteerEw was not asked";
            if (!w2.Grp.EwNotes.Any(n => n.StartsWith("ewf_no_cover:5:MiG-25PD"))) return "note " + string.Join(",", w2.Grp.EwNotes);
            // the return waits for EwReturnClearS and for the fighter to be farther than R_t + 10 from the jammer
            double t0 = w2.Game.Time; double back = -1;
            w2.Step(200, () => { if (w2.St() != "retreat") { back = w2.Game.Time; return true; } return false; });
            if (back >= 0 && back - t0 < 60) return "returned after " + (back - t0) + " s";
            return null;
        }

        private static string Reserve()
        {
            var w = Start(2);
            string e = Launch(w); if (e != null) return e;
            var roles = w.Grp.EwExec.Select(s => s.Uid + ":" + s.Role).ToList();
            if (w.Ew(6) == null || w.Ew(6).Role != "fighters" || w.Ew(5).Role != "ships") return "roles " + string.Join(",", roles);
            if (!w.Log.Any(l => l.Contains("strike ew role grp=A uid=6 role=fighters"))) return "role log";
            // the reserve helps with the ships like the other jammer
            w.Step(3000, () => w.Ew(5).State == "jamming" && w.Ew(6).State == "jamming");
            if (w.Ew(6).State != "jamming" || w.Ew(5).State != "jamming") return "states " + w.St(5) + " " + w.St(6);
            if (w.Ew(6).JamKind != "point" || w.Ew(5).JamKind != "point") return "both jam the ships";
            // a fighter: the reserve moves to it, the ships jammer stays on the ships (attack)
            var f = Inbound(w, 4021, 70, 300);
            w.Step(6, () => w.Calls(" on 4021") > 0);
            if (w.Calls(" on 4021") != 1 || !w.Game.JamObjCalls.Any(x => x.EndsWith(" on 4021") && x.Contains(" 6 "))) return "the reserve takes the fighter " + string.Join("|", w.Game.JamObjCalls);
            if (w.Ew(5).JamKind != "point" || w.Ew(5).Mode != "attack") return "the ships jammer must stay " + w.Ew(5).JamKind + " " + w.Ew(5).Mode;
            if (w.Ew(6).Mode != "transit" || w.Ew(6).JamKind != "object") return "reserve mode " + w.Ew(6).Mode;
            int offsAt = w.Log.FindIndex(l => l.Contains("strike ew off uid=6") && l.Contains("why=fighter"));
            int onAt = w.Log.FindIndex(l => l.Contains("strike ew fighter on uid=6"));
            if (offsAt < 0 || onAt < offsAt) return "ships off before the object on";
            // the other jammer retreats: nobody jams the ships any more, the window must say so
            w.Retreat = (u, g) => u == 5;
            w.Step(3);
            if (w.Ew(6).ShipsLost.Count == 0 || !w.Log.Any(l => l.Contains("fighter ships_lost uid=6"))) return "ships_lost " + w.Ew(6).ShipsLost.Count + " " + w.St(5);
            if (!w.Grp.EwNotes.Any(n => n.StartsWith("ewf_ships_lost:6:"))) return "ships_lost note";
            w.Retreat = (u, g) => false;
            // the fighter turns away: after gone + clear the reserve goes back to the ships (T-346 D3); no flapping
            w.Step(3);
            f.CourseDeg = (f.CourseDeg + 180) % 360;
            double turn = w.Game.Time;
            w.Step(120, () => w.Ew(6).JamKind == "point");
            if (w.Ew(6).JamKind != "point") return "the reserve did not return to the ships " + w.Ew(6).JamKind;
            double back = w.Game.Time - turn;
            if (back < 29 || back > StrikeCalib.EwFighterGoneS + StrikeCalib.EwFighterClearS + 5 + 30) return "back after " + back + " s";
            if (w.Calls(" on 4021") != 1 || w.Calls(" off 4021") != 1) return "flapping: " + string.Join("|", w.Game.JamObjCalls);
            if (w.Ew(6).ShipsLost.Count != 0 || !w.Log.Any(l => l.Contains("fighter ships_resume uid=6"))) return "ships back";
            w.Step(40);
            if (w.Calls(" on 4021") != 1 || w.Ew(6).JamKind != "point") return "stays on the ships";
            return null;
        }

        private static string View()
        {
            var w = Start(2);
            string e = Launch(w); if (e != null) return e;
            var j0 = (Dictionary<string, object>)Arr(Obj(w.Ctl.ViewJson(20))["groups"])[0];
            if (((List<object>)j0["fighter_threats"]).Count != 0 || ((List<object>)j0["fighter_uncovered"]).Count != 0) return "empty lists";
            var ew0 = (Dictionary<string, object>)Arr(j0["ew"])[1];
            if (ew0["role"] as string != "fighters" || ew0["mode"] as string != "transit" || ew0["fighter"] != null || ew0["jam_kind"] != null) return "ew fields before a threat";
            w.Step(400, () => w.St(6) == "to_station" || w.St(6) == "on_station");
            Inbound(w, 4021, 70, 300);
            w.Step(6, () => w.Calls(" on 4021") > 0);
            var j = (Dictionary<string, object>)Arr(Obj(w.Ctl.ViewJson(20))["groups"])[0];
            var th = (Dictionary<string, object>)Arr(j["fighter_threats"])[0];
            if ((th["tgt"] as double?) != 4021 || th["cls"] as string != "F-14A" || th["jammed_by"] == null) return "threat json";
            var ew = (Dictionary<string, object>)Arr(j["ew"]).Cast<Dictionary<string, object>>().First(x => (x["uid"] as double?) == 6);
            var fj = ew["fighter"] as Dictionary<string, object>;
            if (fj == null || (fj["tgt"] as double?) != 4021 || ew["jam_kind"] as string != "object") return "fighter json";
            string raw = w.Ctl.ViewJson(20);
            if (raw.Contains("target_jammers")) return "target_jammers is dev only";
            if (!Arr(j["ew_notes"]).Cast<object>().Any(x => ((string)x).StartsWith("ewf_on:6:4021"))) return "ew_notes";
            return null;
        }

        private static string Save()
        {
            var w = Start();
            string e = Launch(w); if (e != null) return e;
            w.Step(400, () => w.St() == "to_station" || w.St() == "on_station");
            Inbound(w, 4021, 70, 300);
            w.Step(6, () => w.Calls(" on 4021") > 0);
            if (!w.Ew().On) return "object jam not on";
            string json = w.Ctl.Export();
            if (json == null || !json.Contains("\"ew_on\":[5]")) return "ew_on has the uid of the object jam: " + json;
            var game2 = new StrikeFakeGame(); game2.JamOn[5] = true;
            var c2 = new StrikeController(game2, new StrikeParams(), l => { });
            string err; c2.Import(json, out err);
            if (err != null) return "import " + err;
            if (game2.JamCalls.Count != 0) return "nothing before the first tick";
            c2.Tick(game2.Time + 1);
            if (game2.JamCalls.Count != 1 || !game2.JamCalls[0].Contains(" 5 off ") || game2.JamOn[5]) return "first tick after the load switches the stations off " + string.Join("|", game2.JamCalls);
            return null;
        }

        private static string Budget()
        {
            var w = Start(2);
            string e = Launch(w); if (e != null) return e;
            w.Step(400, () => w.St() == "to_station" || w.St() == "on_station");
            for (int i = 0; i < 12; i++) Inbound(w, 4100 + i, 70 + 5 * i, 200 + 10 * i, 30 * i);
            w.Game.FightersCalls = 0; w.Game.TofCalls = 0;
            double best = double.MaxValue;
            for (int r = 0; r < 3; r++)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                w.Step(200);
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds / 200);
            }
            Console.WriteLine("strike ewf tick ms=" + best.ToString("0.00", CultureInfo.InvariantCulture));
            if (w.Game.TofCalls != 0) return "TofCalls " + w.Game.TofCalls;
            if (w.Game.FightersCalls > 3 * 200 + 3) return "Fighters() " + w.Game.FightersCalls;
            return best <= 2 ? null : "tick over 2 ms: " + best;
        }
    }
}
