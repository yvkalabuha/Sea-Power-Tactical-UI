using System;
using System.Collections.Generic;

namespace VoiceBotBridge.Ui
{
    /// <summary>One thing that lets go of a unit on "Return control" (ASW, SAR, CAP, evasion...).</summary>
    internal interface IReleaseParticipant
    {
        string Id { get; }
        void Release(int unitUid);
    }

    /// <summary>Participants of "Return control" in a fixed order; one failing participant does not stop the others (ADR-0044).</summary>
    internal sealed class ReleaseRegistry
    {
        public const int OrderAsw = 10, OrderSar = 20, OrderCap = 30, OrderStrike = 32, OrderIntercept = 35, OrderEvasion = 40, OrderAirEvasion = 45;   // the order of the old hook chain

        private sealed class Entry { public int Order, Seq; public IReleaseParticipant P; }
        private readonly List<Entry> _items = new List<Entry>();

        /// <summary>Stable by order, then by Add sequence.</summary>
        public void Add(IReleaseParticipant p, int order)
        {
            if (p == null) throw new ArgumentNullException("p");
            var e = new Entry { Order = order, Seq = _items.Count, P = p };
            int i = _items.Count;
            while (i > 0 && _items[i - 1].Order > order) i--;
            _items.Insert(i, e);
        }

        public IList<string> Ids
        {
            get
            {
                var l = new List<string>();
                foreach (var e in _items) l.Add(e.P.Id);
                return l;
            }
        }

        public void ReleaseAll(int unitUid, Action<string> logWarn)
        {
            foreach (var e in _items.ToArray())
            {
                try { e.P.Release(unitUid); }
                catch (Exception ex)
                {
                    if (logWarn != null) logWarn("release " + e.P.Id + " failed: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        private sealed class Fake : IReleaseParticipant
        {
            private readonly List<string> _calls; private readonly bool _throw;
            public Fake(string id, List<string> calls, bool thr) { Id = id; _calls = calls; _throw = thr; }
            public string Id { get; private set; }
            public void Release(int unitUid) { _calls.Add(Id + ":" + unitUid); if (_throw) throw new InvalidOperationException("boom"); }
        }

        public static string SelfTest()
        {
            try
            {
                if (!(OrderAsw < OrderSar && OrderSar < OrderCap && OrderCap < OrderStrike && OrderStrike < OrderIntercept && OrderIntercept < OrderEvasion && OrderEvasion < OrderAirEvasion)) return "release: constants out of order";
                var calls = new List<string>();
                var r = new ReleaseRegistry();
                r.Add(new Fake("evasion", calls, false), OrderEvasion);
                r.Add(new Fake("cap", calls, false), OrderCap);
                r.Add(new Fake("asw", calls, true), OrderAsw);
                r.Add(new Fake("asw2", calls, false), OrderAsw);   // same order: after asw
                r.Add(new Fake("sar", calls, false), OrderSar);
                if (string.Join(",", new List<string>(r.Ids).ToArray()) != "asw,asw2,sar,cap,evasion") return "release: Ids = " + string.Join(",", new List<string>(r.Ids).ToArray());
                var warns = new List<string>();
                r.ReleaseAll(7, warns.Add);
                if (string.Join(",", calls.ToArray()) != "asw:7,asw2:7,sar:7,cap:7,evasion:7") return "release: calls = " + string.Join(",", calls.ToArray());
                if (warns.Count != 1 || !warns[0].Contains("asw") || !warns[0].Contains("boom")) return "release: warn line = " + string.Join("|", warns.ToArray());
                r.ReleaseAll(8, null);   // a null logger must not crash
                if (calls.Count != 10) return "release: second run calls = " + calls.Count;
                return null;
            }
            catch (Exception e) { return "release: " + e.GetType().Name + ": " + e.Message; }
        }
    }
}
