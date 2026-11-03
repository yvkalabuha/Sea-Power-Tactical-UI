using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>
    /// Pure combo runner (T-091): walks the steps of a combo, skips the ones whose `when` does not match, classifies every reply
    /// with ActionJournal and, when the combo has stop_on_error, skips the rest after a failed required step. The game is behind
    /// the `exec` delegate, so SelfTest drives it with fake replies.
    /// </summary>
    internal static class ComboRunner
    {
        /// <summary>`when` of a step: "cap:x" runs when the capability is present, "!cap:x" when it is absent.</summary>
        internal static bool WhenMatches(ActionStep st, Func<string, bool> hasCap)
        {
            if (st.When == null) return true;
            bool neg = st.When.StartsWith("!");
            string cap = st.When.Substring(neg ? 5 : 4);
            return hasCap(cap) != neg;
        }

        /// <param name="exec">runs one step, returns the ack JSON of the step command</param>
        /// <param name="before">called right before exec of a step that runs (hook for the control memory)</param>
        /// <param name="after">called right after exec with the step's ack JSON</param>
        public static List<StepOutcome> Run(ActionDef def, Func<string, bool> hasCap, Func<ActionStep, int, string> exec,
            Action<ActionStep> before = null, Action<ActionStep, string> after = null)
        {
            var res = new List<StepOutcome>();
            bool stopped = false;
            for (int i = 0; i < def.Steps.Count; i++)
            {
                var st = def.Steps[i];
                if (stopped)
                {
                    res.Add(new StepOutcome { Index = i, Cmd = st.Cmd, ArgsShort = ArgsShort(st), Optional = st.Optional, Status = StepStatus.Skipped, Reason = "after_error" });
                    continue;
                }
                if (!WhenMatches(st, hasCap))
                {
                    var w = ActionJournal.Classify(i, st, true, true, null, null);
                    w.Reason = w.Reason ?? st.When;
                    res.Add(w);
                    continue;
                }
                if (before != null) { try { before(st); } catch { } }
                string r = exec(st, i);
                if (after != null) { try { after(st, r); } catch { } }
                var d = Json.Parse(r) as Dictionary<string, object>;
                bool ok = d != null && d.ContainsKey("ok") && d["ok"] is bool && (bool)d["ok"];
                string code = d != null && d.ContainsKey("error") ? d["error"] as string : null;
                string detail = d != null && d.ContainsKey("detail") ? d["detail"] as string : null;
                var o = ActionJournal.Classify(i, st, false, ok, code, detail);
                res.Add(o);
                if (def.StopOnError && !st.Optional && o.Status == StepStatus.Error) stopped = true;
            }
            return res;
        }

        /// <summary>Short argument text for the journal: "noisemaker", "air,on", "5".</summary>
        internal static string ArgsShort(ActionStep st)
        {
            var parts = new List<string>();
            foreach (var kv in st.Args)
            {
                var v = kv.Value;
                parts.Add(v is bool ? ((bool)v ? "on" : "off") : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture));
            }
            return string.Join(",", parts.ToArray());
        }
    }
}
