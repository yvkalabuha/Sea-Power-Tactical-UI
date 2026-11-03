using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace VoiceBotBridge
{
    internal sealed partial class GameApi
    {
        private readonly List<string> _outEvents = new List<string>();
        private readonly LinkedList<KeyValuePair<long, string>> _ring = new LinkedList<KeyValuePair<long, string>>();
        private long _evSeq;
        private const int RingMax = 300;
        /// <summary>Cap of the not yet drained lines (a mod without a bot connection must not grow it forever).</summary>
        public const int MaxQueued = 256;

        private sealed class RefEq : IEqualityComparer<object>
        {
            public new bool Equals(object a, object b) { return ReferenceEquals(a, b); }
            public int GetHashCode(object o) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o); }
        }

        private string EvLine(string name, string dataJson)
        {
            long seq = ++_evSeq;
            var met = MissionTimeS();
            var line = "{\"type\":\"event\",\"seq\":" + seq + ",\"name\":" + Json.Str(name) +
                       ",\"t_unix\":" + Json.Num(Math.Round((DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds, 2)) +
                       (met.HasValue ? ",\"mission_time_s\":" + Json.Num(Math.Round(met.Value, 1)) : "") +
                       ",\"data\":" + dataJson + "}";
            _ring.AddLast(new KeyValuePair<long, string>(seq, line));
            while (_ring.Count > RingMax) _ring.RemoveFirst();
            _outEvents.Add(line);
            while (_outEvents.Count > MaxQueued) _outEvents.RemoveAt(0);
            return line;
        }

        /// <summary>Lines created since the last call (to be pushed to clients).</summary>
        public List<string> DrainEvents()
        {
            if (_outEvents.Count == 0) return null;
            var l = new List<string>(_outEvents);
            _outEvents.Clear();
            return l;
        }

        private readonly HashSet<object> _subVehicles = new HashSet<object>(new RefEq());
        private readonly HashSet<object> _subObjects = new HashSet<object>(new RefEq());

        public bool EventsAvailable { get { return Capabilities.Contains("events"); } }

        private void SelfCheckEvents()
        {
            bool e = true;
            e &= Has("events", "SeaPower.Globals", "_mainGameViewModel", true, true);
            e &= Has("events", "SeapowerUI.ViewModels.MainGameViewModel", "EventLog", false, true);
            e &= Has("events", "SeapowerUI.ViewModels.EventLogViewModel", "Alerts", false, true);
            e &= Has("events", "SeapowerUI.ViewModels.Alert", "_alertText", false, true);
            Has("events", "SeapowerUI.ViewModels.Alert", "RaisingObject", false, false);
            Has("events", "SeapowerUI.ViewModels.Alert", "AboutObject", false, false);
            Has("events", "SeaPower.PlottingTable", "NewVehicle", false, false);
            Has("events", "SeaPower.Vehicle", "Deleted", false, false);
            Has("events", "SeaPower.Vehicle", "HasLiveTrack", false, false);
            Has("events", "SeaPower.ObjectBase", "OnDestruction", false, false);
            Has("events", "SeaPower.LanguageResourceHandler", "LanguageDictionary", false, false);
            if (e) Capabilities.Add("events");
        }


        public Action<string> Log = delegate { };

        internal sealed class ContactSnap { public int TrackId; public string Side, Kind, Class; public object Identified; }
        private readonly Dictionary<object, ContactSnap> _contactSnap = new Dictionary<object, ContactSnap>(new RefEq());
        private readonly HashSet<object> _reportedDestroyed = new HashSet<object>(new RefEq());
        // v0.8.1: own units = the player taskforce lists only; own weapons in flight (RIM-66G, chaff...) are tracked
        // vehicles too and must never count as a loss
        private readonly HashSet<object> _ownUnits = new HashSet<object>(new RefEq());

        private void Forget(object o) { if (o == null) return; _subObjects.Remove(o); _contactSnap.Remove(o); _ownUnits.Remove(o); }

        private static IEnumerable<object> Cast(IEnumerable e) { foreach (var x in e) yield return x; }

        internal static bool KnownSide(string side) { return !string.IsNullOrEmpty(side) && side != "unknown"; }

        internal static ContactSnap MergeSnap(ContactSnap fresh, ContactSnap known)
        {
            if (fresh == null) return known;
            if (known == null) return fresh;
            fresh = new ContactSnap { TrackId = fresh.TrackId, Side = fresh.Side, Kind = fresh.Kind, Class = fresh.Class, Identified = fresh.Identified };
            if (!KnownSide(fresh.Side) && KnownSide(known.Side)) fresh.Side = known.Side;
            if (string.IsNullOrEmpty(fresh.Class)) fresh.Class = known.Class;
            if (string.IsNullOrEmpty(fresh.Kind) || fresh.Kind == "Invisible") { if (!string.IsNullOrEmpty(known.Kind)) fresh.Kind = known.Kind; }
            if (!(fresh.Identified is bool && (bool)fresh.Identified) && known.Identified is bool) fresh.Identified = known.Identified;
            return fresh;
        }

        internal static bool IsWeaponKind(string kind)
        {
            return string.IsNullOrEmpty(kind) || kind == "Invisible" || kind == "Missile" || kind == "Torpedo" ||
                kind == "PassiveSonobouy" || kind == "ActiveSonobouy";
        }
    }
}
