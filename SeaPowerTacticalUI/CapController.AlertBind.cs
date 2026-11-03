// T-219 (ADR-0090): the alert binding of a bird to its raid survives a save load, the same wave is recognised after a load (place memory, AirGuard key alias).
// No Unity, no reflection. CapController.Alert.cs calls these.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    internal sealed partial class CapController
    {
        /// <summary>The AirGuard key of a track, null function when AirGuard is off (T-201: no keys, no wait).</summary>
        private Func<int, string> AlertKeyOf()
        {
            var notes = AlertNotes;
            return notes == null || !notes.RaidKeysOn ? (Func<int, string>)null : (id => notes.RaidKeyOf(id));
        }

        /// <summary>The binding of a member for the file; null when it has no alert state.</summary>
        private CapAlertBindDto SaveBind(Mem m)
        {
            if (m.AlertState != "scramble" && m.AlertState != "return") return null;
            return new CapAlertBindDto
            {
                State = m.AlertState, Raid = m.AlertRaid, Key = m.AlertKey, RaidIds = m.RaidIds != null ? new List<int>(m.RaidIds) : null,
                Barrage = m.Barrage, Handed = m.Handed, Tgt = m.Tgt != null ? m.Tgt.Clone() : null,
            };
        }

        /// <summary>Gives the binding of the file back to a loaded member. <paramref name="fileT"/> = mission time of the file: ages in the file are relative to it.</summary>
        private void ApplyBind(Grp g, Mem m, CapAlertBindDto b, double now, double fileT)
        {
            m.AlertState = b.State; m.AlertRaid = b.Raid; m.AlertKey = b.Key;
            m.RaidIds = b.RaidIds != null ? new List<int>(b.RaidIds) : null;
            m.Barrage = b.Barrage; m.Handed = b.Handed; m.FarSince = -1; m.LeashStartNm = -1; m.LeashIn = false;
            if (b.Tgt != null) { m.Tgt = b.Tgt.Clone(); m.Tgt.At = now - Math.Max(0, fileT - b.Tgt.At); }
            m.BindRestoredAt = now;
            Log("alert bind restored uid=" + m.Uid + " group=" + g.Name + " raid=" + (b.Raid ?? "-") + " ids=" + (b.RaidIds == null ? "-" : string.Join(",", b.RaidIds)) + " state=" + b.State);
        }

        /// <summary>The times of the bindings and memories of a waiting file move to the clock <paramref name="now"/> (ages stay).</summary>
        private static void ShiftBindTimes(CapSaveData d, double now)
        {
            double delta = now - d.MissionTimeS;
            if (Math.Abs(delta) < 1e-9) return;
            foreach (var g in d.Groups)
            {
                if (g.Memos != null) foreach (var mm in g.Memos) mm.At += delta;
                if (g.Members != null) foreach (var md in g.Members) if (md.Bind != null && md.Bind.Tgt != null) md.Bind.Tgt.At += delta;
            }
            d.MissionTimeS = now;
        }

        /// <summary>Follows the served raids by their live tracks; drops the memories older than the TTL.</summary>
        private void UpdateMemos(Grp g, IList<CapContact> contacts, double now)
        {
            for (int i = g.Memos.Count - 1; i >= 0; i--)
            {
                var mm = g.Memos[i];
                if (now - mm.At > CapAlertMath.MemoTtlS) { g.Memos.RemoveAt(i); continue; }
                if (contacts == null) continue;
                foreach (var c in contacts)
                    if (c != null && mm.Ids.Contains(c.Id))
                    {
                        mm.Lat = c.Lat; mm.Lon = c.Lon; mm.CourseDeg = c.CourseDeg; mm.Kn = c.SpeedKn; mm.At = now;
                        break;
                    }
            }
        }

        private void JoinMemos(Grp g, ICollection<int> oldIds, string key, IList<int> add)
        {
            foreach (var mm in g.Memos)
            {
                bool hit = key != null && mm.Key == key;
                if (!hit && oldIds != null) foreach (int id in oldIds) if (mm.Ids.Contains(id)) { hit = true; break; }
                if (hit) foreach (int id in add) if (!mm.Ids.Contains(id)) mm.Ids.Add(id);
            }
        }

        /// <summary>The raids of the group this tick (one assembly per group and tick).</summary>
        private List<AlertRaid> RaidsOf(Grp g, IList<CapContact> contacts, double now)
        {
            if (g.RaidsNow != null && g.RaidsAt == now) return g.RaidsNow;
            var rp = AlertRefPoint(g);
            g.RaidsNow = rp.HasValue && g.A != null ? CapAlertMath.Raids(contacts, g.A.Targets, UnknownMinKn != null ? UnknownMinKn() : _p.UnknownMinKn, rp.Value) : new List<AlertRaid>();
            g.RaidsAt = now;
            return g.RaidsNow;
        }

        /// <summary>A bird whose raid tracks are gone takes the raid of the same wave (shared ids, shared AirGuard key, place near the forecast of its last target).</summary>
        private bool TryRebind(Grp g, Mem m, IList<CapContact> contacts, double now)
        {
            if (m.RaidIds == null || contacts == null) return false;
            var keyOf = AlertKeyOf();
            string why;
            var raid = CapAlertMath.RebindRaid(RaidsOf(g, contacts, now), m.RaidIds, m.AlertKey, m.Tgt, keyOf, now, out why);
            if (raid == null) return false;
            var old = new List<int>(m.RaidIds);
            foreach (int id in raid.Ids) { if (!m.RaidIds.Contains(id)) m.RaidIds.Add(id); g.Served.Add(id); }
            string rkey = null;
            if (keyOf != null) foreach (int id in raid.Ids) { rkey = keyOf(id); if (rkey != null) break; }
            if (rkey != null) { g.ServedRaids.Add(rkey); if (m.AlertKey == null) m.AlertKey = rkey; }
            JoinMemos(g, old, m.AlertKey, raid.Ids);
            m.AlertRaid = raid.Key.ToString(CultureInfo.InvariantCulture); m.FarSince = -1;
            Log("alert rebind uid=" + m.Uid + " group=" + g.Name + " raid=" + raid.Key + " why=" + why + " ids=" + string.Join(",", raid.Ids));
            return true;
        }

        /// <summary>A bound member that came back from a file onto a deck and got no permission to launch (the sortie did not happen) returns to the pool;
        /// the raid is un-served when nobody else is bound to it.</summary>
        private void ResetStaleBind(Grp g, Mem m, double now)
        {
            if (m.BindRestoredAt < 0 || m.AlertState != "scramble" || m.Airborne || !Standing(m) || (m.Key != null && m.Allowed) || m.Pending || m.RestoredAt >= 0) return;
            if (now - m.BindRestoredAt < RestoreGraceS) return;
            var ids = m.RaidIds; string key = m.AlertKey;
            m.AlertState = null; m.AlertRaid = null; m.RaidIds = null; m.AlertKey = null; m.Tgt = null; m.Barrage = false; m.Handed = false; m.BindRestoredAt = -1;
            Log("alert unbind uid=" + m.Uid + " group=" + g.Name + " after load (standing)");
            if (ids != null)
                foreach (int id in ids)
                {
                    bool other = false;
                    foreach (var o in g.Members) if (o != m && o.RaidIds != null && o.RaidIds.Contains(id)) other = true;
                    if (!other) g.Served.Remove(id);
                }
            if (key != null)
            {
                bool other = false;
                foreach (var o in g.Members) if (o != m && o.AlertKey == key) other = true;
                if (!other) { g.ServedRaids.Remove(key); g.Memos.RemoveAll(mm => mm.Key == key); }
            }
            if (ids != null) g.Memos.RemoveAll(mm => mm.Ids.Exists(i => ids.Contains(i)) && !g.Members.Exists(o => o.RaidIds != null && o.RaidIds.Exists(i => mm.Ids.Contains(i))));
        }

        /// <summary>A raid recognised as served (by ids, key, key alias or place memory) joins the served sets; one log line per raid.</summary>
        private void AbsorbServed(Grp g, AlertRaid raid, string key, Func<int, string> keyOf, double now)
        {
            string by = null;
            foreach (int id in raid.Ids) if (g.Served.Contains(id)) { by = "ids"; break; }
            if (by == null && key != null && g.ServedRaids.Contains(key)) by = "key";
            if (by == null && keyOf != null)
            {
                var alias = new HashSet<string>();
                foreach (int sid in g.Served) { string sk = keyOf(sid); if (sk != null) alias.Add(sk); }
                foreach (int id in raid.Ids) { string k = keyOf(id); if (k != null && alias.Contains(k)) { by = "alias"; break; } }
            }
            RaidMemo hit = null;
            foreach (var mm in g.Memos) if (CapAlertMath.MemoMatch(raid, mm, now)) { hit = mm; break; }
            if (by == null) by = hit != null ? "memo" : "ids";
            bool news = false;
            foreach (int id in raid.Ids) if (g.Served.Add(id)) news = true;
            if (key != null && g.ServedRaids.Add(key)) news = true;
            if (hit != null) foreach (int id in raid.Ids) if (!hit.Ids.Contains(id)) hit.Ids.Add(id);
            if (news && g.AlertWarned.Add("served" + (key ?? raid.Key.ToString(CultureInfo.InvariantCulture))))
                Log("alert raid served group=" + g.Name + " raid=" + raid.Key + " by=" + by + " ids=" + string.Join(",", raid.Ids));
        }
    }
}
