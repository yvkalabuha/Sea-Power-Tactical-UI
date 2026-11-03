using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace VoiceBotBridge
{
    internal static class LifecycleScenarios
    {
        private const string Full = "asw=1 sar=1 cap=1 smart=1 id=3", Empty = "asw=0 sar=0 cap=0 smart=0 id=0";
        private const string EnterAll = "E:air_tick,E:sar,E:asw,E:cap,E:identify,E:smart_attack";
        private const string LeftAll = "L:doctrine:{0},L:smart_attack:{0},L:identify:{0},L:cap:{0},L:asw:{0},L:sar:{0},L:air_tick:{0}";

        private static string J(IEnumerable<string> l) { return string.Join(",", new List<string>(l).ToArray()); }

        /// <summary>A mission with everything running; <paramref name="savePath"/> null = a new mission. Returns the world (the caller keeps it).</summary>
        private static LifeWorld StartA(LifeRig r, double t, string savePath, bool populate, double realT)
        {
            var a = LifeWorld.MakeA(t, savePath);
            r.Enter(a, realT);
            if (populate) { r.Populate(); r.SetFlags(a); if (!r.RunToLeg(600)) throw new InvalidOperationException("the smart attack did not reach its leg: " + r.SmartState()); }
            return a;
        }

        public static string Order()
        {
            using (var r = new LifeRig())
            {
                StartA(r, 1000, null, false, 0);
                if (J(r.Calls) != EnterAll) return "enter order: " + J(r.Calls);
                r.Calls.Clear();
                r.NoMission(10); r.NoMission(12.5);
                if (J(r.Calls) != string.Format(LeftAll, "menu")) return "left order: " + J(r.Calls);
                if (r.M.InMission) return "still in mission";
            }
            return null;
        }

        public static string Switch()
        {
            using (var r = new LifeRig())
            {
                var a = StartA(r, 1000, null, true, 0);
                if (r.Snapshot() != Full) return "A not running: " + r.Snapshot() + " smart=" + r.SmartState();
                foreach (var u in a.FlagUnits) if (!r.FlagSet(u)) return "flag not set in A";
                r.Calls.Clear();
                r.Destroy();
                var b = LifeWorld.MakeB(2000, null);
                r.Enter(b, 20);
                int firstE = -1, lastL = -1;
                for (int i = 0; i < r.Calls.Count; i++)
                {
                    if (r.Calls[i].StartsWith("E:") && firstE < 0) firstE = i;
                    if (r.Calls[i].StartsWith("L:")) lastL = i;
                }
                if (lastL < 0 || firstE < 0 || lastL > firstE) return "Left must run before Entered: " + J(r.Calls);
                string left = J(r.Calls).Substring(0, J(r.Calls).IndexOf("E:"));
                if (left.TrimEnd(',') != string.Format(LeftAll, "switch")) return "switch left order: " + left;
                if (r.Snapshot() != Empty) return "B must start empty: " + r.Snapshot();
                foreach (var row in r.Asw.View().Aircraft) if (row.Uid == 1 || row.Uid == 2) return "asw aircraft of A still listed";
                foreach (var row in r.Asw.View().Available) if (row.Uid == 1 || row.Uid == 2) return "asw available of A still listed";
                if (r.Sar.Registry.Claims.Count != 0 || r.Sar.ShipActive(30)) return "sar claims/ships of A";
                if (r.Cap.ViewLight().Groups.Count != 0) return "cap groups of A";
                string w; if (r.Own.Owner(1, out w) != null || r.Own.Owner(11, out w) != null) return "ownership of A";
                foreach (var u in a.FlagUnits) if (r.FlagSet(u)) return "flag of A survived";
                r.Run(5);
                if (r.Snapshot() != Empty) return "B after 5 s: " + r.Snapshot();
            }
            return null;
        }

        private static string RestoreFlow(LifeRig r, out string snapA2)
        {
            snapA2 = null;
            var a = StartA(r, 1000, null, true, 0);
            if (r.Snapshot() != Full) return "A not running: " + r.Snapshot();
            if (r.SmartState() == "none") return "no smart attack in A";
            r.SetMarker("/saves/a.sav");
            r.Run(3);
            double saveT = r.Time;
            var torp = r.Destroy();
            var b = LifeWorld.MakeB(2000, "/saves/b.sav");
            r.Enter(b, 20);
            r.Run(5);
            r.Destroy();
            var a2 = LifeWorld.MakeA(saveT, "/saves/a.sav"); a2.Torp = torp;
            r.Enter(a2, 40);
            snapA2 = r.Snapshot();
            return null;
        }

        public static string Restore()
        {
            using (var r = new LifeRig())
            {
                string snap; string e = RestoreFlow(r, out snap); if (e != null) return e;
                if (snap != Full) return "restored in the first frame: " + snap + " (smart=" + r.SmartState() + ")";
                if (!r.Sar.ShipActive(30)) return "SAR ship from the ASW file not restored";
                r.Run(6);
                if (r.Snapshot() != Full) return "after 6 s: " + r.Snapshot();
                if (!r.Sar.ShipActive(30)) return "SAR ship lost after 6 s";
            }
            return null;
        }

        public static string WrongOrder()
        {
            using (var r = new LifeRig(true))
            {
                string snap; string e = RestoreFlow(r, out snap); if (e != null) return e;
                if (r.Sar.ShipActive(30) || r.ShipRuns() != 0) return "mutation not detected: the SAR ship survived asw(20) before sar(30)";
                if (!snap.StartsWith("asw=1 ")) return "the ASW group must still restore: " + snap;
            }
            return null;
        }

        public static string Menu()
        {
            using (var r = new LifeRig())
            {
                var a = StartA(r, 1000, null, true, 0);
                r.SetMarker("/saves/a.sav"); r.Run(3);
                double saveT = r.Time;
                r.Calls.Clear();
                r.NoMission(5); r.NoMission(6.5);
                r.Enter(a, 6.6);   // the same object is back after 1.5 s
                if (r.Calls.Count != 0) return "a blink left or entered: " + J(r.Calls);
                if (r.Snapshot() != Full) return "blink lost the groups: " + r.Snapshot();
                r.NoMission(10); r.NoMission(12.1);
                if (J(r.Calls) != string.Format(LeftAll, "menu")) return "menu left: " + J(r.Calls);
                if (r.Snapshot() != Empty) return "not empty after the menu: " + r.Snapshot();
                r.Calls.Clear();
                r.NoMission(22);
                if (r.Calls.Count != 0) return "second Left: " + J(r.Calls);
                var torp = r.Destroy();
                var a2 = LifeWorld.MakeA(saveT, "/saves/a.sav"); a2.Torp = torp;
                r.Enter(a2, 30);
                if (r.Snapshot() != Full) return "A' after the menu: " + r.Snapshot();
            }
            return null;
        }

        public static string Throwing()
        {
            using (var r = new LifeRig())
            {
                r.M.Subscribe(35, "boom", e => { throw new InvalidOperationException("boom"); }, why => { throw new InvalidOperationException("boom"); });
                var a = StartA(r, 1000, null, true, 0);
                if (r.Snapshot() != Full) return "others must run around a throwing subscriber: " + r.Snapshot();
                r.NoMission(10); r.NoMission(12.5);
                if (r.Snapshot() != Empty) return "left around a throwing subscriber: " + r.Snapshot();
                int en = 0, le = 0;
                foreach (string l in r.Log)
                {
                    if (l == "mission lifecycle: entered boom: InvalidOperationException: boom") en++;
                    if (l == "mission lifecycle: left boom: InvalidOperationException: boom") le++;
                }
                if (en != 1 || le != 1) return "log lines entered/left boom: " + en + "/" + le;
            }
            return null;
        }

        private static Dictionary<string, string> Hashes(string dir)
        {
            var d = new Dictionary<string, string>();
            using (var sha = SHA256.Create())
                foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    d[f] = Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(f)));
            return d;
        }

        public static string Files()
        {
            using (var r = new LifeRig())
            {
                var a = StartA(r, 1000, null, true, 0);
                string groupA = r.Asw.View().Groups[0].Name, capA = r.Cap.View().Groups[0].Line.Group;
                r.SetMarker("/saves/a.sav"); r.Run(3);
                double saveT = r.Time;
                var before = Hashes(r.SaveDir);
                if (before.Count < 3) return "slots of A not written: " + before.Count + "\n" + r.Files();
                var torp = r.Destroy();
                r.Enter(LifeWorld.MakeB(2000, "/saves/b.sav"), 20);
                r.Run(10);
                r.NoMission(40); r.NoMission(43);
                var after = Hashes(r.SaveDir);
                foreach (var kv in before)
                {
                    string h;
                    if (!after.TryGetValue(kv.Key, out h)) return "file of A deleted: " + Path.GetFileName(kv.Key);
                    if (h != kv.Value) return "file of A rewritten by B: " + Path.GetFileName(kv.Key);
                }
                foreach (var kv in after)
                {
                    if (before.ContainsKey(kv.Key)) continue;
                    string text = File.ReadAllText(kv.Key);
                    if (text.Contains(groupA) || text.Contains(capA) || text.Contains("Yorktown")) return "a file of B carries the groups of A: " + kv.Key;
                }
                r.Destroy();
                var a2 = LifeWorld.MakeA(saveT, "/saves/a.sav"); a2.Torp = torp;
                r.Enter(a2, 50);
                if (r.Snapshot() != Full) return "A' from the untouched slots: " + r.Snapshot();
            }
            return null;
        }

        /// <summary>
        /// KNOWN LEAK (T-134 finding, xfail in pytest): a NEW mission started while last_save still points at the slot of A. Entered gets
        /// SavePath = last_save; the file is stale for B, and SmartAttackController.OnMissionChanged deletes it ("stale or broken save state removed").
        /// Run only by name (not part of the default run).
        /// </summary>
        public static string StaleMarker()
        {
            using (var r = new LifeRig())
            {
                StartA(r, 1000, null, true, 0);
                r.SetMarker("/saves/a.sav"); r.Run(3);
                var before = Hashes(r.SaveDir);
                r.Destroy();
                r.Enter(LifeWorld.MakeB(2000, null), 20);   // a new mission: last_save still points at a.sav
                r.Run(10);
                var after = Hashes(r.SaveDir);
                string res = "";
                foreach (var kv in before)
                {
                    string h;
                    string kind = Path.GetFileName(Path.GetDirectoryName(kv.Key));
                    if (!after.TryGetValue(kv.Key, out h)) res += " deleted:" + kind;
                    else if (h != kv.Value) res += " changed:" + kind;
                }
                return res == "" ? null : "slot of the previous mission touched by a new mission:" + res;
            }
        }

        // ------------------------------------------------------------------ weak references

        private sealed class Held { public string Label; public WeakReference Ref; }

        private static void Hold(List<Held> l, string label, object o) { l.Add(new Held { Label = label, Ref = new WeakReference(o) }); }

        /// <summary>Builds mission A for one controller, runs it and returns weak references to the world and the game objects it handed out.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static List<Held> BuildAndLeave(LifeRig r, string kind, bool viaSwitch)
        {
            var held = new List<Held>();
            var a = LifeWorld.MakeA(1000, null);
            r.Enter(a, 0);
            if (kind == "flags") r.SetFlags(a); else r.Populate(kind);
            if (kind == "smart") { if (!r.RunToLeg(600)) throw new InvalidOperationException("no leg"); } else r.Run(5);
            if (kind == "smart" && r.SmartG.Torp == null) throw new InvalidOperationException("no torpedo to track");
            Hold(held, "world", a);
            if (kind == "asw") foreach (var m in a.Asw) Hold(held, "AswMember", m);
            if (kind == "cap") foreach (var b in a.Cap) Hold(held, "FakeBird", b);
            if (kind == "sar") { foreach (var u in a.Sar.Values) Hold(held, "SarUnit", u); foreach (var x in a.Rafts) Hold(held, "SarRaft", x); }
            if (kind == "smart") Hold(held, "TorpSnap", r.SmartG.Torp);
            if (kind == "flags") foreach (var u in a.FlagUnits) Hold(held, "flag unit", u);
            a = null;
            r.Destroy();
            if (viaSwitch) r.Enter(LifeWorld.MakeB(2000, null), 20);
            else { r.NoMission(100); r.NoMission(103); }
            return held;
        }

        public static string Weak()
        {
            foreach (string kind in new[] { "asw", "cap", "sar", "smart", "identify", "flags" })
                foreach (bool viaSwitch in new[] { false, true })
                {
                    using (var r = new LifeRig())
                    {
                        var held = BuildAndLeave(r, kind, viaSwitch);
                        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                        foreach (var h in held)
                            if (h.Ref.IsAlive) return kind + " keeps " + h.Label + " of the left mission" + (viaSwitch ? " (switch)" : " (menu)");
                        GC.KeepAlive(r);
                    }
                }
            return null;
        }
    }
}
