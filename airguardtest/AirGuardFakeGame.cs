using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>Fake game of the air_guard stand: tracks, own birds, ships with rings, the clock.</summary>
    internal sealed class AirGuardFakeGame : IAirGuardGame
    {
        public double Now { get; set; }
        public List<AirTrack> Tracks = new List<AirTrack>();
        public List<AirOwn> Owns = new List<AirOwn>();
        public List<AirSam> Sams = new List<AirSam>();
        public HashSet<int> Destroyed = new HashSet<int>();
        public Dictionary<int, GeoPt> Pos = new Dictionary<int, GeoPt>();
        public Dictionary<int, int> AboardBy = new Dictionary<int, int>();
        public List<string> Events = new List<string>();
        public List<string> Lines = new List<string>();

        public IList<AirTrack> AirTracks() { return Tracks; }
        public IList<AirOwn> OwnAirborne() { return Owns; }
        public IList<AirSam> OwnSams() { return Sams; }
        public bool TrackDestroyed(int id) { return Destroyed.Contains(id); }
        public GeoPt? UnitPos(int uid) { GeoPt p; return Pos.TryGetValue(uid, out p) ? (GeoPt?)p : null; }
        public int Aboard(int uid) { int a; return AboardBy.TryGetValue(uid, out a) ? a : 0; }
        public void Event(string name, string json) { Events.Add(name + " " + json); }
        public void Log(string line) { Lines.Add(line); }

        public int CountEvents(string name) { int n = 0; foreach (var e in Events) if (e.StartsWith(name + " ", StringComparison.Ordinal)) n++; return n; }
    }

    internal sealed class FakeX1 : IAirTimeX1
    {
        public List<string> Offers = new List<string>();
        public void Offer(string kind, string key, string reason) { string k = kind + "|" + key; if (!Offers.Contains(k)) Offers.Add(k); }   // like the real service: a key once
        public int Count(string kind) { int n = 0; foreach (var o in Offers) if (o.StartsWith(kind + "|", StringComparison.Ordinal)) n++; return n; }
    }
}
