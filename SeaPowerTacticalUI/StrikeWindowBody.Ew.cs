using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoiceBotBridge
{
    // T-341 phase D (design 5.8): the jammers in the window: the card of a variant (covered / uncovered, late + the button that shifts T, bands the jammer does not cover) and the rows of the group.
    internal sealed partial class StrikeWindowBody
    {
        private static string Ids(IEnumerable<int> ids) { return string.Join(", ", ids.Select(x => "#" + I(x))); }

        private void DrawEwCard(UiKit k, StrikeGroupView g, StrikeCardView c)
        {
            if (c.Ew.Count == 0) { k.Hint(Tx("strike.ew.none"), UiTone.Dim); return; }
            int total = c.Ew.SelectMany(e => e.Cover.Concat(e.Uncovered)).Distinct().Count(), covered = c.Ew.Where(e => e.Why == null).SelectMany(e => e.Cover).Distinct().Count();
            foreach (var e in c.Ew)
            {
                string line = Tx("strike.ew.line", e.Name ?? ("#" + I(e.Uid)), I(covered), I(total));
                if (e.Why != null) line += "; " + StrikeWindow.EwWhyText(e.Why);
                k.Hint(line, e.Why != null ? UiTone.Error : UiTone.Normal);
                if (e.Uncovered.Count > 0) k.Hint(Tx("strike.ew.uncovered", Ids(e.Uncovered)), UiTone.Warn);
                if (e.LateS > 0)
                {
                    int min = (int)Math.Ceiling(e.LateS / 60.0);
                    k.Hint(Tx("strike.ew.late", I(min)), UiTone.Warn);
                    bool executing = g.Exec == "executing";
                    if (c.Index == g.Chosen && k.Button("shift." + I(c.Index) + "." + I(e.Uid), Tx("strike.btn.shift", I((int)Math.Round(e.ShiftS / 60.0))), Tx("strike.tip.shift"), !executing, UiTone.Warn))
                    {
                        string r = Send("strike_plan", new Dictionary<string, object> { { "group", g.Name }, { "shift_s", e.ShiftS } });
                        if (Err(r)) Say(StrikeWindow.ErrText(r), UiTone.Error); else Say(Tx("strike.say.shifted", I((int)Math.Round(e.ShiftS / 60.0))), UiTone.Ok);
                    }
                }
                foreach (string b in e.BandsMissing)
                {
                    int i = b.IndexOf(':');
                    k.Hint(Tx("strike.ew.band", i < 0 ? b : b.Substring(0, i), i < 0 ? "" : b.Substring(i + 1)), UiTone.Warn);
                }
            }
        }

        private void DrawEwRows(UiKit k, StrikeGroupView g)
        {
            if (g == null || g.Ew.Count == 0) return;
            foreach (var e in g.Ew)
            {
                string name = e.Name ?? ("#" + I(e.Uid));
                string line = Tx("strike.ew.row", name, StrikeWindow.EwStateText(e.State) + (e.On ? " *" : "") + (e.Why == "station_threat" ? "; " + Tx("strike.ew.station_threat") : "") + (e.Why == "clear_wait" && e.ClearLeftS >= 0 ? "; " + Tx("strike.ew.clear_wait", StrikeWindow.Mmss(e.ClearLeftS)) : ""), e.DistToStationNm >= 0 ? e.DistToStationNm.ToString("0", CultureInfo.InvariantCulture) : "-", double.IsNaN(e.TOnS) ? "-" : StrikeWindow.Mmss(e.TOnS));
                UiTone tone = e.State == "jamming" ? UiTone.Ok : (e.State == "guard" || e.State == "retreat" || e.State == "no_station" || e.State == "lost" ? UiTone.Warn : UiTone.Normal);
                k.Hint(line, tone);
                DrawEwFighterLines(k, g, e);
            }
            if (g.FighterUncovered.Count > 0) k.Hint(Tx("strike.ew.fighter.uncovered", I(g.FighterUncovered.Count)), UiTone.Warn);
            foreach (string note in g.EwNotes.Skip(Math.Max(0, g.EwNotes.Count - 2)))
            {
                string text = StrikeWindow.EwNoteText(note);
                UiTone tone = NoteWarn(note, "ewf_no_cover", "ewf_ships_lost") ? UiTone.Warn : UiTone.Dim;
                if (text.Length > 0) k.Hint(text, tone);
            }
        }

        /// <summary>T-342 D: the mode of a jammer that works against the fighters (or may): the fighter it jams, the attack on the ships, the retreat from a threat, the reserve, the ships nobody jams meanwhile.</summary>
        private void DrawEwFighterLines(UiKit k, StrikeGroupView g, StrikeEwRow e)
        {
            bool active = e.State == "to_station" || e.State == "on_station" || e.State == "jamming" || e.State == "guard" || e.State == "retreat";
            if (!active) return;
            string name = e.Name ?? ("#" + I(e.Uid));
            if (e.Role == "fighters") k.Hint(Tx("strike.ew.role.fighters", name), UiTone.Dim);
            if (e.Mode == "retreat" && e.FighterWhy != null && e.FighterWhy.StartsWith("no_cover:", StringComparison.Ordinal))
            {
                int id; int.TryParse(e.FighterWhy.Substring(9), NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
                var th = g.FighterThreats.FirstOrDefault(t => t.Id == id);
                k.Hint(Tx("strike.ew.fighter.no_cover", th != null && !string.IsNullOrEmpty(th.Cls) ? th.Cls : "#" + I(id)), UiTone.Warn);
            }
            else if (e.JamKind == "object")
            {
                k.Hint(StrikeWindow.EwModeText(e.Mode) + ": " + Tx("strike.ew.fighter.jams", string.IsNullOrEmpty(e.FighterCls) ? "" : e.FighterCls, "#" + I(e.FighterId), e.FighterDistNm >= 0 ? e.FighterDistNm.ToString("0", CultureInfo.InvariantCulture) : "-", e.FighterRtNm >= 0 ? e.FighterRtNm.ToString("0", CultureInfo.InvariantCulture) : "-"), UiTone.Accent);
                if (e.FighterIrOnly) k.Hint(Tx("strike.ew.fighter.ir_only"), UiTone.Dim);
                if (e.ShipsLost.Count > 0) k.Hint(Tx("strike.ew.fighter.ships_lost", Ids(e.ShipsLost)), UiTone.Warn);
            }
            else if (e.Mode == "attack") k.Hint(Tx("strike.ew.mode.attack"), UiTone.Dim);
            else if (e.Mode == "retreat") k.Hint(Tx("strike.ew.mode.retreat"), UiTone.Warn);
            if (e.FighterWhy == "jam_refused") k.Hint(Tx("strike.ew.fighter.refused"), UiTone.Warn);
        }
    }
}
