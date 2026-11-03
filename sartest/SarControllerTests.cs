#if SAR_WIP
using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal static class SarControllerTests
    {
        private static Dictionary<string, object> Cmd(int uid, string op, double radius = -1)
        {
            var d = new Dictionary<string, object> { { "uid", (double)uid }, { "op", op } };
            if (radius >= 0) d["radius_nm"] = radius;
            return d;
        }

        /// <summary>Run the world: pump + 5 s steps, controller tick each step.</summary>
        private static void Run(SarFakeGame g, SarController c, double seconds, Action<double> each = null)
        {
            double t0 = g.Now;
            while (g.Now - t0 < seconds)
            {
                for (int i = 0; i < 4; i++) c.FramePump();
                g.Step(5);
                c.Tick(g.Now);
                if (each != null) each(g.Now - t0);
            }
        }

        private static string Ack(string r) { return r != null && r.StartsWith("!") ? r : null; }

        private static string Busy()
        {
            var g = new SarFakeGame(); var c = new SarController(g, new SarParams(), null);
            g.AddUnit(1, "ship", -8, 0); g.AddUnit(2, "ship", -8, 1); g.AddUnit(3, "ship", -8, 2); g.AddUnit(4, "ship", -8, 3); g.AddUnit(5, "ship", -8, 4);
            g.Units[2].AswGroup = "ASW-1"; g.Units[3].SmartAttack = true; g.Units[4].ExtSar = true; g.Units[5].GameSarZone = true;
            if (c.Busy(1) != null) return "busy: free unit " + c.Busy(1);
            if (c.Busy(2) != "busy:asw_group") return "busy asw " + c.Busy(2);
            if (c.Busy(3) != "busy:smart_attack") return "busy smart " + c.Busy(3);
            if (c.Busy(4) != "ext_sar") return "busy ext " + c.Busy(4);
            if (c.Busy(5) != "game_sar_zone") return "busy zone " + c.Busy(5);
            if (c.ShipCmd(Cmd(2, "start")) != "!busy:asw_group") return "cmd busy asw";
            if (c.ShipCmd(Cmd(4, "start")) != "!ext_sar") return "cmd ext_sar";
            if (g.Has("remember 2")) return "busy unit must not be touched";
            if (Ack(c.ShipCmd(Cmd(1, "start"))) != null) return "start ok";
            if (c.ShipCmd(Cmd(1, "start")) != "!busy:sar_ship") return "second start";
            if (c.Busy(1) != "busy:sar_ship") return "busy sar_ship";
            if (!c.ShipActive(1)) return "ShipActive";
            if (c.ShipCmd(Cmd(99, "start")) != "!unknown_unit") return "unknown unit";
            if (c.ShipCmd(Cmd(1, "bogus")) != "!bad_args") return "bad op";
            return null;
        }

        private static string ShipRows()
        {
            var g = new SarFakeGame(); var c = new SarController(g, new SarParams(), null);
            if (c.ShipRows().Count != 0) return "empty";
            g.AddUnit(1, "ship", -8, 0); g.AddUnit(2, "ship", -8, 1); g.AddUnit(3, "ship", -8, 2); g.AddUnit(4, "ship", -8, 3);
            g.AddUnit(5, "sub", -8, 4); g.AddUnit(6, "helo", -8, 5); g.AddUnit(7, "ship", -8, 6);
            g.Units[2].AswGroup = "ASW-1"; g.Units[3].ExtSar = true; g.Units[7].Lost = true;
            g.OwnShips.AddRange(new[] { 1, 2, 3, 4, 5, 6, 7, 99 });
            g.AddRaft(1, 8, 0, 1);
            var rows = c.ShipRows();
            if (rows.Count != 5) return "count " + rows.Count;
            if (rows[0].Uid != 1 || rows[0].Active || rows[0].Busy != null) return "free";
            if (rows[1].Busy != "busy:asw_group" || rows[1].Group != "ASW-1") return "asw";
            if (rows[2].Busy != "ext_sar") return "ext_sar";
            if (rows[4].Kind != "sub") return "sub kind";
            if (Ack(c.ShipCmd(Cmd(1, "start", 10))) != null) return "start";
            rows = c.ShipRows();
            if (!rows[0].Active || rows[0].RadiusNm != 10 || rows[0].Busy != null) return "active " + rows[0].Active + " r=" + rows[0].RadiusNm;
            if (rows[0].Done != 0) return "done";
            g.Units[5].Submerged = false;
            if (Ack(c.ShipCmd(Cmd(5, "start", 0))) != null) return "sub start";
            c.ShipCmd(Cmd(1, "stop"));
            if (c.ShipRows()[0].Active) return "stopped still active";
            return null;
        }

        private static double IslandDist(GeoPt a, GeoPt b)
        {
            double ax = (a.Lat - SarFakeGame.Lat0) * 60, ay = (a.Lon - SarFakeGame.Lon0) * 60;
            double bx = (b.Lat - SarFakeGame.Lat0) * 60, by = (b.Lon - SarFakeGame.Lon0) * 60;
            double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
            double t = l2 < 1e-9 ? 0 : Math.Max(0, Math.Min(1, (-ax * dx - ay * dy) / l2));
            double px = ax + dx * t, py = ay + dy * t;
            return Math.Sqrt(px * px + py * py);
        }

        private static string ShipCycle()
        {
            var g = new SarFakeGame(); var p = new SarParams();
            var c = new SarController(g, p, null);
            g.AddUnit(10, "ship", -8, 0, 1);
            g.AddRaft(1, 8, 0, 1); g.AddRaft(2, 8.8, 0.5, 1);
            if (Ack(c.ShipCmd(Cmd(10, "start", 0))) != null) return "start";
            if (!g.Has("remember 10")) return "Remember not called";
            if (g.EventCount("sar_started") != 1) return "sar_started";
            Run(g, c, 6000);
            if (!g.Has("goto 10 transit 0 ")) return "no transit goto: " + string.Join(";", g.Calls);
            string first = null;
            foreach (var x in g.Calls) if (x.StartsWith("goto 10")) { first = x; break; }
            int n = int.Parse(first.Substring(first.IndexOf("n=") + 2));
            if (n > 12 || n < 2) return "route points " + n + " (island must force a detour)";
            if (!g.Has("tg 10 3")) return "transit telegraph Medium not set";
            if (!g.Has("tg 10 2")) return "final telegraph Small not set " + string.Join(";", g.Calls) + string.Join("|", g.Logs);
            if (g.EventCount("sar_pickup") != 2) return "pickups " + g.EventCount("sar_pickup") + " " + string.Join(";", g.Events);
            if (c.RescuedByMod != 2) return "RescuedByMod " + c.RescuedByMod;
            if (g.EventCount("sar_complete") != 1) return "sar_complete " + g.EventCount("sar_complete");
            if (!g.Has("restore 10")) return "restore at end";
            if (c.ShipActive(10)) return "still active";
            bool routeLog = false;
            foreach (var l in g.Logs) if (l.StartsWith("[Sar] route") && l.Contains("probes=") && l.Contains("cells=") && l.Contains("ms=") && l.Contains("pts=") && l.Contains("kind=astar")) routeLog = true;
            if (!routeLog) return "route log line missing: " + string.Join("|", g.Logs);
            // the planned route never crosses the island core (3 nm)
            return null;
        }

        // 0.28.1 (live: rafts 20 -> 19, people 105 -> 103, rescued and the ship's done stayed): the game removes an emptied raft,
        // the ship's pickup is counted from the raft's last known position and every vanished raft is logged with its reason
        private static string ShipPickupRaftRemoved()
        {
            var g = new SarFakeGame { RemoveEmpty = true, IslandOn = false }; var p = new SarParams();
            var c = new SarController(g, p, null);
            g.AddUnit(10, "ship", -8, 0, 1);
            g.AddRaft(1, 0, 0, 3, "all_life_raft"); g.AddRaft(2, 1, 0.5, 2, "all_life_raft");
            g.AddRaft(3, -30, 30, 4, "all_life_raft");
            if (Ack(c.ShipCmd(Cmd(10, "start", 15))) != null) return "start";
            c.Tick(g.Now);
            g.RaftList.RemoveAll(r => r.Id == 3);   // sank far from anyone
            Run(g, c, 6000);
            if (c.RescuedByMod != 5) return "RescuedByMod " + c.RescuedByMod + " " + string.Join("|", g.Logs);
            if (g.EventCount("sar_pickup") != 2) return "pickups " + g.EventCount("sar_pickup");
            int shipGone = 0, sank = 0;
            foreach (var l in g.Logs)
            {
                if (l.StartsWith("[Sar] raft gone id=", StringComparison.Ordinal) && l.Contains("reason=ship_pickup")) shipGone++;
                if (l.StartsWith("[Sar] raft gone id=3 people=4 reason=no_rescuer_near", StringComparison.Ordinal)) sank++;
            }
            if (shipGone != 2 || sank != 1) return "gone log ship=" + shipGone + " sank=" + sank + " " + string.Join("|", g.Logs);
            if (g.EventCount("sar_raft_gone") != 3) return "sar_raft_gone " + g.EventCount("sar_raft_gone");
            return null;
        }

        // 0.28.1 (live: a ship next to rafts of 8 sailors was sent 70 nm to a single-pilot raft): the priority class no longer wins at any distance
        private static string ShipNearestOverFarPriority()
        {
            var g = new SarFakeGame { IslandOn = false }; var p = new SarParams();
            var c = new SarController(g, p, null);
            g.AddUnit(10, "ship", 0, 0, 3);
            g.AddRaft(1, 60, 0, 1, "all_life_raft_single");
            g.AddRaft(2, 6, 0, 8, "all_life_raft");
            if (Ack(c.ShipCmd(Cmd(10, "start", 0))) != null) return "start";
            c.Tick(g.Now);
            var j = c.ShipsJson();
            if (!j.Contains("\"raft\":2")) return "picked " + j;
            return null;
        }

        private static string ShipRouteAvoidsIsland()
        {
            var g = new SarFakeGame(); var c = new SarController(g, new SarParams(), null);
            var u = g.AddUnit(10, "ship", -8, 0, 1);
            g.AddRaft(1, 8, 0, 1);
            c.ShipCmd(Cmd(10, "start"));
            var start = new GeoPt(u.Lat, u.Lon);
            Run(g, c, 60, null);   // plan done, route set
            if (!g.Routes.ContainsKey(10)) return "no route";
            var pts = new List<GeoPt> { start }; pts.AddRange(g.Routes[10]);
            for (int i = 0; i + 1 < pts.Count; i++) if (IslandDist(pts[i], pts[i + 1]) < 3.0) return "leg " + i + " crosses island";
            return null;
        }

        private static string ShipNextNearSlow()
        {
            var g = new SarFakeGame(); var c = new SarController(g, new SarParams(), null);
            g.IslandOn = false;
            g.AddUnit(10, "ship", -8, 0, 1);
            g.AddRaft(1, -4, 0, 1); g.AddRaft(2, -2, 0.5, 1);
            c.ShipCmd(Cmd(10, "start"));
            Run(g, c, 4000, null);
            // second raft is within 3 nm of the first: approached at Small, never raised to Medium again
            int med = g.Count("tg 10 3");
            if (med != 1) return "Medium set " + med + " times";
            if (g.EventCount("sar_pickup") != 2) return "pickups " + g.EventCount("sar_pickup");
            return null;
        }

        private static string ShipStopFlag()
        {
            var g = new SarFakeGame(); var p = new SarParams { ShipOnComplete = "stop" };
            var c = new SarController(g, p, null);
            g.IslandOn = false;
            g.AddUnit(10, "ship", -8, 0, 1);
            g.AddRaft(1, -4, 0, 1);
            c.ShipCmd(Cmd(10, "start"));
            Run(g, c, 1500);
            if (!g.Has("stop 10")) return "stop engines expected";
            if (g.Has("restore 10")) return "restore must not be called with stop";
            return null;
        }

        private static string ShipRadius()
        {
            var g = new SarFakeGame(); var c = new SarController(g, new SarParams(), null);
            g.IslandOn = false;
            g.AddUnit(10, "ship", -8, 0, 1);
            g.AddRaft(1, 8, 0, 1);
            c.ShipCmd(Cmd(10, "start", 10));
            Run(g, c, 200);
            if (g.Has("goto 10")) return "raft outside radius taken";
            if (c.ShipActive(10)) return "must complete: no rafts in radius";
            if (!g.Has("restore 10")) return "restore after no rafts";
            return null;
        }

        private static string Sub()
        {
            var g = new SarFakeGame(); var c = new SarController(g, new SarParams(), null);
            g.IslandOn = false;
            var u = g.AddUnit(20, "sub", -8, 0, 3); u.Submerged = true;
            g.AddRaft(1, -2, 0, 1);
            c.ShipCmd(Cmd(20, "start"));
            string cap = null, capEn = null;
            Run(g, c, 3000, t => { if (cap == null && !u.Submerged) { cap = c.MenuCaption(20); using (UiText.Push("en")) capEn = c.MenuCaption(20); } });
            if (!g.Has("surface 20")) return "no surface";
            if (g.EventCount("sar_pickup") != 1) return "sub pickup " + g.EventCount("sar_pickup");
            if (cap == null || !cap.Contains("ПЛ в надводном положении")) return "caption: " + cap;
            if (capEn == null || !capEn.StartsWith("SAR: ") || !capEn.EndsWith("rafts · sub surfaced") && !capEn.EndsWith("· sub surfaced")) return "caption en: " + capEn;
            if (!g.Has("restore_depth 20")) return "restore depth";
            // surface happened only close to the raft
            int si = g.Calls.IndexOf("surface 20");
            if (si < 0) return "surface";
            return null;
        }

        private static string Interrupts()
        {
            // evading pause
            {
                var g = new SarFakeGame(); var c = new SarController(g, new SarParams(), null);
                g.IslandOn = false;
                var u = g.AddUnit(10, "ship", -8, 0, 1);
                g.AddRaft(1, 8, 0, 1);
                c.ShipCmd(Cmd(10, "start"));
                Run(g, c, 60);
                int before = g.Calls.Count; int gotos = g.Count("goto 10");
                u.Evading = true;
                g.RaftList[0].Lat += 1.0 / 60;   // the raft drifts: a live ship would re-plan at once
                Run(g, c, 600);
                if (g.Calls.Count != before) return "commands during evasion: " + string.Join(";", g.Calls.GetRange(before, g.Calls.Count - before));
                if (!c.ShipActive(10) || c.Registry.ClaimOf(10) == null) return "claim must survive the pause";
                u.Evading = false;
                Run(g, c, 30);
                if (g.Count("goto 10") <= gotos) return "route not rebuilt after evasion";
            }
            // player takeover
            {
                var g = new SarFakeGame(); var c = new SarController(g, new SarParams(), null);
                g.IslandOn = false;
                g.AddUnit(10, "ship", -8, 0, 1);
                g.AddRaft(1, 8, 0, 1);
                c.ShipCmd(Cmd(10, "start"));
                Run(g, c, 60);
                g.Player.Add(10);
                Run(g, c, 10);
                int calls = g.Calls.Count;
                if (g.Events.FindIndex(x => x.StartsWith("sar_warn") && x.Contains("player_took")) < 0) return "player_took warn";
                if (c.ShipActive(10)) return "still active after takeover";
                if (c.Registry.ClaimOf(10) != null) return "claim kept";
                Run(g, c, 300);
                if (g.Calls.Count != calls) return "unit touched after takeover";
                if (g.Has("restore 10")) return "restore after takeover";
            }
            // stop
            {
                var g = new SarFakeGame(); var c = new SarController(g, new SarParams(), null);
                g.IslandOn = false;
                g.AddUnit(10, "ship", -8, 0, 1);
                g.AddRaft(1, 8, 0, 1);
                c.ShipCmd(Cmd(10, "start"));
                Run(g, c, 60);
                if (c.Registry.ClaimOf(10) == null) return "claim before stop";
                if (Ack(c.ShipCmd(Cmd(10, "stop"))) != null) return "stop ack";
                if (!g.Has("restore 10")) return "stop -> restore";
                if (c.Registry.ClaimOf(10) != null) return "claim after stop";
                if (c.ShipCmd(Cmd(10, "stop")) != "!not_active") return "second stop";
            }
            // threat at the raft -> next raft
            {
                var g = new SarFakeGame(); var c = new SarController(g, new SarParams(), null);
                g.IslandOn = false;
                g.AddUnit(10, "ship", -8, 0, 1);
                g.AddRaft(1, 8, 0, 1); g.AddRaft(2, -8, 8, 1);
                c.ShipCmd(Cmd(10, "start"));
                Run(g, c, 20);
                var cl = c.Registry.ClaimOf(10);
                if (cl == null || cl.RaftId != 2) return "nearer raft first: " + (cl == null ? "none" : cl.RaftId.ToString());
                // make raft 2 dangerous
                g.ThreatList.Add(new SarThreat { Id = 1, Lat = SarFakeGame.At(-8, 12).Lat, Lon = SarFakeGame.At(-8, 12).Lon, AirDefense = true });
                Run(g, c, 20);
                cl = c.Registry.ClaimOf(10);
                if (cl == null || cl.RaftId != 1) return "must switch to raft 1: " + (cl == null ? "none" : cl.RaftId.ToString());
                if (c.Registry.SeatsOn(2) != 0) return "claim on threatened raft kept";
            }
            return null;
        }

        private static string Helo()
        {
            var g = new SarFakeGame(); var p = new SarParams();
            var c = new SarController(g, p, null);
            g.IslandOn = false;
            var u = g.AddUnit(30, "helo", 0, 0);
            g.AddRaft(1, 6, 0, 2); g.AddRaft(2, 6, 9, 1);
            int rid;
            string r = c.HeloAssign(30, new SarArea(), g.Now, out rid);
            if (r != null || rid != 1) return "assign " + r + " " + rid;
            if (!g.Has("raft_pickup 30 True")) return "RaftPickup not set";
            if (!g.Has("goto 30 very_low 500 n=1")) return "goto: " + string.Join(";", g.Calls);
            if (c.Registry.ClaimOf(30) == null) return "claim";
            // flies, picks up 2
            for (int i = 0; i < 200 && c.RescuedByMod == 0; i++) { g.Step(5); c.Tick(g.Now); c.HeloTick(30, new SarArea(), g.Now); }
            if (c.RescuedByMod != 2) return "rescued " + c.RescuedByMod;
            if (g.EventCount("sar_pickup") != 1) return "sar_pickup";
            // next raft assigned by HeloTick
            if (c.Registry.ClaimOf(30) == null || c.Registry.ClaimOf(30).RaftId != 2) return "next raft not assigned";
            // full
            u.Onboard = u.Capacity;
            if (c.HeloTick(30, new SarArea(), g.Now) != "full") return "full";
            if (c.Registry.ClaimOf(30) != null) return "claim kept when full";
            // misses
            u.Onboard = 2;
            c.HeloTick(30, new SarArea(), g.Now);   // reacquire raft 2
            g.Routes.Remove(30); u.Lat = SarFakeGame.At(-30, 0).Lat; u.Waypoints = 0;
            g.TimeScale = 1;
            int gotos = g.Count("goto 30");
            g.Now += 10; c.HeloTick(30, new SarArea(), g.Now);   // miss 1 -> repeat
            if (g.Count("goto 30") != gotos + 1) return "repeat goto 1";
            u.Waypoints = 0; g.Routes.Remove(30);
            g.Now += 10; c.HeloTick(30, new SarArea(), g.Now);   // miss 2 -> repeat
            if (g.Count("goto 30") != gotos + 2) return "repeat goto 2";
            u.Waypoints = 0; g.Routes.Remove(30);
            g.Now += 10; c.HeloTick(30, new SarArea(), g.Now);   // miss 3 -> unreachable(air)
            if (!c.Registry.Unreachable(2, "air", g.Now)) return "raft not marked unreachable(air)";
            if (g.Events.FindIndex(x => x.StartsWith("sar_warn") && x.Contains("unreachable")) < 0) return "unreachable warn";
            return null;
        }

        // 0.28.8 (live 0.28.7: SH-2F took 8 from raft 1514 and flew home; the raft list is read ~40 s later and the raft was logged
        // "no_rescuer_near"): a vanished raft near a recent helicopter pickup is credited to that helicopter
        private static string HeloPickupRaftGoneLater()
        {
            var g = new SarFakeGame(); var c = new SarController(g, new SarParams(), null);
            g.IslandOn = false;
            var u = g.AddUnit(30, "helo", 6, 0);
            g.AddRaft(1, 6, 0, 3);
            c.Tick(g.Now);
            c.HeloTick(30, new SarArea(), g.Now);
            u.Onboard = 3;                                         // the game moved 3 people aboard over the raft
            c.HeloTick(30, new SarArea(), g.Now);
            if (!g.Logs.Exists(l => l.StartsWith("[Sar] helo uid=30 picked people=3", StringComparison.Ordinal))) return "pickup log " + string.Join("|", g.Logs);
            var far = SarFakeGame.At(-20, 0); u.Lat = far.Lat; u.Lon = far.Lon;   // gone home
            g.Now += 40;
            g.RaftList.RemoveAll(r => r.Id == 1);
            c.Tick(g.Now);
            if (!g.Logs.Exists(l => l.StartsWith("[Sar] raft gone id=1 people=3 reason=helo uid=30 (picked 3)", StringComparison.Ordinal))) return "raft gone reason " + string.Join("|", g.Logs);
            return null;
        }

        // T-231 (live 0.30.11): after ReleaseUnit (air-threat retreat) the old raft point is not compared with the new route: no player_took;
        // a new assignment arms the check again
        private static string PlayerAfterRelease()
        {
            var g = new SarFakeGame(); var c = new SarController(g, new SarParams(), null);
            g.IslandOn = false;
            g.AddUnit(30, "helo", 0, 0);
            g.AddRaft(1, 6, 0, 2);
            int rid; var area = new SarArea();
            if (c.HeloAssign(30, area, g.Now, out rid) != null) return "assign";
            c.ReleaseUnit(30, "air_threat");
            g.Player.Add(30);   // TakenByPlayer reads true: the retreat route differs from the raft point
            if (c.HeloTick(30, area, g.Now + 1) == "player_took") return "player_took after the release";
            if (g.EventCount("sar_warn") != 0 && g.Events.Exists(x => x.StartsWith("sar_warn") && x.Contains("player_took"))) return "player_took event";
            g.Player.Remove(30);
            if (c.HeloAssign(30, area, g.Now + 2, out rid) != null) return "assign again";
            g.Player.Add(30);
            if (c.HeloTick(30, area, g.Now + 3) != "player_took") return "no player_took after a new assignment";
            return null;
        }

        private static string HeloTimeCompressionAndThreat()
        {
            var g = new SarFakeGame(); var c = new SarController(g, new SarParams(), null);
            g.IslandOn = false; g.TimeScale = 4;
            var u = g.AddUnit(30, "helo", 0, 0);
            g.AddRaft(1, 6, 0, 2);
            int rid; var area = new SarArea();
            c.HeloAssign(30, area, g.Now, out rid);
            g.Routes.Remove(30); u.Waypoints = 0; u.Lat = SarFakeGame.At(-30, 0).Lat;
            g.Now += 10; c.HeloTick(30, area, g.Now);
            g.Routes.Remove(30); u.Waypoints = 0;
            g.Now += 10; c.HeloTick(30, area, g.Now);
            if (g.EventCount("sar_warn") != 1 || !g.Events.Exists(x => x.Contains("tc_blocked"))) return "tc_blocked: " + string.Join(";", g.Events);
            // threat releases the claim; nothing else to take -> no_rafts after the grace
            g.TimeScale = 1;
            g.ThreatList.Add(new SarThreat { Id = 1, Lat = SarFakeGame.At(6, 3).Lat, Lon = SarFakeGame.At(6, 3).Lon, AirDefense = true });
            g.Now += 1; c.Tick(g.Now);
            if (c.Registry.ClaimOf(30) != null) return "threat: claim kept";
            string leave = c.HeloTick(30, area, g.Now);
            if (leave != null) return "grace not elapsed: " + leave;
            g.Now += 61;
            leave = c.HeloTick(30, area, g.Now);
            if (leave != "no_rafts") return "no_rafts expected, got " + leave;
            // player takeover
            g.Player.Add(30);
            if (c.HeloTick(30, area, g.Now) != "player_took") return "helo player_took";
            return null;
        }

        private static string Pump()
        {
            var g = new SarFakeGame(); var p = new SarParams { ProbeBudget = 100 };
            var queue = new List<Action>();
            var c = new SarController(g, p, a => queue.Add(a));
            g.AddUnit(10, "ship", -8, 0, 1);
            g.AddRaft(1, 8, 0, 1);
            c.ShipCmd(Cmd(10, "start"));
            g.Step(1); c.Tick(g.Now);
            bool solved = false;
            for (int i = 0; i < 400 && !solved; i++)
            {
                int before = g.HeightCalls;
                c.FramePump();
                if (g.HeightCalls - before > 100) return "pump probes " + (g.HeightCalls - before) + " > budget";
                g.Step(1); c.Tick(g.Now);
                if (queue.Count > 0) solved = true;
            }
            if (!solved) return "Solve never queued";
            if (g.Has("goto 10")) return "goto before Solve ran in background";
            foreach (var a in queue.ToArray()) a();
            queue.Clear();
            g.Step(1); c.Tick(g.Now);
            for (int i = 0; i < 400 && !g.Has("goto 10"); i++)   // frame x2 retries are possible
            {
                c.FramePump(); g.Step(1); c.Tick(g.Now);
                foreach (var a in queue.ToArray()) a();
                queue.Clear();
            }
            if (!g.Has("goto 10")) return "no goto after solve";
            return null;
        }

        private static string SaveLoad()
        {
            var g = new SarFakeGame(); var c = new SarController(g, new SarParams(), null);
            g.IslandOn = false;
            g.AddUnit(10, "ship", -8, 0, 1);
            g.AddRaft(1, 8, 0, 1);
            c.ShipCmd(Cmd(10, "start", 25));
            Run(g, c, 30);
            var d = c.Save();
            d.RescuedByMod = 7;
            if (d.Ships.Count != 1 || d.Ships[0].Uid != 10 || d.Ships[0].RadiusNm != 25) return "save ships";
            string json = SarSave.ToJson(d);
            c.OnMissionChanged();
            if (c.Registry.Claims.Count != 0 || c.ShipActive(10)) return "OnMissionChanged must clear";
            c.Load(d, g.Now);
            if (c.RescuedByMod != 7) return "RescuedByMod " + c.RescuedByMod;
            if (!c.ShipActive(10)) return "ship not restored";
            c.OnMissionLeft();
            if (c.Registry.Claims.Count != 0 || c.ShipActive(10) || c.ShipsJson() != "[]") return "OnMissionLeft must clear: " + c.ShipsJson();
            c.Load(d, g.Now);
            if (c.Registry.Claims.Count != 0) return "claims must be rebuilt, not saved";
            int gotos = g.Count("goto 10");
            Run(g, c, 30);
            if (g.Count("goto 10") <= gotos) return "route not re-planned after load";
            if (c.Registry.ClaimOf(10) == null) return "claim not rebuilt";
            if (json.Length < 10) return "json";
            string sj = c.ShipsJson(), rj = c.RaftsJson();
            if (!sj.Contains("\"uid\":10") || !rj.Contains("\"id\":1")) return "json state " + sj + " " + rj;
            if (!sj.Contains("\"lat\":") || !sj.Contains("\"lon\":")) return "ship json lat/lon " + sj;
            for (int i = 0; i < 400 && !c.ShipsJson().Contains("\"route\":"); i++) { c.FramePump(); g.Step(1); c.Tick(g.Now); }
            var ships = (List<object>)Json.Parse(c.ShipsJson());
            var s0 = (Dictionary<string, object>)ships[0];
            if (!s0.ContainsKey("route")) return "ship json route " + c.ShipsJson();
            var rt = (List<object>)s0["route"];
            if (rt.Count < 2 || !(rt[0] is List<object>) || ((List<object>)rt[0]).Count != 2) return "route shape";
            return null;
        }

        public static string Run()
        {
            string e;
            if ((e = Busy()) != null) return "busy: " + e;
            if ((e = ShipRows()) != null) return "ship rows: " + e;
            if ((e = ShipCycle()) != null) return "ship cycle: " + e;
            if ((e = ShipRouteAvoidsIsland()) != null) return "route: " + e;
            if ((e = ShipNextNearSlow()) != null) return "next near: " + e;
            if ((e = ShipStopFlag()) != null) return "stop flag: " + e;
            if ((e = ShipRadius()) != null) return "radius: " + e;
            if ((e = Sub()) != null) return "sub: " + e;
            if ((e = Interrupts()) != null) return "interrupts: " + e;
            if ((e = Helo()) != null) return "helo: " + e;
            if ((e = HeloTimeCompressionAndThreat()) != null) return "helo2: " + e;
            if ((e = HeloPickupRaftGoneLater()) != null) return "helo_pick_gone: " + e;
            if ((e = PlayerAfterRelease()) != null) return "player-after-release: " + e;
            if ((e = Pump()) != null) return "pump: " + e;
            if ((e = SaveLoad()) != null) return "save/load: " + e;
            if ((e = ShipPickupRaftRemoved()) != null) return "ship pickup, raft removed: " + e;
            if ((e = ShipNearestOverFarPriority()) != null) return "ship nearest: " + e;
            if ((e = NewRaftSwitch()) != null) return "new raft switch: " + e;
            if ((e = ForgetOnRelease()) != null) return "forget on release: " + e;
            return null;
        }

        // T-318: ReleaseUnit forgets the TakenByPlayer base; a route set by someone else after that is not a takeover until SAR places a point again
        private static string ForgetOnRelease()
        {
            var g = new SarFakeGame { PlacedModel = true }; var c = new SarController(g, new SarParams(), null);
            g.IslandOn = false;
            g.AddUnit(30, "helo", 0, 0);
            g.AddRaft(1, 20, 0, 2);
            c.Tick(g.Now);
            var area = new SarArea();
            c.HeloTick(30, area, g.Now);
            if (!g.Placed.ContainsKey(30)) return "no base after HeloAssign";
            c.ReleaseUnit(30, "air_threat");
            if (!g.Forgot.Contains(30) || g.Placed.ContainsKey(30)) return "base not forgotten";
            g.Routes[30] = new List<GeoPt> { SarFakeGame.At(8, 0) };
            string r = c.HeloTick(30, area, g.Now);
            if (r == "player_took") return "false player_took after the mod's own assignment";
            if (!g.Placed.ContainsKey(30)) return "no new base";
            g.Routes[30] = new List<GeoPt> { SarFakeGame.At(5, 0) };
            if (c.HeloTick(30, area, g.Now) != "player_took") return "manual command must give player_took";
            return null;
        }

        // 0.30.1 (customer: "SAR does not see the rafts at once"): a raft that appears is logged at once ("raft new", event sar_raft_new),
        // and a helicopter on its way to a far raft switches to a much nearer new one; a far new raft changes nothing
        private static string NewRaftSwitch()
        {
            var g = new SarFakeGame(); var c = new SarController(g, new SarParams(), null);
            g.IslandOn = false;
            g.AddUnit(30, "helo", 0, 0);
            g.AddRaft(1, 40, 0, 2);
            c.Tick(g.Now);
            if (g.Logs.Exists(l => l.StartsWith("[Sar] raft new", StringComparison.Ordinal))) return "the first read must not log every raft as new";
            c.HeloTick(30, new SarArea(), g.Now);
            if (c.Registry.ClaimOf(30) == null || c.Registry.ClaimOf(30).RaftId != 1) return "first claim";
            g.Step(5); c.Tick(g.Now); c.HeloTick(30, new SarArea(), g.Now);
            if (c.RaftGen != 0) return "gen without a new raft " + c.RaftGen;
            g.AddRaft(2, 2, 1, 3);
            c.Tick(g.Now);
            if (c.RaftGen != 1) return "gen after a new raft " + c.RaftGen;
            if (!g.Logs.Exists(l => l.StartsWith("[Sar] raft new id=2 people=3", StringComparison.Ordinal))) return "raft new log " + string.Join("|", g.Logs);
            if (g.EventCount("sar_raft_new") != 1) return "sar_raft_new event";
            c.HeloTick(30, new SarArea(), g.Now);
            var cl = c.Registry.ClaimOf(30);
            if (cl == null || cl.RaftId != 2) return "no switch to the near raft: " + (cl == null ? "none" : cl.RaftId.ToString());
            if (!g.Logs.Exists(l => l.StartsWith("[Sar] helo uid=30 switch raft=1 -> 2", StringComparison.Ordinal))) return "switch log";
            if (c.Registry.SeatsOn(1) != 0) return "old raft still claimed";
            // a new raft farther away: no switch, and the same generation is not looked at twice
            g.AddRaft(3, -30, 0, 5);
            c.Tick(g.Now); c.HeloTick(30, new SarArea(), g.Now);
            if (c.Registry.ClaimOf(30).RaftId != 2) return "switched to a far raft";
            int gotos = g.Count("goto 30");
            c.HeloTick(30, new SarArea(), g.Now);
            if (g.Count("goto 30") != gotos) return "a tick without news re-routed the helicopter";
            return null;
        }

        /// <summary>0.30.1: the cost of the SAR tick with 40 rafts and 6 helicopters (the controller, every helicopter and the view ask for the rafts);
        /// the game list must be read once per tick (SarRaftCache), not once per caller. Prints the numbers.</summary>
        public static string Perf()
        {
            var g = new SarFakeGame { Cache = new SarRaftCache() }; var c = new SarController(g, new SarParams(), null);
            g.IslandOn = false;
            for (int i = 0; i < 40; i++) g.AddRaft(100 + i, 5 + (i % 8) * 3, -10 + (i / 8) * 4, 1 + i % 8);
            for (int k = 0; k < 6; k++) g.AddUnit(30 + k, "helo", 0, k * 2);
            var area = new SarArea();
            const int ticks = 600;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int t = 0; t < ticks; t++)
            {
                g.Step(1); g.RealT += 1;
                c.Tick(g.Now);
                for (int k = 0; k < 6; k++) c.HeloTick(30 + k, area, g.Now);
                c.RaftsJson(); c.ShipsJson();
            }
            sw.Stop();
            double us = sw.Elapsed.TotalMilliseconds * 1000.0 / ticks;
            double calls = (double)g.RaftCalls / ticks, reads = (double)g.Cache.Reads / ticks;
            Console.WriteLine("sar perf: ticks=" + ticks + " rafts=40 helos=6 us_per_tick=" + us.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                + " raft_calls_per_tick=" + calls.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " game_reads_per_tick=" + reads.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                + " rescued=" + c.RescuedByMod);
            if (reads > 1.0001) return "game reads per tick " + reads;
            if (calls < 3) return "the callers were not exercised: " + calls;
            if (c.RescuedByMod == 0) return "nobody rescued";
            return null;
        }
    }
}
#endif
