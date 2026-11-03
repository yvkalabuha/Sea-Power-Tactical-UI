using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using VoiceBotBridge.Ui;

namespace VoiceBotBridge
{
    /// <summary>
    /// Game-side data for the dev diag slot (ADR-0046, T-139). Read only. Uses only helpers that already exist
    /// (InMission, FindContact, IsGone, Relation, Kind, FindOwnUnit, GameTime): its members are in CoreSpecs (T-120 M17).
    /// </summary>
    internal sealed partial class GameApi
    {
        /// <summary>T-129: an event of the UI mod (feature_degraded) into the event ring when the event pump exists; never throws.</summary>
        internal void UiEvent(string name, string dataJson)
        {
            try { if (EventsAvailable) EvLine(name, dataJson); } catch { }
        }

        internal string DiagMissionJson()
        {
            bool inMission = Frame.InMission;
            var sb = new StringBuilder("{\"in_mission\":").Append(Json.Bool(inMission));
            if (inMission)
            {
                try { var p = CallStatic(CoreSpecs.GamePaused); if (p is bool) sb.Append(",\"paused\":").Append(Json.Bool((bool)p)); } catch { }
                try { var tc = D(GetStatic(CoreSpecs.TimeCompression)); if (tc.HasValue) sb.Append(",\"time_compression\":").Append(Json.Num(tc.Value)); } catch { }
            }
            return sb.Append('}').ToString();
        }

        /// <summary>Track of the player's PlottingTable by id: {"rel","class","kind","map_hostile"}; null = no such track.</summary>
        internal string DiagRelationJson(int contactId)
        {
            if (!Frame.InMission) return null;
            var v = FindContact(contactId);
            if (v == null || IsGone(v)) return null;
            string cls = null;
            try { cls = Get(Get(v, CoreSpecs.VehClass), CoreSpecs.SourcedValue) as string; } catch { }
            return "{\"rel\":" + Json.Str(Relation(v)) + ",\"class\":" + Json.Str(cls) + ",\"kind\":" + Json.Str(Kind(v)) +
                   ",\"map_hostile\":" + Json.Bool(MapHostileOf(v)) + "}";
        }

        internal string DiagUnitJson(int uid)
        {
            if (!Frame.InMission) return null;
            var u = FindOwnUnit(uid);
            if (u == null) return null;
            string name = null;
            try { name = RxGet(u, CoreSpecs.ObjName) as string; } catch { }
            return "{\"uid\":" + uid + ",\"name\":" + Json.Str(name) + "}";
        }

        /// <summary>Own player units for the dev channel (section "units"): uid (UniqueID) to target intercept / select_unit / air_evade.
        /// kind: ship | sub | air | helo. Read only; at most `limit` rows.</summary>
        internal string DiagUnitsJson(int limit)
        {
            var sb = new StringBuilder("{\"units\":[");
            int n = 0, total = 0;
            if (Frame.InMission)
            {
                var tf = GetStatic(CoreSpecs.PlayerTaskforce);
                var lists = new[] { CoreSpecs.TaskforceVessels, CoreSpecs.TaskforceSubmarines, RangeRingSpecs.TfAircraft, RangeRingSpecs.TfHelicopters };
                var kinds = new[] { "ship", "sub", "air", "helo" };
                for (int li = 0; li < lists.Length; li++)
                {
                    var list = Get(tf, lists[li]) as IEnumerable;
                    if (list == null) continue;
                    foreach (var u in list)
                    {
                        if (u == null) continue;
                        total++;
                        if (n >= limit) continue;
                        try
                        {
                            var uid = D(Get(u, RangeRingSpecs.ObjUniqueId));
                            if (!uid.HasValue) continue;
                            if (n++ > 0) sb.Append(',');
                            sb.Append("{\"uid\":").Append(Json.Num(uid.Value));
                            sb.Append(",\"name\":").Append(Json.Str(RxGet(u, CoreSpecs.ObjName) as string));
                            sb.Append(",\"type\":").Append(Json.Str(u.GetType().Name));
                            sb.Append(",\"class\":").Append(Json.Str(Get(u, SmartAttackSpecs.ObjClassName) as string));
                            sb.Append(",\"kind\":").Append(Json.Str(kinds[li]));
                            var pos = RxGet(u, SmartAttackSpecs.ObjPosition);
                            var lat = pos == null ? null : D(Get(pos, CoreSpecs.GeoLat)); var lon = pos == null ? null : D(Get(pos, CoreSpecs.GeoLon));
                            if (lat.HasValue && lon.HasValue)
                                sb.Append(",\"lat\":").Append(Json.Num(Math.Round(lat.Value, 4))).Append(",\"lon\":").Append(Json.Num(Math.Round(lon.Value, 4)));
                            sb.Append('}');
                        }
                        catch { }
                    }
                }
            }
            return sb.Append("],\"total\":").Append(total).Append('}').ToString();
        }

