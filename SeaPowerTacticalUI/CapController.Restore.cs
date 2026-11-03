// T-218 (ADR-0088 restore, ADR-0080 section 3): deck members of a group after a save load. The game re-creates the deck tasks with new GUIDs,
// so the uid of a task row (AirOpsMath.TaskUid) of the file never shows up again. No Unity, no reflection.
using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal sealed partial class CapController
    {
        /// <summary>T-218: a restored deck-row member is not listed (the game re-created the deck tasks with new GUIDs): take the best free row of its type on its ship
        /// (CapMath.PickRestoreRow: same state, then a game ready-up task, then cool-down, then hangar). Drops a stale own key from <c>_ourKeys</c> without an abort.
        /// Logs "restore readopt uid=old -> new group= deck= where=[ any]". true = raw is set; false = nothing free (the caller waits RestoreGraceS as before).</summary>
        private bool ReadoptRestored(Grp g, Mem m, Dictionary<int, CapBird> birds, out CapBird raw)
        {
            raw = null;
            if (m.Type == null || m.Pending || m.RestoreDeck < 0 || birds.Count == 0) return false;
            string ow;
            raw = CapMath.PickRestoreRow(birds.Values, m.Type, m.RestoreDeck, m.RestoreWhere ?? m.Where, m.RowLoadout, b =>
                MemOf(g, b.Uid) == null && _own.Owner(b.Uid, out ow) == null && !_g.TakenByPlayer(b.Uid)
                && (b.HangarK <= 0 || SlotFree(b)) && StrikeWhy(b) == null   // T-325 tail: a strike-loaded deck row is not re-adopted
                && (b.PendingKey == null || !_ourKeys.Contains(b.PendingKey)));
            if (raw == null) return false;
            if (_own.Claim(raw.Uid, Win, g.Name) != null) { raw = null; return false; }
            _own.Release(m.Uid);
            m.HangarK = raw.HangarK; m.HangarN = raw.HangarN; m.JoinedAt = raw.HangarK > 0 ? _own.Slots.NextJoin() : 0; m.KeyAt = -1;
            Log("restore readopt uid=" + m.Uid + " -> " + raw.Uid + " group=" + g.Name + " deck=" + raw.DeckUid + " where=" + raw.Where + (m.RestoreDeck == 0 ? " any" : "")
                + (raw.PendingKey != null ? " key=" + raw.PendingKey : ""));
            if (m.RestoreDeck == 0) Log("restore deck any uid=" + m.Uid + " group=" + g.Name + " -> deck " + raw.DeckUid);
            if (m.Key != null) { _ourKeys.Remove(m.Key); m.Key = null; }
            m.Uid = raw.Uid; m.DeckUid = raw.DeckUid > 0 ? raw.DeckUid : m.DeckUid; m.Allowed = false; m.PlanSlot = -1; m.KnownAir = null; m.MissingS = -1; m.TaskRowLogged = false;
            m.RestoreDeck = -1; m.RestoreWhere = null;
            return true;
        }
    }
}
