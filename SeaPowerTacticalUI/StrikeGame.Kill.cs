using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using VoiceBotBridge.Ui;

namespace VoiceBotBridge
{
    // T-342 phase A (ADR-0133): the game side of the sinking model. The damage bonus of the difficulty (HitEstimate reads the same member), the own hits the counter of the mod saw (missiles on ships only: the ledger
    // of HitEstimate, never the hidden state of a ship) and the speed step of the run in (the throttle through the same steer layer as the retreat helper, the step before is remembered).
    internal sealed partial class GameApi
    {
        private sealed partial class StrikeGameImpl
        {
            private int _hitCursor;
            private readonly StrikeHitFilter _hitFilter = new StrikeHitFilter();   // T-363: the tracks seen as surface ships, alive or already destroyed
            private readonly Dictionary<int, int> _speedMem = new Dictionary<int, int>();   // uid -> the throttle step before the mod set one (1-based)

            /// <summary>Globals._damageBonus (0.5 / 0.35 / 0.25 / 0.1 / 0 by the difficulty); not readable = the fallback of the plan and known = false.</summary>
            public double DamageBonus(out bool known)
            {
                try
                {
                    double b = _a.HeDamageBonus(out known);
                    if (known && b >= 0 && b <= 2) return b;
                }
                catch (Exception) { }
                known = false;
                return StrikeCalib.BonusFallback;
            }

            /// <summary>The counter of the mod works (the ledger exists: [HitEstimate] Enabled + Counter).</summary>
            public bool HitsAvailable { get { return _a.HeLedger != null; } }

            /// <summary>The own hits added to the ledger since the last call: a missile on a surface track of the table. Empty without the ledger.</summary>
            public IList<StrikeHitSeen> NewHits()
            {
                var res = new List<StrikeHitSeen>();
                try
                {
                    var led = _a.HeLedger;
                    if (led == null) return res;
                    var hits = led.Since(ref _hitCursor);
                    if (hits.Count == 0) return res;
                    Ships();   // refreshes the filter; a ship that sank in this very tick is no longer in the list, its hits are kept (T-363)
                    foreach (var h in hits)
                    {
                        if (!_hitFilter.Keep(h.Kind == HeAmmoKind.Missile, h.TrackId)) continue;
                        var ammo = _a.HeAmmoByFile(h.AmmoFile);
                        double power = ammo == null ? 0 : (h.Dud ? ammo.DudPower : ammo.Power);
                        res.Add(new StrikeHitSeen
                        {
                            TrackId = h.TrackId, WeaponId = h.WeaponId, Ammo = h.AmmoFile, Power = power, DamageMult = ammo != null && ammo.DamageMult > 0 ? ammo.DamageMult : 1,
                            Warhead = h.Warhead == HeWarhead.Heat ? "heat" : h.Warhead == HeWarhead.Frag ? "frag" : h.Warhead == HeWarhead.Cluster ? "cluster" : "normal",
                            T = h.T, CiwsDmg = h.CiwsDmg, DmgFrac = h.DmgFrac, Dud = h.Dud,
                        });
                    }
                }
                catch (Exception) { }
                return res;
            }

            /// <summary>The throttle step nearest above kn (else the highest); the step before is remembered for RestoreSpeed. null = ok; helo = a helicopter has no steps.</summary>
            public string SetSpeed(int uid, double kn)
            {
                try
                {
                    var u = _a.FindOwnUnit(uid);
                    if (u == null) return "unit_not_found";
                    if (_a.IsHelicopter(u)) return "helo";
                    var st = _a.AgSteerGame();
                    var steps = st.ThrottleSteps(uid);
                    if (steps == null || steps.Count == 0) return "no_steps";
                    CapStep pick = steps[steps.Count - 1];
                    foreach (var s in steps) if (s.Kn >= kn) { pick = s; break; }   // ascending by speed
                    int before = st.Throttle(uid);
                    string err = st.SetThrottle(uid, pick.Step);
                    if (err == null && !_speedMem.ContainsKey(uid) && before > 0) _speedMem[uid] = before;
                    _a.StrikeLog("[Strike] strike speed step uid=" + uid.ToString(CultureInfo.InvariantCulture) + " kn=" + kn.ToString("0", CultureInfo.InvariantCulture) + " step=" + pick.Step.ToString(CultureInfo.InvariantCulture) + " step_kn=" + pick.Kn.ToString("0", CultureInfo.InvariantCulture)
                                 + " before=" + before.ToString(CultureInfo.InvariantCulture) + " -> " + (err ?? "ok"));
                    return err;
                }
                catch (Exception e) { return "speed_failed:" + e.GetType().Name; }
            }

            /// <summary>The step before the mod set one; "none" = nothing was set.</summary>
            public string RestoreSpeed(int uid)
            {
                try
                {
                    int prev;
                    if (!_speedMem.TryGetValue(uid, out prev)) return "none";
                    _speedMem.Remove(uid);
                    var err = _a.AgSteerGame().SetThrottle(uid, prev);
                    _a.StrikeLog("[Strike] strike speed step uid=" + uid.ToString(CultureInfo.InvariantCulture) + " restore=" + prev.ToString(CultureInfo.InvariantCulture) + " -> " + (err ?? "ok"));
                    return err;
                }
                catch (Exception e) { return "speed_failed:" + e.GetType().Name; }
            }
        }
    }
}
