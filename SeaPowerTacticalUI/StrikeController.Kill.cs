using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoiceBotBridge
{
    // T-342 phase A (ADR-0133, design 6.1-6.2): the own hits of the mission (only what the ledger of the mod saw, never the hidden state of a ship), the prior loss of a ship for the next plan,
    // and the observation of the sinking (strike kill). Main thread only; the game clock only.
    internal sealed partial class StrikeController
    {
        /// <summary>The goal of a new group: sink in the game; the stand of v1 sets breach (the v1 sections have no kill budget).</summary>
        internal static string DefaultGoal = "sink";

        private readonly Dictionary<int, List<StrikeHitSeen>> _hits = new Dictionary<int, List<StrikeHitSeen>>();   // target -> its own hits of this mission
        private readonly HashSet<int> _killLogged = new HashSet<int>();
        private readonly Dictionary<int, string> _killCls = new Dictionary<int, string>();
        private readonly Dictionary<string, double> _waveStartT = new Dictionary<string, double>();
        private double _bonus = StrikeCalib.BonusFallback; private bool _bonusKnown;

        /// <summary>The damage bonus of the difficulty as the game gives it (unknown: the fallback, bonus_src = default).</summary>
        private double Bonus(out bool known)
        {
            double b;
            try { b = _g.DamageBonus(out known); } catch (Exception) { known = false; b = StrikeCalib.BonusFallback; }
            if (!known) b = StrikeCalib.BonusFallback;
            _bonus = b; _bonusKnown = known;
            return b;
        }

        private static StrikeMissile HitMissile(StrikeHitSeen h) { return new StrikeMissile { Ammo = h.Ammo, WarheadPower = h.Power, DudPower = h.Power, DamageMult = h.DamageMult > 0 ? h.DamageMult : 1, Warhead = h.Warhead ?? "normal" }; }

        /// <summary>The sum of the loss (2*D, unweighted) of the own hits seen on the ships; the weight PriorHitWeight is applied by StrikeKillMath.</summary>
        private void ApplyPrior(IList<StrikeShip> ships)
        {
            if (ships == null) return;
            bool kn; double bonus = Bonus(out kn);
            foreach (var s in ships)
            {
                if (s == null) continue;
                List<StrikeHitSeen> l;
                if (!_hits.TryGetValue(s.TrackId, out l) || l.Count == 0) { s.PriorLoss = 0; s.PriorHits = 0; continue; }
                s.PriorHits = l.Count;
                s.PriorLoss = l.Sum(h => StrikeKillMath.HitLoss(HitMissile(h), bonus));
            }
        }

        /// <summary>Once per TickS: the new own hits (strike hit), the first observation of a sunk target (strike kill).</summary>
        private void TickKill(double now)
        {
            IList<StrikeHitSeen> fresh = null;
            try { fresh = _g.NewHits(); } catch (Exception) { }
            if (fresh != null)
                foreach (var h in fresh)
                {
                    List<StrikeHitSeen> l;
                    if (!_hits.TryGetValue(h.TrackId, out l)) { l = new List<StrikeHitSeen>(); _hits[h.TrackId] = l; }
                    l.Add(h);
                    _log("[Strike] strike hit tgt=" + I(h.TrackId) + " msl=" + I(h.WeaponId) + " ammo=" + (h.Ammo ?? "-") + " dud=" + (h.Dud ? "1" : "0") + " ciws=" + (h.CiwsDmg < 0 ? "-1" : F3(h.CiwsDmg)) + " dmg_frac=" + (h.DmgFrac < 0 ? "-1" : F3(h.DmgFrac)) + " t=" + F1(h.T > 0 ? h.T : now));
                }
            foreach (var g in _groups) if (g.Timeline != null && g.Timeline.StartT >= 0) _waveStartT[g.Name] = g.Timeline.StartT;
            var watch = new Dictionary<int, StrikeGroup>();
            foreach (var g in _groups)
            {
                if (g.Target != null) { if (g.Target.MainId != 0 && !watch.ContainsKey(g.Target.MainId)) watch[g.Target.MainId] = g; foreach (int id in g.Target.Ships) if (!watch.ContainsKey(id)) watch[id] = g; }
                if (g.Timeline != null) foreach (var r in g.Timeline.Exec) if (!watch.ContainsKey(r.TargetId)) watch[r.TargetId] = g;
                if (g.Results != null) foreach (var r in g.Results) if (!watch.ContainsKey(r.TrackId)) watch[r.TrackId] = g;
            }
            foreach (var kv in watch)
            {
                int id = kv.Key;
                if (_killLogged.Contains(id)) continue;
                var s = Ship(id);
                if (s != null) _killCls[id] = s.ClassIni ?? s.ClassName;
                string state; int hits;
                try { state = _g.Fate(id, out hits); } catch (Exception) { continue; }
                if (state != "sunk") continue;
                _killLogged.Add(id);
                var g = kv.Value;
                bool led = false; try { led = _g.HitsAvailable; } catch (Exception) { }
                List<StrikeHitSeen> l; _hits.TryGetValue(id, out l);
                double ws; bool hasWs = _waveStartT.TryGetValue(g.Name, out ws);
                int before = -1, inWave = -1, weak = -1;
                if (led)
                {
                    int total = l == null ? 0 : l.Count;
                    inWave = hasWs && l != null ? l.Count(h => h.T >= ws) : total;
                    before = total - inWave;
                    weak = l == null ? 0 : l.Count(h => h.CiwsDmg > 0);
                }
                string cls; _killCls.TryGetValue(id, out cls);
                _log("[Strike] strike kill tgt=" + I(id) + " cls=" + (cls ?? "-") + " t=" + F1(now) + " grp=" + g.Name + " wave=" + I(g.Wave) + " hits_before=" + I(before) + " hits_wave=" + I(inWave) + " weakened=" + I(weak) + " ledger=" + (led ? "on" : "off"));
            }
        }

        private void LogKillPlan(StrikeGroup g, int planId, IList<StrikeVariant> variants, double bonus, bool known)
        {
            for (int vi = 0; vi < variants.Count; vi++)
                foreach (var t in variants[vi].PerTarget)
                    _log("[Strike] strike killplan grp=" + g.Name + " plan=" + I(planId) + " var=" + I(vi) + " tgt=" + I(t.TrackId) + " cls=" + (t.ClassIni ?? t.ClassName ?? "-") + " ammo=" + (t.Ammo ?? "-") + " mode=" + (t.Goal ?? "breach")
                        + " h=" + I(t.H) + " h1=" + I(t.H1) + " M=" + I(t.MSink) + " n=" + I(t.N) + " sigma=" + F3(t.Sigma) + " p_sunk=" + F3(t.PSunk) + " p1=" + F3(t.PBreach) + " B=" + F1(t.Budget) + " B1=" + F1(t.BudgetLeft)
                        + " dmg_bonus=" + F3(bonus) + " bonus_src=" + (known ? "game" : "default"));
        }

        private void ResetKill()
        {
            _hits.Clear(); _killLogged.Clear(); _killCls.Clear(); _waveStartT.Clear();
        }
    }
}
