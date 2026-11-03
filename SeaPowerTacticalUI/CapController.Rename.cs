using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-326: rename of a CAP group (like the ASW group rename of T-150). The name is a string key in several places (ownership claims,
    // creation number, alert-group loans); Rename moves all of them. An empty name returns the group to an automatic "<prefix>-<n>".
    internal sealed partial class CapController
    {
        /// <summary>Prefixes of automatic names: the current language and the known ones of both languages.</summary>
        private static string[] CapPrefixes() { return new[] { UiText.Get("cap.group.prefix"), "ПВО", "CAP" }; }   // i18n-allow: auto-name prefixes of both languages (saved names), not a visible text

        /// <summary>The next free automatic name; the counter keeps growing.</summary>
        private string NextAutoName()
        {
            string name;
            do { _no++; name = AswGroupNames.Auto(UiText.Get("cap.group.prefix"), _no); } while (Find(name) != null);
            return name;
        }

        /// <summary>cap_group {op:"rename", group, name}: ok = {"ok":true,"group":new,"old":old}; errors !restore_waiting|missing_group|missing_name|unknown_group|bad_name|name_taken.</summary>
        internal string RenameCmd(Dictionary<string, object> c)
        {
            if (_deferred != null) return "!restore_waiting";
            var r = CapCommands.ParseRename(c);
            if (r.err != null) return "!" + r.err;
            var g = Find(r.group);
            if (g == null) return "!unknown_group";
            string old = g.Name;
            if (r.name.Length == 0)
            {
                if (g.AutoName) return Ack(g.Name, "\"old\":" + Q(old));
                Rename(g, NextAutoName());
                g.AutoName = true;
                return Ack(g.Name, "\"old\":" + Q(old));
            }
            string bad = AswGroupNames.Validate(r.name, x => { var o = Find(x); return o != null && o != g; });
            if (bad != null) return "!" + bad;
            if (r.name != old) Rename(g, r.name);
            g.AutoName = false;
            return Ack(g.Name, "\"old\":" + Q(old));
        }

        private void Rename(Grp g, string n)
        {
            string old = g.Name;
            _own.RenameSeq(Win, old, n);
            _own.RenameClaims(Win, old, n);
            foreach (var o in _groups)
            {
                foreach (var m in o.Members) if (m.LentFrom == old && !m.LentOrphan) m.LentFrom = n;
                foreach (var lr in o.Lent) if (lr.To == old) lr.To = n;
            }
            g.Name = n;
            Log("group rename " + old + " -> " + n);
            Evt("cap_group_renamed", "{\"old\":" + Q(old) + ",\"new\":" + Q(n) + "}");
        }
    }
}