        internal string DiagSelfcheckJson()
        {
            var sb = new StringBuilder("{\"checks\":").Append(Checks.Count);
            var missing = new List<string>();
            foreach (var c in Checks)
                if (!c.Present) missing.Add("{\"member\":" + Json.Str(c.Member) + ",\"capability\":" + Json.Str(c.Capability) + ",\"required\":" + Json.Bool(c.Required) + "}");
            sb.Append(",\"missing\":").Append(missing.Count).Append(",\"missing_list\":[").Append(string.Join(",", missing.ToArray())).Append("]");
            var caps = new List<string>(Capabilities);
            caps.Sort(StringComparer.Ordinal);
            var q = new List<string>();
            foreach (var c in caps) q.Add(Json.Str(c));
            return sb.Append(",\"capabilities\":[").Append(string.Join(",", q.ToArray())).Append("]}").ToString();
        }

        /// <summary>Capabilities that have at least one missing member (the mod runs degraded or the feature is off).</summary>
        internal string DiagDegradationJson()
        {
            var seen = new List<string>();
            foreach (var c in Checks)
                if (!c.Present && c.Capability != null && !seen.Contains(c.Capability)) seen.Add(Json.Str(c.Capability));
            return "{\"capabilities\":[" + string.Join(",", seen.ToArray()) + "]}";
        }

        internal string DiagWindowsJson()
        {
            var rects = ModUiRects.Named();
            var sb = new StringBuilder("[");
            Action<string, bool, bool> add = (name, open, first) =>
            {
                if (!first) sb.Append(',');
                sb.Append("{\"name\":").Append(Json.Str(name)).Append(",\"open\":").Append(Json.Bool(open));
                double[] r;
                if (rects.TryGetValue(name, out r)) sb.Append(",\"rect\":[").Append(Json.Num(r[0])).Append(',').Append(Json.Num(r[1])).Append(',').Append(Json.Num(r[2])).Append(',').Append(Json.Num(r[3])).Append(']');
                sb.Append('}');
            };
            add("overview", OverviewOn, true);
            add("radar", OverlayOn, false);
            add("asw", AswWindow != null && AswWindow.Visible, false);
            add("cap", CapWindow != null && CapWindow.Visible, false);
            add("strike", StrikeWindow != null && StrikeWindow.Visible, false);   // T-341
            // T-240: the game popup the mod windows yield to
            var gp = UiWindowPrefs.Popup;
            sb.Append(",{\"name\":\"game_popup\",\"open\":").Append(Json.Bool(gp.Open)).Append(",\"src\":").Append(gp.Open ? Json.Str(gp.Source) : "null").Append(",\"rect\":");
            if (gp.Open && gp.Rect != null) sb.Append('[').Append(Json.Num(gp.Rect[0])).Append(',').Append(Json.Num(gp.Rect[1])).Append(',').Append(Json.Num(gp.Rect[2])).Append(',').Append(Json.Num(gp.Rect[3])).Append(']');
            else sb.Append("null");
            sb.Append('}');
            return sb.Append(']').ToString();
        }
    }
}

namespace VoiceBotBridge.Ui
{
    /// <summary>Fills DiagSources from the live GameApi; each section only if its capability exists.</summary>
    internal static class DiagWiring
    {
        internal static DiagSources Build(UiContext ctx)
        {
            var api = ctx.Api;
            var s = new DiagSources();
            s.Mission = api.DiagMissionJson;
            s.Selfcheck = api.DiagSelfcheckJson;
            s.Degradation = api.DiagDegradationJson;
            s.Windows = api.DiagWindowsJson;
            s.Relation = api.DiagRelationJson;
            s.Unit = api.DiagUnitJson;
            Action<string, string, Func<string>> add = (section, cap, f) =>
            {
                if (api.Capabilities.Contains(cap)) s.Sections[section] = lim => f();
            };
            add("asw", "asw", api.AswStateJson);
            add("sar", "asw", api.AswStateJson);   // SAR groups are ASW groups with a "sar" order: the text format splits them by "order"
            add("cap", "cap", api.CapStateJson);
            add("smart_attack", "smart_attack", api.SmartAttackStateJson);
            add("radar", "radar_overlay", api.OverlayJson);
            add("overview", "overview", api.OverviewStateJson);
            if (api.Capabilities.Contains("units")) s.Sections["units"] = api.DiagUnitsJson;
            return s;
        }
    }
}
