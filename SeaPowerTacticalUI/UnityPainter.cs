// T-148 U5: IUiPainter over IMGUI (GUI.*). Unity-only. Styles are cached by (kind, font size, tone); text is measured with GUIStyle.CalcSize (UiKit caches the results).
// A click on a disabled button is not delivered by IMGUI, so the painter notes it from Event.current (DisabledPressed).
using System;
using System.Collections.Generic;
using UE = UnityEngine;

namespace VoiceBotBridge
{
    internal sealed class UnityPainter : IUiPainter
    {
        private const string KLabel = "label", KButton = "button", KToggle = "toggle", KField = "field";   // style kinds, not texts
        private readonly UiTheme _t;
        private readonly Dictionary<int, UE.GUIStyle> _styles = new Dictionary<int, UE.GUIStyle>();   // int key: no string building per call (hot path)
        private readonly Dictionary<string, UE.Vector2> _scroll = new Dictionary<string, UE.Vector2>();
        private readonly UE.GUIContent _content = new UE.GUIContent();
        private string _disabledId;
        private readonly UiClickLatch _latch = new UiClickLatch();   // T-239: quick clicks of Button / Check / Hit / Link

        public UnityPainter(UiTheme t) { _t = t; }

        public static UE.Color Tone(UiTone t)
        {
            switch (t)
            {
                case UiTone.Dim: return new UE.Color(0.58f, 0.64f, 0.70f);
                case UiTone.Accent: return new UE.Color(0.60f, 0.82f, 1.00f);
                case UiTone.Warn: return new UE.Color(1.00f, 0.75f, 0.30f);
                case UiTone.Error: return new UE.Color(1.00f, 0.45f, 0.40f);
                case UiTone.Ok: return new UE.Color(0.55f, 0.90f, 0.55f);
                default: return new UE.Color(0.86f, 0.90f, 0.95f);
            }
        }

        public static UE.Color Role(UiColorRole r)
        {
            switch (r)
            {
                case UiColorRole.TitleBar: return new UE.Color(0.10f, 0.14f, 0.18f);
                case UiColorRole.SectionBar: return new UE.Color(0.12f, 0.19f, 0.26f);
                case UiColorRole.Selected: return new UE.Color(0.20f, 0.36f, 0.52f);
                case UiColorRole.Separator: return new UE.Color(0.24f, 0.30f, 0.36f);
                case UiColorRole.Accent: return new UE.Color(0.60f, 0.82f, 1.00f);
                case UiColorRole.Divider: return new UE.Color(0.40f, 0.48f, 0.56f);
                case UiColorRole.Warn: return new UE.Color(1.00f, 0.75f, 0.30f);
                case UiColorRole.Error: return new UE.Color(1.00f, 0.45f, 0.40f);
                default: return new UE.Color(0.06f, 0.08f, 0.10f);
            }
        }

        private int Px(UiFont f) { return f == UiFont.Body ? _t.FontBody : f == UiFont.Small ? _t.FontSmall : f == UiFont.Title ? _t.FontTitle : _t.FontSection; }
        private static bool Bold(UiFont f) { return f == UiFont.Title || f == UiFont.Section; }

        private UE.GUIStyle Style(string kind, UiFont f, UiTone tone)
        {
            int key = (kind == KButton ? 1 : kind == KToggle ? 2 : kind == KField ? 3 : 0) * 10000 + (int)f * 100 + (int)tone;
            UE.GUIStyle s;
            if (_styles.TryGetValue(key, out s)) return s;
            UE.GUIStyle b = kind == KButton ? UE.GUI.skin.button : kind == KToggle ? UE.GUI.skin.toggle : kind == KField ? UE.GUI.skin.textField : UE.GUI.skin.label;
            s = new UE.GUIStyle(b) { fontSize = Px(f), wordWrap = false, clipping = UE.TextClipping.Clip };
            s.fontStyle = Bold(f) ? UE.FontStyle.Bold : UE.FontStyle.Normal;
            UE.Color c = Tone(tone);
            if (kind == KLabel) { s.padding = new UE.RectOffset(0, 0, 0, 0); s.alignment = UE.TextAnchor.MiddleLeft; }
            else if (kind == KButton) { s.padding = new UE.RectOffset(2, 2, 0, 0); s.alignment = UE.TextAnchor.MiddleCenter; }
            else if (kind == KToggle) { s.padding = new UE.RectOffset((int)(_t.CtlH * 0.95f), 0, 0, 0); s.alignment = UE.TextAnchor.MiddleLeft; }
            else { s.padding = new UE.RectOffset(3, 3, 0, 0); s.alignment = UE.TextAnchor.MiddleLeft; }
            s.normal.textColor = c; s.hover.textColor = c; s.active.textColor = c; s.focused.textColor = c;
            s.onNormal.textColor = c; s.onHover.textColor = c; s.onActive.textColor = c; s.onFocused.textColor = c;
            _styles[key] = s;
            return s;
        }

