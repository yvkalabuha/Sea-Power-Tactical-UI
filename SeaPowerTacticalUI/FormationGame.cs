using System;

namespace VoiceBotBridge.Ui
{
    /// <summary>One unit's place in the game's formation (T-242, T-243). Pure data, filled by GameApi.ReadFormation.</summary>
    internal sealed class FormationFacts
    {
        public bool InFormation, IsLeader, Independent, GeoOk;
        public string Name;              // UnitFormation.Name; "" when the formation has no name of its own (empty or the game's "Unnamed formation")
        public int LeaderUid;            // 0 = unknown
        public string LeaderName;
        public double UnitLat, UnitLon, SlotLat, SlotLon, LeaderLat, LeaderLon;
        public bool Globe;               // T-354: world of the game (false = stable flat); set by ReadFormationGeo from AswMath.WorldGlobe; Reset -> false

        public void Reset()
        {
            InFormation = IsLeader = Independent = GeoOk = false;
            Name = null; LeaderUid = 0; LeaderName = null;
            UnitLat = UnitLon = SlotLat = SlotLon = LeaderLat = LeaderLon = 0;
            Globe = false;
        }
    }
}

namespace VoiceBotBridge
{
    internal sealed partial class GameApi
    {
        /// <summary>
        /// Reads the formation of an own unit (no side effects, main thread). false = not in a formation or the member is missing (into is reset).
        /// Fills InFormation, IsLeader, Name, LeaderUid, LeaderName and Independent; the geometry (unit, slot and leader point, T-243) when the members are there; GeoOk=false otherwise.
        /// Never throws.
        /// </summary>
        internal bool ReadFormation(object unit, Ui.FormationFacts into)
        {
            if (into == null) return false;
            into.Reset();
            try
            {
                if (unit == null || !Present(FormationSpecs.ObjFormation)) return false;
                var f = GetOwn(unit, FormationSpecs.ObjFormation);
                if (f == null) return false;
                into.InFormation = true;
                if (Present(FormationSpecs.ObjIsLeader)) into.IsLeader = BoolOf(GetOwn(unit, FormationSpecs.ObjIsLeader));
                object leader = null;
                string name = Present(FormationSpecs.FormationName) ? Get(f, FormationSpecs.FormationName) as string : null;
                into.Name = FormationNameOrEmpty(name);
                if (Present(FormationSpecs.FormationLeaderStation) && Present(FormationSpecs.StationUnit))
                {
                    var lu = Get(Get(f, FormationSpecs.FormationLeaderStation), FormationSpecs.StationUnit);
                    if (lu != null && IsAlive(lu))
                    {
                        leader = lu;
                        into.LeaderName = RxGet(lu, OverviewSpecs.ObjName) as string;
                        int lid = UidOf(lu);
                        into.LeaderUid = lid > 0 ? lid : 0;
                    }
                }
                if (Present(FormationSpecs.FormationStationFor) && Present(FormationSpecs.StationIndependent))
                {
                    var st = Call(f, FormationSpecs.FormationStationFor, unit);
                    if (st != null)
                    {
                        into.Independent = BoolOf(RxGet(st, FormationSpecs.StationIndependent));
                        ReadFormationGeo(unit, leader, st, into);
                    }
                }
                return true;
            }
            catch (Exception)
            {
                into.Reset();
                return false;
            }
        }

        /// <summary>T-243: unit, slot and leader points. GeoOk only when all three are read; any failure leaves GeoOk=false (the ring then offers the command).</summary>
        private void ReadFormationGeo(object unit, object leader, object station, Ui.FormationFacts into)
        {
            into.Globe = AswMath.WorldGlobe;   // T-354
            try
            {
                if (leader == null || !Present(FormationSpecs.StationGeo) || !Present(FormationSpecs.ObjPosition)) return;
                double la, lo;
                if (!GeoPair(Get(station, FormationSpecs.StationGeo), out la, out lo)) return;
                double ula, ulo, lla, llo;
                if (!GeoPair(RxGet(unit, FormationSpecs.ObjPosition), out ula, out ulo)) return;
                if (!GeoPair(RxGet(leader, FormationSpecs.ObjPosition), out lla, out llo)) return;
                into.SlotLat = la; into.SlotLon = lo; into.UnitLat = ula; into.UnitLon = ulo; into.LeaderLat = lla; into.LeaderLon = llo;
                into.GeoOk = true;
            }
            catch (Exception) { into.GeoOk = false; }
        }

        private bool GeoPair(object geo, out double lat, out double lon)
        {
            lat = lon = 0;
            if (geo == null) return false;
            var a = D(Get(geo, CoreSpecs.GeoLat)); var b = D(Get(geo, CoreSpecs.GeoLon));
            if (!a.HasValue || !b.HasValue || double.IsNaN(a.Value) || double.IsNaN(b.Value)) return false;
            lat = a.Value; lon = b.Value;
            return true;
        }

        /// <summary>"" for an empty name or the game's own "Unnamed formation" text (ContextMenu.UnnamedFormation on the current language).</summary>
        private static string FormationNameOrEmpty(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            name = name.Trim();
            var look = UiText.GameLookup;
            if (look != null)
            {
                try
                {
                    var un = look("ContextMenu", "UnnamedFormation");
                    if (!string.IsNullOrEmpty(un) && !UiText.IsMissing(un, "UnnamedFormation") && string.Equals(un.Trim(), name, StringComparison.OrdinalIgnoreCase)) return "";
                }
                catch (Exception) { }
            }
            return name;
        }
    }
}
