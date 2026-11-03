using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-111: result of the player's "Identify yourself" request, per track. Pure logic: no Unity, no game types.
    // No leak: the value is derived only from alerts the game itself showed to the player (never from AI compliance fields).

    internal enum IdentifyResult { None = 0, Answered, Refused, Silent, NoLink }

    internal struct IdentifyEntry { public IdentifyResult Result; public double AtS; }

    internal sealed class IdentifyStore
    {
        private readonly Dictionary<int, IdentifyEntry> _map = new Dictionary<int, IdentifyEntry>();
        public int Version { get; private set; }

        public IReadOnlyDictionary<int, IdentifyEntry> All { get { return _map; } }

        /// <summary>None is ignored. The last result wins (NoLink is overwritten by the next real answer).</summary>
        public void Record(int trackId, IdentifyResult r, double missionTimeS)
        {
            if (r == IdentifyResult.None) return;
            IdentifyEntry old;
            bool changed = !_map.TryGetValue(trackId, out old) || old.Result != r;
            _map[trackId] = new IdentifyEntry { Result = r, AtS = missionTimeS };
            if (changed) Version++;
        }

        public IdentifyResult Get(int trackId)
        {
            IdentifyEntry e;
            return _map.TryGetValue(trackId, out e) ? e.Result : IdentifyResult.None;
        }

        public void Clear()
        {
            if (_map.Count == 0) return;
            _map.Clear();
            Version++;
        }
    }

    /// <summary>Reference texts (already run through the game's FormatCommText for the unit).</summary>
    internal sealed class IdentifyTemplates { public string NoLos, Ignore, Refuse, Request; }

    internal static class IdentifyClassify
    {
        private static string Norm(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = Ui.MessageColorMath.StripColor(s);   // T-329: a colour tag around a text must not break the template comparison
            return string.Join(" ", s.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        }

        private static bool Is(string alert, string template)
        {
            string t = Norm(template);
            return t.Length > 0 && Norm(alert) == t;
        }

        /// <summary>Better empty than wrong: unrecognised multi-alert outcomes give None.</summary>
        public static IdentifyResult Classify(bool resultNotNull, IList<string> alerts, IdentifyTemplates t)
        {
            if (resultNotNull) return IdentifyResult.Answered;
            if (alerts == null || alerts.Count == 0) return IdentifyResult.None;
            t = t ?? new IdentifyTemplates();
            bool noLos = false, ignore = false, refuse = false, request = false;
            foreach (string a in alerts)
            {
                if (Is(a, t.NoLos)) noLos = true;
                else if (Is(a, t.Refuse)) refuse = true;
                else if (Is(a, t.Ignore)) ignore = true;
                else if (Is(a, t.Request)) request = true;
            }
            if (refuse) return IdentifyResult.Refused;
            if (ignore) return IdentifyResult.Silent;
            if (noLos && !request) return IdentifyResult.NoLink;
            // Fallback when localisation mods changed the template: NoLOS is the first and only alert.
            if (alerts.Count == 1 && !request) return IdentifyResult.NoLink;
            return IdentifyResult.None;
        }
    }

    internal static class IdentifyText
    {
        public static string Key(IdentifyResult r)
        {
            switch (r)
            {
                case IdentifyResult.Answered: return "overview.identify.answered";
                case IdentifyResult.Refused: return "overview.identify.refused";
                case IdentifyResult.Silent: return "overview.identify.silent";
                case IdentifyResult.NoLink: return "overview.identify.nolink";
                default: return "overview.identify.none";
            }
        }

        /// <summary>Sort rank: None 0 &lt; NoLink 1 &lt; Silent 2 &lt; Refused 3 &lt; Answered 4.</summary>
        public static int Rank(IdentifyResult r)
        {
            switch (r)
            {
                case IdentifyResult.NoLink: return 1;
                case IdentifyResult.Silent: return 2;
                case IdentifyResult.Refused: return 3;
                case IdentifyResult.Answered: return 4;
                default: return 0;
            }
        }

        /// <summary>Cell text; None gives "". shortForm currently equals the full text (column is narrow, labels are short words).</summary>
        public static string Label(IdentifyResult r, bool shortForm)
        {
            return r == IdentifyResult.None ? "" : UiText.Get(Key(r));
        }
    }
}