        private static UE.Rect R(UiRect r) { return new UE.Rect(r.X, r.Y, r.W, r.H); }

        private UE.GUIContent C(string text, string tip)
        {
            _content.text = text ?? "";
            _content.tooltip = tip ?? "";
            return _content;
        }

        public float Measure(string text, UiFont font)
        {
            if (string.IsNullOrEmpty(text)) return 0f;
            return Style(KLabel, font, UiTone.Normal).CalcSize(new UE.GUIContent(text)).x;
        }

        public void Label(UiRect r, string text, UiFont f, UiTone t, string tip)
        {
            UE.GUI.Label(R(r), C(text, tip), Style(KLabel, f, t));
        }

        public bool Button(UiRect r, string id, string text, string tip, bool enabled, bool selected, UiTone tone = UiTone.Normal)
        {
            var rect = R(r);
            var ev = UE.Event.current;
            // T-178: a grey button keeps a visible 1 px frame and a dim text (it is a button, not a label), without hover highlight
            var style = Style(KButton, UiFont.Body, enabled ? UiTone.Normal : UiTone.Dim);
            bool covered = ev != null && ev.type != UE.EventType.Repaint && UnderOverlay(ev);   // the open list lies over it
            bool prevEnabled = UE.GUI.enabled;
            var prevBg = UE.GUI.backgroundColor;
            UE.GUI.backgroundColor = selected ? Role(UiColorRole.Selected) * 1.6f : ToneBg(tone);
            UE.GUI.enabled = prevEnabled && enabled;
            bool hit = covered ? false : UE.GUI.Button(rect, C(text, tip), style);
            UE.GUI.enabled = prevEnabled;
            UE.GUI.backgroundColor = prevBg;
            if (!enabled) Frame1(rect, UiColorRole.Divider);
            var zone = Zone(id, rect);
            if (!enabled && !covered && ev != null && ev.type == UE.EventType.MouseDown && zone.Contains(ev.mousePosition)) _disabledId = id;
            // Quick click (live check 0.8.7): the game's input can lose the hot control between a very short MouseDown and MouseUp, then GUI.Button
            // stays false. A MouseUp inside the zone whose MouseDown was seen on the same id still counts (once). The zone is the visible rectangle plus
            // the hit slop of the previous pass (UiHit.Inflate), so a click a pixel beside a small button still lands. T-239: shared with Check / Hit / Link.
            hit = Latched(id, zone, hit, ev, enabled && !covered);
            return hit && enabled;
        }

        private static UE.Color ToneBg(UiTone t)
        {
            switch (t)
            {
                case UiTone.Accent: return new UE.Color(0.45f, 0.62f, 0.85f);
                case UiTone.Warn: return new UE.Color(0.95f, 0.65f, 0.25f);
                case UiTone.Error: return new UE.Color(0.90f, 0.40f, 0.35f);
                case UiTone.Ok: return new UE.Color(0.45f, 0.75f, 0.45f);
                default: return new UE.Color(0.55f, 0.60f, 0.68f);
            }
        }

        /// <summary>T-239: the quick-click safety net of every clickable element (UiClickLatch). hit = what IMGUI returned; returns the verdict (a latched click also uses the event).</summary>
        private bool Latched(string id, UE.Rect zone, bool hit, UE.Event ev, bool active)
        {
            if (id == null || ev == null || !active) return hit;
            if (hit) _latch.Consumed(id);   // the normal path consumed the click
            if (ev.button != 0) return hit;
            if (ev.rawType == UE.EventType.MouseDown) _latch.Down(id, zone.Contains(ev.mousePosition));
            else if (ev.rawType == UE.EventType.MouseUp)
            {
                bool counted = _latch.Up(id, ev.type == UE.EventType.MouseUp && zone.Contains(ev.mousePosition), hit);
                if (counted && !hit) ev.Use();
                return counted;
            }
            return hit;
        }

