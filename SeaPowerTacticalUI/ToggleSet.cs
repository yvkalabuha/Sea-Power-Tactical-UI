// T-178 Kb1 (ADR-0077 §2.1): the rules of multi-choice chips. "Auto" (autoIndex) is exclusive: choosing it clears the others, choosing any other clears it,
// clearing the last one lights "Auto" again. The state is never empty (without "Auto" the last lit chip cannot be switched off). A grey chip does not toggle.
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal static class ToggleSet
    {
        /// <summary>The new state after a click on chip `index`; `on` is not changed.</summary>
        public static bool[] Click(bool[] on, int index, int autoIndex, IList<bool> enabled)
        {
            var r = new bool[on == null ? 0 : on.Length];
            if (on != null) System.Array.Copy(on, r, on.Length);
            if (index < 0 || index >= r.Length) return r;
            if (enabled != null && index < enabled.Count && !enabled[index]) return r;
            if (index == autoIndex)
            {
                for (int i = 0; i < r.Length; i++) r[i] = i == autoIndex;
                return r;
            }
            r[index] = !r[index];
            if (r[index] && autoIndex >= 0 && autoIndex < r.Length) r[autoIndex] = false;
            bool any = false;
            for (int i = 0; i < r.Length; i++) if (r[i]) { any = true; break; }
            if (!any)
            {
                if (autoIndex >= 0 && autoIndex < r.Length) r[autoIndex] = true;
                else r[index] = true;   // never empty
            }
            return r;
        }

        private static string Str(bool[] a) { var s = ""; foreach (bool b in a) s += b ? "1" : "0"; return s; }

        public static string SelfTest()
        {
            // chips: 0 Auto, 1 Dip, 2 Corridor, 3 Barrier
            var st = new[] { true, false, false, false };
            var r = Click(st, 1, 0, null);
            if (Str(r) != "0100") return "another chip clears Auto: " + Str(r);
            r = Click(r, 3, 0, null);
            if (Str(r) != "0101") return "chips are independent: " + Str(r);
            r = Click(r, 0, 0, null);
            if (Str(r) != "1000") return "Auto clears the others: " + Str(r);
            r = Click(r, 0, 0, null);
            if (Str(r) != "1000") return "Auto stays on when clicked again: " + Str(r);
            r = Click(new[] { false, false, true, false }, 2, 0, null);
            if (Str(r) != "1000") return "clearing the last chip lights Auto: " + Str(r);
            r = Click(new[] { false, true, false, true }, 1, 0, null);
            if (Str(r) != "0001") return "clearing one of two keeps the other: " + Str(r);
            // a grey chip does not toggle
            var en = new[] { true, true, false, true };
            r = Click(new[] { true, false, false, false }, 2, 0, en);
            if (Str(r) != "1000") return "grey chip: " + Str(r);
            // without Auto the state is never empty
            r = Click(new[] { false, true, false }, 1, -1, null);
            if (Str(r) != "010") return "no Auto: the last chip stays: " + Str(r);
            r = Click(new[] { true, true, false }, 0, -1, null);
            if (Str(r) != "010") return "no Auto: plain toggling: " + Str(r);
            // the input array is not touched; out of range is a no-op
            var keep = new[] { true, false };
            Click(keep, 1, 0, null);
            if (Str(keep) != "10") return "input is not modified";
            if (Str(Click(keep, 5, 0, null)) != "10" || Str(Click(keep, -1, 0, null)) != "10") return "index out of range";
            if (Click(null, 0, 0, null).Length != 0) return "null state";
            return null;
        }
    }
}
