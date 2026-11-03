// T-136 E3: contact information in the ASW window (ADR-0045): a line per contact of the group ("~340 ft ±60 · 80 below layer · seen 0:45 ago") and the
// block of contacts. Pure C# (T-174 W7: the "Own noise" block is gone, own speed / noise are shown at the own icons, ADR-0045 supplement 1); the data come from IContactInfo (the contact_info module).
// Without the module every value is "—". Sockets of AswWindowBody.cs.
using System;
using System.Collections.Generic;
using System.Globalization;
using VoiceBotBridge.Ui;

namespace VoiceBotBridge
{
    internal sealed partial class AswWindowBody
    {
        /// <summary>The contact information service of the contact_info module (null result = the module is off). Set by the ASW module.</summary>
        public Func<IContactInfo> ContactInfo;

        /// <summary>Units of the lines (nm, kn, ft by default).</summary>
        public CiUnits Units;

        private const double AgeShowS = 15;

        /// <summary>The contact line text; "—" when nothing is known.</summary>
        internal static string ContactCiText(ContactInfoRow r, CiUnits u)
        {
            var parts = new List<string>();
            Action<string> add = s => { if (!string.IsNullOrEmpty(s)) parts.Add(s); };
            if (r != null)
            {
                add(ContactInfoMath.FmtDepth(r, r.Own, u));
                add(ContactInfoMath.FmtLayer(r, u));
                add(ContactInfoMath.FmtSpeed(r, u));
                add(ContactInfoMath.FmtAge(r, AgeShowS));
            }
            return parts.Count == 0 ? UiText.Get("asw.ci.none") : string.Join(" · ", parts);
        }

        partial void ContactsSection(UiKit k, AswGroupView sel)
        {
            if (sel == null || sel.Contacts.Count == 0) return;
            if (!k.Section("contacts", Tx("asw.sec.contacts"), sel.Contacts.Count.ToString(CultureInfo.InvariantCulture), true)) return;
            var ci = ContactInfo == null ? null : ContactInfo();
            foreach (var c in sel.Contacts)
            {
                var row = ci == null ? null : ci.Get(c.Id);
                bool stale = row != null && row.Stale;
                k.Text(Tx("asw.ci.contact", c.Name, ContactCiText(row, Units)), c.Held ? UiTone.Warn : stale ? UiTone.Dim : UiTone.Normal, true, 2);
            }
        }
    }
}
