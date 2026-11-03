// T-160 / ADR-0068 (supplement 2026-10-22): per-unit range-ring state of the session: the checked weapons of own units and of foreign tracks
// (a set each, in the order of checking) and the per-unit sonar override. A checked weapon or a sonar override "on" pins the unit / track.
// Not saved (the session of the mission). No Unity, no game.
using System;
using System.Collections.Generic;

namespace VoiceBotBridge.Ui
{
    internal sealed class RangeRingPins
    {
        private readonly List<int> _pinned = new List<int>();                       // own / allied uids in pin order
        private readonly List<int> _tracks = new List<int>();                       // foreign tracks (the player's track id) in pin order
        private readonly Dictionary<int, bool> _sonar = new Dictionary<int, bool>();
        private readonly Dictionary<int, List<string>> _weapon = new Dictionary<int, List<string>>();
        private readonly Dictionary<int, List<string>> _trackWeapon = new Dictionary<int, List<string>>();
        private static readonly IList<string> None = new string[0];

        /// <summary>Grows on every change (the feature rebuilds the scene).</summary>
        public int Version { get; private set; }
        /// <summary>Pinned units.</summary>
        public int Count { get { return _pinned.Count; } }
        /// <summary>Pinned foreign tracks (= tracks with a checked weapon).</summary>
        public int TrackCount { get { return _tracks.Count; } }
        /// <summary>Anything pinned (units or tracks).</summary>
        public bool Any { get { return _pinned.Count > 0 || _tracks.Count > 0; } }
        /// <summary>Explicit sonar overrides.</summary>
        public int OverrideCount { get { return _sonar.Count; } }
        /// <summary>Checked weapons of own units and tracks, all keys counted.</summary>
        public int WeaponCount { get { int n = 0; foreach (var l in _weapon.Values) n += l.Count; foreach (var l in _trackWeapon.Values) n += l.Count; return n; } }

        /// <summary>In pin order (a copy).</summary>
        public IList<int> Pinned() { return new List<int>(_pinned); }
        public bool IsPinned(int uid) { return _pinned.Contains(uid); }

        /// <summary>Pinned tracks in pin order (a copy).</summary>
        public IList<int> PinnedTracks() { return new List<int>(_tracks); }
        public bool IsTrackPinned(int trackId) { return _tracks.Contains(trackId); }

        // ---- own units ------------------------------------------------------------------------------------------------------------------
        /// <summary>The checked weapons of an own unit in the order of checking (a copy); empty = no weapon rings.</summary>
        public IList<string> Weapons(int uid) { List<string> l; return _weapon.TryGetValue(uid, out l) ? new List<string>(l) : None; }
        public bool HasWeapon(int uid, string key) { List<string> l; return key != null && _weapon.TryGetValue(uid, out l) && l.Contains(key); }
        public IList<int> WeaponUnits() { return new List<int>(_weapon.Keys); }

        private bool SonarPin(int uid) { bool v; return _sonar.TryGetValue(uid, out v) && v; }

        private void Unpin(int uid) { if (!_weapon.ContainsKey(uid) && !SonarPin(uid)) _pinned.Remove(uid); }

        /// <summary>Checks / unchecks one weapon of an own unit; returns the new state of that key. Checking pins the unit; when the last weapon goes
        /// the pin stays only with a sonar override "on". An empty key does nothing (false).</summary>
        public bool ToggleWeapon(int uid, string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            List<string> l;
            if (_weapon.TryGetValue(uid, out l) && l.Remove(key))
            {
                if (l.Count == 0) { _weapon.Remove(uid); Unpin(uid); }
                Version++;
                return false;
            }
            if (l == null) _weapon[uid] = l = new List<string>();
            l.Add(key);
            if (!_pinned.Contains(uid)) _pinned.Add(uid);
            Version++;
            return true;
        }

        /// <summary>"Clear": drops every checked weapon of the unit (the pin stays only with a sonar override "on"). Version++ only when something went.</summary>
        public void ClearWeapons(int uid)
        {
            if (!_weapon.Remove(uid)) return;
            Unpin(uid);
            Version++;
        }

        /// <summary>Drops one checked weapon (prune: the magazine is empty); false = it was not checked.</summary>
        public bool RemoveWeapon(int uid, string key) { return HasWeapon(uid, key) && !ToggleWeapon(uid, key); }

        /// <summary>Effective sonar value: the explicit override of the unit, else global.</summary>
        public bool SonarOn(int uid, bool global) { bool v; return _sonar.TryGetValue(uid, out v) ? v : global; }

        /// <summary>Sets the override to the opposite of the effective value; "on" pins the unit, "off" unpins it when no weapon is checked. Returns the new state.</summary>
        public bool ToggleSonar(int uid, bool global)
        {
            bool n = !SonarOn(uid, global);
            _sonar[uid] = n;
            if (n) { if (!_pinned.Contains(uid)) _pinned.Add(uid); }
            else Unpin(uid);
            Version++;
            return n;
        }

