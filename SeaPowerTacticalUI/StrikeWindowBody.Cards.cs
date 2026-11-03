using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoiceBotBridge
{
    internal sealed partial class StrikeWindowBody
    {
        private void DrawPlans(UiKit k, StrikeView v, StrikeGroupView g)
        {
            k.Section("plans", Tx("strike.sec.plans"), g == null ? null : I(g.Cards.Count), true);
            if (g == null) { k.Hint(Tx("strike.hint.no_group_selected")); }
            else
            {
                if (g.Status == "stale") k.Hint(Tx("strike.stale.line", StrikeWindow.StaleText(g.StaleWhy)), UiTone.Warn);
                if (g.Cards.Count == 0) k.Hint(Tx("strike.hint.no_plan"));
                foreach (var c in g.Cards)
                {
                    bool chosen = c.Index == g.Chosen;
                    if (k.Button("card." + I(c.Index), StrikeWindow.CardTitle(c), Tx("strike.tip.card"), true, c.Red ? UiTone.Error : (chosen ? UiTone.Accent : UiTone.Normal)) && !chosen)
                    {
                        string r = Send("strike_plan", new Dictionary<string, object> { { "group", g.Name }, { "choose", (double)c.Index } });
                        if (Err(r)) Say(StrikeWindow.ErrText(r), UiTone.Error);
                    }
                    if (c.Kind == "clear_pause") DrawClearCard(k, c);
                    else
                    foreach (var t in c.PerTarget)
                    {
                        string name = (string.IsNullOrEmpty(t.Name) ? "" : t.Name + " ") + "#" + I(t.Id);
                        string line;
                        if (c.Goal == "sink" && t.PSunk >= 0)
                        {
                            // T-342: the sinking model: hits needed (with the margin), missiles, P(sunk) (an estimate until the calibration); the red card says what is missing
                            line = Tx("strike.card.sink", name, I(t.N), t.H1 > 0 ? I(t.H1) : "-", StrikeWindow.ProbCell(t.PSunk, c.KillEstimate));
                            if (t.Short && c.Kind == "damage_only") line = Tx("strike.card.short", name, I(t.ShortBy), StrikeWindow.ProbCell(t.PSunk, c.KillEstimate), StrikeWindow.ProbCell(t.PBreach, false));
                        }
                        else line = Tx("strike.card.target", name, I(t.N), StrikeWindow.ProbCell(t.P, c.Estimate));
                        if (t.ClearedBy >= 0) line += "; " + Tx("strike.card.cleared", I(t.ClearedBy));
                        k.Hint(line, c.Red && t.Id == g.MainId ? UiTone.Error : UiTone.Normal);
                    }
                    foreach (var car in c.Carriers)
                        k.Hint(Tx("strike.card.carrier", car.Name ?? ("#" + I(car.Uid)), car.AmmoName, I(car.Salvo), I(car.TargetId)), UiTone.Dim);
                    string fuel = c.FuelMin >= 0 ? ", " + Tx("strike.card.fuel", c.FuelMin.ToString("0", CultureInfo.InvariantCulture)) : "";
                    k.Hint(Tx("strike.card.eta", StrikeWindow.Mmss(c.T)) + fuel, UiTone.Dim);
                    if (chosen || (g.Chosen < 0 && c.Index == 0)) DrawDeckCounts(k, g);
                    var shown = new HashSet<string>();
                    foreach (string w in c.Warnings.Concat(c.Why.Where(x => x == "clearing_no_depletion")))
                    {
                        string text = StrikeWindow.WarnText(w, k => { string v; return c.Names.TryGetValue(k, out v) ? v : null; });
                        var warnTone = w.StartsWith("short", StringComparison.Ordinal) || w.StartsWith("kill_short", StringComparison.Ordinal) || w.StartsWith("kill_unknown", StringComparison.Ordinal) || w.StartsWith("mix_late", StringComparison.Ordinal) ? UiTone.Error : UiTone.Warn;
                        if (shown.Add(text)) k.Hint(text, warnTone);
                    }
                    DrawEwCard(k, g, c);
                }
            }
            bool executing = g != null && g.Exec == "executing";
            bool stale = g != null && g.Status == "stale";
            // T-342: the red card (the kill threshold is not reached) is launched only through a confirmation, like a stale plan
            var chosenCard = g == null ? null : g.Cards.FirstOrDefault(x => x.Index == g.Chosen);
            bool red = chosenCard != null && chosenCard.Goal == "sink" && chosenCard.Red;
            bool needConfirm = stale || red;
            if (g == null || !needConfirm || g.Name != _launchFor) _launchConfirm = false;
            string launchText = _launchConfirm ? Tx("strike.btn.launch_confirm") : Tx("strike.btn.launch");
            string launchTip = g == null ? null : g.CanLaunch ? (red ? Tx("strike.confirm.red") : stale ? Tx("strike.hint.launch_stale", StrikeWindow.StaleText(g.StaleWhy)) : Tx("strike.tip.launch")) : StrikeWindow.LaunchWhyText(g.LaunchWhy);
            string pressed = k.Actions(
                new UiAction("cancel", Tx("strike.btn.cancel"), executing ? Tx("strike.tip.cancel_exec") : Tx("strike.tip.cancel"), g != null && (executing || g.Cards.Count > 0)),
                new UiAction("recalc", Tx("strike.btn.recalc"), Tx("strike.tip.recalc"), g != null && !executing),
                new UiAction("launch", launchText, launchTip, g != null && g.CanLaunch, _launchConfirm ? UiTone.Warn : UiTone.Normal));
            if (g == null || pressed == null) return;
            if (pressed == "recalc")
            {
                string r = Send("strike_plan", new Dictionary<string, object> { { "group", g.Name } });
                if (Err(r)) { Say(StrikeWindow.ErrText(r), UiTone.Error); return; }
                var o = Json.Parse(r) as Dictionary<string, object>;
                object vs, why;
                int n = o != null && o.TryGetValue("variants", out vs) && vs is List<object> ? ((List<object>)vs).Count : 0;
                string noneWhy = o != null && o.TryGetValue("why", out why) && why is string ? (string)why : "";
                if (n == 0) Say(Tx("strike.say.plan_none", noneWhy), UiTone.Warn);
                else Say(Tx("strike.say.plan", I(n)), UiTone.Ok);
            }
            else if (pressed == "cancel")
            {
                string r = Send("strike_cancel", new Dictionary<string, object> { { "group", g.Name } });
                if (Err(r)) Say(StrikeWindow.ErrText(r), UiTone.Error); else Say(executing ? Tx("strike.say.cancel_exec") : Tx("strike.say.cancel"), UiTone.Ok);
            }
            else if (pressed == "launch")
            {
                // a stale plan needs the second press (the confirmation); a fresh one goes at once
                if (needConfirm && !_launchConfirm) { _launchConfirm = true; _launchFor = g.Name; Say(red ? Tx("strike.confirm.red") : Tx("strike.hint.launch_stale", StrikeWindow.StaleText(g.StaleWhy)), UiTone.Warn); return; }
                var args = new Dictionary<string, object> { { "group", g.Name }, { "variant", (double)g.Chosen } };
                if (needConfirm) args["confirm"] = true;
                string r = Send("strike_launch", args);
                _launchConfirm = false;
                if (Err(r)) { Say(StrikeWindow.ErrText(r), UiTone.Error); return; }
                var o = Json.Parse(r) as Dictionary<string, object>;
                object rows, roe;
                string rowsText = o != null && o.TryGetValue("rows", out rows) && rows is double ? I((int)(double)rows) : "?";
                string roeText = o != null && o.TryGetValue("roe", out roe) && roe is string ? StrikeWindow.RoeText((string)roe) : "";
                Say(Tx("strike.say.launch", rowsText, roeText), UiTone.Ok);
            }
        }

        private static UiTone RowTone(StrikeTimelineRow t)
        {
            switch (t.State)
            {
                case "skipped": case "failed": return UiTone.Error;
                case "held": return UiTone.Warn;
                case "launched": return t.LateS > StrikeWindow.LateToleranceS ? UiTone.Warn : UiTone.Ok;
                case "issued": case "run_in": case "at_gate": case "to_gate": return t.LateS > StrikeWindow.LateToleranceS ? UiTone.Warn : UiTone.Normal;
                default: return UiTone.Normal;
            }
        }

        private void DrawTimeline(UiKit k, StrikeGroupView g)
        {
            var rows = g == null ? new List<StrikeTimelineRow>() : g.Timeline;
            bool running = g != null && g.Exec == "executing";
            k.Section("timeline", Tx("strike.sec.timeline") + " (" + Tx(running ? "strike.tl.title_exec" : "strike.tl.title") + ")", I(rows.Count), running);
            if (g != null && g.Exec == "executing") k.Hint(Tx("strike.exec.roe", StrikeWindow.RoeText(g.Roe), I(g.Wave)), UiTone.Dim);
            DrawEwRows(k, g);
            if (rows.Count == 0) { k.Hint(Tx("strike.hint.no_plan")); DrawResults(k, g); return; }
            var spec = new UiTable
            {
                Id = "tl", Checkboxes = false,
                Columns = new[]
                {
                    new UiColumn(Tx("strike.col.name"), 0f, true) { MaxEm = UiColumn.ShipMaxEm }, new UiColumn(Tx("strike.col.target"), 3.5f, false, true), new UiColumn(Tx("strike.col.missile"), 5f, false, false),
                    new UiColumn(Tx("strike.col.go"), 4f, false, true), new UiColumn(Tx("strike.col.issue"), 4f, false, true), new UiColumn(Tx("strike.col.launch"), 4f, false, true), new UiColumn(Tx("strike.col.arrive"), 4f, false, true),
                    new UiColumn(Tx("strike.col.state"), 6f, false, false) { MaxEm = UiColumn.StateMaxEm }, new UiColumn(Tx("strike.col.risk"), 6f, false, false) { MaxEm = UiColumn.StateMaxEm },
                },
            };
            k.Table(spec, rows.Count, (r, c) =>
            {
                var t = rows[r];
                switch (c)
                {
                    case 0: return t.Name ?? ("#" + I(t.Uid));
                    case 1: return "#" + I(t.Target);
                    case 2: return (t.Ammo ?? "") + " x" + I(t.Salvo);
                    case 3: return StrikeWindow.Mmss(t.TGo);
                    case 4: return t.FactIssue >= 0 || t.State == "issued" || t.State == "launched" ? StrikeWindow.Mmss(t.FactIssue) : StrikeWindow.Mmss(t.TIssue);
                    case 5: return t.State == "launched" ? StrikeWindow.Mmss(t.FactLaunch) : StrikeWindow.Mmss(t.TLaunch);
                    case 6: return StrikeWindow.Mmss(t.TArrive);
                    case 7: return StrikeWindow.RowStateText(t);
                    default: return StrikeWindow.RiskText(t);
                }
            }, r => RowTone(rows[r]), null, null, 8);
            DrawResults(k, g);
        }

        private void DrawResults(UiKit k, StrikeGroupView g)
        {
            if (g == null || g.Results.Count == 0) return;
            string cancelledMark = g.Exec == "cancelled" ? " (" + Tx("strike.exec.cancelled") + ")" : "";
            k.Section("result", Tx("strike.sec.result") + cancelledMark, I(g.Results.Count), true);
            foreach (var r in g.Results)
            {
                string name = (string.IsNullOrEmpty(r.Name) ? "" : r.Name + " ") + "#" + I(r.Id);
                var fateTone = r.State == "sunk" ? UiTone.Ok : (r.State == "unknown" ? UiTone.Dim : UiTone.Warn);
                k.Hint(Tx("strike.result.line", name, StrikeWindow.FateText(r.State), I(r.Fired), I(r.Arrived), StrikeWindow.Mmss(r.SpreadS)), fateTone);
            }
            if (g.Wave > 1) k.Hint(Tx("strike.result.next_wave", I(g.Wave)), UiTone.Dim);
        }
    }
}
