using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoiceBotBridge
{
    // T-341 phase B: the prepared deck tasks of a group. The key of a task goes to the group; the controller asks the deck broker to allow the launch (window "strike", the tier of
    // the ASW window), and when the task has turned into an aircraft in the air the aircraft is adopted (same type, not a member of any group, not seen before, the nearest to the deck)
    // and flies to the wait point like any member. The plan counts only the aircraft in the air; the deck ones are a warning (deck_pending:n).
    internal sealed partial class StrikeController
    {
        private readonly Dictionary<string, StrikeDeckItem> _deckInfo = new Dictionary<string, StrikeDeckItem>();
        private readonly Dictionary<string, double> _deckReqAt = new Dictionary<string, double>();     // key -> mission time of the allow request (pending)
        private readonly HashSet<string> _deckAllowed = new HashSet<string>();
        private readonly Dictionary<string, HashSet<int>> _deckKnown = new Dictionary<string, HashSet<int>>();   // key -> aircraft uids seen when the allow was asked
        private readonly Dictionary<string, double> _deckGoneAt = new Dictionary<string, double>();

        private void ForgetDeckKey(string key)
        {
            _deckInfo.Remove(key); _deckReqAt.Remove(key); _deckAllowed.Remove(key); _deckKnown.Remove(key); _deckGoneAt.Remove(key);
        }

        /// <summary>The held-for-the-deck time of the group, s (-1 = not waiting): for the window label.</summary>
        public double DeckHeldS(StrikeGroup g) { try { return Deck == null ? -1 : Deck.HeldS("strike", g.Name); } catch (Exception) { return -1; } }

        private AirGroupPrio Prio(StrikeGroup g)
        {
            return new AirGroupPrio { Name = g.Name, Order = "strike", Window = "strike", HasContact = false, WindowIndex = Ownership == null ? g.Seq : Ownership.GroupSeq("strike", g.Name) };
        }

        private void TickDeck(StrikeGroup g, double t)
        {
            if (g.DeckKeys.Count == 0) return;
            var items = _g.DeckItems() ?? new List<StrikeDeckItem>();
            var decks = new HashSet<int>();
            foreach (string key in g.DeckKeys.ToList())
            {
                var it = items.FirstOrDefault(x => x != null && x.Key == key);
                if (it != null)
                {
                    _deckInfo[key] = it; decks.Add(it.DeckUid);
                    _deckGoneAt.Remove(key);
                    if (Deck == null || _deckAllowed.Contains(key) || _deckReqAt.ContainsKey(key)) continue;
                    _deckReqAt[key] = t;
                    var known = new HashSet<int>();
                    foreach (var a in _g.Aircraft() ?? new List<StrikeAircraft>()) if (a != null) known.Add(a.Uid);
                    _deckKnown[key] = known;
                    string k = key, gn = g.Name;
                    Deck.Submit(new DeckRequest { DeckUid = it.DeckUid, Window = "strike", Group = g.Name, Kind = DeckKind.Allow, Key = key, Reason = "strike", Prio = Prio(g) },
                        r =>
                        {
                            if (r.Err == null) { _deckAllowed.Add(k); _log("[Strike] strike deck " + gn + " key=" + k + " -> " + (r.Held ? "held" : "allow")); }
                            else { _deckReqAt.Remove(k); _log("[Strike] strike deck " + gn + " key=" + k + " -> " + r.Err); }
                        });
                    continue;
                }
                // the task is gone: an aircraft was made of it, or the task was cancelled
                StrikeDeckItem info;
                _deckInfo.TryGetValue(key, out info);
                if (!(_deckAllowed.Contains(key) || _deckReqAt.ContainsKey(key)) || info == null)
                {
                    g.DeckKeys.Remove(key); ForgetDeckKey(key);
                    _log("[Strike] strike deck " + g.Name + " key=" + key + " -> gone");
                    continue;
                }
                int uid = PickAdoptee(info, key);
                if (uid != 0)
                {
                    g.DeckKeys.Remove(key); ForgetDeckKey(key);
                    if (!g.Members.Contains(uid)) g.Members.Add(uid);
                    if (Ownership != null) Ownership.Claim(uid, "strike", g.Name);
                    _ms.Remove(uid);
                    _log("[Strike] strike adopt " + g.Name + " key=" + key + " uid=" + I(uid));
                    DeckOnAdopt(g, key, uid);
                    if (g.Timeline != null) MarkStale(g, "aircraft_changed");
                    RecomputeWait(g, "adopt");
                    continue;
                }
                double since;
                if (!_deckGoneAt.TryGetValue(key, out since)) { _deckGoneAt[key] = t; continue; }
                if (t < since) { _deckGoneAt[key] = t; continue; }
                if (t - since >= _p.AdoptGapS)
                {
                    g.DeckKeys.Remove(key); ForgetDeckKey(key);
                    g.Left.Add(new StrikeLeave { Uid = 0, Why = "deck_lost", T = t });
                    DeckOnLost(g, key, "lost");
                    _log("[Strike] strike deck " + g.Name + " key=" + key + " -> no aircraft after " + F1(_p.AdoptGapS) + " s");
                }
            }
            if (Deck != null && decks.Count > 0) { try { Deck.Declare("strike", Prio(g), decks); } catch (Exception) { } }
        }

        /// <summary>The aircraft of a launched deck task: airborne, the same type, a striker, in no group and owned by nobody, unseen when the allow was asked; the nearest to the deck.</summary>
        private int PickAdoptee(StrikeDeckItem info, string key)
        {
            HashSet<int> known;
            _deckKnown.TryGetValue(key, out known);
            int vehicle = info.VehicleUid != 0 ? info.VehicleUid : _g.TaskVehicleUid(key);
            if (vehicle != 0)
            {
                // T-342 B: the aircraft of the launching task itself (not a look-alike of another window)
                var av = Air(vehicle);
                if (av != null && av.Airborne && av.Type == info.Type && GroupOf(av.Uid) == null && (OwnerOf == null || OwnerOf(av.Uid) == null)) return av.Uid;
            }
            int best = 0; double bd = double.MaxValue;
            foreach (var a in _g.Aircraft() ?? new List<StrikeAircraft>())
            {
                if (a == null || !a.Airborne || a.Type != info.Type || GroupOf(a.Uid) != null) continue;
                if (StrikeAirMath.RoleOf(a) != (info.Ew != null ? "ew" : "striker")) continue;
                if (OwnerOf != null && OwnerOf(a.Uid) != null) continue;
                if (known != null && known.Contains(a.Uid)) continue;
                if (Ownership != null) { string w; if (Ownership.Owner(a.Uid, out w) != null) continue; }
                double d = AswMath.DistNm(new GeoPt(a.Lat, a.Lon), new GeoPt(info.Lat, info.Lon));
                if (d < bd) { bd = d; best = a.Uid; }
            }
            return best;
        }
    }
}
