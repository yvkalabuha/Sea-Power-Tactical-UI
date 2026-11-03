using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    // T-341 / ADR-0132 section 6: the participants of the single engage veto slot of the game API (Common/Veto.cs ExtraEngageVeto).
    // cap_ff (CAP friendly fire) and strike (anti-ship missiles of a strike group) register here; the first true wins. Pure.
    internal sealed class EngageVetoChain
    {
        public const int OrderCapFf = 10, OrderStrike = 20;
        public const string NameCapFf = "cap_ff", NameStrike = "strike";

        private sealed class Part { public string Name; public int Order, Seq; public Func<object, bool> Veto; }
        private readonly List<Part> _parts = new List<Part>();
        private int _seq;

        public int VetoedN { get; private set; }
        public string LastBy { get; private set; }

        /// <summary>Registers or replaces (by name) a participant; participants run in the order of "order" (ties: registration order).</summary>
        public void Set(string name, int order, Func<object, bool> veto)
        {
            if (string.IsNullOrEmpty(name) || veto == null) return;
            var p = _parts.FirstOrDefault(x => x.Name == name);
            if (p == null) { p = new Part { Name = name, Seq = _seq++ }; _parts.Add(p); }
            p.Order = order; p.Veto = veto;
            _parts.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.Seq.CompareTo(b.Seq));
        }

        public void Remove(string name) { _parts.RemoveAll(x => x.Name == name); }

        /// <summary>The first participant that says true vetoes. A participant that throws counts as "no veto".</summary>
        public bool Veto(object engageTask)
        {
            foreach (var p in _parts.ToArray())
            {
                bool v;
                try { v = p.Veto(engageTask); } catch (Exception) { v = false; }
                if (!v) continue;
                VetoedN++; LastBy = p.Name;
                return true;
            }
            return false;
        }

        public string[] Names { get { return _parts.Select(x => x.Name).ToArray(); } }

        public static string SelfTest()
        {
            var ch = new EngageVetoChain();
            var calls = new List<string>();
            ch.Set("strike", OrderStrike, o => { calls.Add("strike"); return (o as string) == "asm"; });
            ch.Set("cap_ff", OrderCapFf, o => { calls.Add("cap_ff"); return (o as string) == "ff"; });
            if (string.Join(",", ch.Names) != "cap_ff,strike") return "order " + string.Join(",", ch.Names);
            if (ch.Veto("none") || string.Join(",", calls) != "cap_ff,strike") return "both asked " + string.Join(",", calls);
            calls.Clear();
            if (!ch.Veto("ff") || string.Join(",", calls) != "cap_ff" || ch.LastBy != "cap_ff" || ch.VetoedN != 1) return "first true wins " + string.Join(",", calls);
            if (!ch.Veto("asm") || ch.LastBy != "strike" || ch.VetoedN != 2) return "strike vetoes";
            ch.Set("cap_ff", OrderCapFf, o => (o as string) == "other");
            if (ch.Names.Length != 2) return "replace by name";
            if (ch.Veto("ff")) return "replaced participant is the new one";
            if (!ch.Veto("other")) return "replaced veto";
            ch.Remove("cap_ff");
            if (string.Join(",", ch.Names) != "strike" || ch.Veto("other")) return "remove";
            ch.Remove("nope");
            ch.Set("bad", 5, o => { throw new InvalidOperationException(); });
            if (string.Join(",", ch.Names) != "bad,strike") return "bad order";
            if (ch.Veto("none")) return "a participant that throws is no veto";
            if (!ch.Veto("asm")) return "a throwing participant does not stop the others";
            ch.Set("", 1, o => true); ch.Set("x", 1, null);
            if (ch.Names.Length != 2) return "empty names / null veto are ignored";
            return null;
        }
    }
}
