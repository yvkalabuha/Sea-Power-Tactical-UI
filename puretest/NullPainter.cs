// T-239: a painter that stores nothing and allocates nothing: for allocation measurements of the kit and the settings body.
// Measure = text length x 7 px; buttons and checks return the state they were given.
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal sealed class NullPainter : IUiPainter
    {
        public float Measure(string text, UiFont font) { return string.IsNullOrEmpty(text) ? 0f : text.Length * 7f; }
        public void Label(UiRect r, string text, UiFont f, UiTone t, string tip) { }
        public bool Button(UiRect r, string id, string text, string tip, bool enabled, bool selected, UiTone tone = UiTone.Normal) { return false; }
        public bool DisabledPressed(string id) { return false; }
        public bool Hit(UiRect r, string id, string tip) { return false; }
        public bool Link(UiRect r, string id, string text, UiFont f, UiTone t, string tip) { return false; }
        public bool Check(UiRect r, string id, bool on, string text, string tip) { return on; }
        public string Field(UiRect r, string id, string text) { return text ?? ""; }
        public void Fill(UiRect r, UiColorRole c) { }
        public void BeginScroll(string id, UiRect view, float contentH) { }
        public void EndScroll() { }
        public string Tooltip { get { return ""; } }
        public void Ring(UiRect r, UiColorRole c, float width) { }
        public void Line(float x1, float y1, float x2, float y2, UiColorRole c, float width) { }
        public bool Drag(UiRect r, string id, out float mx, out float my) { mx = 0; my = 0; return false; }
        public bool MouseDown(out float mx, out float my) { mx = 0; my = 0; return false; }
        public bool EscapePressed() { return false; }
        public void Icon(UiRect r, string icon, UiTone t) { }
        public void SetHitRects(IReadOnlyDictionary<string, UiRect> hits) { }
        public void SetOverlay(UiRect blocked, bool layer) { }
    }
}
