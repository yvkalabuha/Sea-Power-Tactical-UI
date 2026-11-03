using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>
    /// Kresta-II pass (stand only): the ship turns away from a pure-pursuit torpedo with EvasionMath.CourseAwaySticky,
    /// every second AntiTorpedoMath.Decide picks a salvo from the RBU mounts (arcs of wp_bpk_kresta2.ini).
    /// </summary>
    internal static class AtwSim
    {
        private sealed class Mount
        {
            public string Type; public double From, To, ReadyAtS;
        }

        private sealed class Result
        {
            public readonly Dictionary<string, int> Salvos = new Dictionary<string, int>();
            public readonly List<double> THit = new List<double>();
            public int Total;
        }

        private const double ShipKn = 32, TorpKn = 45, TurnDegPerS = 3, StartNm = 3, RocketKn = 600;

        private static Result Run(double aspectDeg, bool track, bool useAllocate = false)
        {
            var mounts = new List<Mount>
            {
                new Mount { Type = "rbu6000", From = -115, To = 15 }, new Mount { Type = "rbu6000", From = -35, To = 137 },
                new Mount { Type = "rbu1000", From = -179, To = -15 }, new Mount { Type = "rbu1000", From = 15, To = 179 },
            };
            var reload = new Dictionary<string, double> { { "rbu6000", 60 }, { "rbu1000", 64 } };
            var kill = new Dictionary<string, double> { { "rbu6000", 0.25 }, { "rbu1000", 0.40 } };
            var minNm = new Dictionary<string, double> { { "rbu6000", 0.19 }, { "rbu1000", 0.054 } };
            var maxNm = new Dictionary<string, double> { { "rbu6000", 3.0 }, { "rbu1000", 0.54 } };
            var res = new Result { };
            double heading = 0, sx = 0, sy = 0;
            double rad = aspectDeg * Math.PI / 180.0;
            double tx = StartNm * Math.Sin(rad), ty = StartNm * Math.Cos(rad);
            var st = new EvState();
            var win = new AtwWindows();
            for (int t = 0; t < 1500; t++)
            {
                double dx = tx - sx, dy = ty - sy;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist < 0.03) break;
                double brg = EvasionMath.Norm360(Math.Atan2(dx, dy) * 180.0 / Math.PI);
                double rel = EvasionMath.Diff(heading, brg);
                var inp = new AtwInput { Enabled = true, HasTrack = track, TrackId = 1, NowS = t, DistNm = dist, ReactionS = 3, SafetyNm = 0.15 };
                foreach (string ty2 in new[] { "rbu6000", "rbu1000" })
                {
                    bool ready = false;
                    foreach (Mount m in mounts)
                        if (m.Type == ty2 && m.ReadyAtS <= t && AntiTorpedoMath.InArc(rel, m.From, m.To)) ready = true;
                    var opt = new AtwOption { File = ty2, Display = ty2, Kill = kill[ty2], MinNm = minNm[ty2], MaxNm = maxNm[ty2], SpeedKn = RocketKn, Count = 100, MountReady = ready };
                    for (int mi = 0; mi < mounts.Count; mi++)
                        if (mounts[mi].Type == ty2 && mounts[mi].ReadyAtS <= t && AntiTorpedoMath.InArc(rel, mounts[mi].From, mounts[mi].To)) opt.MountKeys.Add(mi + 1);
                    inp.Options.Add(opt);
                }
                AtwDecision d = AntiTorpedoMath.Decide(inp, win);
                if (useAllocate)
                {
                    // T-145 S8: the same pass through Allocate (one track, MaxTracks 1) must give the same decisions
                    var tin = new AtwTrackIn { TrackId = inp.TrackId, DistNm = inp.DistNm };
                    tin.Options.AddRange(inp.Options);
                    tin.Cands.Add(new AtwThreatCand { Uid = 1, IsShooter = true, DistNm = dist });
                    var rin = new AtwRoundIn { ShooterUid = 1, Role = "evade", NowS = t, ReactionS = 3, SafetyNm = 0.15, MaxTracks = 1, FireNm = 3 };
                    if (track) rin.Tracks.Add(tin);
                    var al = AntiTorpedoMath.Allocate(rin, win, new AtwMounts());
                    d = new AtwDecision();
                    if (al.Count == 1 && al[0].Fire) { d.Fire = true; d.Ammo = al[0].Ammo; d.THitS = al[0].THitS; }
                }
                if (d.Fire)
                {
                    foreach (Mount m in mounts)
                        if (m.Type == d.Ammo.File && m.ReadyAtS <= t && AntiTorpedoMath.InArc(rel, m.From, m.To)) { m.ReadyAtS = t + reload[m.Type]; break; }
                    win.Note(1, d.THitS);
                    res.THit.Add(d.THitS);
                    int n; res.Salvos.TryGetValue(d.Ammo.File, out n);
                    res.Salvos[d.Ammo.File] = n + 1;
                    res.Total++;
                }
                // ship: turn towards the evasion course, then move; torpedo: pure pursuit
                double course = EvasionMath.CourseAwaySticky(heading, new EvTorp { TrackId = 1, BrgDeg = brg, DistNm = dist }, null, st);
                double turn = Math.Max(-TurnDegPerS, Math.Min(TurnDegPerS, EvasionMath.Diff(heading, course)));
                heading = EvasionMath.Norm360(heading + turn);
                double hr = heading * Math.PI / 180.0, step = ShipKn / 3600.0;
                sx += Math.Sin(hr) * step; sy += Math.Cos(hr) * step;
                double tstep = TorpKn / 3600.0;
                dx = sx - tx; dy = sy - ty;
                double nd = Math.Sqrt(dx * dx + dy * dy);
                if (nd > 1e-9) { tx += dx / nd * tstep; ty += dy / nd * tstep; }
            }
            return res;
        }

        private static int Count(Result r, string type) { int n; r.Salvos.TryGetValue(type, out n); return n; }

        /// <summary>S8: salvos by aspect and the expected hit times are equal for Decide and for Allocate with MaxTracks = 1.</summary>
        public static string CompareAllocateWithDecide()
        {
            foreach (double aspect in new[] { 0.0, 90, 120, 165 })
            {
                Result a = Run(aspect, true), b = Run(aspect, true, true);
                if (a.Total != b.Total) return "aspect " + aspect + ": salvos " + a.Total + " (Decide) vs " + b.Total + " (Allocate)";
                foreach (string ammo in new[] { "rbu6000", "rbu1000" })
                    if (Count(a, ammo) != Count(b, ammo)) return "aspect " + aspect + ": " + ammo + " salvos differ";
                for (int i = 0; i < a.THit.Count; i++)
                    if (Math.Abs(a.THit[i] - b.THit[i]) > 1e-9) return "aspect " + aspect + ": T_hit differs";
            }
            return null;
        }

        public static string SelfTest()
        {
            var aspects = new[] { new KeyValuePair<string, double>("bow", 0), new KeyValuePair<string, double>("beam", 90), new KeyValuePair<string, double>("quarter", 120), new KeyValuePair<string, double>("stern", 165) };
            foreach (var a in aspects)
            {
                Result r = Run(a.Value, true);
                foreach (string ammo in new[] { "rbu6000", "rbu1000" })
                    Console.WriteLine("atw sim " + ammo + " " + a.Key + " salvos=" + Count(r, ammo));
                for (int i = 1; i < r.THit.Count; i++)
                    if (r.THit[i] - r.THit[i - 1] < AtwWindows.ImmunityS + AtwWindows.SpreadS - 1e-9)
                        return a.Key + ": two salvos with T_hit closer than 13 s";
                if (a.Key == "bow" && Count(r, "rbu6000") < 1) return "bow: expected >= 1 RBU-6000 salvo";
                if (a.Key == "beam" && Count(r, "rbu6000") < 1) return "beam: expected >= 1 RBU-6000 salvo";
                if (a.Key == "stern" && Count(r, "rbu6000") != 0) return "stern: RBU-6000 must not fire (rear arcs only)";
                // a pure stern chase ends exactly astern (180 deg), in the 2-degree gap between the RBU-1000 arcs: no salvo is possible
                if (a.Key == "stern" && Count(r, "rbu1000") != 0) return "stern: RBU-1000 salvo in the astern gap is not expected";
            }
            if (Run(0, false).Total != 0) return "no track: expected 0 salvos";
            Console.WriteLine("atw sim: windows keep >= 13 s between expected hits");
            return null;
        }
    }
}
