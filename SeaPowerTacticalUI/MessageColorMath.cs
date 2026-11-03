using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace VoiceBotBridge.Ui
{
    /// <summary>
    /// T-329: pure logic of the red hostile messages in the game's message window (no Unity, no reflection, no game).
    /// The window shows Alert.AlertContent; the only place where a colour can be set is a Unity rich text tag in the string that
    /// MainGameViewModel.Raise*Alert converts (UnityRichTextToXaml understands &lt;color=name&gt;). Wrap puts the tag around a text;
    /// IsHostileText recognises the texts of the game that tell about a HOSTILE classification (templates come from the game's own ini via the lookup).
    /// </summary>
    internal static class MessageColorMath
    {
        public const string Color = "red";

        /// <summary>Text inside the colour tag. Null / empty / an already coloured text stays as it is.</summary>
        public static string Wrap(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            if (text.TrimStart().StartsWith("<color", StringComparison.OrdinalIgnoreCase)) return text;
            return "<color=" + Color + ">" + text + "</color>";
        }

        private static readonly Regex ColorTag = new Regex(@"</?color(=[^>]*)?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>The text without Unity colour tags (what a template comparison should see).</summary>
        public static string StripColor(string s) { return string.IsNullOrEmpty(s) ? s : ColorTag.Replace(s, ""); }

        private static readonly string[] HostileKeys = { "ClassifiedHostileAir", "ClassifiedHostileAirMultiple", "ClassifiedHostileMultiple" };
        private static readonly string[] Genders = { "", "F", "M", "N" };   // OrderLog Hostile + the gender mark the game takes from the unit type ("Aircraft|F")
        private static readonly Regex Placeholder = new Regex(@"\$\{(\w+)\}", RegexOptions.CultureInvariant);

        /// <summary>Regular expression of a game template: ${Relation} = the given word, any other ${..} = anything.</summary>
        public static Regex FromTemplate(string template, string relationWord)
        {
            if (string.IsNullOrWhiteSpace(template)) return null;
            var sb = new System.Text.StringBuilder("^\\s*");
            int pos = 0;
            foreach (Match m in Placeholder.Matches(template))
            {
                sb.Append(Regex.Escape(template.Substring(pos, m.Index - pos)));
                if (m.Groups[1].Value == "Relation")
                {
                    if (string.IsNullOrEmpty(relationWord)) return null;
                    sb.Append(Regex.Escape(relationWord));
                }
                else sb.Append(".+?");
                pos = m.Index + m.Length;
            }
            sb.Append(Regex.Escape(template.Substring(pos))).Append("\\s*$");
            return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
        }

        /// <summary>
        /// True when the text is one of the game's "classified as hostile" messages: Bandit!, Bandits!, multiple hostile contacts and
        /// "Track N classified as hostile X". lookup(section, key) = the game's getText (null = no text table: nothing is hostile).
        /// </summary>
        public static bool IsHostileText(string text, Func<string, string, string> lookup)
        {
            if (string.IsNullOrWhiteSpace(text) || lookup == null) return false;
            foreach (var key in HostileKeys)
            {
                var rx = FromTemplate(lookup("Contact", key), "");
                if (rx != null && rx.IsMatch(text)) return true;
            }
            string track = lookup("Contact", "TrackClassified");
            if (string.IsNullOrWhiteSpace(track)) return false;
            var seen = new HashSet<string>();
            foreach (var g in Genders)
            {
                string word = lookup("OrderLog", "Hostile" + g);
                if (string.IsNullOrWhiteSpace(word) || !seen.Add(word.ToLowerInvariant())) continue;
                var rx = FromTemplate(track, word.ToLowerInvariant());
                if (rx != null && rx.IsMatch(text)) return true;
            }
            return false;
        }

        // ---- self test (puretest section "msgcolor") -------------------------------------------------------------------

        private static Func<string, string, string> Table(bool ru)
        {
            var d = ru
                ? new Dictionary<string, string>
                {
                    { "Contact.ClassifiedHostileMultiple", "Множественные контакты опознаны как вражеские!" }, // i18n-allow: self-test copy of the game ru templates (test data)
                    { "Contact.ClassifiedHostileAir", "Вражеский самолёт!" }, // i18n-allow: self-test copy of the game ru templates (test data)
                    { "Contact.ClassifiedHostileAirMultiple", "Вражеские самолёты!" }, // i18n-allow: self-test copy of the game ru templates (test data)
                    { "Contact.ClassifiedContactMultiple", "Множественные контакты классифицированы!" }, // i18n-allow: self-test copy of the game ru templates (test data)
                    { "Contact.TrackClassified", "Цель ${TrackId} опознана как ${Relation} ${Type}" }, // i18n-allow: self-test copy of the game ru templates (test data)
                    { "OrderLog.Hostile", "Вражеский" }, { "OrderLog.HostileF", "Вражеская" }, { "OrderLog.HostileN", "Вражеское" }, // i18n-allow: self-test copy of the game ru templates (test data)
                }
                : new Dictionary<string, string>
                {
                    { "Contact.ClassifiedHostileMultiple", "Multiple ${ContactType} hostile contacts!" },
                    { "Contact.ClassifiedHostileAir", "Bandit!" },
                    { "Contact.ClassifiedHostileAirMultiple", "Bandits! Bandits!" },
                    { "Contact.ClassifiedContactMultiple", "Multiple ${ContactType} contacts classified!" },
                    { "Contact.TrackClassified", "Track ${TrackId} classified as ${Relation} ${Type}" },
                    { "OrderLog.Hostile", "Hostile" }, { "OrderLog.HostileF", "Hostile" }, { "OrderLog.HostileN", "Hostile" },
                };
            return (s, k) => { string v; return d.TryGetValue(s + "." + k, out v) ? v : null; };
        }

        public static string SelfTest()
        {
            if (Wrap("Bandit!") != "<color=red>Bandit!</color>") return "wrap";
            if (Wrap("<color=blue>x</color>") != "<color=blue>x</color>") return "wrap keeps an existing colour";
            if (Wrap("") != "" || Wrap(null) != null) return "wrap empty";
            if (StripColor("<color=red>Track 5: refuses</color>") != "Track 5: refuses") return "strip";
            if (StripColor("a <COLOR=#ff0000>b</COLOR> c") != "a b c") return "strip case";
            var en = Table(false);
            foreach (var t in new[] { "Bandit!", "Bandits! Bandits!", "Multiple air hostile contacts!", "Track 12 classified as hostile surface ship", "bandit!" })
                if (!IsHostileText(t, en)) return "en hostile: " + t;
            foreach (var t in new[] { "Track 12 classified as neutral surface ship", "Track 12 classified as friendly surface ship", "Multiple air contacts classified!",
                "New radar contact! Designate track 4", "Enemy comms detected!", "Missile launch detected", "", null })
                if (IsHostileText(t, en)) return "en not hostile: " + t;
            var ru = Table(true);
            foreach (var t in new[] { "Вражеский самолёт!", "Вражеские самолёты!", "Множественные контакты опознаны как вражеские!", "Цель 7 опознана как вражеская подлодка", "Цель 7 опознана как вражеский корабль" }) // i18n-allow: self-test data, ru game texts
                if (!IsHostileText(t, ru)) return "ru hostile: " + t;
            foreach (var t in new[] { "Цель 7 опознана как нейтральный корабль", "Множественные контакты классифицированы!", "Новый радарный контакт! Присвоен номер 3" }) // i18n-allow: self-test data, ru game texts
                if (IsHostileText(t, ru)) return "ru not hostile: " + t;
            if (IsHostileText("Bandit!", null)) return "no lookup";
            if (IsHostileText("Bandit!", (s, k) => null)) return "empty table";
            // regex metacharacters in a template are literal; the game's text with ${TrackId} replaced already still matches
            var rx = FromTemplate("Alert (${Relation})? ${X}.", "bad+");
            if (rx == null || !rx.IsMatch("Alert (bad+)? anything.") || rx.IsMatch("Alert (badd)? anything.")) return "template escape";
            if (FromTemplate("x ${Relation}", "") != null || FromTemplate("  ", "a") != null) return "template null";
            return null;
        }
    }
}
