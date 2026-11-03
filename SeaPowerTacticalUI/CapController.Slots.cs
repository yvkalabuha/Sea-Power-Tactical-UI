// T-186 (ADR-0082): the CAP window on the shared hangar slots. A hangar machine has one uid in both windows (AirOpsMath.HangarSlotUid);
// the claims of both windows are rebound by one book (AirOwnership.Slots). No Unity, no reflection.
using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal sealed partial class CapController
    {
        /// <summary>The mod waits this long for the task row of an ordered machine to be listed (the game lists it a frame or two later); same as the ASW window.</summary>
        internal const double OwnRowWaitS = 30;
        private const double HangarWarnEveryS = 600;
        private readonly Dictionary<string, double> _hangarWarnAt = new Dictionary<string, double>();

        /// <summary>The claims of the members that sit in a hangar slot, for the shared book (window "cap"). An order of the slot in flight pins it.</summary>
        private IEnumerable<AirOpsMath.SlotClaim> SlotClaims()
        {
            var res = new List<AirOpsMath.SlotClaim>();
            foreach (var g in _groups)
                foreach (var m in g.Members)
                {
                    if (m.HangarK <= 0 || m.Airborne) continue;
                    res.Add(new AirOpsMath.SlotClaim { Uid = m.Uid, Group = g.Name, Window = Win, Seq = g.Seq, JoinedAt = m.JoinedAt, K = m.HangarK, DeckUid = m.DeckUid, Type = m.Type, Pinned = m.Pending });
                }
            return res;
        }

        /// <summary>Machines of the pair in the hangar by the last scan; -1 = this window holds no slot of the pair (the book then asks the others).</summary>
        private int SlotNumbers(int deck, string type)
        {
            bool mine = false;
            foreach (var g in _groups)
            {
                foreach (var m in g.Members) if (m.HangarK > 0 && !m.Airborne && m.DeckUid == deck && m.Type == type) { mine = true; break; }
                if (mine) break;
            }
            if (!mine) return -1;
            // read fresh: the other window may rebind before this window's tick refreshed its list (in the game the air tick passes the game numbers instead)
            IList<CapBird> rows = null;
            try { rows = _g.Birds(); } catch (Exception) { }
            if (rows == null) return -1;
            int n = 0;
            foreach (var b in rows) if (b != null && b.HangarK > 0 && b.DeckUid == deck && b.Type == type) n = Math.Max(n, b.HangarN);
            return n;
        }

        /// <summary>A move or a drop decided by the book (ADR-0082 s.2) for a member of this window.</summary>
        private void ApplySlot(AirOpsMath.SlotMove mv)
        {
            var g = Find(mv.Group);
            var m = g == null ? null : MemOf(g, mv.Uid);
            if (m == null) return;
            if (mv.Drop) { HangarTaken(g, m); return; }
            string w;
            if (mv.ToUid == m.Uid || _own.Owner(mv.ToUid, out w) != null) return;   // the target is held (the book retries on the next tick)
            _own.Release(m.Uid);
            _own.Claim(mv.ToUid, Win, g.Name);
            Log("slot " + g.Name + " uid=" + m.Uid + " -> " + mv.ToUid + " k=" + mv.FromK + "->" + mv.ToK + " n=" + mv.N);
            m.Uid = mv.ToUid; m.HangarK = mv.ToK;
        }

        /// <summary>A slot row nobody holds: no owner of its uid and no claim of the book on its k.</summary>
        private bool SlotFree(CapBird b)
        {
            string w, g;
            return _own.Owner(b.Uid, out w) == null && !_own.Slots.Held(b.DeckUid, b.Type, b.HangarK, out w, out g);
        }

        /// <summary>The free hangar slot of a pair with the lowest k (deckUid 0 = any deck, the lowest deck uid first); null = none.</summary>
        private CapBird FreeSlot(int deckUid, string type, IEnumerable<CapBird> birds)
        {
            CapBird best = null;
            foreach (var b in birds)
            {
                if (b == null || b.HangarK <= 0 || b.Type != type || b.Where != "hangar" || (deckUid != 0 && b.DeckUid != deckUid)) continue;
                if (!SlotFree(b) || _g.TakenByPlayer(b.Uid)) continue;
                if (best == null || (b.DeckUid == best.DeckUid ? b.HangarK < best.HangarK : b.DeckUid < best.DeckUid)) best = b;
            }
            return best;
        }

        /// <summary>The order of a slot was accepted by the deck: a slot member leaves the slot at once and becomes the uid its task row will have (ADR-0082 s.4).
        /// A row without a slot (fake / legacy) waits for the task row as before.</summary>
        private void AfterOrder(Grp g, Mem m)
        {
            if (m.HangarK > 0 && m.Key != null) HandOver(g, m, m.Key); else Rekey(g, m);   // T-186: fake/legacy rows without a slot go the old way
        }

        private void HandOver(Grp g, Mem m, string key)
        {
            int nu = AirOpsMath.TaskUid(key);
            if (nu == m.Uid || MemOf(g, nu) != null) return;
            if (_own.Claim(nu, Win, g.Name) != null) return;
            int old = m.Uid, k = m.HangarK;
            _own.Release(old);
            Log("handover " + g.Name + " slot=" + old + "#" + k + " key=" + key + " -> uid=" + nu);
            m.Uid = nu; m.HangarK = 0; m.JoinedAt = 0; m.KeyAt = _now;
        }

        /// <summary>The machine of the slot is gone for good (the book dropped the claim after AbortTicks ticks): not "lost".</summary>
        private void HangarTaken(Grp g, Mem m)
        {
            Log("member hangar_taken " + g.Name + " uid=" + m.Uid + " type=" + m.Type + " deck=" + m.DeckUid);
            double last;
            if (!_hangarWarnAt.TryGetValue(g.Name, out last) || _now - last >= HangarWarnEveryS)
            {
                _hangarWarnAt[g.Name] = _now;
                ClearWarn(g, "hangar_taken");
                Warn(g, "hangar_taken", m.Type);
            }
            Evt("cap_member_out", "{\"group\":" + Q(g.Name) + ",\"unit\":" + m.Uid + ",\"reason\":\"hangar_taken\"}");
            DropMember(g, m, false, "hangar_taken");
        }
    }
}
