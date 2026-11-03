namespace VoiceBotBridge.Dev
{
    /// <summary>T-282: decides when the dev channel may open: not during the load/JIT window. Pure logic, no Unity (time is passed in).</summary>
    internal sealed class DevStartGate
    {
        private readonly double _min, _quiet, _max;
        private bool _ready;
        private double _lastScene;
        private bool _fired;

        /// <summary>0/0/0 = start at once (the old behaviour).</summary>
        public DevStartGate(double minDelayS, double quietS, double maxDelayS) { _min = minDelayS; _quiet = quietS; _max = maxDelayS; }

        public string Why { get; private set; }

        /// <summary>T-285: build indexes of the main menu (1, additive over the boot scene 0) and the mission scene (GameMain, 2); other scenes (splash, additive UI) do not open the gate.</summary>
        public static bool IsReadyScene(int buildIndex) { return buildIndex == 1 || buildIndex == 2; }

        public bool Ready { get { return _ready; } }

        /// <summary>Any scene load restarts the quiet window; only a main-menu/mission scene marks the game ready.</summary>
        public void OnSceneLoaded(double nowS, bool ready) { _lastScene = nowS; if (ready) _ready = true; }

        /// <summary>Menu/mission scene was already active when we looked (loaded before the hook); starts quiet from now if not yet ready.</summary>
        public void MarkReady(double nowS) { if (_ready) return; _ready = true; _lastScene = nowS; }

        /// <summary>True exactly once.</summary>
        public bool ShouldStart(double nowS)
        {
            if (_fired) return false;
            string why = null;
            if (_min <= 0 && _quiet <= 0 && _max <= 0) why = "immediate";
            else if (_max > 0 && nowS >= _max) why = "max_delay";
            else if (nowS >= _min && _ready && nowS - _lastScene >= _quiet) why = "quiet";
            if (why == null) return false;
            _fired = true; Why = why;
            return true;
        }

        public static string SelfTest()
        {
            var g = new DevStartGate(0, 0, 0);
            if (!g.ShouldStart(0) || g.Why != "immediate") return "immediate";
            if (g.ShouldStart(1)) return "second call must be false";
            g = new DevStartGate(20, 5, 180);
            if (g.ShouldStart(100)) return "no scene before max";
            if (!g.ShouldStart(180) || g.Why != "max_delay") return "max_delay";
            g = new DevStartGate(20, 5, 180);
            g.OnSceneLoaded(10, false);
            if (g.ShouldStart(100)) return "non-menu scene must not open the gate";
            g.OnSceneLoaded(30, true);
            if (g.ShouldStart(34.9)) return "quiet after menu scene";
            if (!g.ShouldStart(35) || g.Why != "quiet") return "quiet at 35";
            if (g.ShouldStart(36)) return "once only";
            g = new DevStartGate(20, 5, 180);
            g.OnSceneLoaded(10, true);
            if (g.ShouldStart(19)) return "min delay at 19";
            if (!g.ShouldStart(20)) return "quiet at 20";
            g = new DevStartGate(20, 5, 180);
            g.OnSceneLoaded(10, true);
            g.OnSceneLoaded(30, false);
            if (g.ShouldStart(34)) return "any scene resets quiet";
            if (!g.ShouldStart(35)) return "quiet after reset";
            g = new DevStartGate(20, 5, 180);
            g.MarkReady(50);
            g.MarkReady(60);
            if (g.ShouldStart(54)) return "MarkReady quiet";
            if (!g.ShouldStart(55)) return "MarkReady at 55";
            if (!IsReadyScene(1) || !IsReadyScene(2) || IsReadyScene(5) || IsReadyScene(0)) return "IsReadyScene";
            return null;
        }
    }
}
