using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace VoiceBotBridge
{
    internal sealed partial class GameApi
    {
        private void SetField(object o, string name, object value)
        {
            var f = o.GetType().GetField(name, AllInst);
            if (f == null) throw new MissingMemberException(o.GetType().Name, name);
            f.SetValue(o, value);
        }

        private string StateName(object u, string prop)
        {
            try { return Get(Get(u, "StateMachine"), prop) as string; } catch { return null; }
        }

        /// <summary>Maps the unit's state-machine state to "who is steering": player or one of the game's automatic behaviours.</summary>
        internal static string ManeuverBy(string state, bool prep, bool override_, bool aligning)
        {
            if (override_ || state == "PlayerOverride") return "player_override";
            switch (state)
            {
                case "Aligning": return prep ? "ai_prep_engage" : "ai_engage";
                case "AvoidingCollision": return "ai_collision";
                case "TorpedoEvasion":
                case "CounterLaunch": return "ai_evasion";
                case "MovingInFormation":
                case "FormationSprint":
                case "FormationDrift":
                case "ReturnToFormation": return "formation";
                case "PerformingAirOps": return "air_ops";
                case "Sprint":
                case "Drift":
                case "ClearBafflesTurningBack":
                case "ClearBaffles": return "ai_doctrine";
                case "Default":
                case "Loitering":
                case null:
                case "": return aligning ? "ai_engage" : "player";
                default: return "other_ai";
            }
        }

        private int? ContactIdOf(object targetObject)
        {
            if (targetObject == null) return null;
            var vehicles = Vehicles();
            if (vehicles == null) return null;
            foreach (var v in vehicles)
            {
                if (ReferenceEquals(Get(v, "Object"), targetObject))
                {
                    var d = D(Get(v, "Id"));
                    return d.HasValue ? (int?)d.Value : null;
                }
            }
            return null;
        }

        /// <summary>Returns the "maneuver" JSON object; byOut = value of "by".</summary>
        public string ManeuverJson(object u, out string byOut)
        {
            string state = StateName(u, "CurrentStateName");
            string prev = StateName(u, "PreviousStateName");
            bool aligning = Get(u, "Aligning") is bool && (bool)Get(u, "Aligning");
            bool over = Get(u, "_playerCommandOverride") is bool && (bool)Get(u, "_playerCommandOverride");
            bool prep = false; string reason = state; string weaponDisp = null; int? track = null;
            var tasks = Get(u, "_alignmentTasks") as IEnumerable;
            if (tasks != null)
            {
                foreach (var t in tasks)
                {
                    prep = Get(t, "_isPrep") is bool && (bool)Get(t, "_isPrep");
                    track = ContactIdOf(Get(t, "_targetObject")); // player's track number only, never the true object
                    weaponDisp = Get(Get(t, "_ap"), "_natoDisplayedName") as string;
                    break;
                }
            }
            string by = ManeuverBy(state, prep, over, aligning);
            byOut = by;
            if (track.HasValue) reason += "; track " + track.Value;
            if (!string.IsNullOrEmpty(weaponDisp)) reason += "; " + weaponDisp;

            var sb = new StringBuilder("{");
            sb.Append("\"by\":").Append(Json.Str(by));
            sb.Append(",\"reason\":").Append(Json.Str(reason));
            sb.Append(",\"state\":").Append(Json.Str(state));
            sb.Append(",\"prev_state\":").Append(Json.Str(prev));
            var sct = D(Get(Get(u, "StateMachine"), "StateChangeTime"));
            var met = D(GetStatic("SeaPower.GameTime", "missionElapsedTime"));
            if (sct.HasValue && met.HasValue && met.Value - sct.Value >= 0 && met.Value - sct.Value < 1e7)
                sb.Append(",\"since_s\":").Append(Json.Num(Math.Round(met.Value - sct.Value, 1)));
            var tp = D(Get(u, "_previousTelegraphSetting"));
            sb.Append(",\"telegraph_player\":").Append(tp.HasValue ? Json.Num(tp.Value) : "null");
            var tn = Telegraph(u);
            sb.Append(",\"telegraph_now\":").Append(tn.HasValue ? tn.Value.ToString() : "null");
            sb.Append(",\"resumes_waypoint\":").Append(Json.Bool(Waypoints(u).Count > 0));
            object aa = null;
            try { aa = Get(Get(Get(u, "TacticalDoctrine"), "AllowAlignment"), "ResolvedValue"); } catch { }
            sb.Append(",\"auto_align\":").Append(aa is bool ? Json.Bool((bool)aa) : "null");
            sb.Append(",\"override\":").Append(Json.Bool(over));
            return sb.Append('}').ToString();
        }

        private void AppendUnitSystems(StringBuilder sb, object u)
        {
            try
            {
                string by;
                sb.Append(",\"maneuver\":").Append(ManeuverJson(u, out by));
            }
            catch (Exception e) { sb.Append(",\"maneuver_error\":").Append(Json.Str(e.GetType().Name)); }

            try
            {
                sb.Append(",\"sensors\":{");
                bool first = true;
                foreach (var kv in new[] { new[] { "emcon", "Emcon" }, new[] { "air_radar", "IsAirSearchRadarsOn" },
                                           new[] { "surface_radar", "IsSurfaceSearchRadarsOn" }, new[] { "active_sonar", "IsActiveSonarsOn" } })
                {
                    var v = RxGet(u, kv[1]);
                    if (!(v is bool)) continue;
                    if (!first) sb.Append(','); first = false;
                    sb.Append(Json.Str(kv[0])).Append(':').Append(Json.Bool((bool)v));
                }
                sb.Append('}');
            }
            catch { }

            try
            {
                var ws = RxGet(u, "WeaponState");
                if (ws != null) sb.Append(",\"weapon_status\":").Append(Json.Str(ws.ToString().ToLowerInvariant()));
            }
            catch { }

            try
            {
                AppendDamage(sb, u);
            }
            catch { }

            try
            {
                var f = Get(u, "Formation");
                if (f != null)
                {
                    sb.Append(",\"formation\":{\"name\":").Append(Json.Str(Get(f, "Name") as string));
                    var mode = Get(f, "SelectedControlMode");
                    sb.Append(",\"mode\":").Append(Json.Str(mode == null ? null : mode.ToString().ToLowerInvariant()));
                    var ls = Get(f, "LeaderStation");
                    var lu = Get(ls, "UnitObject");
                    var luid = D(Get(lu, "UniqueID"));
                    sb.Append(",\"leader_uid\":").Append(luid.HasValue ? Json.Num(luid.Value) : "null");
                    var fol = Get(u, "FollowingFormation");
                    sb.Append(",\"following\":").Append(fol is bool ? Json.Bool((bool)fol) : "null");
                    var ind = RxGet(u, "ActsIndependentlyInFormation");
                    sb.Append(",\"independent\":").Append(ind is bool ? Json.Bool((bool)ind) : "null");
                    var pat = Get(f, "Pattern");
                    sb.Append(",\"pattern\":").Append(Json.Str(pat == null ? null : FormationArgs.ToSnake(pat.ToString())));
                    var fsp = D(Get(f, "Spacing"));
                    sb.Append(",\"spacing_nm\":").Append(fsp.HasValue ? Json.Num(Math.Round(fsp.Value, 2)) : "null");
                    sb.Append(",\"members_n\":").Append(MembersOf(f).Count);
                    sb.Append('}');
                }
            }
            catch { }
        }


        private string UnitJson(object u, string source, bool full)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            var uid = D(Get(u, "UniqueID"));
            sb.Append("\"uid\":").Append(uid.HasValue ? Json.Num(uid.Value) : "null");
            sb.Append(",\"name\":").Append(Json.Str(RxGet(u, "Name") as string));
            sb.Append(",\"type\":").Append(Json.Str(u.GetType().Name));
            sb.Append(",\"class\":").Append(Json.Str(Get(u, "ClassName") as string));
            if (source != null) sb.Append(",\"source\":").Append(Json.Str(source));
            var own = Get(u, "IsPlayerObject");
            if (own is bool) sb.Append(",\"player\":").Append(Json.Bool((bool)own));
            var ctl = Get(u, "IsControllable");
            if (ctl is bool) sb.Append(",\"controllable\":").Append(Json.Bool((bool)ctl));

            var hdg = D(RxGet(u, "Heading"));
            sb.Append(",\"heading_deg\":").Append(hdg.HasValue ? Json.Num(Math.Round(((hdg.Value % 360) + 360) % 360, 1)) : "null");
            double? spd = null;
            try { spd = D(Call(u, "getVelocityInKnots")); } catch { }
            sb.Append(",\"speed_kn\":").Append(spd.HasValue ? Json.Num(Math.Round(spd.Value, 2)) : "null");

            try
            {
                var sc = RxGet(u, "SpeedCommand");
                if (sc != null)
                {
                    var dem = D(Call(sc, "GetDemandedSpeedInKnots", u));
                    sb.Append(",\"speed_cmd_kn\":").Append(dem.HasValue ? Json.Num(Math.Round(dem.Value, 2)) : "null");
                }
                else sb.Append(",\"speed_cmd_kn\":null");
            }
            catch { sb.Append(",\"speed_cmd_kn\":null"); }

            try
            {
                var cc = RxGet(u, "CourseCommand");
                if (cc != null)
                {
                    var dc = D(Call(cc, "GetDemandedCourse", u));
                    sb.Append(",\"course_cmd_deg\":").Append(dc.HasValue ? Json.Num(Math.Round(((dc.Value % 360) + 360) % 360, 1)) : "null");
                }
                else sb.Append(",\"course_cmd_deg\":null");
            }
            catch { sb.Append(",\"course_cmd_deg\":null"); }

            try
            {
                var tg = D(Call(u, "getTelegraph"));
                if (tg.HasValue) sb.Append(",\"telegraph\":").Append(Json.Num(tg.Value));
            }
            catch { }

            try
            {
                var pos = RxGet(u, "Position");
                if (pos != null)
                {
                    var lat = D(Get(pos, "_latitude")); var lon = D(Get(pos, "_longitude"));
                    if (lat.HasValue && lon.HasValue)
                        sb.Append(",\"lat\":").Append(Json.Num(lat.Value)).Append(",\"lon\":").Append(Json.Num(lon.Value));
                }
            }
            catch { }

            try
            {
                var wps = Waypoints(u);
                sb.Append(",\"has_waypoint\":").Append(Json.Bool(wps.Count > 0));
                sb.Append(",\"waypoints\":[");
                var here = RxGet(u, "Position");
                double? lat0 = D(Get(here, "_latitude")), lon0 = D(Get(here, "_longitude"));
                for (int i = 0; i < wps.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"lat\":").Append(Json.Num(wps[i][0])).Append(",\"lon\":").Append(Json.Num(wps[i][1]));
                    if (i == 0 && lat0.HasValue && lon0.HasValue)
                    {
                        sb.Append(",\"bearing_deg\":").Append(Json.Num(Math.Round(Bearing(lat0.Value, lon0.Value, wps[i][0], wps[i][1]), 1)));
                        sb.Append(",\"distance_nm\":").Append(Json.Num(Math.Round(DistanceNm(lat0.Value, lon0.Value, wps[i][0], wps[i][1]), 2)));
                    }
                    sb.Append('}');
                }
                sb.Append(']');
            }
            catch { }
            if (full) { AppendUnitSystems(sb, u); AppendWeapons(sb, u); }
            else if (own is bool && (bool)own)
            {
                // v0.7 (M3): integrity/fires/flooding and the last cause of damage for every own unit, not only the selected one
                try { AppendDamage(sb, u); } catch { }
            }
            // v0.9 (T-064): tactical flags of own ships/aircraft (selected unit and lists)
            if (own is bool && (bool)own && Capabilities.Contains("unit_flags") && UnitKindOfObj(u) != null)
            {
                try { sb.Append(",\"flags\":").Append(FlagsJson(u)); } catch { }
            }
            return sb.Append('}').ToString();
        }
    }
}