        // ---- foreign tracks -------------------------------------------------------------------------------------------------------------
        /// <summary>The checked weapons of a foreign track in the order of checking (a copy).</summary>
        public IList<string> TrackWeapons(int track) { List<string> l; return _trackWeapon.TryGetValue(track, out l) ? new List<string>(l) : None; }
        public bool HasTrackWeapon(int track, string key) { List<string> l; return key != null && _trackWeapon.TryGetValue(track, out l) && l.Contains(key); }
        public IList<int> WeaponTracks() { return new List<int>(_trackWeapon.Keys); }

        /// <summary>Checks / unchecks one weapon of a foreign track; returns the new state of that key. The first check pins the track, the last uncheck unpins it.</summary>
        public bool ToggleTrackWeapon(int track, string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            List<string> l;
            if (_trackWeapon.TryGetValue(track, out l) && l.Remove(key))
            {
                if (l.Count == 0) { _trackWeapon.Remove(track); _tracks.Remove(track); }
                Version++;
                return false;
            }
            if (l == null) _trackWeapon[track] = l = new List<string>();
            l.Add(key);
            if (!_tracks.Contains(track)) _tracks.Add(track);
            Version++;
            return true;
        }

        /// <summary>"Clear": drops every checked weapon of the track and its pin.</summary>
        public void ClearTrackWeapons(int track) { DropTrack(track); }

        /// <summary>Drops one checked weapon of a track (prune: the class has no such weapon); false = it was not checked.</summary>
        public bool RemoveTrackWeapon(int track, string key) { return HasTrackWeapon(track, key) && !ToggleTrackWeapon(track, key); }

        /// <summary>The track is gone for the player (destroyed / lost) or cleared: its pin and its checked weapons go at once. false = nothing was there.</summary>
        public bool DropTrack(int track)
        {
            bool had = _tracks.Remove(track) | _trackWeapon.Remove(track);
            if (had) Version++;
            return had;
        }

        // ---- bulk -----------------------------------------------------------------------------------------------------------------------
        /// <summary>Drops pins, weapons and overrides of units that keep() rejects; returns how many units were dropped.</summary>
        public int Prune(Func<int, bool> keep)
        {
            var gone = new HashSet<int>();
            foreach (int u in _pinned) if (!keep(u)) gone.Add(u);
            foreach (int u in _sonar.Keys) if (!keep(u)) gone.Add(u);
            foreach (int u in _weapon.Keys) if (!keep(u)) gone.Add(u);
            if (gone.Count == 0) return 0;
            foreach (int u in gone) { _pinned.Remove(u); _sonar.Remove(u); _weapon.Remove(u); }
            Version++;
            return gone.Count;
        }

        /// <summary>T-164: drops own / allied pins, weapons and overrides; pinned tracks stay. Returns the dropped units; Version++ only when something went.</summary>
        public int ClearOwn()
        {
            var gone = new HashSet<int>(_pinned);
            foreach (int u in _sonar.Keys) gone.Add(u);
            foreach (int u in _weapon.Keys) gone.Add(u);
            if (gone.Count == 0) return 0;
            _pinned.Clear(); _sonar.Clear(); _weapon.Clear();
            Version++;
            return gone.Count;
        }

        public void Clear()
        {
            if (_pinned.Count == 0 && _tracks.Count == 0 && _sonar.Count == 0 && _weapon.Count == 0 && _trackWeapon.Count == 0) return;
            _pinned.Clear(); _tracks.Clear(); _sonar.Clear(); _weapon.Clear(); _trackWeapon.Clear();
            Version++;
        }

