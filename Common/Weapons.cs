using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace VoiceBotBridge
{
    /// <summary>
    /// Contacts (player's plotting table only) and weapons/attack. Honest mode: targets come from
    /// Taskforce.PlottingTable.Vehicles (what the player sees). The true ObjectBase behind a track is touched only
    /// because the game's own click-attack path needs it (UnitSelectedState.AutoAttack -> AI.AutoAttackByClick -> EngageTask).
    /// Main thread only.
    /// </summary>
    internal sealed partial class GameApi
    {
        public bool AllowAttack = true;
        private const double UnityPerNm = 27.559496; // Unity units per nautical mile (game constant in AmmunitionParameters)

        private void SelfCheckWeapons(bool st)
        {
            bool w = st;
            w &= Has("weapons", "SeaPower.ObjectBase", "AmmunitionAmountDictionary", false, true);
            w &= Has("weapons", "SeaPower.ObjectBase", "getAmmunitionByName", false, true);
            w &= Has("weapons", "SeaPower.AmmunitionParameters", "_type", false, true);
            w &= Has("weapons", "SeaPower.AmmunitionParameters", "_targetType", false, true);
            Has("weapons", "SeaPower.AmmunitionParameters", "_launchRangesInUnity", false, false);
            Has("weapons", "SeaPower.AmmunitionParameters", "_natoDisplayedName", false, false);
            Has("weapons", "SeaPower.ObjectBase", "GetWeaponSystemsForAmmunition", false, false);
            Has("weapons", "SeaPower.ObjectBase", "IsOperableMount", true, false);
            Has("weapons", "SeaPower.ObjectBase", "_currentEngageTasks", false, false);
            if (w) Capabilities.Add("weapons");

            bool a = w && Capabilities.Contains("contacts");
            a &= Has("attack", "SeaPower.ObjectBase", "_ai", false, true);
            a &= Has("attack", "SeaPower.AI", "AmmunitionForTarget", false, true);
            a &= Has("attack", "SeaPower.ObjectBase", "AddEngageTask", false, true);
            a &= Has("attack", "SeaPower.ObjectBase", "GetMaxRangeForAmmo", false, true);
            a &= Has("attack", "SeaPower.WeaponSelector", "GetWeaponSystemsForAmmunition", true, true);
            a &= Has("attack", "SeaPower.Vehicle", "CurrentRelationship", false, true);
            a &= Has("attack", "SeaPower.Vehicle", "Object", false, true);
            Has("attack", "SeaPower.Vehicle", "GetMapType", false, false);
            Has("attack", "SeaPower.Vehicle", "Class", false, false);
            Has("attack", "SeaPower.Vehicle", "IsClassified", false, false);
            bool ctor = FindEngageCtor() != null;
            Checks.Add(new Check { Capability = "attack", Member = "SeaPower.EngageTask..ctor(string,ObjectBase,ObjectBase,int,...)", Required = true, Present = ctor });
            a &= ctor && FindType("SeaPower.Ammunition+Type") != null;
            if (a) Capabilities.Add("attack");
        }

        private ConstructorInfo FindEngageCtor()
        {
            var et = FindType("SeaPower.EngageTask");
            var ob = FindType("SeaPower.ObjectBase");
            if (et == null || ob == null) return null;
            foreach (var c in et.GetConstructors())
            {
                var p = c.GetParameters();
                if (p.Length >= 4 && p[0].ParameterType == typeof(string) && p[1].ParameterType == ob && p[2].ParameterType == ob && p[3].ParameterType == typeof(int))
                    return c;
            }
            return null;
        }

        private object SelectedUnitOrNull()
        {
            string src; return SelectedUnit(out src);
        }

        // ---------- contacts ----------

        private IEnumerable Vehicles()
        {
            var tf = GetStatic("SeaPower.Globals", "_playerTaskforce");
            var pt = Get(tf, "PlottingTable");
            return Get(pt, "Vehicles") as IEnumerable;
        }

        private string Relation(object v)
        {
            try { return Call(v, "CurrentRelationship").ToString().ToLowerInvariant(); } catch { return "unknown"; }
        }

        private string Kind(object v)
        {
            try { return Call(v, "GetMapType").ToString(); } catch { return null; }
        }

        private static string StringOrNull(object o) { return o as string; }

        private double[] TrackPos(object v)
        {
            var pos = RxGet(v, "Position");
            var la = D(Get(pos, "_latitude")); var lo = D(Get(pos, "_longitude"));
            return la.HasValue && lo.HasValue ? new[] { la.Value, lo.Value } : null;
        }

        private bool IsGone(object v)
        {
            var o = Get(v, "Object") as UnityEngine.Object;
            if (o == null) return true; // destroyed or missing
            var d = Get(o, "IsDestroyed");
            return d is bool && (bool)d;
        }

        private object FindContact(int id)
        {
            var vehicles = Vehicles();
            if (vehicles == null) return null;
            foreach (var v in vehicles)
            {
                var vid = D(Get(v, "Id"));
                if (vid.HasValue && (int)vid.Value == id) return v;
            }
            return null;
        }

        // ---------- weapons ----------

        private static string Category(string type, string target)
        {
            if (type == "Missile") return target == "ASuW" ? "antiship" : target == "AAW" ? "sam" : target == "ASW" ? "asw" : "missile";
            if (type == "ASROC" || type == "Torpedo" || type == "RBU" || type == "AirDepthCharge") return "asw";
            if (type == "Projectile") return "gun";
            return type == null ? "unknown" : type.ToLowerInvariant();
        }

        private bool HasOperableMount(object u, string ammoName)
        {
            try
            {
                var list = Call(u, "GetWeaponSystemsForAmmunition", ammoName, false) as IEnumerable;
                if (list == null) return false;
                var op = Find(FindType("SeaPower.ObjectBase"), "IsOperableMount", true) as MethodInfo;
                foreach (var ws in list) { if (op == null || (bool)op.Invoke(null, new[] { ws })) return true; }
            }
            catch { }
            return false;
        }

        private List<string[]> WeaponRows(object u)
        {
            // [file, display, type, target, category, count, rangeNm, canEngage, guidance, midcourse, needsIllumination]
            var rows = new List<string[]>();
            var dict = Get(u, "AmmunitionAmountDictionary") as IEnumerable;
            if (dict == null) return rows;
            foreach (var kv in dict)
            {
                try
                {
                    string file = Get(kv, "Key") as string;
                    int count = (int)(D(Get(kv, "Value")) ?? 0);
                    var ammo = Call(u, "getAmmunitionByName", file);
                    var ap = Get(ammo, "_ap");
                    if (ap == null) continue;
                    string type = (Get(ap, "_type") ?? "").ToString();
                    string target = (Get(ap, "_targetType") ?? "").ToString();
                    if (type == "Chaff" || type == "Noisemaker" || type == "Unknown") continue;
                    string disp = Get(ap, "_natoDisplayedName") as string;
                    if (string.IsNullOrEmpty(disp)) disp = file;
                    var lr = Get(ap, "_launchRangesInUnity");
                    var ry = D(Get(lr, "y"));
                    string rng = ry.HasValue ? Json.Num(Math.Round(ry.Value / UnityPerNm, 1)) : "null";
                    string mid; string gd = GuidanceOf(ap, out mid); // v0.9: guidance / needs_illumination
                    rows.Add(new[] { file, disp, type, target, Category(type, target), count.ToString(), rng, Json.Bool(count > 0 && HasOperableMount(u, file)), gd, mid, Json.Bool(TacticsMath.NeedsIllumination(gd, mid)) });
                }
                catch { }
            }
            return rows;
        }

        private sealed class DecoyRow { public string Name, Display, Type; public int Count; }

        /// <summary>Chaff and noisemaker ammunition (left out of weapons[]: they are not attack weapons).</summary>
        private List<DecoyRow> DecoyRows(object u)
        {
            var rows = new List<DecoyRow>();
            var dict = Get(u, "AmmunitionAmountDictionary") as IEnumerable;
            if (dict == null) return rows;
            foreach (var kv in dict)
            {
                try
                {
                    string file = Get(kv, "Key") as string;
                    var ap = Get(Call(u, "getAmmunitionByName", file), "_ap");
                    if (ap == null) continue;
                    string type = (Get(ap, "_type") ?? "").ToString();
                    if (type != "Chaff" && type != "Noisemaker") continue;
                    string disp = Get(ap, "_natoDisplayedName") as string;
                    rows.Add(new DecoyRow { Name = file, Display = string.IsNullOrEmpty(disp) ? file : disp, Type = type, Count = (int)(D(Get(kv, "Value")) ?? 0) });
                }
                catch { }
            }
            return rows;
        }

        private void AppendDecoys(StringBuilder sb, object u)
        {
            sb.Append(",\"decoys\":[");
            var rows = DecoyRows(u);
            for (int i = 0; i < rows.Count && i < 10; i++)
            {
                var r = rows[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"name\":").Append(Json.Str(r.Name)).Append(",\"display\":").Append(Json.Str(r.Display))
                  .Append(",\"type\":").Append(Json.Str(r.Type)).Append(",\"ammo\":").Append(r.Count).Append('}');
            }
            sb.Append(']');
            int mounts = 0, ready = 0;
            var list = Get(Get(u, "_obp"), "_chaffWeapons") as IEnumerable;
            if (list != null)
                foreach (var ws in list)
                {
                    if (ws == null) continue;
                    mounts++;
                    try { if ((bool)Call(ws, "readyToLaunch")) ready++; } catch { }
                }
            sb.Append(",\"chaff_launchers\":").Append(mounts).Append(",\"chaff_ready\":").Append(ready);
        }

        private void AppendWeapons(StringBuilder sb, object u)
        {
            if (!Capabilities.Contains("weapons")) return;
            try
            {
                sb.Append(",\"weapons\":[");
                var rows = WeaponRows(u);
                for (int i = 0; i < rows.Count && i < 30; i++)
                {
                    var r = rows[i];
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"name\":").Append(Json.Str(r[0])).Append(",\"display\":").Append(Json.Str(r[1]))
                      .Append(",\"type\":").Append(Json.Str(r[2])).Append(",\"target\":").Append(Json.Str(r[3]))
                      .Append(",\"category\":").Append(Json.Str(r[4])).Append(",\"ammo\":").Append(r[5])
                      .Append(",\"range_nm\":").Append(r[6]).Append(",\"can_engage\":").Append(r[7])
                      .Append(",\"guidance\":").Append(Json.Str(r[8])).Append(",\"midcourse\":").Append(Json.Str(r[9])).Append(",\"needs_illumination\":").Append(r[10]).Append('}');
                }
                sb.Append("],\"engage_tasks\":[");
                var tasks = Get(u, "_currentEngageTasks") as IEnumerable;
                bool first = true;
                if (tasks != null)
                {
                    foreach (var t in tasks)
                    {
                        if (t == null) continue;
                        if (!first) sb.Append(','); first = false;
                        int? cid = null;
                        var tgt = Get(t, "_targetObject");
                        var vehicles = Vehicles();
                        if (vehicles != null && tgt != null)
                            foreach (var v in vehicles) { if (ReferenceEquals(Get(v, "Object"), tgt)) { var d = D(Get(v, "Id")); if (d.HasValue) cid = (int)d.Value; break; } }
                        sb.Append("{\"ammo\":").Append(Json.Str(Get(t, "_ammoId") as string))
                          .Append(",\"shots_left\":").Append(Json.Num(D(Get(t, "_shotsToFire")) ?? 0))
                          .Append(",\"target_id\":").Append(cid.HasValue ? cid.Value.ToString() : "null").Append('}');
                    }
                }
                sb.Append(']');
                try { var dsb = new StringBuilder(); AppendDecoys(dsb, u); sb.Append(dsb); } catch { }
            }
            catch (Exception e) { sb.Append("],\"weapons_error\":").Append(Json.Str(e.GetType().Name)); }
        }

        private string WeaponsCmd(string id, Dictionary<string, object> cmd)
        {
            Need("weapons");
            string err; var u = Target(cmd, out err);
            if (u == null) return Err(id, err, null);
            var sb = new StringBuilder();
            AppendWeapons(sb, u);
            return Ok(id, sb.ToString().TrimStart(',').Replace("\"weapons\":", "\"weapons\":"));
        }

        // ---------- attack ----------

        private string Attack(string id, Dictionary<string, object> cmd, bool nearest)
        {
            Need("attack");
            if (!AllowAttack) return Err(id, "attack_disabled", "AllowAttack=false in mod config");
            // dangerous action: explicit confirmation is mandatory
            if (!(cmd.ContainsKey("confirm") && cmd["confirm"] is bool && (bool)cmd["confirm"]))
                return Err(id, "confirm_required", "attack needs \"confirm\":true");
            string err; var u = Target(cmd, out err);
            if (u == null) return Err(id, err, null);
            double lat0, lon0, h;
            if (!OwnPos(u, out lat0, out lon0, out h)) return Err(id, "no_position", null);

            // 1. target: a contact from the player's plotting table
            object v = null;
            var nearestSkipped = new List<int>();
            if (nearest || !cmd.ContainsKey("target_id") || cmd["target_id"] == null)
            {
                v = PickNearestHostile(u, lat0, lon0, out nearestSkipped);
                if (v == null) return Err(id, "no_hostile_contact", "no hostile surface contact in the plotting table");
            }
            else
            {
                var tid = D(cmd["target_id"]);
                if (!tid.HasValue) return Err(id, "bad_args", "target_id must be a number");
                v = FindContact((int)tid.Value);
                if (v == null || IsGone(v)) return Err(id, "unknown_target", "no such contact in the plotting table");
                if (Relation(v) != "hostile") return Err(id, "not_hostile", "contact side: " + Relation(v));
            }
            var targetObj = Get(v, "Object");
            var tp = TrackPos(v);
            int contactId = (int)(D(Get(v, "Id")) ?? -1);
            string tcls = Get(Get(v, "Class"), "Value") as string;
            double tdist = tp == null ? -1 : DistanceNm(lat0, lon0, tp[0], tp[1]);
            double tbrg = tp == null ? -1 : Bearing(lat0, lon0, tp[0], tp[1]);

            // 1b. neutral guard (T-079): a neutral surface track in the seeker corridor needs confirm_neutral:true
            string neutralJson;
            string guardErr = NeutralGuardError(id, cmd, u, v, out neutralJson);
            if (guardErr != null) return guardErr;

            // 2. weapon
            string weapon = cmd.ContainsKey("weapon") ? cmd["weapon"] as string : null;
            if (string.IsNullOrEmpty(weapon)) weapon = "antiship";
            // v0.7 (K2): a category (antiship|sam|gun|asw, as weapons[].category) or an ammo file/display name
            string category = null;
            foreach (var c in new[] { "antiship", "sam", "gun", "asw" })
                if (weapon.Equals(c, StringComparison.OrdinalIgnoreCase)) category = c;
            bool byCategory = category != null;
            int salvo = 1;
            if (cmd.ContainsKey("salvo") && D(cmd["salvo"]).HasValue) salvo = (int)D(cmd["salvo"]).Value;
            if (salvo < 1 || salvo > 8) return Err(id, "bad_args", "salvo 1..8");

            var ai = Get(u, "_ai");
            var typeEnum = FindType("SeaPower.Ammunition+Type");
            object ammoType = Enum.Parse(typeEnum, category == "antiship" || category == "sam" ? "Missile" : "None");
            var cands = Call(ai, "AmmunitionForTarget", targetObj, ammoType) as IEnumerable; // count>0 and able to hit this target
            var ranked = new List<object>();
            if (cands != null)
            {
                foreach (var ammo in cands)
                {
                    var ap = Get(ammo, "_ap");
                    string file = Get(ap, "_ammunitionFileName") as string;
                    string disp = Get(ap, "_natoDisplayedName") as string;
                    string type = (Get(ap, "_type") ?? "").ToString();
                    string tgt = (Get(ap, "_targetType") ?? "").ToString();
                    bool ok = byCategory
                        ? Category(type, tgt) == category
                        : (string.Equals(file, weapon, StringComparison.OrdinalIgnoreCase) ||
                           (disp != null && disp.IndexOf(weapon, StringComparison.OrdinalIgnoreCase) >= 0));
                    if (ok) ranked.Add(ammo);
                }
            }
            if (ranked.Count == 0)
            {
                // no_ammo if the unit simply has none of the requested kind, else no_weapon (cannot engage this target)
                int have = 0;
                foreach (var r in WeaponRows(u))
                {
                    bool match = byCategory ? r[4] == category : (string.Equals(r[0], weapon, StringComparison.OrdinalIgnoreCase) || r[1].IndexOf(weapon, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (match) have += int.Parse(r[5]);
                }
                return Err(id, have == 0 ? "no_ammo" : "no_weapon", weapon);
            }

            // 3. range (same check the game does: GetMaxRangeForAmmo vs distance) and a ready mount
            var selfT = ((Component)u).transform; var tgtT = ((Component)targetObj).transform;
            float distUnity = (tgtT.position - selfT.position).magnitude;
            object chosen = null; double maxRangeNm = 0;
            foreach (var ammo in ranked)
            {
                string file = Get(Get(ammo, "_ap"), "_ammunitionFileName") as string;
                float maxR = Convert.ToSingle(Call(u, "GetMaxRangeForAmmo", ammo, targetObj));
                maxRangeNm = Math.Max(maxRangeNm, maxR / UnityPerNm);
                if (maxR < distUnity) continue;
                var mounts = CallStatic("SeaPower.WeaponSelector", "GetWeaponSystemsForAmmunition", u, tgtT.position, file, targetObj) as ICollection;
                if (mounts == null || mounts.Count == 0) continue;

                chosen = ammo; break;
            }
            if (chosen == null)
            {
                if (maxRangeNm * UnityPerNm < distUnity)
                    return Err(id, "out_of_range", "distance " + Math.Round(distUnity / UnityPerNm, 1) + " nm, max " + Math.Round(maxRangeNm, 1) + " nm");
                return Err(id, "no_weapon", "no ready launcher/mount for this target");
            }
            string chosenFile = Get(Get(chosen, "_ap"), "_ammunitionFileName") as string;
            string chosenDisp = Get(Get(chosen, "_ap"), "_natoDisplayedName") as string;
            int before = 0;
            foreach (var r in WeaponRows(u)) { if (r[0] == chosenFile) before = int.Parse(r[5]); }
            if (before <= 0) return Err(id, "no_ammo", chosenFile);
            if (salvo > before) salvo = before;

            // 4. fire: exactly what AI.AutoAttackByClick does for the chosen ammo
            var ctor = FindEngageCtor();
            var ps = ctor.GetParameters();
            var args = new object[ps.Length];
            args[0] = chosenFile; args[1] = targetObj; args[2] = u; args[3] = salvo;
            for (int i = 4; i < ps.Length; i++) args[i] = DefaultFor(ps[i]);
            var task = ctor.Invoke(args);
            Call(u, "AddEngageTask", task);

            return Ok(id, "\"uid\":" + Json.Num(D(Get(u, "UniqueID")) ?? -1) +
                ",\"target_id\":" + contactId + ",\"target_class\":" + Json.Str(tcls) +
                ",\"target_bearing_deg\":" + Json.Num(Math.Round(tbrg, 1)) + ",\"target_distance_nm\":" + Json.Num(Math.Round(tdist, 2)) +
                ",\"weapon\":" + Json.Str(chosenFile) + ",\"weapon_display\":" + Json.Str(chosenDisp) +
                ",\"salvo_ordered\":" + salvo + ",\"ammo_before\":" + before +
                ",\"max_range_nm\":" + Json.Num(Math.Round(maxRangeNm, 1)) +
                ",\"neutral_near\":" + neutralJson + ",\"confirm_neutral\":" + Json.Bool(ConfirmedNeutral(cmd)) +
                (nearest ? ",\"nearest_skipped\":[" + string.Join(",", nearestSkipped.ConvertAll(x => x.ToString()).ToArray()) + "]" : "") +
                ",\"note\":\"order queued as EngageTask; verify launch via state.selected.weapons[].ammo and engage_tasks\"");
        }

        // ---------- T-141 (ADR-0048): anti-torpedo fire for the UI mod's own evasion; the voice mod never calls these ----------

        /// <summary>
        /// One entry per anti-torpedo ammo (AmmunitionParameters._antiTorpedo) that AmmunitionForTarget(torpedo, None) offers:
        /// max range by GetMaxRangeForAmmo, min range, speed, count, ready mount with the torpedo in its arc. Never throws.
        /// </summary>
        internal List<AtwAmmoInfo> AntiTorpedoOptions(object u, object torpVehicle)
        {
            var res = new List<AtwAmmoInfo>();
            try
            {
                var targetObj = Get(torpVehicle, "Object");
                if (u == null || targetObj == null) return res;
                var typeEnum = FindType("SeaPower.Ammunition+Type");
                if (typeEnum == null) return res;
                var cands = Call(Get(u, "_ai"), "AmmunitionForTarget", targetObj, Enum.Parse(typeEnum, "None")) as IEnumerable;
                if (cands == null) return res;
                var tgtPos = ((Component)targetObj).transform.position;
                var rows = WeaponRows(u);
                foreach (var ammo in cands)
                {
                    try
                    {
                        var ap = Get(ammo, "_ap");
                        if (ap == null || !(Get(ap, "_antiTorpedo") is bool) || !(bool)Get(ap, "_antiTorpedo")) continue;
                        string file = Get(ap, "_ammunitionFileName") as string;
                        if (string.IsNullOrEmpty(file)) continue;
                        string disp = Get(ap, "_natoDisplayedName") as string;
                        int count = 0;
                        foreach (var r in rows) if (r[0] == file) count = int.Parse(r[5]);
                        float maxR = Convert.ToSingle(Call(u, "GetMaxRangeForAmmo", ammo, targetObj));
                        var mounts = CallStatic("SeaPower.WeaponSelector", "GetWeaponSystemsForAmmunition", u, tgtPos, file, targetObj) as ICollection;
                        var mv = D(Get(ap, "_muzzleVelocityInMeterPerSecond"));
                        res.Add(new AtwAmmoInfo
                        {
                            File = file,
                            Display = string.IsNullOrEmpty(disp) ? file : disp,
                            Kill = D(Get(ap, "_killProbability")) ?? 0,
                            MinNm = D(Get(ap, "_minLaunchRangeInMiles")) ?? 0,
                            MaxNm = maxR / UnityPerNm,
                            SpeedKn = mv.HasValue ? mv.Value * 1.943844 : 0,
                            Count = count,
                            MountReady = mounts != null && mounts.Count > 0,
                        });
                    }
                    catch { }
                }
            }
            catch { }
            return res;
        }

        /// <summary>
        /// Steps 3-4 of Attack for a torpedo track: EngageTask(file, torpedo, u, shots) + AddEngageTask. No hostile check, no neutral corridor,
        /// not gated by ROE or AllowAttack (self-defence, ADR-0048). error: no_capability | no_object | no_ctor | no_ammo | failed.
        /// </summary>
        internal bool EngageAntiTorpedo(object u, object torpVehicle, string ammoFile, int shots, out AtwShot shot, out string error)
        {
            shot = null; error = null;
            try
            {
                if (!Capabilities.Contains("attack")) { error = "no_capability"; return false; }
                var targetObj = Get(torpVehicle, "Object");
                if (u == null || targetObj == null) { error = "no_object"; return false; }
                var ctor = FindEngageCtor();
                if (ctor == null) { error = "no_ctor"; return false; }
                int before = 0;
                foreach (var r in WeaponRows(u)) { if (r[0] == ammoFile) before = int.Parse(r[5]); }
                if (before <= 0) { error = "no_ammo"; return false; }
                if (shots < 1) shots = 1;
                if (shots > before) shots = before;
                var ps = ctor.GetParameters();
                var args = new object[ps.Length];
                args[0] = ammoFile; args[1] = targetObj; args[2] = u; args[3] = shots;
                for (int i = 4; i < ps.Length; i++) args[i] = DefaultFor(ps[i]);
                var task = ctor.Invoke(args);
                Call(u, "AddEngageTask", task);
                string disp = null;
                try { disp = Get(Get(Call(u, "getAmmunitionByName", ammoFile), "_ap"), "_natoDisplayedName") as string; } catch { }
                double lat0, lon0, h;
                var tp = TrackPos(torpVehicle);
                double dist = -1, brg = -1;
                if (tp != null && OwnPos(u, out lat0, out lon0, out h)) { dist = DistanceNm(lat0, lon0, tp[0], tp[1]); brg = Bearing(lat0, lon0, tp[0], tp[1]); }
                shot = new AtwShot { File = ammoFile, Display = string.IsNullOrEmpty(disp) ? ammoFile : disp, Shots = shots, Before = before, DistNm = dist, BrgDeg = brg, Task = task };
                return true;
            }
            catch (Exception) { error = "failed"; return false; }
        }

        /// <summary>true when the unit already has an EngageTask whose _targetObject is this object.</summary>
        internal bool EngageTaskOn(object u, object targetObj)
        {
            try
            {
                if (u == null || targetObj == null) return false;
                var tasks = Get(u, "_currentEngageTasks") as IEnumerable;
                if (tasks == null) return false;
                foreach (var t in tasks)
                    if (t != null && ReferenceEquals(Get(t, "_targetObject"), targetObj)) return true;
            }
            catch { }
            return false;
        }
    }
}

namespace VoiceBotBridge
{
    /// <summary>T-141: one anti-torpedo ammo of a unit against a torpedo track (UI mod only; the voice mod does not call it).</summary>
    internal sealed class AtwAmmoInfo
    {
        public string File, Display;
        public double Kill, MinNm, MaxNm, SpeedKn;
        public int Count;
        public bool MountReady;
    }

    /// <summary>T-141: what EngageAntiTorpedo ordered.</summary>
    internal sealed class AtwShot
    {
        public string File, Display;
        public int Shots, Before;
        public double DistNm, BrgDeg;
        public object Task;   // T-230: the EngageTask that was added (the UI mod follows it to the launch)
    }
}
