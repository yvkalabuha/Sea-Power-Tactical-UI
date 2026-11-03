using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoiceBotBridge
{
    // T-342 phase B (ADR-0133 section 7): the block "Preparing from the deck": what the window ordered from the decks for the group (one row per aircraft with the time to its readiness
    // and its state), the totals of the magazine, the cancel buttons and "order again". Commands: strike_deck_cancel {group, key?}, strike_deck_reorder {group}. Pure, tested in strikewintest.
    internal sealed partial class StrikeWindowBody
    {
        private static string BlockedKey(string block) { return block == "loaded" ? "strike.deck.blocked.loaded" : "strike.deck.blocked.player"; }
        private static bool DeckVisible(StrikeGroupView g)
        {
            return g != null && (g.DeckOrders.Count > 0 || g.DeckBlock != null || (g.HasDeckPlan && g.ShortAfterAll));
        }

        private static UiTone DeckRowTone(StrikeDeckOrderRow o)
        {
            switch (o.State)
            {
                case "failed": return UiTone.Error;
                case "in_group": return UiTone.Ok;
                case "cancelled": case "aborted": return UiTone.Dim;
                case "launch": return UiTone.Accent;
                default: return UiTone.Normal;
            }
        }

        private void DrawDeck(UiKit k, StrikeGroupView g)
        {
            if (!DeckVisible(g)) return;
            var live = g.DeckOrders.Where(o => o.Live).ToList();
            k.Section("deck", Tx("strike.deck.title"), I(live.Count), true);
            if (g.DeckAircraft > 0 || live.Count > 0)
                k.Hint(Tx("strike.deck.summary", I(live.Count), I(live.Sum(o => o.Missiles)), g.DeckLastReadyS >= 0 ? StrikeWindow.Mmss(g.DeckLastReadyS) : "-"), UiTone.Normal);
            if (g.DeckStock.Count == 0) k.Hint(Tx("strike.deck.stock_unknown"), UiTone.Dim);
            foreach (string row in g.DeckStock)
            {
                var p = row.Split('|');
                if (p.Length >= 4) k.Hint(Tx("strike.deck.stock", p[0], p[1], p[2], p[3] == "-1" ? "?" : p[3]), UiTone.Dim);
            }
            if (g.HasDeckPlan && g.ShortAfterAll) k.Hint(Tx("strike.deck.short_all", StrikeWindow.ProbCell(g.AllP, true)), UiTone.Error);
            foreach (string rj in g.DeckRejected)
            {
                var p = rj.Split('|');
                if (p.Length >= 3) k.Hint(Tx("strike.deck.rejected", p[0], p[1], StrikeWindow.DeckErrText(p[2])), UiTone.Dim);
            }
            foreach (var o in g.DeckOrders.Where(x => x.Live || x.State == "failed" || x.State == "in_group").Reverse().Take(12).Reverse())
            {
                string state = StrikeWindow.DeckStateText(o.State) + (o.Err != null ? ": " + StrikeWindow.DeckErrText(o.Err) : "");
                string ready = o.Live ? Tx("strike.deck.ready_in", StrikeWindow.Mmss(o.ReadyS)) : "";
                k.Hint(Tx("strike.deck.row", o.DeckName ?? "?", o.Type ?? "?", o.Display ?? o.Loadout ?? "?", I(o.Missiles), ready, state), DeckRowTone(o));
                if (o.Live && o.State != "launch" && o.Key != null && k.Button("deck.cancel." + o.Key, Tx("strike.deck.cancel"), Tx("strike.tip.deck_cancel"), !ExecRunning(g)))
                {
                    string r = Send("strike_deck_cancel", new Dictionary<string, object> { { "group", g.Name }, { "key", o.Key } });
                    if (Err(r)) Say(StrikeWindow.ErrText(r), UiTone.Error); else Say(Tx("strike.say.deck_cancel"), UiTone.Ok);
                }
            }
            foreach (string note in g.DeckNotes.Skip(Math.Max(0, g.DeckNotes.Count - 3)))
            {
                string text = StrikeWindow.DeckNoteText(note);
                UiTone tone = NoteWarn(note, "deck_err") ? UiTone.Warn : UiTone.Dim;
                if (text.Length > 0) k.Hint(text, tone);
            }
            if (g.DeckBlock != null)
            {
                string blockKey = BlockedKey(g.DeckBlock);
                k.Hint(Tx(blockKey), UiTone.Warn);
            }
            string pressed = k.Actions(
                new UiAction("deck.cancel_all", Tx("strike.deck.cancel_all"), Tx("strike.tip.deck_cancel_all"), live.Any(o => o.State != "launch") && g.Exec != "executing"),
                new UiAction("deck.reorder", Tx("strike.deck.reorder"), Tx("strike.tip.deck_reorder"), g.DeckBlock != null && g.Exec != "executing", g.DeckBlock != null ? UiTone.Accent : UiTone.Normal));
            if (pressed == "deck.cancel_all")
            {
                string r = Send("strike_deck_cancel", new Dictionary<string, object> { { "group", g.Name } });
                if (Err(r)) Say(StrikeWindow.ErrText(r), UiTone.Error); else Say(Tx("strike.say.deck_cancel_all"), UiTone.Ok);
            }
            else if (pressed == "deck.reorder")
            {
                string r = Send("strike_deck_reorder", new Dictionary<string, object> { { "group", g.Name } });
                if (Err(r)) Say(StrikeWindow.ErrText(r), UiTone.Error); else Say(Tx("strike.say.deck_reorder"), UiTone.Ok);
            }
        }

        /// <summary>Under the chosen card: "now N missiles - damage only; with the deck M - P 84%" (only when the deck was planned).</summary>
        private void DrawDeckCounts(UiKit k, StrikeGroupView g)
        {
            if (g == null || !g.HasDeckPlan) return;
            string now = g.NowOk ? StrikeWindow.ProbCell(g.NowP, true) : Tx("strike.deck.damage_only") + (g.NowN > 0 ? ", P " + StrikeWindow.ProbCell(g.NowP, true) : "");
            k.Hint(Tx("strike.card.counts", I(g.NowN), now, I(g.WithN), StrikeWindow.ProbCell(g.WithP, true)), g.WithOk ? UiTone.Ok : UiTone.Warn);
        }
    }
}
