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
        // Singleton<T> without auto-creating it
        private object SingletonIfExists(string typeName)
        {
            var t = FindType(typeName);
            if (t == null) return null;
            try
            {
                var ie = t.GetMethod("InstanceExists", BindingFlags.Static | BindingFlags.Public | BindingFlags.FlattenHierarchy);
                if (ie != null)
                {
                    var ps = ie.GetParameters();
                    if (!(bool)ie.Invoke(null, ps.Length == 0 ? null : new object[] { false })) return null;
                }
                var p = t.GetProperty("Instance", AllStat);
                var o = p == null ? null : p.GetValue(null, null);
                return IsAlive(o) ? o : null;
            }
            catch { return null; }
        }

        /// <summary>Type name of the current menu window (MainMenuView, ScenarioMenuView, BlankMenuView, ...), or null.</summary>
        public string WindowName()
        {
            var ms = SingletonIfExists("SeapowerUI.ViewModels.MenuSystemViewModel");
            var w = ms == null ? null : Get(ms, "CurrentWindow");
            return w == null ? null : w.GetType().Name;
        }

        public bool IsLoading(out double percent, out string text)
        {
            percent = -1; text = null;
            var ls = SingletonIfExists("SeaPower.LoadScreen");
            if (ls == null) return false;
            var p = D(Get(ls, "LoadPercent"));
            text = Get(ls, "LoadText") as string;
            if (p.HasValue) percent = p.Value;
            // the load screen keeps 100 % / "Done" after the last load; 0..100 exclusive = a load is running
            return percent > 0 && percent < 100;
        }

        /// <summary>Message boxes shown on the load screen (FileManager.CheckCompatibility / CheckSafeMode).</summary>
        private List<object> LoadScreenBoxes()
        {
            var res = new List<object>();
            var ls = SingletonIfExists("SeaPower.LoadScreen");
            if (ls == null) return res;
            var host = Get(ls, "WindowHost");
            var wins = Get(host, "Windows") as IEnumerable;
            if (wins == null) return res;
            foreach (var w in wins) if (w != null && w.GetType().Name == "MessageBoxViewModel") res.Add(w);
            return res;
        }

        /// <summary>One of: loading, briefing, debrief, pause_menu, mission, main_menu, scenarios_list, other_menu, unknown.</summary>
        public string ScreenName()
        {
            double pc; string tx;
            string w = WindowName();
            try { if (LoadScreenBoxes().Count > 0) return "dialog"; } catch { }
            if (IsLoading(out pc, out tx)) return "loading";
            if (w == "MissionBriefView") return "briefing";
            if (InMission())
            {
                if (w == "MissionDebriefView") return "debrief";
                if (w == "PauseMenuView") return "pause_menu";
                return "mission";
            }
            if (w == null) return "unknown";
            if (w == "MainMenuView") return "main_menu";
            if (w == "ScenarioMenuView") return "scenarios_list";
            if (w == "BlankMenuView") return "loading";
            return "other_menu";
        }

        /// <summary>
        /// Seconds since the current mission started. The game never resets GameTime.missionElapsedTime
        /// (Restart Mission keeps counting), but GameInitializer.init resets missionSessionTime.
        /// Fallback: elapsed time minus the value seen when the mod saw the mission start.
        /// </summary>
        public double? MissionTimeS()
        {
            var st = D(GetStatic("SeaPower.GameTime", "missionSessionTime"));
            if (st.HasValue) return st.Value;
            var mt = D(GetStatic("SeaPower.GameTime", "missionElapsedTime"));
            if (!mt.HasValue) return null;
            return Math.Max(0, mt.Value - _missionTimeBase);
        }
        private double _missionTimeBase;
    }
}
