using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    // T-341 phase B (L2): the prepared launch tasks of the decks of own ships whose loadout carries anti-ship missiles. Read only: the deck broker allows the launch.
    // A task is listed when its loadout (Loadouts(deck, type).Weapons) has a weapon that the missile class says is anti-ship; the ammunition parameters are read by file name
    // (the cache of the aircraft layer), no data = Why "loadout_unknown" (listed grey).
    internal sealed partial class GameApi
    {
        private sealed partial class StrikeGameImpl
        {
            private List<StrikeDeckItem> _deck = new List<StrikeDeckItem>();
            private int _deckAt = int.MinValue;

            public IList<StrikeDeckItem> DeckItems()
            {
                if (Fresh(_deckAt)) return _deck;
                var res = new List<StrikeDeckItem>();
                try
                {
                    foreach (var ship in _a.DeckShipsTick())
                    {
                        try
                        {
                            int deckUid = _a.UidOf(ship);
                            double lat, lon, h;
                            bool pos = _a.OwnPos(ship, out lat, out lon, out h);
                            string name = _a.NameOf(ship);
                            ScanVehicles(ship);
                            var loadouts = new Dictionary<string, List<LoadoutOption>>();
                            foreach (var t in _a.AirOps.DeckTasks(deckUid))
                            {
                                if (t == null || t.Key == null || !t.Key.StartsWith("task:", StringComparison.Ordinal) || t.Type == null || t.Loadout == null) continue;
                                List<LoadoutOption> los;
                                if (!loadouts.TryGetValue(t.Type, out los)) { los = _a.AirOps.Loadouts(deckUid, t.Type) ?? new List<LoadoutOption>(); loadouts[t.Type] = los; }
                                var lo = los.FirstOrDefault(x => x.Name == t.Loadout);
                                if (lo == null) continue;
                                var weapons = lo.Weapons ?? new Dictionary<string, int>();   // phase D: the loadout of a jammer (EW) has no ammunition
                                var it = new StrikeDeckItem { DeckUid = deckUid, Key = t.Key, Type = t.Type, Loadout = t.Loadout, State = t.State, DeckName = name, RemainS = t.RemainS, Lat = pos ? lat : 0, Lon = pos ? lon : 0 };
                                int vu; if (_vehOfKey.TryGetValue(t.Key, out vu)) it.VehicleUid = vu;
                                bool unknown = false;
                                foreach (var kv in weapons)
                                {
                                    var m = MissileOf(deckUid, kv.Key, kv.Value);
                                    if (m == null) { unknown = true; continue; }
                                    it.Missiles.Add(m);
                                }
                                StrikeAirMath.Classify(new[] { new StrikeAircraft { Missiles = it.Missiles } });
                                if (!it.Missiles.Any(StrikeAirMath.IsStrikeMissile))
                                {
                                    var dew = DeckEw(t.Type, t.Loadout);   // phase D: a jammer aircraft (loadout without anti-ship missiles, the class has an offensive jammer)
                                    if (dew != null) { it.Ew = dew; res.Add(it); continue; }
                                }
                                if (!it.Missiles.Any(StrikeAirMath.IsStrikeMissile)) { if (unknown && it.Missiles.Count == 0 && weapons.Count > 0) { it.Why = "loadout_unknown"; res.Add(it); } continue; }
                                res.Add(it);
                            }
                        }
                        catch (Exception) { }
                    }
                }
                catch (Exception) { }
                _deck = res; _deckAt = Environment.TickCount;
                return res;
            }

            private readonly Dictionary<string, int> _vehOfKey = new Dictionary<string, int>();   // "task:<uid>" -> the aircraft that task spawned (T-342 B: the adoption takes exactly this one)

            /// <summary>The aircraft a deck task has spawned (LaunchTask.Vehicle), by the key of the task; 0 = not known. Only "task:" keys are read.</summary>
            public int TaskVehicleUid(string key) { int u; return key != null && _vehOfKey.TryGetValue(key, out u) ? u : 0; }

            private void ScanVehicles(object ship)
            {
                try
                {
                    if (!_a.Present(AirOpsSpecs.TaskVehicle)) return;
                    var tasks = _a.Get(_a.DeckOf(ship), AirOpsSpecs.DeckTasks) as IEnumerable;
                    if (tasks == null) return;
                    foreach (var t in tasks)
                    {
                        if (t == null) continue;
                        var veh = _a.Get(t, AirOpsSpecs.TaskVehicle);
                        if (veh == null || !GameApi.IsAlive(veh)) continue;
                        var uid = GameApi.D(_a.Get(t, AirOpsSpecs.TaskUid));
                        int vu = _a.UidOf(veh);
                        if (uid.HasValue && vu > 0) _vehOfKey["task:" + ((long)uid.Value).ToString(System.Globalization.CultureInfo.InvariantCulture)] = vu;
                    }
                }
                catch (Exception) { }
            }

            private List<StrikeDeckInfo> _decks = new List<StrikeDeckInfo>();
            private int _decksAt = int.MinValue;

            public string DeckTimingsMode() { try { return _a.AirOps.DeckTimingsMode(); } catch (Exception) { return "Realistic"; } }

            private Dictionary<string, int> IntMap(object d)
            {
                var e = d as IEnumerable;
                if (e == null) return null;
                var res = new Dictionary<string, int>();
                foreach (var kv in e)
                {
                    string k = _a.Get(kv, CoreSpecs.PairKey) as string; var n = GameApi.D(_a.Get(kv, CoreSpecs.PairValue));
                    if (k != null && n.HasValue) res[k] = (int)n.Value;
                }
                return res;
            }

            private bool DeckFlag(object deck)
            {
                try
                {
                    if (_a.Present(StrikeSpecs.DeckLaunchesDisabled)) return BoolOf(_a.Get(deck, StrikeSpecs.DeckLaunchesDisabled));
                    if (_a.Present(StrikeSpecs.DeckLaunchesDisabledField)) return BoolOf(_a.Get(deck, StrikeSpecs.DeckLaunchesDisabledField));
                }
                catch (Exception) { }
                return false;
            }

            /// <summary>The own decks with their hangar rows (one per type: Numbers = the smaller of the hangar record and the squadrons) and the loadouts of EACH row (Loadouts(deck, type) of that very row, never of another type), the magazine of the deck by category; read only, cached DeckRefreshS.</summary>
            public IList<StrikeDeckInfo> Decks()
            {
                if (_decksAt != int.MinValue && unchecked(Environment.TickCount - _decksAt) >= 0 && unchecked(Environment.TickCount - _decksAt) < StrikeCalib.DeckRefreshS * 1000) return _decks;
                var res = new List<StrikeDeckInfo>();
                try
                {
                    string mode = DeckTimingsMode();
                    foreach (var ship in _a.DeckShipsTick())
                    {
                        try
                        {
                            int deckUid = _a.UidOf(ship);
                            double lat, lon, h;
                            if (!_a.OwnPos(ship, out lat, out lon, out h)) continue;
                            var di = new StrikeDeckInfo { DeckUid = deckUid, Name = _a.NameOf(ship), Lat = lat, Lon = lon, Down = _a.AirOps.DeckInoperable(deckUid) };
                            var deck = _a.DeckOf(ship);
                            if (deck == null) continue;
                            di.LaunchesDisabled = DeckFlag(deck);
                            if (_a.Present(StrikeSpecs.DeckMagazine)) di.Stock = IntMap(_a.Get(deck, StrikeSpecs.DeckMagazine));
                            if (di.Stock != null && _a.Present(StrikeSpecs.DeckMagazineMax)) di.StockMax = IntMap(_a.Get(deck, StrikeSpecs.DeckMagazineMax));
                            var vs = _a.Get(deck, AirOpsSpecs.DeckVehicles) as IEnumerable;
                            var rows = new Dictionary<string, StrikeHangarRow>(); var vtSum = new Dictionary<string, double>(); var sqSum = new Dictionary<string, double>(); var rec = new Dictionary<string, object>();
                            if (vs != null)
                                foreach (var v in vs)
                                {
                                    string type = _a.Get(v, AirOpsSpecs.VtFileName) as string;
                                    if (type == null) continue;
                                    double n = GameApi.D(_a.Get(v, AirOpsSpecs.VtNumbers)) ?? 0, sq = 0;
                                    var sqs = _a.Get(v, AirOpsSpecs.VtSquadrons) as IEnumerable;
                                    if (sqs != null) foreach (var s0 in sqs) sq += GameApi.D(_a.Get(s0, AirOpsSpecs.SquadronNumbers)) ?? 0;
                                    if (!rows.ContainsKey(type))
                                    {
                                        string cls = (_a.Get(v, AirOpsSpecs.VtType) ?? "").ToString();
                                        rows[type] = new StrikeHangarRow { DeckUid = deckUid, Type = type, ObjType = cls.IndexOf("Helicopter", StringComparison.OrdinalIgnoreCase) >= 0 ? "Helicopter" : "Aircraft" };
                                        vtSum[type] = 0; sqSum[type] = 0; rec[type] = v;
                                    }
                                    vtSum[type] += n; sqSum[type] += sq;
                                }
                            foreach (var kv in rows)
                            {
                                var row = kv.Value;
                                row.Numbers = (int)Math.Min(vtSum[kv.Key], sqSum[kv.Key]);
                                double[] rg;
                                if (_typeRange.TryGetValue(row.Type, out rg)) { row.MaxRangeNm = rg[0]; row.CruiseKn = rg[1]; row.CruiseAltFt = rg[2]; }
                                if (row.Numbers > 0) FillLoadouts(deckUid, row, rec[kv.Key], mode);
                                di.Rows.Add(row);
                            }
                            res.Add(di);
                        }
                        catch (Exception) { }
                    }
                }
                catch (Exception) { }
                _decks = res; _decksAt = Environment.TickCount;
                return res;
            }

            private void FillLoadouts(int deckUid, StrikeHangarRow row, object vt, string mode)
            {
                var cats = new Dictionary<string, Dictionary<string, int>>();
                try
                {
                    var raw = _a.Get(vt, AirOpsSpecs.VtLoadouts) as IEnumerable ?? _a.Get(vt, AirOpsSpecs.VtLoadoutsField) as IEnumerable;
                    if (raw != null && _a.Present(StrikeSpecs.LoCategories))
                        foreach (var lo in raw)
                        {
                            string name = _a.Get(lo, AirOpsSpecs.LoName) as string;
                            var m = name == null ? null : IntMap(_a.Get(lo, StrikeSpecs.LoCategories));
                            if (m != null && m.Count > 0) cats[name] = m;
                        }
                }
                catch (Exception) { }
                foreach (var o in _a.AirOps.Loadouts(deckUid, row.Type) ?? new List<LoadoutOption>())   // the list of THIS row's type
                {
                    var l = new StrikeDeckLoadout { Name = o.Name, Display = o.Display, Available = o.Available, ReadyS = AirOpsMath.ReadyS(o, mode, 0) };
                    foreach (var w in o.Weapons ?? new Dictionary<string, int>())
                    {
                        var m = MissileOf(deckUid, w.Key, w.Value);
                        if (m == null) l.Unknown = true; else l.Missiles.Add(m);
                    }
                    StrikeAirMath.Classify(new[] { new StrikeAircraft { Missiles = l.Missiles } });
                    Dictionary<string, int> c;
                    if (o.Name != null && cats.TryGetValue(o.Name, out c)) l.Cats = c;
                    row.Loadouts.Add(l);
                }
            }

            private readonly Dictionary<string, double> _deckAmmoMiss = new Dictionary<string, double>();

            /// <summary>The missile row of a loadout weapon (file name, count) from the ammunition file; null = the file is unknown (asked again after 10 s) or not a missile.</summary>
            private StrikeMissile MissileOf(int deckUid, string ammo, int count)
            {
                double at;
                double now = Environment.TickCount / 1000.0;
                if (_deckAmmoMiss.TryGetValue(ammo, out at) && now - at < 10 && now >= at) return null;
                object ap = ApOf(ammo);
                if (ap == null)
                {
                    try
                    {
                        if (_a.Present(StrikeSpecs.AmmoGetShared)) ap = _a.CallStatic(StrikeSpecs.AmmoGetShared, ammo);
                        if (ap != null) _apByAmmo[ammo] = ap;
                    }
                    catch (Exception) { ap = null; }
                }
                if (ap == null) { _deckAmmoMiss[ammo] = now; return null; }
                var m = ReadMissileFromAp(ap, ammo, count);
                return m;
            }
        }
    }
}
