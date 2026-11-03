namespace VoiceBotBridge
{
    /// <summary>T-228: state of one map legend: the permanent config key plus the session check of the map menu. Pure, no Unity.</summary>
    internal sealed class LegendSwitch
    {
        private bool _cfgOn, _sessionOn = true;

        /// <param name="id">"rings" | "radar" (diagnostics, log)</param>
        public LegendSwitch(string id, bool cfgOn) { Id = id; _cfgOn = cfgOn; }

        public string Id { get; private set; }
        /// <summary>The permanent key ([RangeRings] Legend / [RadarCoverage] Legend).</summary>
        public bool CfgOn { get { return _cfgOn; } }
        /// <summary>The menu check; true at start, never saved.</summary>
        public bool SessionOn { get { return _sessionOn; } }
        /// <summary>The legend is drawn.</summary>
        public bool Visible { get { return _cfgOn && _sessionOn; } }
        /// <summary>The map menu item is active.</summary>
        public bool MenuEnabled { get { return _cfgOn; } }
        /// <summary>Incremented when Visible or MenuEnabled changes.</summary>
        public int Version { get; private set; }

        /// <summary>The key changed in Settings. Off -> on gives the session check back as true (T-228-Q2).</summary>
        public void SetCfg(bool on)
        {
            if (on == _cfgOn) return;
            _cfgOn = on;
            if (on) _sessionOn = true;
            Version++;
        }

        /// <summary>The menu check was clicked. Ignored while the key is off. true = Visible changed.</summary>
        public bool SetSession(bool on)
        {
            if (!_cfgOn || on == _sessionOn) return false;
            _sessionOn = on;
            Version++;
            return true;
        }

        public string DiagJson()
        {
            return "{\"cfg\":" + B(_cfgOn) + ",\"session\":" + B(_sessionOn) + ",\"visible\":" + B(Visible) + "}";
        }

        private static string B(bool v) { return v ? "true" : "false"; }

        /// <summary>null = ok, else the first failed check.</summary>
        public static string SelfTest()
        {
            var s = new LegendSwitch("radar", true);
            if (!s.Visible || !s.MenuEnabled || !s.SessionOn || s.Version != 0) return "default visible";
            if (!s.SetSession(false) || s.Visible || s.Version != 1) return "session off";
            if (s.SetSession(false) || s.Version != 1) return "session repeat";
            s.SetCfg(false);
            if (s.MenuEnabled || s.Visible || s.Version != 2) return "cfg off";
            if (s.SetSession(true)) return "session ignored while cfg off";
            if (s.Visible) return "still hidden";
            s.SetCfg(true);
            if (!s.SessionOn || !s.Visible || !s.MenuEnabled || s.Version != 3) return "cfg on resets session";
            s.SetCfg(true);
            if (s.Version != 3) return "same cfg keeps version";
            if (s.DiagJson() != "{\"cfg\":true,\"session\":true,\"visible\":true}") return "diag " + s.DiagJson();
            s.SetSession(false);
            if (s.DiagJson() != "{\"cfg\":true,\"session\":false,\"visible\":false}") return "diag off " + s.DiagJson();
            return null;
        }
    }

    /// <summary>T-228: the range-ring module gives its legend switch to the map menu.</summary>
    internal interface IRingLegend { LegendSwitch Legend { get; } }

    /// <summary>T-228: the radar module gives its legend switch to the map menu (null when the overlay is off).</summary>
    internal interface IRadarLegend { LegendSwitch Legend { get; } }
}
