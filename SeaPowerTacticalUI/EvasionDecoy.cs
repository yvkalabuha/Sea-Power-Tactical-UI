using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    /// <summary>T-286 (ADR-0116): noisemaker spending of the own surface evasion: a reserve for the close range, the rest in the log.</summary>
    internal sealed partial class GameApi
    {
        private readonly TrackLastTti _evLastTti = new TrackLastTti();   // T-297: per (uid, track) the last tti while the mark moved
        private readonly Dictionary<int, double> _evDecoyLastS = new Dictionary<int, double>();   // uid -> mission time of the last mod launch (survives a run restart)

        /// <summary>Sum of Noisemaker rows (chaff not counted); -1 = rows cannot be read.</summary>
        private int EvasionNoisemakersLeft(object u)
        {
            try
            {
                int n = 0;
                foreach (var d in DecoyRows(u)) if (d.Type == "Noisemaker") n += d.Count;
                return n;
            }
            catch { return -1; }
        }

        /// <summary>One decoy step; returns true when a noisemaker was ordered. src = "fresh" | "frozen".</summary>
        private bool EvasionDecoyStep(object u, EvRun run, int trackId, double now, double distNm, bool wakeHoming, double ttiS, string src)
        {
            if (!Capabilities.Contains("launch_decoy")) return false;
            int uid = UidOf(u);
            double mem;
            double last = run.S.LastDecoyS;
            if (_evDecoyLastS.TryGetValue(uid, out mem) && mem > last) last = mem;
            int left = EvasionNoisemakersLeft(u);
            string v = EvasionDecoyMath.Decide(now - last, distNm, wakeHoming, left, ttiS, src == "frozen");
            var inv = CultureInfo.InvariantCulture;
            if (!EvasionDecoyMath.IsLaunch(v))
            {
                if (v == "wake" && !run.DecoyWakeLogged)
                {
                    run.DecoyWakeLogged = true;
                    Log(EvasionDecoyMath.WakeHoldText(uid, trackId, distNm, src));
                }
                else if (v == "reserve" && !run.DecoyHoldLogged)
                {
                    run.DecoyHoldLogged = true;
                    Log("[Evasion] evasion decoy hold uid=" + uid + " reason=reserve left=" + left + " dist=" + distNm.ToString("0.00", inv));
                }
                else if (v == "closing" && src == "frozen" && !run.DecoyFrozenLogged)
                {
                    run.DecoyFrozenLogged = true;
                    Log("[Evasion] evasion decoy hold uid=" + uid + " reason=closing src=frozen tti=" + TtiText(ttiS) + " dist=" + distNm.ToString("0.00", inv));
                }
                else if (v == "closing" && src != "frozen" && !run.DecoyClosingLogged)
                {
                    run.DecoyClosingLogged = true;
                    Log("[Evasion] evasion decoy hold uid=" + uid + " reason=closing tti=" + TtiText(ttiS) + " dist=" + distNm.ToString("0.00", inv));
                }
                else if (v == "empty" && !run.DecoyEmptyLogged)
                {
                    run.DecoyEmptyLogged = true;
                    Log("[Evasion] evasion decoy empty uid=" + uid + " dist=" + distNm.ToString("0.00", inv));
                }
                return false;
            }
            bool launched = false;
            string ammo = "na";
            try
            {
                foreach (var d in DecoyRows(u))
                    if (d.Type == "Noisemaker" && d.Count >= 1) { Call(u, EvasionSpecs.ObjLaunchNoisemaker, d.Name); launched = true; ammo = d.Name; break; }
            }
            catch { }
            run.S.LastDecoyS = now;   // also when none were launched: do not retry every second
            _evDecoyLastS[uid] = now;
            run.DecoyHoldLogged = false;
            if (launched)
                Log("[Evasion] evasion decoy uid=" + uid + " by=mod band=" + v + " src=" + src + " dist=" + distNm.ToString("0.00", inv) + " tti=" + TtiText(ttiS) +
                    " left=" + (left >= 0 ? (left - 1).ToString() : "na") + " reserve=" + EvasionDecoyMath.ReserveN + " ammo=" + ammo);
            return launched;
        }

        private static string TtiText(double ttiS)
        {
            return double.IsInfinity(ttiS) || double.IsNaN(ttiS) ? "inf" : ttiS.ToString("0", CultureInfo.InvariantCulture);
        }

        private void EvasionDecoyClear() { _evDecoyLastS.Clear(); _evLastTti.Clear(); }
    }
}
