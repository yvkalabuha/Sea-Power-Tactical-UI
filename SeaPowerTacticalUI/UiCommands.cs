using System;
using System.Collections.Generic;
using System.Reflection;

namespace VoiceBotBridge
{
    /// <summary>Interface mod implementations of the GameApi hooks (ADR-0033). No HarmonyLib / BepInEx / IMGUI references: compiled into the UI selfcheck.</summary>
    internal sealed partial class GameApi
    {
        private partial string ExecuteMod(string id, string name, Dictionary<string, object> cmd)
        {
            switch (name)
            {
                case "radar_coverage": return RadarCoverageCmd(id, cmd);
                case "radar_overlay": return RadarOverlayCmd(id, cmd);
                case "ring_group": return RingGroupCmd(id, cmd);   // T-328
                case "overview": return OverviewCmd(id, cmd);
                case "ui_window": return UiWindowCmd(id, cmd);
                case "ui_undo": return UiUndoCmd(id, cmd);
                case "center_map": return CenterMapCmd(id, cmd);
                case "select_contact": return SelectContactCmd(id, cmd);
                case "drop_sonobuoy": return DropSonobuoyCmd(id, cmd);
                case "dip_sonar": return DipSonarCmd(id, cmd);
                case "set_sonobuoy_depth": return SetSonobuoyDepthCmd(id, cmd);
                case "asw_group":
                case "asw_search_course":
                case "asw_search_area":
                case "asw_stop":
                case "asw_depth":
                case "asw_attack":
                case "asw_attack_cancel":
                case "asw_order":
                case "asw_strike_skip":
                case "asw_strike_resume":
                case "asw_restore":
                case "asw_set_tactics":
                case "asw_loadout":
                case "asw_preview":
                case "asw_preview_clear": return AswCmd(id, name, cmd);
                case "sar_order":
                case "sar_ship": return AswCmd(id, name, cmd);
                case "cap_group":
                case "cap_order":
                case "cap_loadout":
                case "cap_params":
                case "cap_scramble":
                case "cap_stop": return CapCmd(id, name, cmd);
                case "strike_group":   // T-341
                case "strike_target":
                case "strike_plan":
                case "strike_goal":   // T-342
                case "strike_deck_cancel":   // T-342 B
                case "strike_deck_reorder":
                case "strike_auto":   // T-342 C
                case "strike_cancel":
                case "strike_launch":
                case "strike_probe": return StrikeCmd(id, name, cmd);
                case "qa_force": return QaForceCmd(id, cmd);   // T-361 (ADR-0136): dev only, refused without the diag slot
                case "set_doctrine": return SetDoctrineCmd(id, cmd);
                case "doctrine": return DoctrineCmd(id, cmd);
                case "smart_attack":
                case "smart_attack_cancel":
                case "smart_attack_status": return SmartAttackCmd(id, name, cmd);
                case "intercept":
                case "intercept_cancel":
                case "intercept_status": return InterceptCmd(id, name, cmd);
                case "range_rings":
                case "contact_info": return LayerSwitchCmd(id, name, cmd);
                case "air_evade":
                case "air_evade_status": return AirEvadeCmd(id, name, cmd);
                case "air_guard_ignore":
                case "air_guard_state": return AirGuardCmd(id, name, cmd);
                case "sub_evade":
                case "sub_evade_status": return SubEvadeCmd(id, name, cmd);
                case "sub_datum_list":
                case "sub_datum_search":
                case "sub_datum_dismiss": return SubDatumCmd(id, name, cmd);
                default: return null;
            }
        }

        private partial void SelfCheckMod()
        {
            // T-120 (ADR-0043): the checks and the capabilities come from the registry only (the *Specs.cs of the modules, UiRegistry.All())
            LoadNoesisOffline();   // selfcheck only: the Noesis assembly is not loaded yet
            var report = RegistryReport.Run(this, UiSpecs ?? UiRegistry.All());
            SetRegistry(report);
            CoverageMembers = 0;
            foreach (var c in report.Checks) if (c.Capability == "radar_coverage" && c.Present) CoverageMembers++;
            Checks.AddRange(report.Checks);
            Capabilities.UnionWith(report.Capabilities);
        }

        /// <summary>v0.16 (T-103): the game's getText as the first source of UiText and the game's language for [Ui] Language=auto. True once the language table exists.</summary>
        internal bool BindUiText()
        {
            UiText.GameLang = DoctrineLang;
            var h = Singleton(CoreSpecs.LanguageHandler);
            if (h == null) return false;
            UiText.GameLookup = GameTextLookup(h);
            try { _langSub?.Dispose(); } catch { }
            _langSub = UiText.SubscribeLanguageChanged(h, Member(TextSpecs.LanguageChanged) as EventInfo); // null = no event: the plugin polls at cheap moments instead
            return true;
        }

        /// <summary>The game's getText(section, key) as a lookup for UiText; null without the handler or without the method.</summary>
        internal Func<string, string, string> GameTextLookup(object handler) { return UiText.MakeGameLookup(handler, Member(TextSpecs.GetText) as MethodInfo); }

        /// <summary>One line into the game's own alert list (the top of the screen). false = the alert member or the view model is not there (the caller shows its own line).</summary>
        internal bool RaiseGameAlert(string text)
        {
            try
            {
                var vm = GetStatic(CoreSpecs.MainViewModel);
                if (vm == null || !Present(CoreSpecs.RaiseAlertText)) return false;
                Call(vm, CoreSpecs.RaiseAlertText, text);
                return true;
            }
            catch { return false; }
        }

        private IDisposable _langSub;

        /// <summary>True when the game's LanguageChanged event is subscribed (no polling needed).</summary>
        internal bool LanguageEventBound { get { return _langSub != null; } }

        /// <summary>Unsubscribes from the game's LanguageChanged event (plugin unload).</summary>
        internal void UnbindUiText()
        {
            try { _langSub?.Dispose(); } catch { }
            _langSub = null;
        }

        /// <summary>Own aircraft for link_state.flags_by_uid (AirUnits is private to Common).</summary>
        internal List<object> AirUnitsForLink() { return AirUnits(); }

        internal partial bool ModFlagOn(string flag) { return flag == "overview" ? OverviewOn : OverlayOn; }

        internal partial void SetModFlag(string flag, bool on, string source)
        {
            if (flag == "overview") SetOverview(on, source); else SetOverlay(on, source);
        }
    }
}
