using System;
using System.Collections.Generic;
using System.Text;

namespace VoiceBotBridge
{
    // T-098 C7 (ADR-0037 section 2): implementations of the partial-void hooks declared in Common/Tactics.cs and Common/RestoreControl.cs,
    // plus the interface-mod commands set_doctrine / doctrine. The voice mod has none of this: the compiler removes the hook calls there.
    // No patching library or engine references (compiled by the offline selfcheck-ui). Routing of the two commands into UiCommands.ExecuteMod is done by the integrator.
    internal sealed partial class GameApi
    {
        private bool _vetoMerged;

        partial void FlagStoreResolve(object u, string flag, ref bool? value)
        {
            var s = FlagStore;
            if (s == null) return;
            try
            {
                string key = VbKeys.KeyOfFlag(flag);
                if (key != null) value = s.Resolve(u, key);
            }
            catch { }
        }

        partial void FlagStorePostLaunch(object u, ref string mode)
        {
            var s = FlagStore;
            if (s == null) return;
            try { mode = FlagProtocol.ResolvePostLaunch(s, u); } catch { }
        }

        partial void FlagStoreSource(object u, string flag, ref string src)
        {
            var s = FlagStore;
            if (s == null) return;
            try
            {
                string key = flag == "post_launch" ? VbKeys.PostLaunchBreak : VbKeys.KeyOfFlag(flag);
                if (key == null) return;
                src = SourceText(s, u, key);
            }
            catch { }
        }

        /// <summary>unit | group | global | default (global that still holds the built-in value).</summary>
        private string SourceText(IFlagStore s, object u, string key)
        {
            string src = s.Source(u, key);
            if (src != "global") return src;
            var tf = PlayerTaskforce();
            return tf != null && (s.Get(tf, DoctrineLevel.Global, key) ?? VbKeys.Default(key)) == VbKeys.Default(key) ? "default" : "global";
        }

        partial void FlagStoreDefaultsJson(ref string json)
        {
            var s = FlagStore;
            if (s == null) return;
            try { var tf = PlayerTaskforce(); if (tf != null) json = FlagProtocol.DefaultsJson(s, tf); } catch { }
        }

        partial void FlagStoreSetUnit(object u, bool? autoEvade, bool? vetoNeutral, string postLaunch)
        {
            var s = FlagStore;
            if (s == null) return;
            try { FlagProtocol.ApplySetFlags(s, u, autoEvade, vetoNeutral, postLaunch); } catch { }
        }

        partial void FlagStoreSetDefaults(bool? shipAutoEvade, bool? shipVeto, bool? airVeto, string airPostLaunch)
        {
            var s = FlagStore;
            if (s == null) return;
            try
            {
                var tf = PlayerTaskforce();
                if (tf == null) return;
                FlagProtocol.ApplyDefaults(s, tf, shipAutoEvade, shipVeto, airVeto, airPostLaunch, out _vetoMerged);
                if (_vetoMerged) Log("[Doctrine] set_doctrine_defaults: ship/air veto_neutral differ, merged into one VB_VetoNeutral (OR)");
            }
            catch { }
        }

        partial void FlagStoreMissionReset()
        {
            try { DoctrineMissionReset(); } catch { }
        }

        /// <summary>"Return control": the unit's own overrides go off (ship: auto evade and air defense ready; aircraft: both post-launch bits).</summary>
        partial void FlagStoreRestoreControl(object u, string kind)
        {
            var s = FlagStore;
            if (s == null) return;
            try
            {
                if (kind == "ship")
                {
                    s.Set(u, DoctrineLevel.Unit, VbKeys.AutoEvade, false);
                    s.Set(u, DoctrineLevel.Unit, VbKeys.AirDefenseReady, false);
                }
                else if (kind == "air")
                {
                    s.Set(u, DoctrineLevel.Unit, VbKeys.PostLaunchBreak, false);
                    s.Set(u, DoctrineLevel.Unit, VbKeys.PostLaunchRetarget, false);
                }
            }
            catch { }
        }

