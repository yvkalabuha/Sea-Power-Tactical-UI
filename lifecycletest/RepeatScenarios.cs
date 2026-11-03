// T-212: the "N loads" stand. Counters of MissionLifecycle, subscribers / mod threads / live worlds after repeated save-load and mission changes.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using VoiceBotBridge.Ui;

namespace VoiceBotBridge
{
    internal static class RepeatScenarios
    {
        // the world is created inside the helper so that no temporary of the caller's frame keeps it alive
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference EnterB(LifeRig r, double missionT, double realT)
        {
            var w = LifeWorld.MakeB(missionT, null);
            r.Enter(w, realT);
            return new WeakReference(w);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference EnterA(LifeRig r, double missionT, string sav, TorpSnap torp, double realT)
        {
            var w = LifeWorld.MakeA(missionT, sav); w.Torp = torp;
            r.Enter(w, realT);
            return new WeakReference(w);
        }

        private static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }

        public static string Counters()
        {
            using (var r = new LifeRig())
            {
                var weaks = new List<WeakReference>();
                double t = 0;
                for (int i = 0; i < 5; i++)
                {
                    weaks.Add(EnterB(r, 100 + i, t)); t += 10;
                    r.NoMission(t); t += 3; r.NoMission(t); t += 3;
                }
                if (r.M.SubscriberCount != LifeRig.Wiring.Length) return "subscribers " + r.M.SubscriberCount + " != " + LifeRig.Wiring.Length;
                if (r.M.Generation != 5) return "generation " + r.M.Generation;
                Collect();
                if (r.M.PrevAlive() != 0) return "prev_alive after GC must be 0, got " + r.M.PrevAlive();
                string js = r.M.CountersJson();
                if (!js.Contains("\"generation\":5") || !js.Contains("\"subscribers\":" + LifeRig.Wiring.Length) || !js.Contains("\"prev_alive\":0") || !js.Contains("\"prev_tracked\":5")) return "counters json: " + js;
                // a world the test itself holds is seen as a leak
                var held = LifeWorld.MakeB(900, null);
                r.Enter(held, t); t += 1;
                weaks.Add(EnterB(r, 901, t));
                Collect();
                if (r.M.PrevAlive() != 1) return "a held old world must be counted, prev_alive=" + r.M.PrevAlive();
                GC.KeepAlive(held);
            }
            return null;
        }

        public static string Repeat()
        {
            using (var r = new LifeRig())
            {
                // the real stop-runtime of SAR: route jobs go through the tagged pool runner
                var sar = new SarController(r.SarG, new SarParams(), ModThreads.PoolRunner("sar.solve"));
                r.Sar = sar; r.Asw.Sar = sar;
                var weaks = new List<WeakReference>();
                double t = 0;
                weaks.Add(EnterA(r, 1000, null, null, t)); t += 10;
                r.Populate(); if (!r.RunToLeg(600)) return "the smart attack did not reach its leg: " + r.SmartState();
                int subs0 = r.M.SubscriberCount, threads0 = -1, alive0 = -1; 
                var snaps = new List<string>();
                for (int cycle = 0; cycle < 8; cycle++)
                {
                    bool toMenuB = cycle >= 6;
                    if (!toMenuB)
                    {
                        // "save -> load A": the same file again, through a switch
                        // every cycle is a new save of the player (a new last_save), so the controllers rewrite their slots; the loaded file is the one just saved
                        string sav = cycle % 2 == 0 ? "/saves/a.sav" : "/saves/b.sav";
                        r.SetMarker(sav); r.Run(3);
                        double saveT = r.Time;
                        var torp = r.Destroy();
                        weaks.Add(EnterA(r, saveT, sav, torp, t)); t += 10;
                        r.Run(3);
                        snaps.Add(r.Snapshot());
                        if (snaps[snaps.Count - 1] != "asw=1 sar=1 cap=1 smart=1 id=3") return "cycle " + cycle + ": groups after the load: " + snaps[snaps.Count - 1];   // T-211 merged: the groups survive a load
                    }
                    else
                    {
                        // "-> menu -> B"
                        r.Destroy();
                        r.NoMission(t); t += 3; r.NoMission(t); t += 3;
                        weaks.Add(EnterB(r, 2000 + cycle, t)); t += 10;
                        r.Run(3);
                        if (r.Snapshot() != "asw=0 sar=0 cap=0 smart=0 id=0") return "B after the menu must start empty: " + r.Snapshot();
                    }
                    if (r.M.SubscriberCount != subs0) return "cycle " + cycle + ": subscribers " + r.M.SubscriberCount + " != " + subs0;
                    Collect();
                    int threads = Process.GetCurrentProcess().Threads.Count;
                    int modAlive = ModThreads.AliveCount();
                    if (cycle == 0) { threads0 = threads; alive0 = modAlive; }
                    else
                    {
                        if (Math.Abs(threads - threads0) > 2) return "cycle " + cycle + ": process threads " + threads + " vs " + threads0;
                        if (modAlive > alive0 + 2) return "cycle " + cycle + ": live mod threads " + modAlive + " vs " + alive0;
                    }
                }
                // every left world is collectable; the current one is still held by the rig only weakly
                r.NoMission(t); t += 3; r.NoMission(t);
                Collect();
                for (int i = 0; i < weaks.Count; i++) if (weaks[i].IsAlive) return "world #" + i + " of " + weaks.Count + " is still alive after GC";
                if (r.M.PrevAlive() != 0) return "prev_alive " + r.M.PrevAlive();
            }
            return null;
        }
    }
}
