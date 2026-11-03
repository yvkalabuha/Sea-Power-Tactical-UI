#if SAR_WIP
using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-109 stream H: SAR groups in the ASW controller on fakes (AswFakeGame for the decks and the list of machines, SarFakeGame for rafts and flight).
    internal static class AswSarTests
    {
        private static Dictionary<string, object> P(string json) { return (Dictionary<string, object>)Json.Parse(json); }

        private sealed class World
        {
            public AswFakeGame F = new AswFakeGame();
            public SarController S;
            public AswController C;
            public int Landings;

            public World(string mode = "Arcade", bool addLoadout = true)
            {
                F.Mode = mode;
                if (addLoadout) F.LoadoutList.Add(new LoadoutOption { Name = "Default", Display = "Default", Capacity = 2, ReadyUpMin = 15, CoolDownMin = 15, Available = true });
                S = new SarController(F.SarWorld, new SarParams(), null);
                C = new AswController(F, new AswParams(), new AswConfig { RestoreOnLoad = "off" });
                C.Sar = S;
            }

            public AswMember Deck(int uid, int cap, int best, string where = "deck_ready", string type = "SH-3H", string loadout = "Default")
            {
                var m = AswFakeGame.Helo(uid, where);
                m.Type = type; m.DeckKey = "task:t" + uid; m.Loadout = loadout; m.Capacity = cap; m.BestCapacity = best; m.Passive = 0; m.ActiveBuoys = 0; m.Vlad = 0; m.Torpedoes = 0;
                m.DeckUid = 9000; m.ReadyInS = where == "deck_prep" ? 60 : 0; m.FuelFrac = 1;
                F.Mem.Add(m);
                return m;
            }

            public void Raft(int id, double n, double e, int people) { F.SarWorld.AddRaft(id, n, e, people); }

            /// <summary>One second: deck timers, flight, pickups, landings, then the controllers.</summary>
            public void Step()
            {
                var sw = F.SarWorld;
                foreach (var m in F.Mem)
                {
                    if (m.Where == "air" && !sw.Units.ContainsKey(m.Uid))
                    {
                        var u = sw.AddUnit(m.Uid, "helo", 0, 0);
                        u.Capacity = m.Capacity; u.Onboard = m.Onboard;
                    }
                    if (m.Where == "deck_prep") { m.ReadyInS -= 1; if (m.ReadyInS <= 0) { m.ReadyInS = 0; m.Where = "deck_ready"; } }
                    if (m.Where == "air") m.FuelFrac -= 1.0 / m.EnduranceS;
                }
                sw.Step(1);
                F.Now = sw.Now;
                foreach (var m in new List<AswMember>(F.Mem))
                {
                    if (m.Where != "air") continue;
                    SarUnit u;
                    if (!sw.Units.TryGetValue(m.Uid, out u)) continue;
                    m.Lat = u.Lat; m.Lon = u.Lon; m.Onboard = u.Onboard;
                    double at;
                    if (F.RtbAt.TryGetValue(m.Uid, out at) && F.Now - at >= 30)   // landed: the aircraft is gone, a hangar row appears
                    {
                        F.Mem.Remove(m); sw.Units.Remove(m.Uid); sw.Routes.Remove(m.Uid); F.RtbAt.Remove(m.Uid); Landings++;
                        var h = AswFakeGame.Helo(-(800 + Landings), "deck_service");
                        h.Type = m.Type; h.DeckKey = "hangar:" + m.Type; h.Loadout = "Default"; h.Capacity = 2; h.BestCapacity = m.BestCapacity; h.DeckUid = m.DeckUid; h.Passive = 0; h.ActiveBuoys = 0; h.Vlad = 0; h.Torpedoes = 0;
                        F.Mem.Add(h);
                    }
                }
                F.Tick(C);
            }

            public void Run(int s) { for (int i = 0; i < s; i++) Step(); }

            public int LogCount(string sub) { int n = 0; foreach (var l in F.Logs) if (l.Contains(sub)) n++; return n; }
            public int EventCount(string name, string sub = null) { int n = 0; foreach (var e in F.Events) if (e.StartsWith(name + " ", StringComparison.Ordinal) && (sub == null || e.Contains(sub))) n++; return n; }

            public AswGroupView Group(string name) { foreach (var g in C.View().Groups) if (g.Name == name) return g; return null; }
        }

        public static string Run()
        {
            string e;
            if ((e = TwoHelos()) != null) return "two helos: " + e;
            if ((e = Sh2f()) != null) return "sh-2f: " + e;
            if ((e = IdleAndResume()) != null) return "idle and resume: " + e;
            if ((e = Unfit()) != null) return "unfit: " + e;
            if ((e = StopAndSwitch()) != null) return "stop and switch: " + e;
            if ((e = Lists()) != null) return "lists: " + e;
            if ((e = Persist()) != null) return "persist: " + e;
            if ((e = Loiter()) != null) return "loiter: " + e;
            if ((e = Takeover()) != null) return "takeover: " + e;
            if ((e = Commands()) != null) return "commands: " + e;
            if ((e = EmptyGroup()) != null) return "empty group: " + e;
            if ((e = IdleScan()) != null) return "idle scan: " + e;
            if ((e = EmptyHangar()) != null) return "empty hangar: " + e;
            if ((e = StrikeGroupRefused()) != null) return "strike group refused: " + e;
            if ((e = AllowOnce()) != null) return "allow once: " + e;
            return null;
        }

        // 0.30.1 (live 0.30.0: "sar cmd sar_order {group=STRIKE-1,...} -> ok"): a strike group does not take the rescue order
        private static string StrikeGroupRefused()
        {
            var w = new World();
            var m = w.Deck(1, 2, 2); m.Torpedoes = 2;
            w.Raft(11, 6, 3, 2);
            string err = Create(w, 1); if (err != null) return err;
            string r = w.C.Order(P("{\"group\":\"SAR-1\",\"order\":\"strike\"}"));
            if (AswController.IsErr(r)) return "strike order " + r;
            string name = w.C.View().Groups[0].Name;
            r = w.C.SarOrder(P("{\"group\":\"" + name + "\"}"));
            if (r == null || !r.StartsWith("!wrong_group_kind", StringComparison.Ordinal)) return "sar_order on " + name + " -> " + r;
            var g = w.Group(name);
            if (g == null || g.Order != "strike") return "the strike group changed: " + (g == null ? "gone" : g.Order);
            // a new (order none) group still takes it
            w.Deck(2, 2, 2);
            if (AswController.IsErr(w.C.Group(P("{\"group\":\"G2\",\"op\":\"create\",\"uids\":[2]}")))) return "create G2";
            r = w.C.SarOrder(P("{\"group\":\"G2\"}"));
            if (AswController.IsErr(r)) return "sar_order on a new group " + r;
            return null;
        }

        // 0.30.1 (live 0.30.0: "launch SAR-2 uid=-1830039994 key=task:52562023... route=allow -> ok" four times): an allowed deck task that waits
        // for its turn on the deck is not allowed again every 120 s; after AllowWaitS one more allow goes out (logged as "again"), then the wait doubles
        private static int T1(World w) { int n = 0; foreach (var a in w.F.Allows) if (a.EndsWith(":task:t1", StringComparison.Ordinal)) n++; return n; }

        private static string AllowOnce()
        {
            var w = new World();
            w.Deck(1, 2, 2);
            w.Raft(11, 6, 3, 2); w.Raft(12, -5, 6, 2);
            w.F.NoAirframeDecks.Add(9000);   // no other machine: the plan keeps asking for this one
            w.F.AllowStaysOnDeck = true;   // the deck takes the allow, the helicopter waits for a free spot
            string err = Create(w, 1); if (err != null) return err;
            if ((err = Order(w)) != null) return err;
            w.Run(3);
            int n1 = T1(w);
            if (n1 != 1) return "first allow " + n1;
            if (!w.C.SarAllowWaiting("SAR-1", "task:t1")) return "not marked as waiting";
            w.Run(250);
            if (T1(w) != 1) return "allow repeated within the wait: " + T1(w) + " " + string.Join(" | ", w.F.Logs.FindAll(x => x.StartsWith("[Sar] launch", StringComparison.Ordinal)).ToArray());
            w.Run(60);
            if (T1(w) != 2) return "no second allow after AllowWaitS: " + T1(w);
            if (w.LogCount("key=task:t1 route=allow -> ok (again after ") != 1) return "second allow not logged as again";
            w.Run(500);
            if (T1(w) != 2) return "third allow before the doubled wait: " + T1(w);
            // the helicopter takes off: the task is forgotten
            w.F.AllowStaysOnDeck = false;
            w.Run(200);
            if (w.C.SarAllowWaiting("SAR-1", "task:t1")) return "launched task still waiting";
            if (AswController.SarAllowBackoffS(1) != 300 || AswController.SarAllowBackoffS(2) != 600 || AswController.SarAllowBackoffS(5) != 1200) return "backoff";
            return null;
        }

        // 0.28.1 (live bug: "[Sar] order SAR-1 deck=8 usn_sh-2f/Empty mission=sar -> launch_failed" every few seconds, Yorktown hangar empty):
        // a deck that has no machine of the type left is asked once, the refusal is logged once with its reason, the next try waits for
        // the deck to change or 600 s; a change of the group's machines on the deck lets it try again at once
        private static string EmptyHangar()
        {
            var w = new World(addLoadout: false);
            w.F.LoadoutList.Add(new LoadoutOption { Name = "ASW", Capacity = 2, ReadyUpMin = 15, CoolDownMin = 15, Available = true });
            w.F.LoadoutList.Add(new LoadoutOption { Name = "Empty", Capacity = 8, ReadyUpMin = 15, CoolDownMin = 15, Available = true });
            w.Deck(1, 2, 8, type: "SH-2F", loadout: "ASW");
            w.Raft(11, 60, 30, 5); w.Raft(12, 61, 31, 5); w.Raft(13, 62, 29, 5);
            w.F.NoAirframeDecks.Add(9000);
            string err = Create(w, 1); if (err != null) return err;
            if ((err = Order(w)) != null) return err;
            w.Run(300);
            int orders = 0; foreach (var o in w.F.OrderLog) if (o.StartsWith("9000:", StringComparison.Ordinal)) orders++;
            int fails = 0, retries = 0;
            foreach (var l in w.F.Logs)
            {
                if (l.StartsWith("[Sar] order SAR-1 deck=9000", StringComparison.Ordinal) && l.Contains("-> no_airframes (no machine of this type left in the hangar")) fails++;
                if (l.Contains(" retry after no_airframes")) retries++;
            }
            if (fails != 1) return "refusal logged " + fails + " times";
            // the launch of the deck machine changes the deck once (deck_ready -> air): at most one more try in 300 s
            if (orders < 1 || orders > 2 || retries != orders - 1) return "orders " + orders + " retries " + retries;
            bool warn = false; foreach (var l in w.F.Logs) if (l.StartsWith("asw warn no_reserve_deck (SAR-1): deck 9000: no SH-2F left", StringComparison.Ordinal)) warn = true;
            if (!warn) return "no warning for the window";
            // the hangar has a machine again: after the back-off the order goes through
            w.F.NoAirframeDecks.Clear();
            w.Run(700);
            bool ok = false; foreach (var l in w.F.Logs) if (l.StartsWith("[Sar] order SAR-1 deck=9000 SH-2F/Empty mission=sar -> ok", StringComparison.Ordinal)) ok = true;
            if (!ok) return "no order after the hangar refilled: " + string.Join(" | ", w.F.Logs.FindAll(x => x.StartsWith("[Sar] order", StringComparison.Ordinal)).ToArray());
            if (SarAirOrderBackoff("launch_failed", 1) != 60 || SarAirOrderBackoff("launch_failed", 3) != 240 || SarAirOrderBackoff("launch_failed", 9) != 600 || SarAirOrderBackoff("no_airframes", 1) != 600) return "backoff";
            return null;
        }

        private static double SarAirOrderBackoff(string err, int fails) { return AswController.SarOrderBackoffS(err, fails); }

        // 0.27.10 (live bug: the mod never showed rafts): with no SAR order the rafts are still counted for the window and the dev state
        // ("raft_scan"), but not drawn; without the game hook the state says null
        private static string IdleScan()
        {
            var w = new World();
            w.Deck(1, 2, 2);
            w.Raft(11, 4, 4, 3); w.Raft(12, -3, 9, 0);
            w.Run(3);
            if (w.C.View().RaftsInWater != -1) return "no hook: count must be unknown";
            var o0 = P(w.C.StateJson());
            if (!o0.ContainsKey("raft_scan") || o0["raft_scan"] != null) return "no hook: raft_scan must be null";
            int calls = 0;
            w.C.RaftScanOf = () =>
            {
                calls++;
                var sc = new SarRaftScan(); sc.Begin(w.F.SarWorld.RaftList.Count, 0);
                foreach (var r in w.F.SarWorld.RaftList) sc.Count(r);
                return sc;
            };
            w.Run(3);
            var v = w.C.View();
            if (calls == 0) return "hook not asked without a SAR order";
            if (v.Rafts.Count != 1 || !v.SarIdle || v.RaftsDrawn) return "idle: rafts are in the view but not drawn (T-192), got " + v.Rafts.Count + " idle=" + v.SarIdle + " drawn=" + v.RaftsDrawn;
            if (v.RaftsInWater != 1 || v.PeopleInWater != 3) return "idle counts " + v.RaftsInWater + "/" + v.PeopleInWater;
            var o = P(w.C.StateJson());
            var rs = o["raft_scan"] as Dictionary<string, object>;
            if (rs == null) return "raft_scan missing";
            if (Convert.ToDouble(rs["list"]) != 2 || Convert.ToDouble(rs["candidates"]) != 1 || Convert.ToDouble(rs["empty"]) != 1 || Convert.ToDouble(rs["people"]) != 3) return "raft_scan counts list=" + rs["list"] + " candidates=" + rs["candidates"] + " empty=" + rs["empty"] + " people=" + rs["people"];
            return null;
        }

        // T-192: without a SAR order the rafts of the last read stay in the view and in ui_state "rafts" (equal to raft_scan), the map does not draw them
        internal static string RaftsIdle()
        {
            var w = new World();
            w.Deck(1, 2, 2);
            w.Raft(11, 4, 4, 3); w.Raft(12, -3, 9, 0);
            w.C.RaftScanOf = () =>
            {
                var sc = new SarRaftScan(); sc.Begin(w.F.SarWorld.RaftList.Count, 0);
                foreach (var r in w.F.SarWorld.RaftList) sc.Count(r);
                return sc;
            };
            w.Run(3);
            var v = w.C.View();
            if (v.Rafts.Count != 1 || v.RaftsInWater != 1) return "idle rafts " + v.Rafts.Count + " in water " + v.RaftsInWater;
            if (!v.SarIdle || v.RaftsDrawn) return "idle flags " + v.SarIdle + "/" + v.RaftsDrawn;
            var o = P(w.C.StateJson());
            var rafts = o["rafts"] as List<object>;
            if (rafts == null || rafts.Count != 1) return "ui_state rafts " + (rafts == null ? "null" : rafts.Count.ToString());
            var r0 = rafts[0] as Dictionary<string, object>;
            if (r0 == null || Convert.ToInt32(r0["id"]) != 11 || Convert.ToInt32(r0["people"]) != 3) return "ui_state raft 0";
            var rs = o["raft_scan"] as Dictionary<string, object>;
            if (rs == null || Convert.ToInt32(rs["candidates"]) != 1) return "raft_scan candidates";
            int calls = w.F.SarRaftsCalls;
            w.Run(2);
            if (w.F.SarRaftsCalls != calls) return "the game was asked between two scans: " + calls + " -> " + w.F.SarRaftsCalls;
            if (w.C.View().Rafts.Count != 1) return "the cache is lost between two scans";
            w.Run(10);
            if (w.F.SarRaftsCalls <= calls) return "no new scan after RaftIdleScanS";
            string err = Create(w, 1); if (err != null) return err;
            if ((err = Order(w)) != null) return err;
            w.Run(3);
            v = w.C.View();
            if (v.SarIdle || !v.RaftsDrawn) return "after the order: idle=" + v.SarIdle + " drawn=" + v.RaftsDrawn;
            return null;
        }

        // T-142: a SAR group without members and without returns is not removed automatically
        private static string EmptyGroup()
        {
            var w = new World();
            string err = Create(w); if (err != null) return err;
            w.Run(600);
            var g = w.Group("SAR-1");
            if (g == null) return "empty SAR group removed";
            if (g.Status != "empty") return "status " + g.Status;
            return null;
        }

        private static string Create(World w, params int[] uids)
        {
            var sb = new System.Text.StringBuilder("{\"group\":\"SAR-1\",\"op\":\"create\",\"uids\":[");
            for (int i = 0; i < uids.Length; i++) { if (i > 0) sb.Append(','); sb.Append(uids[i]); }
            string r = w.C.Group(P(sb.Append("]}").ToString()));
            return AswController.IsErr(r) ? r : null;
        }

        private static string Order(World w, string json = "{\"group\":\"SAR-1\"}")
        {
            string r = w.C.SarOrder(P(json));
            return AswController.IsErr(r) ? r : null;
        }

        // two SH-3H with 2 seats, three rafts of two: both fly, the ordered third one takes the last raft; every helicopter comes back
        private static string TwoHelos()
        {
            var w = new World();
            w.Deck(1, 2, 2); w.Deck(2, 2, 2);
            w.Raft(11, 6, 3, 2); w.Raft(12, -5, 6, 2); w.Raft(13, 2, -8, 2);
            string err = Create(w, 1, 2); if (err != null) return err;
            if ((err = Order(w)) != null) return err;
            w.Run(3);
            if (w.F.Allows.Count != 2) return "launches " + w.F.Allows.Count;
            if (w.F.OrderLog.Count != 2 || !w.F.OrderLog[0].EndsWith(":Default:sar:False")) return "orders " + w.F.OrderLog.Count + " " + (w.F.OrderLog.Count > 0 ? w.F.OrderLog[0] : "");
            var g = w.Group("SAR-1");
            if (g == null || g.Order != "sar") return "group order";
            w.Run(1500);
            int rescued = 0;
            foreach (var r in w.F.SarWorld.RaftList) rescued += 2 - r.People;
            if (rescued != 6) return "rescued " + rescued + " of 6";
            if (w.EventCount("sar_leave", "\"reason\":\"full\"") < 3) return "full leaves " + w.EventCount("sar_leave", "\"reason\":\"full\"");
            if (w.S.RescuedByMod != 6) return "mod counter " + w.S.RescuedByMod;
            if (w.Landings < 3) return "landings " + w.Landings;
            g = w.Group("SAR-1");
            if (g.SarRescued != 6) return "group rescued " + g.SarRescued;
            if (g.Status != "idle_no_rafts") return "status " + g.Status;
            // a reserve stays ready, no empty group left behind
            if (g.Members.Count == 0) return "group emptied";
            if (w.LogCount("[Sar] leave") < 3) return "leave log";
            // second cycle: new rafts are taken by the same fleet after landing and a new order
            w.Raft(14, 5, -4, 2); w.Raft(15, -6, -6, 2);
            w.Run(1200);
            int again = 0; foreach (var r in w.F.SarWorld.RaftList) if (r.Id >= 14) again += 2 - r.People;
            if (again != 4) return "second cycle rescued " + again;
            return null;
        }

        // SH-2F: ASW loadout has 2 seats, Transport/Empty 8: it flies as it is, and an 8-seat helicopter is ordered in parallel
        // live finding 38: a ready SH-2F with its ASW loadout (2 seats) and 5 people waiting is re-armed on deck to Empty (8 seats): the ready
        // task is aborted and the same type is ordered with Empty; it is not launched with 2 seats
        private static World Sh2fWorld(string mode = "Arcade", double emptyReadyMin = 15)
        {
            var w = new World(mode, addLoadout: false);
            w.F.LoadoutList.Add(new LoadoutOption { Name = "ASW", Capacity = 2, ReadyUpMin = 15, CoolDownMin = 15, Available = true });
            w.F.LoadoutList.Add(new LoadoutOption { Name = "Transport", Capacity = 8, ReadyUpMin = emptyReadyMin, CoolDownMin = 15, Available = true });
            w.F.LoadoutList.Add(new LoadoutOption { Name = "Empty", Capacity = 8, ReadyUpMin = emptyReadyMin, CoolDownMin = 15, Available = true });
            w.Deck(1, 2, 8, type: "SH-2F", loadout: "ASW");
            return w;
        }

        private static string Logs(World w, string prefix) { return string.Join(" | ", w.F.Logs.FindAll(x => x.StartsWith(prefix, StringComparison.Ordinal)).ToArray()); }

        private static string Sh2f()
        {
            var w = Sh2fWorld();
            w.Raft(11, 6, 3, 5);
            string err = Create(w, 1); if (err != null) return err;
            if ((err = Order(w)) != null) return err;
            w.Run(3);
            if (w.F.Allows.Count != 0) return "launched with 2 seats " + w.F.Allows.Count;
            if (!w.F.Calls.Contains("abort:task:t1")) return "ready ASW task not aborted: " + string.Join(",", w.F.Calls.ToArray());
            if (w.LogCount("[Sar] loadout SAR-1 uid=1 SH-2F loadout=ASW seats=2 best=Empty/8 people=5 prep_s=90 -> rearm why=more_seats") != 1) return "no loadout line: " + Logs(w, "[Sar]");
            if (w.LogCount("[Sar] rearm SAR-1 uid=1 key=task:t1 SH-2F ASW->Empty abort -> ok") != 1) return "no rearm line: " + Logs(w, "[Sar]");
            if (w.F.OrderLog.Count < 1 || w.F.OrderLog.Count > 2) return "orders x" + w.F.OrderLog.Count;
            foreach (var o in w.F.OrderLog) if (o != "9000:SH-2F:Empty:sar:False") return "order " + o;
            // the Empty one is ready after 90 s (Arcade), flies and takes all five at once
            w.Run(600);
            if (w.F.SarWorld.RaftList.Count > 0 && w.F.SarWorld.RaftList[0].People > 0) return "raft not emptied: " + w.F.SarWorld.RaftList[0].People;
            if (w.LogCount("-> launch why=") != 0) return "a low-seat launch: " + Logs(w, "[Sar] loadout");
            if ((err = Sh2fAsIs()) != null) return err;
            // game refuses a loadout (no ammunition): the next best one is ordered, the refusal is remembered
            var w2 = new World(addLoadout: false);
            w2.F.LoadoutList.Add(new LoadoutOption { Name = "Transport", Capacity = 8, ReadyUpMin = 15, CoolDownMin = 15, Available = true });
            w2.F.LoadoutList.Add(new LoadoutOption { Name = "Empty", Capacity = 8, ReadyUpMin = 15, CoolDownMin = 15, Available = true });
            w2.F.NoAmmo.Add("Empty");
            w2.Deck(1, 8, 8, "deck_service", "SH-2F", "Transport").DeckKey = "hangar:SH-2F";
            if ((err = Create(w2, 1)) != null) return err;
            if ((err = Order(w2)) != null) return err;
            w2.Run(80);
            if (w2.F.OrderLog.Count < 2 || !w2.F.OrderLog[0].Contains(":Empty:") || !w2.F.OrderLog[1].Contains(":Transport:")) return "no_ammo fallback " + string.Join("|", w2.F.OrderLog.ToArray());
            return null;
        }

        // live finding 38: when the re-arm is not worth it the ready helicopter goes as it is, with one journal line saying why
        private static string Sh2fAsIs()
        {
            // two people fit the two seats: no re-arm
            var w = Sh2fWorld();
            w.Raft(11, 6, 3, 2);
            string err = Create(w, 1); if (err != null) return err;
            if ((err = Order(w)) != null) return err;
            w.Run(3);
            if (w.F.Allows.Count != 1 || w.F.Calls.Contains("abort:task:t1")) return "few people: allows " + w.F.Allows.Count;
            if (w.LogCount("[Sar] loadout SAR-1 uid=1 SH-2F loadout=ASW seats=2 best=Empty/8 people=2 prep_s=90 -> launch why=enough_seats") != 1) return "few people line: " + Logs(w, "[Sar]");
            // Realistic, Empty takes 30 min to prepare (over RearmMaxMin 20): as is
            w = Sh2fWorld("Realistic", 30);
            w.Raft(11, 6, 3, 5);
            if ((err = Create(w, 1)) != null) return err;
            if ((err = Order(w)) != null) return err;
            w.Run(3);
            if (w.F.Allows.Count != 1 || w.F.Calls.Contains("abort:task:t1")) return "prep long: allows " + w.F.Allows.Count;
            if (w.LogCount("-> launch why=prep_long") != 1) return "prep long line: " + Logs(w, "[Sar]");
            // a task of the player the deck refuses to abort: one try, then it goes as is
            w = Sh2fWorld();
            w.F.ForeignKeys.Add("task:t1");
            w.Raft(11, 6, 3, 5);
            if ((err = Create(w, 1)) != null) return err;
            if ((err = Order(w)) != null) return err;
            w.Run(5);
            if (!w.F.Calls.Contains("abort_refused:task:t1")) return "foreign: no abort try";
            if (w.LogCount("[Sar] rearm SAR-1 uid=1 key=task:t1 SH-2F ASW->Empty abort -> not_ours (goes as is)") != 1) return "foreign rearm line: " + Logs(w, "[Sar]");
            if (w.F.Allows.Count != 1) return "foreign: allows " + w.F.Allows.Count;
            if (w.LogCount("-> launch why=cannot_rearm") != 1) return "foreign launch line: " + Logs(w, "[Sar]");
            // RearmMaxMin = 0: never re-armed
            w = Sh2fWorld();
            w.C.SarOptions.RearmMaxMin = 0;
            w.Raft(11, 6, 3, 5);
            if ((err = Create(w, 1)) != null) return err;
            if ((err = Order(w)) != null) return err;
            w.Run(3);
            if (w.F.Allows.Count != 1 || w.F.Calls.Contains("abort:task:t1") || w.LogCount("-> launch why=rearm_off") != 1) return "rearm off: " + Logs(w, "[Sar]");
            // the game layer hands the player's task over to the mod (AirOpsGame.Delegate): the re-arm goes through
            w = Sh2fWorld();
            w.F.ForeignKeys.Add("task:t1");
            var fk = w.F.ForeignKeys;
            w.C.DelegateDeckTask = (deck, key) => deck == 9000 && fk.Remove(key);
            w.Raft(11, 6, 3, 5);
            if ((err = Create(w, 1)) != null) return err;
            if ((err = Order(w)) != null) return err;
            w.Run(3);
            if (w.F.Allows.Count != 0 || !w.F.Calls.Contains("abort:task:t1")) return "handed over: allows " + w.F.Allows.Count;
            if (w.LogCount("[Sar] rearm SAR-1 uid=1 key=task:t1 SH-2F ASW->Empty handed_over abort -> ok") != 1) return "handed over line: " + Logs(w, "[Sar]");
            if ((err = Sh2fTaskAndHangar()) != null) return "task and hangar: " + err;
            return null;
        }

        // live 0.28.7: a group of a ready ASW task and a hangar row of the same type (shown "ready" because the hangar machine can launch ASW):
        // the task is re-armed (one abort, logged), the hangar row is never aborted (it is no task), its airframe is ordered with Empty;
        // one order per deck and tick; the hangar row that empties leaves the group with a journal line
        private static string Sh2fTaskAndHangar()
        {
            var w = Sh2fWorld("Realistic");
            var hrow = w.Deck(2, 2, 8, type: "SH-2F", loadout: "ASW");
            hrow.DeckKey = "hangar:SH-2F"; hrow.DeckCount = 1;
            for (int i = 0; i < 22; i++) w.Raft(20 + i, 6 + i, 3, 8);
            string err = Create(w, 1, 2); if (err != null) return err;
            if ((err = Order(w)) != null) return err;
            w.Step();
            int orders1 = w.F.OrderLog.Count;
            if (orders1 > 1) return "orders in the first tick " + orders1;
            w.Run(2);
            int aborts = 0; foreach (var c in w.F.Calls) if (c.StartsWith("abort", StringComparison.Ordinal)) aborts++;
            if (aborts != 1 || !w.F.Calls.Contains("abort:task:t1")) return "aborts " + string.Join(",", w.F.Calls.ToArray());
            if (w.LogCount("[Sar] rearm SAR-1 uid=1 ") != 1 || w.LogCount("[Sar] loadout SAR-1 uid=1 ") != 1) return "rearm lines: " + Logs(w, "[Sar]");
            if (w.LogCount("[Sar] loadout SAR-1 uid=2 ") != 0) return "hangar row has a loadout line";
            if (w.F.Allows.Count != 0) return "launched with 2 seats";
            if (w.F.OrderLog.Count < 2 || w.F.OrderLog.Count > 3) return "orders " + w.F.OrderLog.Count;
            foreach (var o in w.F.OrderLog) if (!o.Contains(":Empty:")) return "order " + o;
            // the game took the hangar airframe for an order: the row is gone, the journal says so
            w.F.Mem.Remove(hrow);
            w.Run(2);
            if (w.LogCount("[Sar] SAR-1 Helo 2 uid=2 deck row gone: hangar:SH-2F deck_ready (hangar empty") != 1) return "no deck row line: " + Logs(w, "[Sar]");
            return null;
        }

        private static string IdleAndResume()
        {
            var w = new World();
            w.Deck(1, 2, 2);
            string err = Create(w, 1); if (err != null) return err;
            if ((err = Order(w)) != null) return err;
            w.Run(30);
            var g = w.Group("SAR-1");
            if (g.Status != "idle_no_rafts") return "idle status " + g.Status;
            if (w.F.Allows.Count != 0 || w.F.OrderLog.Count != 0) return "idle but acted " + w.F.Allows.Count + "/" + w.F.OrderLog.Count;
            w.Raft(11, 4, 4, 2);
            w.Run(10);
            if (w.F.Allows.Count != 1) return "no launch on a new raft";
            w.Run(300);
            if (w.F.SarWorld.RaftList[0].People != 0) return "raft not taken";
            return null;
        }

        private static string Unfit()
        {
            var w = new World();
            w.Deck(1, 2, 2);
            var plane = w.Deck(2, 0, 0, type: "P-3C"); plane.Helicopter = false; plane.Passive = 40;
            var zero = w.Deck(3, 0, 0); zero.Passive = 40;   // a helicopter whose loadouts have no seats
            string err = Create(w, 1, 2, 3); if (err != null) return err;
            // Create into an ASW-ordered group is allowed; the order "sar" drops the unfit ones with a reason
            if ((err = Order(w)) != null) return err;
            var g = w.Group("SAR-1");
            if (g.Members.Count != 1 || g.Members[0] != 1) return "members after order " + g.Members.Count;
            var v = w.C.View();
            string r2 = null, r3 = null;
            foreach (var x in v.ExcludedSar) { if (x.Row.Uid == 2) r2 = x.Reason; if (x.Row.Uid == 3) r3 = x.Reason; }
            if (r2 != "no_capacity" || r3 != "no_capacity") return "reasons " + r2 + "/" + r3;
            bool warned = false; foreach (var wr in v.Warnings) if (wr.StartsWith("removed_unfit")) warned = true;
            if (!warned) return "no warning";
            // adding an unfit one to a SAR group is ignored; a free fit one is accepted
            w.C.Group(P("{\"group\":\"SAR-1\",\"op\":\"add\",\"uids\":[2]}"));
            if (w.Group("SAR-1").Members.Count != 1) return "unfit added";
            // a group that has nobody who can serve does not take the order
            var w2 = new World();
            var p2 = w2.Deck(5, 0, 0, type: "P-3C"); p2.Helicopter = false; p2.Passive = 40;
            if ((err = Create(w2, 5)) != null) return err;
            string r = w2.C.SarOrder(P("{\"group\":\"SAR-1\"}"));
            if (r != "!no_capacity") return "all unfit -> " + r;
            // the group of another unit is "in_group"
            bool ingroup = false; foreach (var x in v.ExcludedSar) if (x.Row.Uid == 1 && x.Reason == "in_group:SAR-1") ingroup = true;
            if (!ingroup) return "in_group reason missing";
            return null;
        }

        private static string StopAndSwitch()
        {
            var w = new World();
            w.Deck(1, 2, 2); w.Deck(2, 2, 2);
            w.Raft(11, 6, 3, 2); w.Raft(12, -5, 6, 2); w.Raft(13, 2, -8, 2);
            string err = Create(w, 1, 2); if (err != null) return err;
            if ((err = Order(w)) != null) return err;
            w.Run(40);
            if (w.F.OrderLog.Count == 0) return "no own order";
            string stop = w.C.Stop(P("{\"group\":\"SAR-1\"}"));
            if (AswController.IsErr(stop)) return stop;
            w.F.Flush();   // the window command's requests leave at the end of the frame
            if (w.Group("SAR-1").Order != "none") return "not stopped";
            int rtbs = 0; foreach (var c in w.F.Calls) if (c.StartsWith("rtb:")) rtbs++;
            if (rtbs == 0) return "airborne not sent home";
            bool aborted = false; foreach (var c in w.F.Calls) if (c.StartsWith("abort:")) aborted = true;
            if (!aborted && w.F.OrderLog.Count > 0)
            {
                // an own order that was already launched has nothing to abort; at least no pending own order stays
                foreach (var m in w.F.Mem) if (m.DeckKey != null && m.DeckKey.StartsWith("task:o") && m.Where != "air") return "own order left behind";
            }
            if (w.S.Registry.Claims.Count != 0) return "claims left";
            // switching to an ASW order: machines that cannot hunt submarines leave the group with a reason
            var w2 = new World();
            w2.Deck(1, 2, 2).Passive = 12;
            var noasw = w2.Deck(2, 2, 2); noasw.Passive = 0; noasw.Torpedoes = 0;
            if ((err = Create(w2, 1, 2)) != null) return err;
            if ((err = Order(w2)) != null) return err;
            string r = w2.C.SearchArea(P("{\"group\":\"SAR-1\",\"rect\":[35,20,35,20.2,35.1,20.2,35.1,20]}"));
            if (AswController.IsErr(r)) return r;
            var g = w2.Group(UiText.Get("asw.group.prefix") + "-1");
            if (g.Order != "area" || g.Members.Count != 1 || g.Members[0] != 1) return "switch result " + g.Order + "/" + g.Members.Count;
            bool noload = false; foreach (var x in w2.C.View().Excluded) if (x.Row.Uid == 2 && x.Reason == "no_asw_load") noload = true;
            if (!noload) return "no_asw_load reason";
            return null;
        }

        private static string Lists()
        {
            var w = new World();
            w.Deck(1, 2, 2);
            w.Raft(11, 4, 4, 3); w.Raft(12, -3, 9, 1);
            var plain = w.Deck(4, 2, 2); plain.Where = "sar";   // held by AutomaticSAR
            var zone = w.Deck(5, 2, 2); zone.GameSarZone = true;
            string err = Create(w, 1); if (err != null) return err;
            if ((err = Order(w)) != null) return err;
            w.Run(20);
            var v = w.C.View();
            string r4 = null, r5 = null;
            foreach (var x in v.ExcludedSar) { if (x.Row.Uid == 4) r4 = x.Reason; if (x.Row.Uid == 5) r5 = x.Reason; }
            if (r4 != "ext_sar" || r5 != "game_sar_zone") return "reasons " + r4 + "/" + r5;
            if (v.Rafts.Count != 2) return "rafts " + v.Rafts.Count;
            var g = w.Group("SAR-1");
            if (g.SarRafts == 0 && v.Rafts.Count > 0 && w.F.SarWorld.RaftList[0].People > 0 && g.SarPeople == 0) return "group totals";
            string j = w.C.StateJson();
            var o = (Dictionary<string, object>)Json.Parse(j);
            var groups = (List<object>)o["groups"];
            var gj = (Dictionary<string, object>)groups[0];
            if ((string)gj["order"] != "sar" || !(gj["sar"] is Dictionary<string, object>)) return "state json order/sar";
            var sj = (Dictionary<string, object>)gj["sar"];
            foreach (string k in new[] { "rafts", "people", "active", "rescued", "next_launch_s" }) if (!sj.ContainsKey(k)) return "sar field " + k;
            if (!o.ContainsKey("ships_sar") || !o.ContainsKey("rafts") || !o.ContainsKey("available_sar") || !o.ContainsKey("excluded_sar")) return "state json lists";
            var rj = (List<object>)o["rafts"];
            var r0 = (Dictionary<string, object>)rj[0];
            foreach (string k in new[] { "id", "lat", "lon", "people", "claimed_by", "unreachable" }) if (!r0.ContainsKey(k)) return "raft field " + k;
            // an airborne helicopter reports seats and its raft
            w.Run(60);
            AswRow row = null; foreach (var x in w.Group("SAR-1").MemberRows) if (x.Type == "SH-3H" && x.Uid > 0) row = x;
            if (row == null) return "no member row";
            // T-109 follow-up: airborne row carries its position and the target raft (overlay draws the line)
            if (!row.HasPos) return "row has no position";
            if (row.TargetRaft <= 0 || row.TargetDistNm < 0) return "row has no target raft";
            AswRow ar = null; foreach (var x in w.C.View().Aircraft) if (x.Uid == row.Uid) ar = x;
            if (ar == null || !ar.HasPos || ar.TargetRaft != row.TargetRaft) return "aircraft row position/target";
            foreach (var x in w.C.View().Available) if (x.HasPos) return "deck row must not have a position";
            return null;
        }

        /// <summary>T-198: a ship on a rescue run only (no group) is saved by the game-save hook at once, without a tick.</summary>
        internal static string HookShipSave()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aswsar198-" + Guid.NewGuid().ToString("N"));
            try
            {
                var w = new World();
                w.F.SaveDir = dir; w.F.LastSavePath = "/saves/a.sav";
                w.C = new AswController(w.F, new AswParams(), new AswConfig { RestoreOnLoad = "auto" }); w.C.Sar = w.S;
                w.F.SarWorld.AddUnit(30, "ship", 0, 0, 3).Name = "Yorktown";
                string ship = w.S.ShipCmd(P("{\"uid\":30,\"op\":\"start\",\"radius_nm\":10}"));
                if (AswController.IsErr(ship) || (ship ?? "").StartsWith("!")) return "ship start " + ship;
                w.Run(2);
                w.C.OnGameSaved("/saves/b.sav", SavStamp.Now(2425.6, new DateTime(2026, 10, 23, 2, 7, 37, DateTimeKind.Utc), "hook"));
                string file = System.IO.Path.Combine(dir, SaveSlotStore.Key("/saves/b.sav") + ".json");
                if (!System.IO.File.Exists(file)) return "file of the ship run not written by the hook";
                string text = System.IO.File.ReadAllText(file);
                if (!text.Contains("\"sar\"") || !text.Contains("\"ships\"") || !text.Contains("\"sav\"")) return "sar part or sav missing";
                var w2 = new World();
                w2.F.SaveDir = dir; w2.F.LastSavePath = "/saves/b.sav"; w2.F.Now = 3; w2.F.SarWorld.Now = 3;
                w2.F.SarWorld.AddUnit(30, "ship", 0, 0, 3).Name = "Yorktown";
                w2.C = new AswController(w2.F, new AswParams(), new AswConfig { RestoreOnLoad = "auto" }); w2.C.Sar = w2.S;
                w2.C.OnMissionChanged("/saves/b.sav", 3, SavStamp.Now(2425.6, new DateTime(2026, 10, 23, 2, 7, 37, DateTimeKind.Utc), "sav"));
                if (!w2.S.ShipActive(30)) return "ship run not restored (the clock restarted at 3 s)";
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
            return null;
        }

        private static string Persist()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aswsar-" + Guid.NewGuid().ToString("N"));
            try
            {
                var w = new World();
                w.F.SaveDir = dir; w.F.LastSavePath = "/saves/a.sav";
                w.C = new AswController(w.F, new AswParams(), new AswConfig { RestoreOnLoad = "auto" }); w.C.Sar = w.S;
                w.Deck(1, 2, 2); w.Raft(11, 4, 4, 2);
                string err = Create(w, 1); if (err != null) return err;
                if ((err = Order(w, "{\"group\":\"SAR-1\",\"on_complete\":\"loiter\",\"area\":{\"lat\":35.1,\"lon\":20.1,\"radius_nm\":15}}")) != null) return err;
                // a ship on a rescue run is part of the file as well
                w.F.SarWorld.AddUnit(30, "ship", 0, 0, 3).Name = "Yorktown";
                string ship = w.S.ShipCmd(P("{\"uid\":30,\"op\":\"start\",\"radius_nm\":10}"));
                if (AswController.IsErr(ship) || (ship ?? "").StartsWith("!")) return "ship start " + ship;
                w.Run(3);
                w.F.LastSavePath = "/saves/b.sav";   // the game saved
                w.Run(2);
                // a new mission = the same save is loaded
                var w2 = new World();
                w2.F.SaveDir = dir; w2.F.LastSavePath = "/saves/b.sav"; w2.F.Now = 1005; w2.F.SarWorld.Now = 1005;
                w2.F.SarWorld.AddUnit(30, "ship", 0, 0, 3).Name = "Yorktown";
                w2.C = new AswController(w2.F, new AswParams(), new AswConfig { RestoreOnLoad = "auto" }); w2.C.Sar = w2.S;
                w2.Deck(1, 2, 2); w2.F.Mem[0].Name = w.F.Mem.Count > 0 ? w.F.Mem[0].Name : "Helo 1";
                w2.C.OnMissionChanged("/saves/b.sav", 1005);
                var g = w2.Group("SAR-1");
                if (g == null || g.Order != "sar") return "group not restored " + (g == null ? "null" : g.Order);
                if (g.SarArea != "circle" || g.SarOnComplete != "loiter" || Math.Abs(g.SarRadiusNm - 15) > 1e-9) return "area not restored " + g.SarArea + "/" + g.SarOnComplete;
                if (!w2.S.ShipActive(30)) return "ship run not restored on the first step (T-130: no 2 s pause)";
                w2.Run(5);
                if (!w2.S.ShipActive(30)) return "ship run lost after 5 s";
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
            return null;
        }

        private static int CountCalls(AswFakeGame f, string prefix) { int n = 0; foreach (var c in f.Calls) if (c.StartsWith(prefix, StringComparison.Ordinal)) n++; return n; }

        private static string Loiter()
        {
            var w = new World();
            w.Deck(1, 2, 2);
            w.Raft(11, 4, 4, 1);
            string err = Create(w, 1); if (err != null) return err;
            if ((err = Order(w, "{\"group\":\"SAR-1\",\"on_complete\":\"loiter\"}")) != null) return err;
            w.Run(400);   // the only raft is taken (1 of 2 seats), nothing else to do: the helicopter waits over its ship, not goes home
            if (w.F.SarWorld.RaftList[0].People != 0) return "raft not taken";
            bool hovered = false; foreach (var c in w.F.Calls) if (c.StartsWith("hoverover:")) hovered = true;
            // it carries a survivor: onboard > 0 means home even in loiter mode
            bool rtb = false; foreach (var c in w.F.Calls) if (c.StartsWith("rtb:")) rtb = true;
            if (!rtb && !hovered) return "neither hover nor rtb";
            // an empty helicopter with no work in loiter mode: waits over its own ship, not rtb
            var w2 = new World();
            w2.Deck(1, 2, 2);
            w2.Raft(11, 4, 4, 1);
            if ((err = Create(w2, 1)) != null) return err;
            if ((err = Order(w2, "{\"group\":\"SAR-1\",\"on_complete\":\"loiter\"}")) != null) return err;
            w2.Run(5);
            w2.F.SarWorld.RaftList[0].People = 0;   // somebody else picked them
            w2.Run(200);
            bool h2 = false, r2 = false, plain = false;
            foreach (var c in w2.F.Calls) { if (c.StartsWith("hoverover:")) h2 = true; if (c.StartsWith("rtb:")) r2 = true; if (c.StartsWith("hover:")) plain = true; }
            if (!h2 || r2 || plain) return "loiter hoverover=" + h2 + " rtb=" + r2 + " plain hover=" + plain;
            // T-147 H: it waits over its own ship (SarHoverOver with the ship uid); the ship moves -> the call is repeated; low fuel -> home
            AswMember air = null; foreach (var m in w2.F.Mem) if (m.Where == "air") air = m;
            if (air == null) return "no hovering aircraft";
            w2.F.Tgt.Lat = air.Lat; w2.F.Tgt.Lon = air.Lon;    // the ship is under the helicopter: nothing to repeat
            w2.Run(8);
            int n1 = CountCalls(w2.F, "hoverover:" + air.Uid + ":" + w2.F.Tgt.Uid);
            if (n1 < 1) return "hoverover without the ship uid";
            w2.Run(60);
            if (CountCalls(w2.F, "hoverover:" + air.Uid) != n1) return "repeated although the ship did not move";
            w2.F.Tgt.Lat += 1.0 / 60.0;                         // 1 nm away
            w2.Run(7);
            if (CountCalls(w2.F, "hoverover:" + air.Uid) != n1 + 1) return "not repeated after the ship moved: " + CountCalls(w2.F, "hoverover:" + air.Uid) + " vs " + n1;
            air.FuelFrac = 0.01;
            w2.Run(3);
            bool rtb2 = false; foreach (var c in w2.F.Calls) if (c.StartsWith("rtb:" + air.Uid)) rtb2 = true;
            if (!rtb2) return "low fuel: no rtb";
            return null;
        }

        private static string Commands()
        {
            var w = new World();
            w.Deck(1, 2, 2);
            string err = Create(w, 1); if (err != null) return err;
            // an area measured from the selected ship when the command has no origin
            string r = w.C.SarOrder(P("{\"group\":\"SAR-1\",\"area\":{\"bearing\":90,\"dist_nm\":10,\"size_nm\":10}}"));
            if (AswController.IsErr(r)) return r;
            var g = w.Group("SAR-1");
            if (g.SarArea != "rect" || g.SarRect == null || Math.Abs((g.SarRect[1] + g.SarRect[3]) / 2 - (w.F.Tgt.Lon + 10.0 / 60.0)) > 0.01) return "bearing area";
            if (w.C.SarOrder(P("{}")) != "!missing_group") return "missing group";
            if (w.C.SarOrder(P("{\"group\":\"nope\"}")) != "!no_group") return "unknown group";
            if (w.C.SarOrder(P("{\"group\":\"SAR-1\",\"on_complete\":\"x\"}")) != "!bad_on_complete") return "bad on_complete";
            // ships and submarines
            var ship = w.F.SarWorld.AddUnit(30, "ship", 0, 0, 3); ship.Name = "Yorktown";
            w.F.SarWorld.AddRaft(21, 5, 5, 1);
            if (w.C.SarShip(P("{\"uid\":30,\"op\":\"start\",\"radius_nm\":10}")).StartsWith("!")) return "ship start";
            if (!w.S.ShipActive(30)) return "ship not active";
            if (w.C.SarShip(P("{\"uid\":30,\"op\":\"start\"}")) != "!busy:sar_ship") return "second start";
            if (w.C.SarShip(P("{\"uid\":30,\"op\":\"fly\"}")) != "!bad_op") return "bad op";
            if (w.C.SarShip(P("{\"uid\":30,\"op\":\"start\",\"radius_nm\":7}")) != "!bad_radius") return "bad radius";
            if (w.C.SarShip(P("{\"op\":\"stop\"}")) != "!missing_uid") return "missing uid";
            w.Run(3);
            string js = w.C.StateJson();
            var o = (Dictionary<string, object>)Json.Parse(js);
            var ships = (List<object>)o["ships_sar"];
            if (ships.Count != 1) return "ships_sar " + ships.Count;
            // "Return control" on the ship ends the run and restores it
            w.C.ReleaseUid(30);
            if (w.S.ShipActive(30)) return "run not ended by release";
            if (!w.F.SarWorld.Has("restore 30")) return "ship not restored";
            // a run that is stopped by the command
            if (w.C.SarShip(P("{\"uid\":30,\"op\":\"start\"}")).StartsWith("!")) return "restart";
            if (w.C.SarShip(P("{\"uid\":30,\"op\":\"stop\"}")).StartsWith("!")) return "stop";
            // the unit of an ASW/SAR group is busy for ships
            var h = w.F.SarWorld.AddUnit(31, "ship", 0, 0); h.AswGroup = "SAR-1";
            if (w.C.SarShip(P("{\"uid\":31,\"op\":\"start\"}")) != "!busy:asw_group") return "busy asw_group";
            return null;
        }

        private static string Takeover()
        {
            var w = new World();
            w.Deck(1, 2, 2);
            w.Raft(11, 4, 4, 1);
            string err = Create(w, 1); if (err != null) return err;
            if ((err = Order(w)) != null) return err;
            w.Run(5);
            int air = 0; foreach (var m in w.F.Mem) if (m.Where == "air") { air = m.Uid; break; }
            if (air == 0) return "nobody launched";
            w.F.SarWorld.Player.Add(air);
            w.Run(3);
            if (w.Group("SAR-1").Members.Contains(air)) return "taken helicopter stays in the group";
            bool warned = false; foreach (var wr in w.C.View().Warnings) if (wr.StartsWith("player_took")) warned = true;
            if (!warned) return "no player_took";
            // "Return control" on a helicopter of the group: leaves the group, the claim is gone
            var w2 = new World();
            w2.Deck(1, 2, 2); w2.Raft(11, 4, 4, 1);
            if ((err = Create(w2, 1)) != null) return err;
            if ((err = Order(w2)) != null) return err;
            w2.Run(5);
            int air2 = 0; foreach (var m in w2.F.Mem) if (m.Where == "air") { air2 = m.Uid; break; }
            if (air2 == 0 || w2.S.Registry.ClaimOf(air2) == null) return "no claim before release";
            w2.C.ReleaseUid(air2);
            if (w2.S.Registry.ClaimOf(air2) != null) return "claim kept after release";
            if (w2.Group("SAR-1").Members.Contains(air2)) return "member kept after release";
            return null;
        }
    }
}
#endif
