using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-176 C9 / ADR-0072 s.3: the lanes of a group (who flies which tactic / side) and the hand-over to the reserve.
    internal sealed partial class AswController
    {

        private LaneMember LaneOf(AswMember m)
        {
            int buoys = AswMath.SearchBuoys(m);
            return new LaneMember { Uid = m.Uid, Dip = m.Dipping, Mad = m.Mad, Buoys = buoys > 0, BuoyCount = buoys, Air = m.Where == "air", SpeedKn = SpeedOf(m), RemainS = AswMath.RemainS(m, null), ReadyInS = AswMath.ReadyS(m), EtaS = m.Where == "air" ? 0 : AswMath.ReadyS(m) + m.LaunchS };
        }

        /// <summary>
        /// ADR-0076 s.4 / ADR-0072 s.3: the aircraft that hold nothing yet (<paramref name="fresh"/>) take the tactics nobody holds (one each, a specialist first),
        /// then the rest of them go to the fitting tactic with the least weight so far (the kept holders count). An aircraft that fits nothing is "reserve".
        /// </summary>
        private Dictionary<int, string> AssignFresh(Grp g, List<AswMember> fresh, List<string> want, Dictionary<int, string> keep)
        {
            var res = new Dictionary<int, string>();
            var ms = new List<LaneMember>();
            foreach (var m in fresh) if (!Excluded(m)) ms.Add(LaneOf(m)); else res[m.Uid] = "reserve";
            var first = AswPatternMath.AssignLanes(ms, want);   // one aircraft per tactic nobody holds
            foreach (var kv in first) res[kv.Key] = kv.Value;
            var load = new Dictionary<string, double>();
            foreach (string t in g.Tactics) load[t] = 0;
            foreach (var kv in keep)
            {
                AswMember km; double w = 1;
                if (_live.TryGetValue(kv.Key, out km)) w = AswPatternMath.Weight(SpeedOf(km), AswMath.RemainS(km, null));
                if (load.ContainsKey(kv.Value)) load[kv.Value] += w;
            }
            foreach (var kv in first) { AswMember fm; if (_live.TryGetValue(kv.Key, out fm) && load.ContainsKey(kv.Value)) load[kv.Value] += AswPatternMath.Weight(SpeedOf(fm), AswMath.RemainS(fm, null)); }
            foreach (var lm in ms)
            {
                if (res.ContainsKey(lm.Uid)) continue;
                string pick = null;
                foreach (string t in g.Tactics)
                {
                    bool fits = t == "dip" ? lm.Dip : t == "mad" ? lm.Mad : lm.Buoys;
                    if (fits && (pick == null || load[t] < load[pick] - 1e-9)) pick = t;
                }
                if (pick == null) { res[lm.Uid] = "reserve"; continue; }
                res[lm.Uid] = pick; load[pick] += AswPatternMath.Weight(lm.SpeedKn, lm.RemainS);
            }
            // T-181 (ADR-0080 s.5): with the deck reserve on (AswMath.ReserveOnDeck) the [Asw] Standby machines that are ready last stay on the deck; by default every machine flies
            int usable = keep.Count + res.Count, holders = 0;
            foreach (var v in keep.Values) if (v != "reserve") holders++;
            foreach (var v in res.Values) if (v != "reserve") holders++;
            int excess = holders - AswMath.HolderCap(g.Tactics.Count, usable, g.P.Standby);
            if (excess > 0)
            {
                var grounded = new List<LaneMember>();
                foreach (var lm in ms) if (!lm.Air && res.ContainsKey(lm.Uid) && res[lm.Uid] != "reserve") grounded.Add(lm);
                grounded.Sort((x, y) => y.ReadyInS.CompareTo(x.ReadyInS));
                foreach (var lm in grounded) { if (excess-- <= 0) break; res[lm.Uid] = "reserve"; }
            }
            return res;
        }

        /// <summary>Journal (ADR-0076 s.9): the lanes of the group, one line per tactic: how many aircraft fly it and their weight.</summary>
        private void LogLanes(Grp g, Dictionary<int, string> assign)
        {
            foreach (string t in g.Tactics)
            {
                int k = 0; double w = 0;
                foreach (var kv in assign)
                {
                    AswMember m;
                    if (kv.Value != t || !_live.TryGetValue(kv.Key, out m)) continue;
                    k++; w += AswPatternMath.Weight(SpeedOf(m), AswMath.RemainS(m, null));
                }
                _game.Log("asw lanes " + g.Name + " " + t + " k=" + I(k) + " w=" + N(Math.Round(w)));
            }
        }
    }
}