        public bool DisabledPressed(string id)
        {
            if (_disabledId != id) return false;
            _disabledId = null;
            return true;
        }

        public bool Hit(UiRect r, string id, string tip)
        {
            var ev = UE.Event.current;
            // T-178 Kd4: the table row under the mouse is highlighted (its Hit is "<table>.row.<n>")
            if (ev != null && ev.type == UE.EventType.Repaint && id != null && id.Contains(".row.") && R(r).Contains(ev.mousePosition) && !UnderOverlay(ev))
            {
                var prev = UE.GUI.color;
                var c = Role(UiColorRole.Selected); c.a = 0.45f;
                UE.GUI.color = c;
                UE.GUI.DrawTexture(R(r), UE.Texture2D.whiteTexture);
                UE.GUI.color = prev;
            }
            if (ev != null && ev.type != UE.EventType.Repaint && UnderOverlay(ev)) return false;
            bool hit = UE.GUI.Button(R(r), C("", tip), UE.GUIStyle.none);
            return Latched(id, Zone(id, R(r)), hit, ev, UE.GUI.enabled);
        }

        /// <summary>T-155 K3: a link: normal text; the accent tone and a 1 px (x scale) underline while the mouse is over it.</summary>
        public bool Link(UiRect r, string id, string text, UiFont f, UiTone t, string tip)
        {
            var rect = R(r);
            var ev = UE.Event.current;
            bool covered = ev != null && ev.type != UE.EventType.Repaint && UnderOverlay(ev);   // T-178 KR B1: the open list lies over it
            bool over = ev != null && rect.Contains(ev.mousePosition) && !UnderOverlay(ev);
            bool hit = covered ? false : UE.GUI.Button(rect, C(text, tip), Style(KLabel, f, over ? UiTone.Accent : t));
            hit = Latched(id, Zone(id, rect), hit, ev, !covered && UE.GUI.enabled);
            if (over && ev != null && ev.type == UE.EventType.Repaint)
            {
                float tw = Math.Min(rect.width, Measure(text, f));
                float h = Math.Max(1f, (float)Math.Round(_t.S));
                var prev = UE.GUI.color;
                UE.GUI.color = Tone(UiTone.Accent);
                UE.GUI.DrawTexture(new UE.Rect(rect.x, rect.y + rect.height - h - Math.Max(1f, 1f * _t.S), tw, h), UE.Texture2D.whiteTexture);
                UE.GUI.color = prev;
            }
            return hit;
        }

        public bool Check(UiRect r, string id, bool on, string text, string tip)
        {
            var ev = UE.Event.current;
            if (ev != null && ev.type != UE.EventType.Repaint && UnderOverlay(ev)) return on;   // T-178 KR B1
            var rect = R(r);
            bool now = UE.GUI.Toggle(rect, on, C(text, tip), Style(KToggle, UiFont.Body, UiTone.Normal));
            // T-239: a quick click (hot control lost between MouseDown and MouseUp) and a click a pixel beside the small box toggle it through the latch of the hit zone
            return Latched(id, Zone(id, rect), now != on, ev, UE.GUI.enabled) ? !on : on;
        }

        public string Field(UiRect r, string id, string text)
        {
            var ev = UE.Event.current;
            if (ev != null && ev.type != UE.EventType.Repaint && UnderOverlay(ev)) return text ?? "";   // T-178 KR B1
            UE.GUI.SetNextControlName(id);
            return UE.GUI.TextField(R(r), text ?? "", Style(KField, UiFont.Body, UiTone.Normal));
        }

        public void Fill(UiRect r, UiColorRole c)
        {
            var ev = UE.Event.current;
            if (ev != null && ev.type != UE.EventType.Repaint) return;
            var prev = UE.GUI.color;
            UE.GUI.color = Role(c);
            UE.GUI.DrawTexture(R(r), UE.Texture2D.whiteTexture);
            UE.GUI.color = prev;
        }

        public void BeginScroll(string id, UiRect view, float contentH)
        {
            UE.Vector2 pos;
            _scroll.TryGetValue(id, out pos);
            float bar = _t.ScrollW;
            _scroll[id] = UE.GUI.BeginScrollView(R(view), pos, new UE.Rect(0, 0, Math.Max(1f, view.W - bar), contentH));
        }

        public void EndScroll() { UE.GUI.EndScrollView(); }

        public string Tooltip { get { return UE.GUI.tooltip; } }