        // ---------- commands ----------

        private static readonly string[] DoctrineFlagNames = { "auto_evade", "veto_neutral", "air_defense_ready", "post_launch" };

        private static string KindApplyText(string flag) { return flag == "post_launch" ? "post_launch is for aircraft" : flag == "air_defense_ready" ? "air_defense_ready is for ships" : "not for this unit type"; }

        private bool FlagApplies(string flag, string kind4)
        {
            if (flag == "post_launch") return VbKeys.AppliesTo(VbKeys.PostLaunchBreak, kind4);
            string key = VbKeys.KeyOfFlag(flag);
            return key != null && VbKeys.AppliesTo(key, kind4);
        }

        private object OwnerAtLevel(object u, DoctrineLevel level)
        {
            if (level == DoctrineLevel.Unit) return u;
            if (level == DoctrineLevel.Group) return Get(u, DoctrineSpecs.ObjFormation);
            return PlayerTaskforce();
        }

        /// <summary>Resolved value of a flag for a unit: bool, or the mode text for post_launch.</summary>
        private string ResolvedJson(IFlagStore s, object u, string flag)
        {
            if (flag == "post_launch") return Json.Str(FlagProtocol.ResolvePostLaunch(s, u));
            return Json.Bool(s.Resolve(u, VbKeys.KeyOfFlag(flag)));
        }

        private string LevelValueJson(IFlagStore s, object owner, DoctrineLevel level, string flag)
        {
            if (owner == null) return "null";
            if (flag == "post_launch")
            {
                bool? b = s.Get(owner, level, VbKeys.PostLaunchBreak), r = s.Get(owner, level, VbKeys.PostLaunchRetarget);
                if (!b.HasValue && !r.HasValue) return "null";
                return Json.Str(PostLaunchBits.ToMode(b ?? false, r ?? false));
            }
            bool? v = s.Get(owner, level, VbKeys.KeyOfFlag(flag));
            return v.HasValue ? Json.Bool(v.Value) : "null";
        }

        /// <summary>set_doctrine {uid, level?, flag, value}: see PROTOCOL.md "0.16.0". Argument errors come first (also outside a mission).</summary>
        internal string SetDoctrineCmd(string id, Dictionary<string, object> cmd)
        {
            DoctrineArgs a;
            var perr = DoctrineArgs.Parse(cmd, false, out a);
            if (perr != null) return Err(id, "bad_args", perr);
            if (!Frame.InMission) return Err(id, "not_in_session", null);
            EnsureFlagStore();
            var s = FlagStore;
            if (s == null) return Err(id, "unsupported", "flag store");
            Need("unit_flags");
            if (a.Flag == "post_launch" && a.Value is string && (string)a.Value == "retarget")
            {
                if (!AllowAttack) return Err(id, "attack_disabled", "AllowAttack=false in mod config");
                if (!Confirmed(cmd)) return Err(id, "confirm_required", "post_launch \"retarget\" needs \"confirm\":true");
            }
            string err; var u = Target(cmd, out err);
            if (u == null) return Err(id, err, null);
            string kind4 = UnitKind4(u);
            if (kind4 == null || !FlagApplies(a.Flag, kind4)) return Err(id, "not_applicable", KindApplyText(a.Flag));
            var owner = OwnerAtLevel(u, a.Level);
            if (owner == null) return Err(id, "not_applicable", "the unit is not in a group");
            TacticsEnsureMission();
            string before = ResolvedJson(s, u, a.Flag);
            if (a.Flag == "post_launch")
            {
                if (a.Value == null) { s.Set(owner, a.Level, VbKeys.PostLaunchBreak, null); s.Set(owner, a.Level, VbKeys.PostLaunchRetarget, null); }
                else
                {
                    bool b, r;
                    PostLaunchBits.FromMode((string)a.Value, out b, out r);
                    s.Set(owner, a.Level, VbKeys.PostLaunchBreak, b);
                    s.Set(owner, a.Level, VbKeys.PostLaunchRetarget, r);
                }
            }
            else s.Set(owner, a.Level, VbKeys.KeyOfFlag(a.Flag), a.Value == null ? (bool?)null : (bool)a.Value);
            string key = a.Flag == "post_launch" ? VbKeys.PostLaunchBreak : VbKeys.KeyOfFlag(a.Flag);
            string levelName = a.Level == DoctrineLevel.Unit ? "unit" : a.Level == DoctrineLevel.Group ? "group" : "global";
            string valueJson = a.Value == null ? "null" : a.Value is bool ? Json.Bool((bool)a.Value) : Json.Str((string)a.Value);
            string resolved = ResolvedJson(s, u, a.Flag);
            Log("[Doctrine] set_doctrine uid=" + UidOf(u) + " level=" + levelName + " flag=" + a.Flag + " value=" + valueJson + " resolved=" + resolved + " (was " + before + ")");
            return Ok(id, "\"uid\":" + UidOf(u) + ",\"level\":" + Json.Str(levelName) + ",\"flag\":" + Json.Str(a.Flag) + ",\"value\":" + valueJson +
                      ",\"resolved\":" + resolved + ",\"src\":" + Json.Str(SourceText(s, u, key)) + ",\"previous\":" + before);
        }

