using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;

namespace VoiceBotBridge
{
    // T-341 phase C (R1-R2): the orders of the execution on the game side. The launch goes through the shared Attack only (the same one strike_probe op=fire and the dev attack use):
    // the target is checked first (a hostile, identified, not civilian surface track of the player's table), confirm:true, the neutral override is never given (the neutral corridor of the game stays a refusal).
    // The weapon status goes through the RoeCmd path with the control memory (as the CAP: "Return control" restores it). The fate of a target and the missiles in flight are read only.
    internal sealed partial class GameApi
    {
        private sealed partial class StrikeGameImpl
        {
            private readonly StrikeMslCache _mslCache = new StrikeMslCache();

            /// <summary>null = ok; else the error code of Attack (not_hostile | out_of_range | neutral_near | no_launcher | unit_not_found | ...) or a code of this layer (excluded | no_target | no_ammo).</summary>
            public string Issue(int uid, int targetId, string ammo, int salvo)
            {
                try
                {
                    if (uid <= 0 || string.IsNullOrEmpty(ammo)) return "bad_args";
                    var a = Aircraft().FirstOrDefault(x => x != null && x.Uid == uid);
                    if (a == null) return "unit_not_found";
                    var m = a.Missiles.FirstOrDefault(x => x.Ammo == ammo);
                    if (m == null || m.Count <= 0) return "no_ammo";
                    if (!StrikeAirMath.IsStrikeMissile(m)) return "not_asm";
                    var ship = Ships().FirstOrDefault(x => x != null && x.TrackId == targetId);   // review of phase A: a real launch only at a hostile, identified, non-civilian surface track
                    if (ship == null) return "no_target";
                    if (StrikePlanMath.Exclusion(ship) != null) return "excluded";
                    int n = Math.Max(1, Math.Min(Math.Min(8, salvo), m.Count));
                    var c = new Dictionary<string, object> { { "uid", (double)uid }, { "target_id", (double)targetId }, { "weapon", ammo }, { "salvo", (double)n }, { "confirm", true } };
                    string ack = _a.Attack("null", c, false);
                    if (ack != null && ack.Contains("\"ok\":true")) return null;
                    string code = "attack_failed";
                    try { var d = Json.Parse(ack) as Dictionary<string, object>; if (d != null && d.ContainsKey("error")) code = d["error"] as string ?? code; } catch (Exception) { }
                    return code;
                }
                catch (Exception e) { return "issue_failed:" + e.GetType().Name; }
            }

            /// <summary>The weapon status of an aircraft (hold | tight | free); previous = the one before. The first value is kept in the control memory for "Return control".</summary>
            public string SetRoe(int uid, string roe, out string previous)
            {
                previous = null;
                try
                {
                    var u = _a.FindOwnUnit(uid);
                    if (u == null) return "unit_not_found";
                    string want = (roe ?? "").ToLowerInvariant();
                    if (want != "tight" && want != "hold" && want != "free") return "bad_roe";
                    previous = _a.WeaponStatusName(u);
                    _a._ctlMem.RememberRoe(u, previous);
                    if (previous != want)
                    {
                        var cmd = new Dictionary<string, object> { { "uid", (double)uid }, { "status", want } };
                        if (want == "free") cmd["confirm"] = true;
                        string ack = _a.RoeCmd("null", cmd);
                        if (ack == null || !ack.Contains("\"ok\":true")) return "roe_failed";
                    }
                    _a._ctlMem.NoteRoeSet(u, want);
                    return null;
                }
                catch (Exception e) { return "roe_failed:" + (e.InnerException ?? e).GetType().Name; }
            }

            /// <summary>sunk | damaged | intact | unknown, only from what the player can see. Without the hit ledger (hit_estimate off): sunk (the object of the track is destroyed) or unknown.</summary>
            public string Fate(int trackId, out int hits)
            {
                hits = 0;
                try
                {
                    var ledger = _a.HeLedger;
                    int n = ledger == null ? -1 : ledger.Count(trackId);
                    hits = Math.Max(0, n);
                    var v = _a.FindContact(trackId);
                    bool gone = v == null || _a.IsGone(v);
                    if (gone) { if (ledger == null) return v != null ? "sunk" : "unknown"; return n > 0 ? "sunk" : "unknown"; }
                    if (ledger == null) return "unknown";
                    return n > 0 ? "damaged" : "intact";
                }
                catch (Exception) { return "unknown"; }
            }

            /// <summary>T-342 C: destroyed | alive | gone | unknown for a track (the clear_pause sensor, read once a second). The contact left the table = gone; the contact is there but its object is gone or IsDestroyed = destroyed. Read only, no EstimateShot.</summary>
            public string Destroyed(int trackId)
            {
                try
                {
                    var v = _a.FindContact(trackId);
                    if (v == null) return "gone";
                    var obj = _a.Get(v, CoreSpecs.VehObject);   // leak-ok: only "is it destroyed" (the same fact the player sees as the wreck vanishing from the map)
                    if (obj == null || !IsAlive(obj)) return "destroyed";
                    return BoolOf(_a.Get(obj, ContactInfoSpecs.ObjIsDestroyed)) ? "destroyed" : "alive";
                }
                catch (Exception) { return "unknown"; }
            }

            /// <summary>The missiles of the aircraft that are in flight (the launched list of its weapon systems): id (hash of the object), the last target as a track, the position.</summary>
            public IList<StrikeMslSeen> InFlight(int uid)
            {
                var res = new List<StrikeMslSeen>();
                try
                {
                    // T-353: the missiles are noted while the carrier is alive and followed by their own objects after its loss (the list of the carrier is gone with it, the missiles still fly and hit)
                    var unit = _a.FindOwnUnit(uid);
                    var systems = unit == null ? null : _a.Get(_a.Get(unit, AirOpsSpecs.ObjObp), StrikeSpecs.ObpWeaponSystems) as IEnumerable;
                    if (systems != null)
                    {
                        foreach (var ws in systems)
                        {
                            var list = ws == null ? null : _a.Get(ws, StrikeSpecs.WsLaunched) as IEnumerable;
                            if (list == null) continue;
                            foreach (var w in list)
                            {
                                try { if (w != null && IsAlive(w)) _mslCache.Note(uid, RuntimeHelpers.GetHashCode(w), w); }
                                catch (Exception) { }
                            }
                        }
                    }
                    _mslCache.Prune(h => IsAlive(h));
                    double now = Now;
                    foreach (var kv in _mslCache.Live(uid, h => IsAlive(h)))
                    {
                        try
                        {
                            var w = kv.Value;
                            var last = _a.Get(w, StrikeSpecs.WbLastTarget);
                            var init = _a.Get(w, StrikeSpecs.WbInitTarget);
                            int tid = last != null && IsAlive(last) ? _a.RrMenuTrack(last) : (init != null && IsAlive(init) ? _a.RrMenuTrack(init) : -1);
                            double lat, lon, alt;
                            if (_a.OwnPos(w, out lat, out lon, out alt)) _mslCache.SetPos(kv.Key, lat, lon);
                            else if (!_mslCache.LastPos(kv.Key, out lat, out lon)) continue;   // R1: an orphan missile the game refuses a position for keeps its last one
                            res.Add(new StrikeMslSeen { Id = kv.Key, Uid = uid, TargetId = tid, Lat = lat, Lon = lon, T = now });
                        }
                        catch (Exception) { }
                    }
                }
                catch (Exception) { }
                return res;
            }
        }
    }
}