        // ---------------------------------------------------------------- T-178 Kd3/Kd4: zones, overlay, primitives

        private IReadOnlyDictionary<string, UiRect> _hits;
        private UiRect _blocked;
        private bool _layer;
        private string _dragId;
        private readonly Dictionary<long, UE.Texture2D> _tex = new Dictionary<long, UE.Texture2D>();
        private readonly Dictionary<string, UE.Texture2D> _icons = new Dictionary<string, UE.Texture2D>();

        public void SetHitRects(IReadOnlyDictionary<string, UiRect> hits) { _hits = hits; }

        public void SetOverlay(UiRect blocked, bool layer)
        {
            _layer = layer;
            _blocked = layer ? default(UiRect) : blocked;
        }

        /// <summary>The zone the mouse is tested against: the inflated one of the previous pass (UiHit.Inflate), else the visible rectangle.</summary>
        private UE.Rect Zone(string id, UE.Rect visible)
        {
            UiRect z;
            if (id != null && _hits != null && _hits.TryGetValue(id, out z) && z.W >= visible.width && z.H >= visible.height) return R(z);
            return visible;
        }

        /// <summary>The mouse is over the open drop-down list of the previous pass: the controls under it do not react (the list is drawn over them).</summary>
        private bool UnderOverlay(UE.Event ev)
        {
            return !_layer && _blocked.W > 0 && ev != null && R(_blocked).Contains(ev.mousePosition);
        }

        private UE.Texture2D Mask(int px, int width, bool ring)
        {
            long key = ((long)px << 16) | (long)(width << 1) | (ring ? 1L : 0L);
            UE.Texture2D t;
            if (_tex.TryGetValue(key, out t) && t) return t;
            t = new UE.Texture2D(px, px, UE.TextureFormat.RGBA32, false) { hideFlags = UE.HideFlags.HideAndDontSave, filterMode = UE.FilterMode.Bilinear };
            var cols = new UE.Color32[px * px];
            double c = px / 2.0, outer = px / 2.0 - 0.5, inner = outer - Math.Max(1, width);
            for (int y = 0; y < px; y++)
                for (int x = 0; x < px; x++)
                {
                    double d = Math.Sqrt((x + 0.5 - c) * (x + 0.5 - c) + (y + 0.5 - c) * (y + 0.5 - c));
                    double cover = Math.Min(1.0, Math.Max(0.0, outer + 0.5 - d));
                    if (ring) cover = Math.Min(cover, Math.Min(1.0, Math.Max(0.0, d - inner + 0.5)));
                    cols[y * px + x] = new UE.Color32(255, 255, 255, (byte)Math.Round(255 * cover));
                }
            t.SetPixels32(cols); t.Apply();
            _tex[key] = t;
            return t;
        }

        /// <summary>A circle outline inscribed in r (the compass disc).</summary>
        public void Ring(UiRect r, UiColorRole c, float width)
        {
            var ev = UE.Event.current;
            if (ev != null && ev.type != UE.EventType.Repaint) return;
            int px = Math.Max(8, Math.Min(256, (int)Math.Round(Math.Min(r.W, r.H))));
            var prev = UE.GUI.color;
            UE.GUI.color = Role(c);
            UE.GUI.DrawTexture(new UE.Rect(r.X + (r.W - px) / 2f, r.Y + (r.H - px) / 2f, px, px), Mask(px, Math.Max(1, (int)Math.Round(width)), true));
            UE.GUI.color = prev;
        }

        public void Line(float x1, float y1, float x2, float y2, UiColorRole c, float width)
        {
            var ev = UE.Event.current;
            if (ev != null && ev.type != UE.EventType.Repaint) return;
            float dx = x2 - x1, dy = y2 - y1, len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 0.01f) return;
            float w = Math.Max(1f, width);
            var prev = UE.GUI.color; var m = UE.GUI.matrix;
            UE.GUI.color = Role(c);
            UE.GUIUtility.RotateAroundPivot((float)(Math.Atan2(dy, dx) * 180.0 / Math.PI), new UE.Vector2(x1, y1));
            UE.GUI.DrawTexture(new UE.Rect(x1, y1 - w / 2f, len, w), UE.Texture2D.whiteTexture);
            UE.GUI.matrix = m; UE.GUI.color = prev;
        }

