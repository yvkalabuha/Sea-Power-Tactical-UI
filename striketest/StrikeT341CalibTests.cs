using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace VoiceBotBridge
{
    // T-341 phase E1 stand sections "t341 <scenario>": the difficulty in the plan, the "strike calib" line of a launch, diff= / gid= of an order, the view fields of the calibration.
    internal static class StrikeT341CalibTests
    {
        private static Dictionary<string, object> C(params object[] kv) { return StrikeT341CtlTests.C(kv); }

        public static readonly string[] Names = { "calib-diff", "calib-log", "calib-view" };

        public static string Dispatch(string n)
        {
            switch (n)
            {
                case "calib-diff": return CalibDiff();
                case "calib-log": return CalibLog();
                case "calib-view": return CalibView();
            }
            return "unknown scenario";
        }

        private static StrikeFakeGame World()
        {
            var g = new StrikeFakeGame();
            for (int i = 1; i <= 2; i++) g.AddPlane(i, "usn_a-6e", "usn_agm-84c", 4, 39.5 - 0.02 * i, 10 + 0.02 * i);
            g.AddKidd(100, 41.0, 10.0);
            return g;
        }

        private static StrikeController Ctl(StrikeFakeGame g, List<string> log)
        {
            var c = new StrikeController(g, new StrikeParams(), log.Add);
            c.Group(C("op", "create", "group", "A", "uids", new[] { 1, 2 }));
            c.Target(C("group", "A", "target_id", 100));
            return c;
        }

        private static string CalibDiff()
        {
            var g1 = World(); var l1 = new List<string>(); var c1 = Ctl(g1, l1);
            if (StrikeController.IsErr(c1.Plan(C("group", "A")))) return "plan 1";
            var v1 = c1.Groups[0].Variants[0];
            var g2 = World(); g2.DifficultyValue = 0.8; g2.DiffSrcValue = "game"; var l2 = new List<string>(); var c2 = Ctl(g2, l2);
            if (StrikeController.IsErr(c2.Plan(C("group", "A")))) return "plan 2";
            var v2 = c2.Groups[0].Variants[0];
            int n1 = v1.PerTarget[0].N, n2 = v2.PerTarget[0].N;
            if (n1 != 8) return "difficulty 1: kidd needs 8, got " + n1;
            if (n2 != 7) return "difficulty 0.8: kidd needs 7, got " + n2;
            if (!l2.Any(l => l.StartsWith("[Strike] strike plan A id=1", StringComparison.Ordinal) && l.Contains(" diff=0.8 ") && l.EndsWith(" calib=" + StrikeCalib.CalibId, StringComparison.Ordinal))) return "plan log: " + string.Join(" | ", l2);
            if (!l1.Any(l => l.Contains(" diff=1 "))) return "plan log diff=1";
            // the zones of the target are in the variant, with the scale
            var z = v2.PerTarget[0].Zones;
            if (z.Count != 1 || !z[0].IsTarget || z[0].TrackId != 100 || z[0].ClassIni != "usn_ddg_kidd" || Math.Abs(z[0].Scale - 0.8 * StrikeCalib.KScale) > 1e-9) return "zones snapshot " + z.Count;
            return null;
        }

        private static Dictionary<string, string> Fields(string line)
        {
            var d = new Dictionary<string, string>();
            foreach (string tok in line.Split(' '))
            {
                int eq = tok.IndexOf('=');
                if (eq > 0 && !d.ContainsKey(tok.Substring(0, eq))) d[tok.Substring(0, eq)] = tok.Substring(eq + 1);
            }
            return d;
        }

        private static string CalibLog()
        {
            var game = World(); game.DifficultyValue = 0.8; game.GameIds[1] = 1001; game.GameIds[2] = 1002;
            var log = new List<string>(); var c = Ctl(game, log);
            if (StrikeController.IsErr(c.Plan(C("group", "A")))) return "plan";
            string r = c.Launch(C("group", "A"));
            if (StrikeController.IsErr(r)) return "launch " + r;
            var lines = log.Where(l => l.StartsWith("[Strike] strike calib ", StringComparison.Ordinal)).ToList();
            if (lines.Count != c.Groups[0].Variants[c.Groups[0].Chosen].PerTarget.Count) return "one calib line per target: " + lines.Count;
            var f = Fields(lines[0]);
            string[] keys = { "t", "plan", "grp", "wave", "tgt", "cls", "ammo", "sup", "n", "p", "sig", "diff", "calib", "ew", "clr", "zones" };
            foreach (string k in keys) if (!f.ContainsKey(k)) return "calib field " + k + ": " + lines[0];
            if (f["tgt"] != "100" || f["cls"] != "usn_ddg_kidd" || f["n"] != "7" || f["diff"] != "0.8" || f["calib"] != StrikeCalib.CalibId || f["t"] != "1000.0") return "calib fields: " + lines[0];
            string[] z = f["zones"].Split(';');
            if (z.Length != 1 || !z[0].StartsWith("100/usn_ddg_kidd/", StringComparison.Ordinal) || !z[0].EndsWith("/0.8/1/-", StringComparison.Ordinal)) return "zones: " + f["zones"];
            if (!log.Any(l => l.StartsWith("[Strike] strike launch A ", StringComparison.Ordinal) && l.EndsWith(" t=1000.0", StringComparison.Ordinal))) return "launch line t=";
            // the order: diff= gid= t=
            for (double s = 0; s < 3000 && !log.Any(l => l.StartsWith("[Strike] strike issue uid=", StringComparison.Ordinal)); s++) { game.Advance(1); c.Tick(game.Time); }
            var iss = log.FirstOrDefault(l => l.StartsWith("[Strike] strike issue uid=", StringComparison.Ordinal));
            if (iss == null) return "no issue line";
            if (!Regex.IsMatch(iss, @" diff=0\.8 gid=100[12] t=\d+(\.\d)?$")) return "issue diff/gid/t: " + iss;
            return null;
        }

        private static string CalibView()
        {
            var game = World(); game.DifficultyValue = 0.8; game.DiffSrcValue = "game";
            var c = Ctl(game, new List<string>());
            var o = StrikeT341CtlTests.Obj(c.ViewJson(20));
            var calib = (Dictionary<string, object>)o["calib"];
            if (!calib.ContainsKey("difficulty") || Math.Abs((double)calib["difficulty"] - 0.8) > 1e-9) return "difficulty " + o["calib"];
            if (calib["diff_src"] as string != "game" || calib["calib_id"] as string != StrikeCalib.CalibId || Math.Abs((double)calib["k_scale"] - StrikeCalib.KScale) > 1e-9) return "calib fields";
            return null;
        }
    }
}
