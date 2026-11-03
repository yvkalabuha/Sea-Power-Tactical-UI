using System;
using System.Collections.Generic;
using System.Globalization;
using VoiceBotBridge.Ui;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-168 / ADR-0064 items 20-21: the hits of the player's weapons by track. The source is the game's weapon report: the hook on TaskforceWeaponReportExtensions.RecordWeaponHitUnit
    /// (the player's taskforce only), which the game calls once per weapon and target. From the weapon the ammunition, the malfunction flag and the shooter are read; from the hit unit
    /// only its id (to ask the game whether the report counts it) and the player's track of it (the game resolves it). The blast zone and the compartments of the target are never read.
    /// Shells are not counted: RecordProjectileHitUnit has no target (item 33a). Scanner: tests/test_sea_power_hit_estimate_static.py (test_hits_files_do_not_read_the_target_damage).
    /// </summary>
    internal sealed partial class GameApi
    {
        /// <summary>Set by the hit_estimate module: the ledger the hook writes to (null = the counter is off).</summary>
        internal HitLedger HeLedger;
        /// <summary>The estimate service of the hit_estimate module; the Overview reads it lazily (the module registers after the Overview).</summary>
        internal IHitEstimateDetail HitEstimate;
        internal int HeClassCount { get { return _heClasses.Count; } }
        internal int HeAmmoCount { get { return _heAmmo.Count; } }
        private bool _heLoggedHitFail;

        /// <summary>Prefix: is this a hit the report will count (the player's weapon, a target it has not been reported for)? The answer goes to the postfix.</summary>
        internal bool HeBeforeHit(object taskforce, object weapon, object hitObject)
        {
            try
            {
                if (HeLedger == null || weapon == null || hitObject == null) return false;
                var player = GetStatic(CoreSpecs.PlayerTaskforce);
                if (player == null || !ReferenceEquals(taskforce, player)) return false;
                var ids = Get(weapon, HitEstimateSpecs.WeaponReportIds) as HashSet<int>;
                if (ids == null) return true;
                var uid = D(Get(hitObject, RangeRingSpecs.ObjUniqueId));
                return !uid.HasValue || !ids.Contains((int)uid.Value);
            }
            catch (Exception e) { HeHitFail("before", e); return false; }
        }

        /// <summary>Postfix: records the hit when the prefix said it counts. Never throws into the game.</summary>
        internal void HeOnOwnHit(object taskforce, object weapon, object hitObject)
        {
            try
            {
                var player = GetStatic(CoreSpecs.PlayerTaskforce);
                if (HeLedger == null || player == null || !ReferenceEquals(taskforce, player) || weapon == null || hitObject == null) return;
                var veh = Call(hitObject, InterceptSpecs.ObjMapVehicle, player);   // the game resolves the player's own track of the unit
                var id = veh == null ? null : D(Get(veh, OverviewSpecs.VehId));
                if (!id.HasValue) return;
                var a = HeAmmoOf(Get(weapon, HitEstimateSpecs.WeaponAp));
                if (a == null) return;
                var shooter = Get(weapon, HitEstimateSpecs.WeaponLaunchPlatform);
                string who = RrIsOwnSide(shooter, false) ? RxGet(shooter, CoreSpecs.ObjName) as string : null;
                HeLedger.Add(new HeHit
                {
                    TrackId = (int)id.Value,
                    Clock = HeClock(),
                    AmmoFile = a.File,
                    Weapon = a.Name,
                    Shooter = string.IsNullOrWhiteSpace(who) ? "?" : who,
                    Kind = a.Kind,
                    Dud = BoolOf(Get(weapon, HitEstimateSpecs.WeaponMalfunction)),
                    Warhead = a.Warhead,
                    WeaponId = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(weapon),
                    CiwsDmg = HeCiwsDamage(weapon),
                    T = Frame.MissionTimeS ?? -1,
                });
            }
            catch (Exception e) { HeHitFail("after", e); }
        }

        /// <summary>T-342: WeaponBase._ciwsDamageTaken (the damage the AA gun did to the missile before it hit); -1 = the member is missing. The denominator (the health of the missile) is not read: DmgFrac stays -1.</summary>
        private double HeCiwsDamage(object weapon)
        {
            try { var v = Present(HitEstimateSpecs.WeaponCiwsDamage) ? D(Get(weapon, HitEstimateSpecs.WeaponCiwsDamage)) : null; return v.HasValue && !double.IsNaN(v.Value) ? v.Value : -1; }
            catch { return -1; }
        }

        private void HeHitFail(string where, Exception e)
        {
            if (_heLoggedHitFail) return;
            _heLoggedHitFail = true;
            try { Log("[HitEstimate] hit hook " + where + " failed (once): " + e.GetType().Name + ": " + e.Message); } catch { }
        }

        /// <summary>The clock the player sees (the game's Environment.DateTime, HH:mm:ss); else the mission time "+m:ss".</summary>
        private string HeClock()
        {
            try
            {
                var env = SingletonInstance(HitEstimateSpecs.EnvironmentType);
                var dt = env == null ? null : Get(env, HitEstimateSpecs.EnvironmentDateTime);
                if (dt is DateTime) return ((DateTime)dt).ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            }
            catch { }
            var t = D(GetStatic(HitEstimateSpecs.GameTimeSession));
            if (!t.HasValue) return "--:--";
            int s = (int)Math.Max(0, t.Value);
            return "+" + (s / 60).ToString(CultureInfo.InvariantCulture) + ":" + (s % 60).ToString("00", CultureInfo.InvariantCulture);
        }
    }
}