        public static string SelfTest()
        {
            var p = new RangeRingPins();
            if (p.Count != 0 || p.Any || p.IsPinned(1) || p.SonarOn(1, false) || !p.SonarOn(1, true)) return "empty";
            // own weapons: a set in the order of checking; the first pins, the last unchecked unpins
            int v0 = p.Version;
            if (!p.ToggleWeapon(5, "sm2") || !p.ToggleWeapon(5, "harpoon") || !p.IsPinned(5) || p.Version <= v0) return "two weapons pin";
            var w5 = p.Weapons(5);
            if (w5.Count != 2 || w5[0] != "sm2" || w5[1] != "harpoon" || p.WeaponCount != 2) return "weapon set order";
            w5.Clear();
            if (p.Weapons(5).Count != 2) return "Weapons() is a copy";
            if (p.ToggleWeapon(5, "sm2") || p.HasWeapon(5, "sm2") || !p.HasWeapon(5, "harpoon") || !p.IsPinned(5)) return "uncheck one keeps the other";
            if (p.ToggleWeapon(5, "harpoon") || p.IsPinned(5) || p.Weapons(5).Count != 0) return "last weapon unchecked: unpinned";
            if (p.ToggleWeapon(5, "") || p.ToggleWeapon(5, null) || p.IsPinned(5)) return "empty key does nothing";
            p.ToggleWeapon(5, "a"); p.ToggleWeapon(5, "b");
            p.ClearWeapons(5);
            if (p.IsPinned(5) || p.Weapons(5).Count != 0) return "ClearWeapons unpins";
            int pv = p.Version; p.ClearWeapons(5);
            if (p.Version != pv) return "ClearWeapons of nothing";
            p.ToggleWeapon(6, "y"); p.ToggleWeapon(6, "z");
            if (!p.RemoveWeapon(6, "y") || p.RemoveWeapon(6, "y") || !p.HasWeapon(6, "z")) return "RemoveWeapon";
            // pin order
            p.Clear();
            p.ToggleWeapon(3, "a"); p.ToggleWeapon(1, "a"); p.ToggleWeapon(2, "a");
            var order = p.Pinned();
            if (order.Count != 3 || order[0] != 3 || order[1] != 1 || order[2] != 2) return "pin order";
            // sonar override: on pins, off unpins unless a weapon is checked
            var s = new RangeRingPins();
            if (!s.ToggleSonar(2, false) || !s.SonarOn(2, false) || !s.IsPinned(2)) return "sonar on pins";
            if (s.ToggleSonar(2, false) || s.SonarOn(2, true) || s.IsPinned(2)) return "sonar off unpins";
            s.ToggleWeapon(2, "x");
            if (!s.ToggleSonar(2, false) || s.ToggleSonar(2, false) || !s.IsPinned(2)) return "sonar off keeps the pin of a weapon";
            s.ToggleSonar(2, false); s.ToggleWeapon(2, "x");
            if (!s.IsPinned(2)) return "last weapon unchecked keeps the sonar pin";
            if (s.OverrideCount != 1) return "override count";
            // prune / clear
            p = new RangeRingPins();
            p.ToggleWeapon(1, "a"); p.ToggleWeapon(2, "a"); p.ToggleSonar(1, false);
            if (p.Prune(u => u != 1) != 1 || p.IsPinned(1) || p.Count != 1 || p.SonarOn(1, false) || p.Weapons(1).Count != 0) return "prune";
            if (p.Prune(u => true) != 0) return "prune nothing";
            // tracks: the first check pins, the last uncheck unpins
            var t = new RangeRingPins();
            if (t.ToggleTrackWeapon(9, "") || t.Any) return "track empty key";
            t.ToggleTrackWeapon(9, "k"); t.ToggleTrackWeapon(9, "m");
            var t9 = t.TrackWeapons(9);
            if (t9.Count != 2 || t9[0] != "k" || t9[1] != "m" || !t.IsTrackPinned(9) || t.TrackCount != 1 || t.IsPinned(9)) return "track weapons: a set, separate key space";
            if (t.ToggleTrackWeapon(9, "k") || t.HasTrackWeapon(9, "k") || !t.HasTrackWeapon(9, "m")) return "track weapon unchecked alone";
            if (t.ToggleTrackWeapon(9, "m") || t.IsTrackPinned(9) || t.TrackWeapons(9).Count != 0) return "last track weapon unchecked: unpinned";
            t.ToggleTrackWeapon(9, "k"); t.ToggleTrackWeapon(9, "j");
            t.ClearTrackWeapons(9);
            if (t.IsTrackPinned(9) || t.WeaponCount != 0) return "ClearTrackWeapons";
            t.ToggleTrackWeapon(10, "k"); t.ToggleTrackWeapon(10, "j"); t.ToggleTrackWeapon(11, "k");
            var to = t.PinnedTracks();
            if (to.Count != 2 || to[0] != 10 || to[1] != 11) return "track order";
            if (!t.RemoveTrackWeapon(10, "j") || t.RemoveTrackWeapon(10, "j") || !t.HasTrackWeapon(10, "k") || !t.IsTrackPinned(10)) return "RemoveTrackWeapon";
            int dv = t.Version;
            if (!t.DropTrack(10) || t.IsTrackPinned(10) || t.TrackWeapons(10).Count != 0 || t.Version <= dv) return "DropTrack";
            dv = t.Version;
            if (t.DropTrack(10) || t.Version != dv) return "DropTrack of nothing";
            // ClearOwn keeps the tracks; Clear drops all
            t.ToggleWeapon(7, "a"); t.ToggleSonar(8, false);
            int tv = t.Version;
            if (t.ClearOwn() != 2 || t.Count != 0 || t.OverrideCount != 0 || !t.HasTrackWeapon(11, "k") || t.Version <= tv) return "ClearOwn keeps track choices";
            tv = t.Version;
            if (t.ClearOwn() != 0 || t.Version != tv) return "ClearOwn twice";
            t.Clear();
            if (t.WeaponCount != 0 || t.Any || t.Version == tv) return "Clear drops all choices";
            return null;
        }
    }
}
