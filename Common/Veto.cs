using System;
using System.Collections.Generic;
using System.Reflection;

namespace VoiceBotBridge
{
    /// <summary>
    /// Fire veto (T-064): the body of the Harmony prefix on ObjectBase.AddEngageTask. Only automatic attacks of the
    /// player's own units are examined (orders of the player/bot are never blocked: they already went through "yes").
    /// Never throws: any problem means "fire is allowed". Main thread only.
    /// </summary>
    internal sealed partial class GameApi
    {
        /// <summary>T-333: set by the UI mod (CapFfGuard.Veto); null = none. Called on the game main thread for automatic attacks of own units.</summary>
        internal Func<object, bool> ExtraEngageVeto;
        private double _vetoNeutralsAt = -1;
        private readonly List<double[]> _vetoNeutrals = new List<double[]>();
        private readonly List<int> _vetoNeutralIds = new List<int>();
        private readonly Dictionary<string, double> _vetoEventAt = new Dictionary<string, double>();

        private void SelfCheckVeto()
        {
            var et = FindType("SeaPower.EngageTask");
            var ob = FindType("SeaPower.ObjectBase");
            bool sig = false;
            if (et != null && ob != null)
            {
                foreach (var m in ob.GetMethods(AllInst))
                {
                    if (m.Name != "AddEngageTask") continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == et) sig = true;
                }
            }
            Checks.Add(new Check { Capability = "fire_veto", Member = "SeaPower.ObjectBase.AddEngageTask(EngageTask)", Required = false, Present = sig });
            Has("fire_veto", "SeaPower.EngageTask", "_isAutoAttack", false, false);
            Has("fire_veto", "SeaPower.EngageTask", "_targetObject", false, false);
            Has("fire_veto", "SeaPower.EngageTask", "_baseObject", false, false);
            Has("fire_veto", "SeaPower.EngageTask", "_ammoId", false, false);
        }

        private static string DomainOf(object target)
        {
            for (var t = target == null ? null : target.GetType(); t != null; t = t.BaseType)
            {
                if (t.Name == "Vessel") return "surface";
                if (t.Name == "Submarine") return "submarine";
            }
            return null;
        }

        private void RefreshVetoNeutrals(double now)
        {
            if (now - _vetoNeutralsAt < 0.5 && now >= _vetoNeutralsAt) return;
            _vetoNeutralsAt = now;
            _vetoNeutrals.Clear(); _vetoNeutralIds.Clear();
            var vehicles = Vehicles();
            if (vehicles == null) return;
            foreach (var v in vehicles)
            {
                if (Relation(v) != "neutral" || Kind(v) != "Surface" || IsGone(v)) continue;
                var p = TrackPos(v); if (p == null) continue;
                _vetoNeutrals.Add(p);
                var id = D(Get(v, "Id"));
                _vetoNeutralIds.Add(id.HasValue ? (int)id.Value : -1);
            }
        }

        /// <summary>True = do not add this engage task.</summary>
        internal bool ShouldVetoEngage(object engageTask)
        {
            try
            {
                if (engageTask == null || !BoolOf(Get(engageTask, "_isAutoAttack"))) return false;
                var shooter = Get(engageTask, "_baseObject");
                if (shooter == null || !IsAlive(shooter) || !BoolOf(Get(shooter, "IsPlayerObject"))) return false;
                // T-333 (ADR-0129): an extra veto of the UI mod (friendly-fire guard of CAP aircraft); a throwing delegate means "no veto"
                var x = ExtraEngageVeto;
                if (x != null) { bool v; try { v = x(engageTask); } catch { v = false; } if (v) return true; }
                if (!ResolveFlag(shooter, "veto_neutral")) return false;
                var target = Get(engageTask, "_targetObject");
                if (target == null || !IsAlive(target) || DomainOf(target) == null) return false;
                double now = Clock();
                RefreshVetoNeutrals(now);
                if (_vetoNeutrals.Count == 0) return false;
                double la, lo, h, ta, to;
                if (!OwnPos(shooter, out la, out lo, out h) || !OwnPos(target, out ta, out to, out h)) return false;
                int idx; double dist;
                if (!TacticsMath.ShouldVeto(new[] { la, lo }, new[] { ta, to }, _vetoNeutrals, VetoRadiusNm, out idx, out dist)) return false;
                int? tt = TrackIdOfObject(target);
                string key = UidOf(shooter) + "|" + (tt.HasValue ? tt.Value : -1);
                double last;
                if (!_vetoEventAt.TryGetValue(key, out last) || now - last >= 30.0 || now < last)
                {
                    _vetoEventAt[key] = now;
                    if (EventsAvailable)
                        EvLine("fire_vetoed", "{\"uid\":" + UidOf(shooter) + ",\"name\":" + Json.Str(NameOf(shooter)) + ",\"target_track_id\":" + (tt.HasValue ? tt.Value.ToString() : "null") +
                            ",\"neutral_track_id\":" + _vetoNeutralIds[idx] + ",\"neutral_dist_nm\":" + Json.Num(Math.Round(dist, 2)) + ",\"ammo\":" + Json.Str(Get(engageTask, "_ammoId") as string) + "}");
                }
                return true;
            }
            catch { return false; }
        }
    }
}