        /// <summary>doctrine {uid}: the three levels of the unit. "unit" = resolved value and source of every flag that applies to its type; "group"/"global" = what is set there (null = not set).</summary>
        internal string DoctrineCmd(string id, Dictionary<string, object> cmd)
        {
            DoctrineArgs a;
            var perr = DoctrineArgs.Parse(cmd, true, out a);
            if (perr != null) return Err(id, "bad_args", perr);
            if (!Frame.InMission) return Err(id, "not_in_session", null);
            EnsureFlagStore();
            var s = FlagStore;
            if (s == null) return Err(id, "unsupported", "flag store");
            string err; var u = Target(cmd, out err);
            if (u == null) return Err(id, err, null);
            string kind4 = UnitKind4(u);
            var group = Get(u, DoctrineSpecs.ObjFormation);
            var tf = PlayerTaskforce();
            var unit = new StringBuilder("{");
            var grp = new StringBuilder("{");
            var glob = new StringBuilder("{");
            bool f1 = true, f2 = true, f3 = true;
            foreach (var flag in DoctrineFlagNames)
            {
                string key = flag == "post_launch" ? VbKeys.PostLaunchBreak : VbKeys.KeyOfFlag(flag);
                if (FlagApplies(flag, kind4))
                {
                    if (!f1) unit.Append(','); f1 = false;
                    unit.Append(Json.Str(flag)).Append(":{\"value\":").Append(ResolvedJson(s, u, flag)).Append(",\"src\":").Append(Json.Str(SourceText(s, u, key))).Append('}');
                }
                if (group != null) { if (!f2) grp.Append(','); f2 = false; grp.Append(Json.Str(flag)).Append(':').Append(LevelValueJson(s, group, DoctrineLevel.Group, flag)); }
                if (!f3) glob.Append(','); f3 = false;
                glob.Append(Json.Str(flag)).Append(':');
                if (flag == "post_launch" && tf != null)
                    glob.Append(Json.Str(PostLaunchBits.ToMode(s.Get(tf, DoctrineLevel.Global, VbKeys.PostLaunchBreak) ?? false, s.Get(tf, DoctrineLevel.Global, VbKeys.PostLaunchRetarget) ?? false)));
                else glob.Append(LevelValueJson(s, tf, DoctrineLevel.Global, flag));
            }
            return Ok(id, "\"uid\":" + UidOf(u) + ",\"kind\":" + Json.Str(kind4) + ",\"store\":" + Json.Str(s.Name) + ",\"unit\":" + unit.Append('}') +
                      ",\"group\":" + (group == null ? "null" : grp.Append('}').ToString()) + ",\"global\":" + glob.Append('}'));
        }
    }
}
