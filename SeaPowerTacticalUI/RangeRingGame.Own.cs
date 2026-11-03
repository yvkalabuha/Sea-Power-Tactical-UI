using System;
using System.Collections;
using System.Collections.Generic;
using VoiceBotBridge.Ui;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-137 / ADR-0055: game side of the range rings for the PLAYER'S OWN units (and the allied ones when the option is on). The truth about the
    /// armament of a unit is read only for these sides. Every internal Rr* method starts with the RrIsOwnSide guard (RrSideOf reads the side only).
    /// Foreign units live in RangeRingGame.Class.cs. Scanner: tests/test_sea_power_range_rings_static.py.
    /// </summary>
    internal sealed partial class GameApi
    {
        /// <summary>The unit of the player's or the allied taskforce by UniqueID, else null. Nothing but the id is read here.</summary>
        private object RrFindUnit(int uid)
        {
            try
            {
                var tfs = new[] { GetStatic(RangeRingSpecs.GlobalsPlayerTaskforce), GetStatic(RangeRingSpecs.GlobalsAlliedTaskforce) };
                var lists = new[] { RangeRingSpecs.TfVessels, RangeRingSpecs.TfSubmarines, RangeRingSpecs.TfAircraft, RangeRingSpecs.TfHelicopters };
                foreach (var tf in tfs)
                {
                    if (tf == null) continue;
                    foreach (var ls in lists)
                    {
                        var list = Get(tf, ls) as IEnumerable;
                        if (list == null) continue;
                        foreach (var u in list) if (u != null && RrUid(u) == uid) return u;
                    }
                }
            }
            catch { }
            return null;
        }

        private int RrUid(object u) { return (int)(D(Get(u, RangeRingSpecs.ObjUniqueId)) ?? -1); }

        private string RrSideName(object u)
        {
            var s = Get(Get(u, RangeRingSpecs.ObjTaskforce), RangeRingSpecs.TaskforceSide);
            return s == null ? null : s.ToString();
        }

        /// <summary>The side check of ADR-0055: the unit is Player's, or Ally's when allowAlly. Reads the side and nothing else.</summary>
        private bool RrIsOwnSide(object u, bool allowAlly)
        {
            if (u == null || !IsAlive(u) || BoolOf(Get(u, ContactInfoSpecs.ObjIsDestroyed))) return false;   // 2026-10-22: a sinking unit has no rings
            try
            {
                var side = RrSideName(u);
                return side == "Player" || (allowAlly && side == "Ally");
            }
            catch { return false; }
        }

        /// <summary>The side of a unit of the player's or the allied taskforce (Other for anything else); no facts.</summary>
        internal UnitSide RrSideOf(int uid)
        {
            var u = RrFindUnit(uid);
            if (u == null || !IsAlive(u) || BoolOf(Get(u, ContactInfoSpecs.ObjIsDestroyed))) return UnitSide.Other;   // destroyed: no rings, the pin goes
            try
            {
                var side = RrSideName(u);
                return side == "Player" ? UnitSide.Player : side == "Ally" ? UnitSide.Ally : UnitSide.Other;
            }
            catch { return UnitSide.Other; }
        }

        /// <summary>UniqueID of the selected unit when it is the player's own (an ally is not accepted), else 0.</summary>
        internal int RrSelectedOwnUid()
        {
            string src;
            var u = SelectedUnit(out src);
            if (!RrIsOwnSide(u, false)) return 0;
            return RrUid(u);
        }

        /// <summary>The player's units of the same formation (connection); a unit without a formation is its own group.</summary>
        internal IList<int> RrGroupOf(int uid)
        {
            var u = RrFindUnit(uid);
            if (!RrIsOwnSide(u, false)) return new List<int>();
            var res = new List<int> { uid };
            try
            {
                var stations = Get(Get(u, RangeRingSpecs.ObjFormation), RangeRingSpecs.FormationStations) as IEnumerable;
                if (stations == null) return res;
                foreach (var st in stations)
                {
                    var su = st == null ? null : Get(st, RangeRingSpecs.StationUnit);
                    if (!RrIsOwnSide(su, false)) continue;
                    int id = RrUid(su);
                    if (id >= 0 && !res.Contains(id)) res.Add(id);
                }
            }
            catch { }
            return res;
        }

        private static RingTarget RrTarget(object t)
        {
            var s = t == null ? null : t.ToString();
            return s == "AAW" ? RingTarget.Aaw : s == "ASuW" ? RingTarget.Asuw : s == "ASW" ? RingTarget.Asw : RingTarget.Unknown;
        }

        private static RingAmmoType RrAmmoType(object t)
        {
            RingAmmoType r;
            return t != null && Enum.TryParse(t.ToString(), true, out r) ? r : RingAmmoType.Other;
        }

        /// <summary>The kind of an own ammunition: _type, refined by _subType (a buoy is Bomb + Sonobuoy in the game, see RangeRingReach.EffectiveType).</summary>
        private RingAmmoType RrOwnAmmoType(object ap)
        {
            var type = RrAmmoType(Get(ap, RangeRingSpecs.AmmoType));
            if (!Present(RangeRingSpecs.AmmoSubType)) return type;
            return RangeRingReach.EffectiveType(type, RrAmmoType(Get(ap, RangeRingSpecs.AmmoSubType)));
        }

        private double RrComponent(object vec, MemberSpec c)
        {
            var d = vec == null ? null : D(Get(vec, c));
            return d.HasValue ? d.Value : double.NaN;
        }

        private void RrReadAmmo(object u, object obp, OwnArmament res)
        {
            var seen = new HashSet<string>();
            bool countAvailable = Present(RangeRingSpecs.ObjAmmoAmountByName);
            foreach (var spec in new[] { RangeRingSpecs.ParLauncherGroups, RangeRingSpecs.ParGunGroups })
            {
                var dict = Get(obp, spec) as IDictionary;
                if (dict == null) continue;
                foreach (var group in dict.Values)
                {
                    var ap = group == null ? null : Get(group, RangeRingSpecs.GroupAp);
                    var name = ap == null ? null : Get(ap, RangeRingSpecs.AmmoFileName) as string;
                    if (name == null || !seen.Add(name)) continue;
                    var ranges = Get(ap, RangeRingSpecs.AmmoLaunchRanges);
                    var a = new AmmoFact
                    {
                        Name = name,
                        Primary = RrTarget(Get(ap, RangeRingSpecs.AmmoTargetType)),
                        Secondary = RrTarget(Get(ap, RangeRingSpecs.AmmoSecondaryTargetType)),
                        Type = RrOwnAmmoType(ap),
                        MinNm = RrComponent(ranges, RangeRingSpecs.VecX) * RangeRingMath.NmPerUnity,
                        MaxNm = RrComponent(ranges, RangeRingSpecs.VecY) * RangeRingMath.NmPerUnity,
                        AntiTorpedo = BoolOf(Get(ap, RangeRingSpecs.AmmoAntiTorpedo)),
                        Left = -1,
                    };
                    if (Present(HitEstimateSpecs.AmmoDisplayedName))
                    {
                        string shown = Get(ap, HitEstimateSpecs.AmmoDisplayedName) as string;
                        if (!string.IsNullOrWhiteSpace(shown) && shown != "MISSING TEXT") a.Display = shown.Trim();
                    }
                    if (countAvailable)
                    {
                        try
                        {
                            var left = D(Call(u, RangeRingSpecs.ObjAmmoAmountByName, name));
                            if (left.HasValue) { a.Left = (int)left.Value; res.AmmoCountKnown = true; }
                        }
                        catch { }
                    }
                    res.Ammo.Add(a);
                }
            }
        }

        private void RrReadMounts(object obp, OwnArmament res)
        {
            foreach (var spec in new[] { RangeRingSpecs.ParGunWeapons, RangeRingSpecs.ParCiwsWeapons, RangeRingSpecs.ParLauncherWeapons, RangeRingSpecs.ParHardpointWeapons })
            {
                var list = Get(obp, spec) as IEnumerable;
                if (list == null) continue;
                foreach (var ws in list)
                {
                    if (ws == null) continue;
                    var m = new MountFact();
                    var names = Get(ws, RangeRingSpecs.WsAmmoNames) as IEnumerable;
                    if (names != null) foreach (var n in names) { var s = n as string; if (s != null) m.AmmoNames.Add(s); }
                    if (m.AmmoNames.Count == 0) continue;
                    var arcs = Get(Get(ws, RangeRingSpecs.WsParameters), RangeRingSpecs.WpFiringArcs) as IEnumerable;
                    if (arcs != null)
                        foreach (var v in arcs)
                        {
                            double x = RrComponent(v, RangeRingSpecs.VecX), y = RrComponent(v, RangeRingSpecs.VecY);
                            if (!double.IsNaN(x) && !double.IsNaN(y)) m.Arcs.Add(new ArcDeg { A = x, B = y });
                        }
                    res.Mounts.Add(m);
                }
            }
        }

        private void RrReadSonars(object obp, OwnArmament res)
        {
            var seen = new List<object>();
            foreach (var spec in new[] { RangeRingSpecs.ParSonarSystems, RangeRingSpecs.ParActiveSonarSystems, RangeRingSpecs.ParTowedSonarSystems })
            {
                var list = Get(obp, spec) as IEnumerable;
                if (list == null) continue;
                foreach (var ss in list)
                {
                    if (ss == null || seen.Exists(x => ReferenceEquals(x, ss))) continue;
                    seen.Add(ss);
                    var sp = Get(Get(ss, RangeRingSpecs.SonarSystemSonar), RangeRingSpecs.SonarParams);
                    var act = sp == null ? null : D(Get(sp, RangeRingSpecs.SpActiveRangeKm));
                    var pas = sp == null ? null : D(Get(sp, RangeRingSpecs.SpPassiveRangeKm));
                    var f = new SonarFact { ActiveNm = (act ?? 0) * RangeRingMath.NmPerKm, PassiveNm = (pas ?? 0) * RangeRingMath.NmPerKm };
                    if (f.ActiveNm > 0 || f.PassiveNm > 0) res.Sonars.Add(f);
                }
            }
        }

        /// <summary>Ammunition groups (nominal ranges, remaining count), mounts with their arcs and sonars of an own (or allied) unit; null for any other.</summary>
        internal OwnArmament RrOwn(int uid, bool allowAlly)
        {
            var u = RrFindUnit(uid);
            if (!RrIsOwnSide(u, allowAlly)) return null;
            var res = new OwnArmament();
            try
            {
                var obp = Get(u, RadarSpecs.ObjObp);
                if (obp == null) return res;
                RrReadAmmo(u, obp, res);
                RrReadMounts(obp, res);
                RrReadSonars(obp, res);
            }
            catch { }
            return res;
        }

        /// <summary>Position, heading and air flag of an own (or allied) unit.</summary>
        internal bool RrOwnPose(int uid, bool allowAlly, out RingPt p, out double heading, out bool air)
        {
            p = default(RingPt);
            heading = 0;
            air = false;
            var u = RrFindUnit(uid);
            if (!RrIsOwnSide(u, allowAlly)) return false;
            try
            {
                double lat, lon, h;
                if (!OwnPos(u, out lat, out lon, out h)) return false;
                p = new RingPt(lat, lon);
                heading = D(RxGet(u, EvasionSpecs.ObjHeading)) ?? 0;
                var type = StrOrNull(Get(u, OverviewSpecs.ObjType));
                air = type == "Aircraft" || type == "Helicopter";
                return true;
            }
            catch { return false; }
        }
    }
}