        /// <summary>LMB pressed inside r (its hit zone): true on every event while it is held, also outside r, and once more on the release.</summary>
        public bool Drag(UiRect r, string id, out float mx, out float my)
        {
            mx = 0; my = 0;
            var ev = UE.Event.current;
            if (ev == null) return false;
            if (_dragId == id && (ev.rawType == UE.EventType.MouseUp || !UE.Input.GetMouseButton(0)))
            {
                _dragId = null;
                if (ev.rawType != UE.EventType.MouseUp) return false;
                mx = ev.mousePosition.x; my = ev.mousePosition.y;
                return true;
            }
            if (_dragId == null && ev.type == UE.EventType.MouseDown && ev.button == 0 && !UnderOverlay(ev) && Zone(id, R(r)).Contains(ev.mousePosition)) { _dragId = id; ev.Use(); }
            if (_dragId != id) return false;
            mx = ev.mousePosition.x; my = ev.mousePosition.y;
            return true;
        }

        public bool MouseDown(out float mx, out float my)
        {
            var ev = UE.Event.current;
            bool down = ev != null && ev.rawType == UE.EventType.MouseDown && ev.button == 0;
            mx = down ? ev.mousePosition.x : 0f; my = down ? ev.mousePosition.y : 0f;
            return down;
        }

        public bool EscapePressed()
        {
            var ev = UE.Event.current;
            return ev != null && ev.type == UE.EventType.KeyDown && ev.keyCode == UE.KeyCode.Escape;
        }

        /// <summary>"glyph:X" = a text glyph; "ntds:Type:Posture" = the own NTDS symbol rasterised once per size.</summary>
        public void Icon(UiRect r, string icon, UiTone t)
        {
            if (string.IsNullOrEmpty(icon)) return;
            if (icon.StartsWith("glyph:"))
            {
                UE.GUI.Label(R(r), icon.Substring(6), Style(KLabel, UiFont.Body, t));
                return;
            }
            var ev = UE.Event.current;
            if (ev != null && ev.type != UE.EventType.Repaint) return;
            int px = Math.Max(8, (int)Math.Round(Math.Min(r.W, r.H)));
            string key = icon + "@" + px;
            UE.Texture2D tex;
            if (!_icons.TryGetValue(key, out tex) || !tex)
            {
                string[] p = icon.Split(':');
                var paths = NtdsIcons.OwnSymbol(p.Length > 1 && p[1] != "" ? p[1] : null, p.Length > 2 ? p[2] : null);
                bool dot; NtdsIcons.KeyFor(p.Length > 1 ? p[1] : null, p.Length > 2 ? p[2] : null, out dot);
                byte[] m = NtdsIcons.RasterMask(paths, dot, px, Math.Max(1.5, px / 10.0));
                if (m == null) return;
                tex = new UE.Texture2D(px, px, UE.TextureFormat.RGBA32, false) { hideFlags = UE.HideFlags.HideAndDontSave };
                var cols = new UE.Color32[px * px];
                for (int y = 0; y < px; y++) for (int x = 0; x < px; x++) cols[(px - 1 - y) * px + x] = new UE.Color32(255, 255, 255, m[y * px + x]);
                tex.SetPixels32(cols); tex.Apply();
                _icons[key] = tex;
            }
            var prev = UE.GUI.color;
            UE.GUI.color = Tone(t);
            UE.GUI.DrawTexture(new UE.Rect(r.X + (r.W - px) / 2f, r.Y + (r.H - px) / 2f, px, px), tex);
            UE.GUI.color = prev;
        }

        /// <summary>The 1 px frame of a grey control.</summary>
        private void Frame1(UE.Rect rect, UiColorRole c)
        {
            var ev = UE.Event.current;
            if (ev != null && ev.type != UE.EventType.Repaint) return;
            float w = Math.Max(1f, (float)Math.Round(_t.S));
            var prev = UE.GUI.color;
            UE.GUI.color = Role(c);
            var wt = UE.Texture2D.whiteTexture;
            UE.GUI.DrawTexture(new UE.Rect(rect.x, rect.y, rect.width, w), wt);
            UE.GUI.DrawTexture(new UE.Rect(rect.x, rect.yMax - w, rect.width, w), wt);
            UE.GUI.DrawTexture(new UE.Rect(rect.x, rect.y + w, w, rect.height - 2 * w), wt);
            UE.GUI.DrawTexture(new UE.Rect(rect.xMax - w, rect.y + w, w, rect.height - 2 * w), wt);
            UE.GUI.color = prev;
        }
    }
}
