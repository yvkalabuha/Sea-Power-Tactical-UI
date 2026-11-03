using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoiceBotBridge
{
    // T-342 phase C (ADR-0133 sections 4, 5, 7): the flag "Auto" of the group (the wave, the state, the reason of a stop, the last messages) and the status of the clearing with a pause.
    // The flag sends strike_auto {group, on}. Pure, tested in strikewintest.
    internal sealed partial class StrikeWindowBody
    {
        private void DrawAuto(UiKit k, StrikeGroupView g)
        {
            if (g == null) return;
            string label = Tx("strike.auto.label") + (g.AutoOn ? " - " + Tx("strike.auto.wave", I(g.AutoWave), I(g.AutoMax)) : "");
            bool on = k.Check("strike.auto", g.AutoOn, label, Tx("strike.auto.tip", I(g.AutoMax)));
            if (on != g.AutoOn)
            {
                string r = Send("strike_auto", new Dictionary<string, object> { { "group", g.Name }, { "on", on } });
                if (Err(r)) Say(StrikeWindow.ErrText(r), UiTone.Error); else Say(on ? Tx("strike.say.auto_on") : Tx("strike.say.auto_off"), UiTone.Ok);
            }
            if (g.AutoOn)
            {
                string state = StrikeWindow.AutoStateText(g.AutoState);
                string why = StrikeWindow.AutoWhyText(g.AutoWhy);
                if (g.AutoWhy == "clear" && g.ClearStage != null) why = ClearWaitLine(g);
                k.Hint(why.Length == 0 ? state : state + ": " + why, UiTone.Accent);
            }
            else if (g.AutoStopWhy != null) k.Hint(Tx("strike.auto.stopped", StrikeWindow.AutoStopText(g.AutoStopWhy)), UiTone.Warn);
            foreach (string note in g.AutoNotes.Skip(Math.Max(0, g.AutoNotes.Count - 3)))
            {
                string text = StrikeWindow.AutoNoteText(note);
                UiTone tone = NoteWarn(note, "auto_red", "auto_stop", "clear_red", "clear_stale") ? UiTone.Warn : UiTone.Dim;
                if (text.Length > 0) k.Hint(text, tone);
            }
            DrawClearStatus(k, g);
        }

        private static string NeighbourName(StrikeGroupView g) { return (string.IsNullOrEmpty(g.ClearName) ? "" : g.ClearName + " ") + "#" + g.ClearNeighbour.ToString(CultureInfo.InvariantCulture); }

        private static string ClearWaitLine(StrikeGroupView g)
        {
            return Tx("strike.clear.status", NeighbourName(g), StrikeWindow.Mmss(g.ClearWaitS), g.ClearTimeoutS >= 0 ? StrikeWindow.Mmss(g.ClearTimeoutS) : "-", g.ClearFuelS >= 0 ? StrikeWindow.Mmss(g.ClearFuelS) : "-");
        }

        private void DrawClearStatus(UiKit k, StrikeGroupView g)
        {
            if (g.ClearStage == null) return;
            switch (g.ClearStage)
            {
                case "timeout": case "fuel": case "lost":
                    k.Hint(Tx("strike.clear.choice", NeighbourName(g), StrikeWindow.ClearEndText(g.ClearStage)), UiTone.Error);
                    break;
                case "red_wait":
                    k.Hint(Tx("strike.msg.clear_red"), UiTone.Warn);
                    break;
                case "stale_wait":
                    k.Hint(Tx("strike.msg.clear_stale"), UiTone.Warn);
                    break;
                case "released":
                    k.Hint(Tx("strike.msg.clear_release", g.ClearHow ?? ""), UiTone.Ok);
                    break;
                default:
                    k.Hint(ClearWaitLine(g), UiTone.Accent);
                    break;
            }
        }

        /// <summary>The card of clear_pause: stage 1 (the neighbour), stage 2 (the main target after the observed sinking), the condition.</summary>
        private void DrawClearCard(UiKit k, StrikeCardView c)
        {
            var s1 = c.PerTarget.FirstOrDefault(t => t.Stage == 1);
            var s2 = c.PerTarget.FirstOrDefault(t => t.Stage == 2);
            if (s1 == null || s2 == null) return;
            string n1 = (string.IsNullOrEmpty(s1.Name) ? "" : s1.Name + " ") + "#" + I(s1.Id), n2 = (string.IsNullOrEmpty(s2.Name) ? "" : s2.Name + " ") + "#" + I(s2.Id);
            k.Hint(Tx("strike.card.clear_stage1", n1, I(s1.N), StrikeWindow.ProbCell(s1.PSunk >= 0 ? s1.PSunk : s1.P, c.KillEstimate)), UiTone.Normal);
            string s2line = Tx("strike.card.clear_stage2", n2, n1, I(s2.N), StrikeWindow.ProbCell(s2.PSunk >= 0 ? s2.PSunk : s2.P, c.KillEstimate));
            if (s2.PIfAlive >= 0) s2line += "; " + Tx("strike.card.clear_if_alive", StrikeWindow.ProbCell(s2.PIfAlive, c.KillEstimate));
            k.Hint(s2line, c.Red ? UiTone.Error : UiTone.Normal);
            k.Hint(Tx("strike.card.clear_cond"), UiTone.Dim);
        }
    }
}
