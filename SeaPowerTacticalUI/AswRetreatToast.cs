using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-180: one short notice per group when its machines start to retreat from an air threat and one when they go back to the task. Pure (no game, no Unity).
    /// The retreat notice comes at most once per MinGapS for a group (a flapping threat does not spam); the return notice only follows a retreat notice that was shown.
    /// </summary>
    internal sealed class AswRetreatToastFeed
    {
        private sealed class St { public bool Retreating, Shown; public double LastShowS = -1e9; }

        public double MinGapS = 30;
        private readonly Dictionary<string, St> _st = new Dictionary<string, St>();

        public static string KeyOf(string group) { return "asw_retreat:" + group; }

        public void Apply(AswView v, IUiToasts toasts, bool on, double hideAfterS, double now)
        {
            if (toasts == null || v == null) return;
            if (!on) { _st.Clear(); return; }
            var alive = new HashSet<string>();
            foreach (var g in v.Groups)
            {
                alive.Add(g.Name);
                bool cur = g.Retreat != null && g.Retreat.Members > 0;
                St s;
                if (!_st.TryGetValue(g.Name, out s)) { s = new St(); _st[g.Name] = s; }
                if (cur == s.Retreating) continue;
                s.Retreating = cur;
                if (cur)
                {
                    s.Shown = false;
                    if (now - s.LastShowS < MinGapS) continue;
                    s.Shown = true; s.LastShowS = now;
                    string text = g.Retreat.Cover != null ? UiText.Fmt("asw.toast.retreat", g.Name, g.Retreat.Cover) : UiText.Fmt("asw.toast.retreat.away", g.Name);
                    toasts.Show(KeyOf(g.Name), "asw_retreat", Body(text, UiTone.Warn), now, hideAfterS, true);
                }
                else if (s.Shown)
                {
                    s.Shown = false;
                    toasts.Show(KeyOf(g.Name), "asw_retreat", Body(UiText.Fmt("asw.toast.resume", g.Name), UiTone.Ok), now, hideAfterS, true);
                }
            }
            foreach (string name in new List<string>(_st.Keys)) if (!alive.Contains(name)) _st.Remove(name);
        }

        private static Action<UiKit> Body(string text, UiTone tone) { return k => k.Text(text, tone, true, 2, text); }
    }
}
