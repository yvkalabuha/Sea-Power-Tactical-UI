using System.Collections.Generic;

namespace VoiceBotBridge
{
    // Types shared by the CAP controller and the air threat service. A separate file: the CAP controller (captest, capwintest) is built without AirGuardMath.cs.

    /// <summary>The area of a group, given by its owner (ASW / SAR / strike / CAP).</summary>
    internal sealed class AirGuardGroup
    {
        public string Window, Name, Kind;    // Window: asw | cap; Kind: course | area | sar | strike | cap
        public List<int> Uids = new List<int>();          // members in the air and on the deck
        public List<GeoPt> AreaPts = new List<GeoPt>();   // zone / route / reference ship
        public List<int> DeckUids = new List<int>();      // launch decks
        public bool Helicopter;              // has helicopters (helicopter thresholds for the area)
    }

    /// <summary>T-179 (ADR-0079 section 7): the CAP "Alert" group reports its reaction to a raid; the air threat service puts it as a line into the raid card.</summary>
    internal interface IAirAlertNotes
    {
        /// <summary>action: scramble | not_ready | preparing (T-204: count = machines preparing, typeLabel = the time text); trackIds: tracks of the raid (the id of a CapContact is the id of the service track).</summary>
        void AlertLine(string group, IList<int> trackIds, string action, int count, string typeLabel);
        /// <summary>T-197: the sticky raid key of the air threat service for a track ("r7039") or null.</summary>
        string RaidKeyOf(int trackId);
        /// <summary>T-201: false when the service gives no raid keys at all (Mode=off): waiting for a key makes no sense.</summary>
        bool RaidKeysOn { get; }
    }
}
