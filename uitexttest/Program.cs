using System;
using System.IO;

namespace VoiceBotBridge
{
    internal sealed class FakeHandler
    {
        public event EventHandler LanguageChanged;
        public void Raise() { var h = LanguageChanged; if (h != null) h(this, EventArgs.Empty); }
    }

    internal static class Program
    {
        private static int Main(string[] args)
        {
            // args[0] = path to SeaPowerTacticalUI/lang (real tables); optional.
            string err = UiText.SelfTest();
            Console.WriteLine("ui text selftest " + (err == null ? "ok" : "FAILED: " + err));
            int bad = err == null ? 0 : 1;
            string ext = ExtTests();
            Console.WriteLine("ui text ext " + (ext == null ? "ok" : "FAILED: " + ext));
            if (ext != null) bad++;
            if (args.Length > 0)
            {
                UiText.Reset();
                int n = UiText.LoadDir(args[0]);
                UiText.SetLanguage("ru");
                bool ok = n >= 2 && UiText.Get("MapLayers", "VOICEBOT_ASW") != "MapLayers.VOICEBOT_ASW" && UiText.Get("asw.title") != "SeaPowerTacticalUI.asw.title";
                UiText.SetLanguage("de");
                ok = ok && UiText.Get("MapLayers", "VOICEBOT_ASW") != "MapLayers.VOICEBOT_ASW";
                ok = ok && UiText.DuplicateKeys.GetEnumerator().MoveNext() == false;
                Console.WriteLine("ui text real tables files=" + n + " " + (ok ? "ok" : "FAILED"));
                if (!ok) bad++;
            }
            return bad == 0 ? 0 : 1;
        }

        // S0.1: Revision/Poll, GetOwn, Has, Push, SelfTest keeps state, new language by file, LoadForTests.
        private static string ExtTests()
        {
            UiText.Reset();
            UiText.LoadIni("en", "[SeaPowerTacticalUI]\nhello=Hello\n");
            UiText.LoadIni("ru", "[SeaPowerTacticalUI]\nhello=Privet\n");
            UiText.SetLanguage("auto");
            string game = "en";
            int lookups = 0;
            UiText.GameLang = () => game;
            if (UiText.Lang != "en") return "a: initial lang";
            int rev = UiText.Revision;
            game = "ru";
            if (UiText.Lang != "en") return "a: lang must not change before Poll";
            if (UiText.Revision != rev) return "a: revision changed without Poll";
            if (!UiText.Poll()) return "a: Poll must report change";
            if (UiText.Lang != "ru" || UiText.Revision != rev + 1) return "a: lang/revision after Poll";
            if (UiText.Poll() || UiText.Revision != rev + 1) return "a: second Poll is a no-op";
            if (UiText.Get("hello") != "Privet") return "a: get after Poll";

            // answers are cached until Revision changes
            UiText.GameLookup = (s, k) => { lookups++; return null; };
            UiText.Get("hello"); UiText.Get("hello");
            if (lookups != 1) return "cache: lookups=" + lookups;
            game = "en"; UiText.Poll();
            if (UiText.Get("hello") != "Hello" || lookups != 2) return "cache reset on Revision";

            // b: GetOwn never calls GameLookup
            lookups = 0;
            UiText.GameLookup = (s, k) => { lookups++; return "Game"; };
            if (UiText.GetOwn("SeaPowerTacticalUI", "hello") != "Hello" || lookups != 0) return "b: GetOwn";
            if (UiText.GetOwn("SeaPowerTacticalUI", "nope") != null) return "b: GetOwn missing -> null";
            if (!UiText.Has("SeaPowerTacticalUI", "nope")) return "b: Has via game";
            UiText.GameLookup = null;
            if (UiText.Has("SeaPowerTacticalUI", "nope") || !UiText.Has("SeaPowerTacticalUI", "hello")) return "b: Has";

            // c: Push
            UiText.SetLanguage("ru");
            int r0 = UiText.Revision;
            using (UiText.Push("en"))
            {
                if (UiText.Lang != "en" || UiText.Get("hello") != "Hello") return "c: inside Push";
            }
            if (UiText.Lang != "ru" || UiText.Get("hello") != "Privet") return "c: restored";
            if (UiText.Revision <= r0) return "c: revision grows";
            UiText.SetLanguage("auto"); UiText.GameLang = () => "ru"; UiText.Poll();
            using (UiText.Push("en")) { if (UiText.Lang != "en") return "c: push over auto"; }
            if (UiText.Lang != "ru") return "c: restored auto";

            // d: unknown language -> en; new language by file only
            UiText.GameLang = () => "de"; UiText.Poll();
            if (UiText.Lang != "de" || UiText.Get("hello") != "Hello") return "d: de -> en";
            string tmp = Path.Combine(Path.GetTempPath(), "uitext_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(tmp, "language_de"));
                File.WriteAllText(Path.Combine(tmp, "language_de", "x.ini"), "[SeaPowerTacticalUI]\nhello=Hallo\n");
                if (UiText.LoadDir(tmp) != 1) return "d: LoadDir count";
                if (UiText.Get("hello") != "Hallo") return "d: de table";
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }

            // duplicate keys in two files of one language
            UiText.Reset();
            UiText.LoadIni("en", "[S]\na=1\n");
            UiText.LoadIni("en", "[S]\na=2\nb=3\n");
            int dups = 0; foreach (var k in UiText.DuplicateKeys) { dups++; if (k != "en:s.a") return "dup key name " + k; }
            if (dups != 1) return "dup count " + dups;

            // g: SelfTest keeps loaded tables
            UiText.Reset();
            UiText.LoadIni("en", "[SeaPowerTacticalUI]\nkeep=Kept\n");
            UiText.SetLanguage("en");
            UiText.GameLookup = (s, k) => null;
            var gl = UiText.GameLookup;
            string st = UiText.SelfTest();
            if (st != null) return "g: selftest " + st;
            if (UiText.Get("keep") != "Kept" || UiText.GameLookup != gl) return "g: state restored after SelfTest";

            // f: LanguageChanged subscription by reflection
            UiText.Reset();
            var fake = new FakeHandler();
            string gl2 = "en";
            UiText.GameLang = () => gl2;
            if (UiText.Lang != "en") return "f: initial";
            var tok = UiText.SubscribeLanguageChanged(fake, typeof(FakeHandler).GetEvent("LanguageChanged"));
            if (tok == null) return "f: subscribe";
            gl2 = "ru"; fake.Raise();
            if (UiText.Lang != "ru") return "f: event must Poll";
            tok.Dispose(); tok.Dispose();
            gl2 = "de"; fake.Raise();
            if (UiText.Lang != "ru") return "f: unsubscribed";
            if (UiText.SubscribeLanguageChanged(new object(), null) != null || UiText.SubscribeLanguageChanged(null, null) != null) return "f: no event -> null";

            // e: real tables
            UiText.Reset();
            string dir = UiText.FindLangDir(AppContext.BaseDirectory);
            if (dir == null) return "e: FindLangDir null";
            if (UiText.FindLangDir(Path.GetPathRoot(Path.GetTempPath())) != null) return "e: FindLangDir root must be null";
            int n = UiText.LoadForTests("ru");
            if (n < 2 || UiText.Lang != "ru" || UiText.Get("MapLayers", "VOICEBOT_ASW") == "MapLayers.VOICEBOT_ASW") return "e: LoadForTests n=" + n;
            UiText.Reset();
            return null;
        }
    }
}
