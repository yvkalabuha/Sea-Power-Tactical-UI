using System;
using System.Collections;
using System.Collections.Generic;
using VoiceBotBridge.Ui;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-168 / ADR-0064 (supplement 5): game side of the hits-to-kill estimate for the PLAYER'S OWN armament. The truth about the armament of a unit is read only for the
    /// player's own side: every unit passes RrIsOwnSide(u, false) before any read (the scanner checks it). Candidates = ALL own carriers of the mission:
    /// the launchers and magazines of ships and submarines (rounds left), the loadouts of the aircraft types in the hangars and on the decks (the loadout rows of the
    /// ASW / CAP windows, IAirOps.Loadouts, times the airframes in the hangar), the current loads of the own aircraft in the air. Foreign units live in
    /// HitEstimateGame.Class.cs. Scanner: tests/test_sea_power_hit_estimate_static.py.
    /// </summary>
    internal sealed partial class GameApi
    {
        private readonly Dictionary<string, HeAmmo> _heAmmo = new Dictionary<string, HeAmmo>();
        private IList<HeCandidate> _heCands;
        private int _heCandsAt;
        private bool _heLoggedNoBonus;
        private const double HeUnityPerMetre = 0.0148809375;

        internal void HeLeaveMissionOwn() { _heAmmo.Clear(); _heCands = null; }

        /// <summary>Parameters of an own ammunition by file: the cache (filled by the scan and by the hit hook), else the game's shared parameters of the file. null = unknown.</summary>
        internal HeAmmo HeAmmoByFile(string file)
        {
            if (string.IsNullOrEmpty(file)) return null;
            HeAmmo a;
            if (_heAmmo.TryGetValue(file, out a)) return a;
            try
            {
                if (!Present(HitEstimateSpecs.AmmoGetShared)) return null;
                var ap = CallStatic(HitEstimateSpecs.AmmoGetShared, file);
                a = ap == null ? null : HeAmmoOf(ap);
            }
            catch { a = null; }
            _heAmmo[file] = a;
            return a;
        }

        internal double HeDamageBonus(out bool known)
        {
            var v = D(GetStatic(HitEstimateSpecs.GlobalsDamageBonus));
            known = v.HasValue;
            if (!known && !_heLoggedNoBonus) { _heLoggedNoBonus = true; Log("[HitEstimate] Globals._damageBonus is missing: bonus 0 (a cautious estimate)"); }
            return v ?? 0;
        }

        /// <summary>The ammunition parameters of an ap that belongs to a unit or a weapon of the player (the caller has passed the side check); null = no useful damage.</summary>
        private HeAmmo HeAmmoOf(object ap)
        {
            if (ap == null) return null;
            string file = Get(ap, RangeRingSpecs.AmmoFileName) as string;
            if (string.IsNullOrEmpty(file)) return null;
            HeAmmo cached;
            if (_heAmmo.TryGetValue(file, out cached) && cached != null) return cached;
            var type = RrOwnAmmoType(ap);   // 2026-10-22: a buoy / fuel tank is _type Bomb in the game: the sub-type names it
            HeAmmoKind kind;
            switch (type)
            {
                case RingAmmoType.Missile: kind = HeAmmoKind.Missile; break;
                case RingAmmoType.Torpedo: kind = HeAmmoKind.Torpedo; break;
                case RingAmmoType.Asroc: kind = HeAmmoKind.Torpedo; break;
                case RingAmmoType.Bomb: case RingAmmoType.Cluster: kind = HeAmmoKind.Bomb; break;
                case RingAmmoType.AerialRocket: case RingAmmoType.Mlrs: kind = HeAmmoKind.Rocket; break;
                case RingAmmoType.Rbu: kind = HeAmmoKind.DepthCharge; break;
                case RingAmmoType.AirDepthCharge: kind = HeAmmoKind.DepthCharge; break;
                default: return null;   // guns, countermeasures, buoys, fuel tanks, MOSS, paratroopers, pods: not modelled
            }
            object src = ap;   // the parameters that carry the warhead: the payload of an ASROC / a missile-torpedo, else the ammunition itself
            double power = D(Get(ap, HitEstimateSpecs.AmmoWarheadPower)) ?? 0;
            if (BoolOf(Get(ap, HitEstimateSpecs.AmmoHasSubmunition)) && power <= 0 && Present(HitEstimateSpecs.AmmoGetShared))
            {
                string subFile = Get(ap, HitEstimateSpecs.AmmoSubmunitionFile) as string;
                var sub = string.IsNullOrEmpty(subFile) ? null : CallStatic(HitEstimateSpecs.AmmoGetShared, subFile);
                if (sub == null) return null;
                src = sub;
                power = D(Get(sub, HitEstimateSpecs.AmmoWarheadPower)) ?? 0;
                if (RrOwnAmmoType(sub) == RingAmmoType.Torpedo) kind = HeAmmoKind.Torpedo;
            }
            if (!(power > 0)) return null;
            HeWarhead wh;
            switch (StrOrNull(Get(src, HitEstimateSpecs.AmmoWarheadType)))
            {
                case "HighExplosive": case "ArmorPiercing": wh = HeWarhead.Normal; break;
                case "HEAT": wh = HeWarhead.Heat; break;
                case "Cluster": wh = HeWarhead.Cluster; break;
                case "Fragmentation": wh = HeWarhead.Frag; break;
                default: return null;   // none, illumination, runway cratering: no damage to a unit
            }
            var a = new HeAmmo { File = file, Kind = kind, Warhead = wh, Power = power };
            a.DudPower = D(Get(src, HitEstimateSpecs.AmmoDudPower)) ?? power * (kind == HeAmmoKind.Missile ? 0.5 : 0.2);
            var mult = D(Get(src, HitEstimateSpecs.AmmoDamageMult));
            a.DamageMult = mult.HasValue && mult.Value > 0 ? mult.Value : 1;
            a.RadiusM = (D(Get(src, HitEstimateSpecs.AmmoImpactSize)) ?? 0) / HeUnityPerMetre;
            string pen = StrOrNull(Get(src, HitEstimateSpecs.AmmoPenetration));
            a.Penetration = string.IsNullOrEmpty(pen) ? "minor" : pen.ToLowerInvariant();
            string name = Get(ap, HitEstimateSpecs.AmmoDisplayedName) as string;
            a.Name = string.IsNullOrWhiteSpace(name) || name == "MISSING TEXT" ? file : name.Trim();
            var t1 = RrTarget(Get(ap, RangeRingSpecs.AmmoTargetType));
            var t2 = RrTarget(Get(ap, RangeRingSpecs.AmmoSecondaryTargetType));
            a.Surface = t1 == RingTarget.Asuw || t2 == RingTarget.Asuw;
            a.Subsurface = t1 == RingTarget.Asw || t2 == RingTarget.Asw;
            var no = Get(ap, HitEstimateSpecs.AmmoCannotAttack) as IEnumerable;   // the game's _canNotAttackTypes: object types this ammunition never engages
            if (no != null)
                foreach (var t in no)
                {
                    string tn = t == null ? null : t.ToString();
                    if (tn == "Vessel") a.NoShip = true;
                    else if (tn == "Submarine") a.NoSub = true;
                }
            var land = Get(ap, HitEstimateSpecs.AmmoLandAttack) as IEnumerable;
            if (land != null)
                foreach (var l in land)
                {
                    string n = l == null ? null : l.ToString();
                    if (n == "Installation") a.LandInstall = true;
                    else if (n == "Mobile") a.LandMobile = true;
                }
            a.Land = a.LandInstall || a.LandMobile;
            _heAmmo[file] = a;
            return a;
        }

        /// <summary>ALL own carriers of the mission (cached for 1.5 s of real time: the Overview asks once per rebuild per expanded row).</summary>
        internal IList<HeCandidate> HeCandidates()
        {
            int now = Environment.TickCount;
            if (_heCands != null && unchecked(now - _heCandsAt) < 1500) return _heCands;
            var res = new List<HeCandidate>();
            try
            {
                var tf = GetStatic(RangeRingSpecs.GlobalsPlayerTaskforce);
                var lists = new[] { RangeRingSpecs.TfVessels, RangeRingSpecs.TfSubmarines, RangeRingSpecs.TfAircraft, RangeRingSpecs.TfHelicopters };
                for (int i = 0; i < lists.Length; i++)
                {
                    var list = tf == null ? null : Get(tf, lists[i]) as IEnumerable;
                    if (list == null) continue;
                    foreach (var u in list) HeUnitCandidates(u, i >= 2, res);
                }
                HeHangarCandidates(res);
            }
            catch (Exception e) { Log("[HitEstimate] candidates failed: " + e.GetType().Name + ": " + e.Message); }
            _heCands = res; _heCandsAt = now;
            return res;
        }

        /// <summary>The ammunition groups of one own unit with the rounds left. A ship or a submarine: its name; an aircraft in the air: its class (the loads of the same class merge).</summary>
        private void HeUnitCandidates(object u, bool air, List<HeCandidate> into)
        {
            if (!RrIsOwnSide(u, false)) return;
            if (!Present(RangeRingSpecs.ObjAmmoAmountByName)) return;
            string label = air ? StrOrNull(Get(u, AirOpsSpecs.ObjClassName)) : null;
            if (string.IsNullOrWhiteSpace(label)) label = RxGet(u, CoreSpecs.ObjName) as string;
            if (string.IsNullOrWhiteSpace(label)) return;
            if (air) label = UiText.Fmt("he.airborne", label);
            var obp = Get(u, RadarSpecs.ObjObp);
            if (obp == null) return;
            var seen = new HashSet<string>();
            foreach (var spec in new[] { RangeRingSpecs.ParLauncherGroups, HitEstimateSpecs.ParHardpointGroups })
            {
                var dict = Get(obp, spec) as IDictionary;
                if (dict == null) continue;
                foreach (var group in dict.Values)
                {
                    var ap = group == null ? null : Get(group, RangeRingSpecs.GroupAp);
                    string file = ap == null ? null : Get(ap, RangeRingSpecs.AmmoFileName) as string;
                    if (file == null || !seen.Add(file)) continue;
                    var left = D(Call(u, RangeRingSpecs.ObjAmmoAmountByName, file));
                    if (!left.HasValue || left.Value <= 0) continue;
                    var a = HeAmmoOf(ap);
                    if (a != null) into.Add(new HeCandidate { Ammo = a, Unit = label, Count = (int)left.Value });
                }
            }
        }

        /// <summary>The aircraft types in the hangars and on the decks of the own ships: per ammunition the largest load over the loadouts that can launch now, times the airframes.</summary>
        private void HeHangarCandidates(List<HeCandidate> into)
        {
            if (!Capabilities.Contains("air_ops")) return;
            var ops = AirOps;
            foreach (var ship in DeckShips())
            {
                if (!RrIsOwnSide(ship, false)) continue;
                var deck = DeckOf(ship);
                var types = deck == null ? null : Get(deck, AirOpsSpecs.DeckVehicles) as IEnumerable;
                if (types == null) continue;
                string shipName = RxGet(ship, CoreSpecs.ObjName) as string;
                int uid = UidOf(ship);
                foreach (var vt in types)
                {
                    string type = vt == null ? null : Get(vt, AirOpsSpecs.VtFileName) as string;
                    var airframes = vt == null ? null : D(Get(vt, AirOpsSpecs.VtNumbers));
                    if (type == null || !airframes.HasValue || airframes.Value < 1) continue;
                    string disp = Get(vt, AirOpsSpecs.VtDisplayName) as string;
                    string label = (string.IsNullOrWhiteSpace(shipName) ? "" : shipName + ": ") + (string.IsNullOrWhiteSpace(disp) ? type : disp);
                    var best = new Dictionary<string, int>();
                    foreach (var lo in ops.Loadouts(uid, type))
                    {
                        if (!lo.Available || lo.Weapons == null) continue;
                        foreach (var kv in lo.Weapons) if (kv.Value > 0 && (!best.ContainsKey(kv.Key) || best[kv.Key] < kv.Value)) best[kv.Key] = kv.Value;
                    }
                    foreach (var kv in best)
                    {
                        var a = HeAmmoByFile(kv.Key);
                        if (a != null) into.Add(new HeCandidate { Ammo = a, Unit = label, Count = kv.Value * (int)airframes.Value });
                    }
                }
            }
        }
    }
}
