using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace VoiceBotBridge
{
    // T-245 / ADR-0098: the save keeps the axis of the band (not of the frame), the frame is held while the mod steers the ship,
    // the "to row" label of every barrier package, the anchor / radar lines in the journal without Debug. `aswtest t245 <name>`.
    internal static partial class AswT176Tests
    {
        public static readonly string[] Names245 = { "save-band-axis", "evade-frame-held", "load-evading", "row-label", "row-label-far", "row-label-nopos", "row-label-many", "log-always" };

        public static string RunT245(string only = null)
        {
            string e;
            foreach (string n in Names245)
            {
                if (only != null && only != n) continue;
                switch (n)
                {
                    case "save-band-axis": e = SaveBandAxis(); break;
                    case "evade-frame-held": e = EvadeFrameHeld(); break;
                    case "load-evading": e = LoadEvading(); break;
                    case "row-label": e = RowLabel(0); break;
                    case "row-label-far": e = RowLabel(0.4); break;
                    case "row-label-nopos": e = RowLabel(-35); break;
                    case "row-label-many": e = RowLabelMany(); break;
                    case "log-always": e = LogAlways245(); break;
                    default: e = null; break;
                }
                if (e != null) return n + ": " + e;
            }
            if (only != null && Array.IndexOf(Names245, only) < 0) return "unknown scenario " + only;
            return null;
        }

        /// <summary>A ship on the barrier at 167, the mod takes it onto 1.6 (evasion); the route record keeps 167.</summary>
        private static AswController EvadingBarrier(AswFakeGame f, string dir)
        {
            if (dir != null) { f.SaveDir = dir; f.LastSavePath = "/s/a.sav"; }
            f.Tgt.CourseDeg = 167; f.Tgt.RouteBrg = 167;
            var c = new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "auto" });
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            c.SearchCourse(P(Barrier229));
            Run(c, f, 90);
            f.Tgt.ModSteered = true; f.Tgt.SavedCourseDeg = 167; f.Tgt.RouteBrg = null; f.Tgt.CourseDeg = 1.6;
            return c;
        }

        private static string SaveBandAxis()
        {
            string dir = Path.Combine(Path.GetTempPath(), "asw245-" + Guid.NewGuid().ToString("N"));
            try
            {
                var f = CorridorGame(400);
                var c = EvadingBarrier(f, dir);
                Run(c, f, 200);   // long enough for the old code to follow the evasion course
                c.OnGameSaved("/s/b.sav", new SavStamp { ElapsedS = 1100.6, Created = "2026-10-23T02:07:38Z", Src = "sav" });
                foreach (string file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    var m = Regex.Match(File.ReadAllText(file), "\"frame_axis_deg\":([-0-9.eE]+)");
                    if (!m.Success) continue;
                    double v = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    if (!Near(v, 167, 0.5)) return "frame_axis_deg " + v + " (the evasion course, not the band)";
                    return null;
                }
                return "no frame_axis_deg in the save";
            }
            finally { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception) { } }
        }

        private static string EvadeFrameHeld()
        {
            var f = CorridorGame(400);
            var c = EvadingBarrier(f, null);
            Run(c, f, 240);
            if (Logged(f, "reanchor reason=turn") || Logged(f, "reanchor reason=speed")) return "reanchor while the mod steers: " + string.Join(" | ", f.Logs.Where(l => l.Contains("reanchor")).ToArray());
            if (Logged(f, "packet=cancelled")) return "a packet was cancelled";
            return null;
        }

        private static string LoadEvading()
        {
            string dir = Path.Combine(Path.GetTempPath(), "asw245-" + Guid.NewGuid().ToString("N"));
            try
            {
                var f = CorridorGame(400);
                var c = EvadingBarrier(f, dir);
                Run(c, f, 60);
                var stamp = new SavStamp { ElapsedS = 1100.6, Created = "2026-10-23T02:07:38Z", Src = "sav" };
                c.OnGameSaved("/s/b.sav", stamp);
                var g = CorridorGame(400);
                g.SaveDir = dir; g.LastSavePath = "/s/b.sav"; g.Now = 2000;
                g.Tgt.Lat = f.Tgt.Lat; g.Tgt.Lon = f.Tgt.Lon;
                g.Tgt.CourseDeg = 1.6; g.Tgt.ModSteered = true; g.Tgt.SavedCourseDeg = 167;   // the ship is still on the evasion course
                var c2 = new AswController(g, new AswParams(), new AswConfig { RestoreOnLoad = "auto" });
                c2.OnMissionChanged("/s/b.sav", g.Now, stamp);
                for (int i = 0; i < 4; i++) g.Tick(c2);
                if (!Logged(g, "anchor axis=167") || !Logged(g, "src=save")) return "anchor not from the band: " + string.Join(" | ", g.Logs.Where(l => l.Contains("anchor") || l.Contains("frame")).ToArray());
                Run(c2, g, 120);
                if (Logged(g, "reanchor reason=turn")) return "reanchor turn after the load";
                if (Logged(g, "packet=cancelled")) return "a packet was cancelled";
                return null;
            }
            finally { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception) { } }
        }

        private static List<UnitActionLabel> Labels(AswController c)
        {
            var l = new List<UnitActionLabel>();
            ((IUnitActionSource)c).CollectActions(0, l);
            return l;
        }

        /// <summary>The aircraft far from the first point gets "to row N/M" until it arrives, then "laying buoys".</summary>
        private static string RowLabel(double farDeg)
        {
            var f = CorridorGame(400);
            f.DropDelayS = 600;
            var h = f.Mem[0]; h.Lat = 35 + farDeg; h.Lon = farDeg == -35 ? 0 : 20;   // -35: the game gave no position (0,0) f.FixPos.Add(h.Uid);
            var c = StartCourse(f, Barrier229);
            if (c == null) return "order refused";
            for (int i = 0; i < 20; i++)
            {
                Run(c, f, 1);
                var l = Labels(c).Where(x => x.Uid == 1).ToList();
                if (f.Drops.Count == 0) continue;
                if (l.Count != 1) return "labels " + l.Count;
                if (l[0].PhaseKey != "ua.ph.asw.barrier_row") return "t=" + i + " phase " + l[0].PhaseKey + " drops=" + f.Drops.Count;
            }
            if (f.Drops.Count == 0) return "no package";
            return null;
        }

        /// <summary>Three aircraft, several rows, 20 minutes: the phases seen (diagnosis).</summary>
        private static string RowLabelMany()
        {
            var f = CorridorGame(400, 400, 400);
            f.DropDelayS = 150;
            foreach (var h in f.Mem) { h.Lat = 35.2; h.Lon = 20; f.FixPos.Add(h.Uid); }
            var c = StartCourse(f, "{\"tactics\":[\"barrier\"],\"barrier_rows\":3}");
            if (c == null) return "order refused";
            var seen = new Dictionary<string, int>();
            for (int i = 0; i < 1200; i++)
            {
                Run(c, f, 1);
                foreach (var l in Labels(c)) { string k = l.PhaseKey ?? "-"; int n; seen.TryGetValue(k, out n); seen[k] = n + 1; }
            }
            return seen.ContainsKey("ua.ph.asw.barrier_row") ? null : "no barrier_row phase";
        }

        private static string LogAlways245()
        {
            string[] yes =
            {
                "asw radar on uid=3 list=surface", "asw radar restore uid=3", "asw radar player_off uid=3", "asw radar none uid=3",
                "asw pattern ASW-1 barrier anchor axis=145 o=35.0,20.0 v=15 R=2.7 src=save", "asw pattern ASW-1 reanchor reason=turn d_axis=30 lat_nm=0.1 packet=cancelled",
            };
            foreach (string l in yes) if (!AswMath.LogAlways(l)) return "not always: " + l;
            if (AswMath.LogAlways("asw frame ASW-1 axis=0 src=heading")) return "frame line is always";
            return null;
        }
    }
}
