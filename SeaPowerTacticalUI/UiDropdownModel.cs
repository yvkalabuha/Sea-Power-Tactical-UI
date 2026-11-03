// T-178 Ka1 (ADR-0077 §2.3): the pure model of an open drop-down list: which list is open (one per window), where it lies (below the button or above it),
// the grid of items (columns fill top to bottom) and the hit test. UiKit.Dropdown draws it; the Overview uses it without UiKit.
using System;

namespace VoiceBotBridge
{
    internal sealed class UiDropdownModel
    {
        private int _count, _columns, _rows;
        private float _itemW, _itemH;

        public string OpenId { get; private set; }
        public bool IsOpen(string id) { return OpenId != null && OpenId == id; }
        public UiRect ListRect { get; private set; }
        public UiRect Anchor { get; private set; }
        public bool Above { get; private set; }
        public int Count { get { return _count; } }

        /// <summary>Opens the list of `id` (the previously open one is closed). viewH = the height the list may use (0 = unknown: below), viewW likewise.</summary>
        public void Open(string id, UiRect anchor, int count, int columns, float itemW, float itemH, float viewH) { Open(id, anchor, count, columns, itemW, itemH, viewH, 0f); }

        public void Open(string id, UiRect anchor, int count, int columns, float itemW, float itemH, float viewH, float viewW)
        {
            OpenId = id; Anchor = anchor;
            _count = Math.Max(0, count); _columns = Math.Max(1, Math.Min(columns, Math.Max(1, _count)));
            _rows = _count == 0 ? 0 : (_count + _columns - 1) / _columns;
            _itemW = Math.Max(1f, itemW); _itemH = Math.Max(1f, itemH);
            float w = _columns * _itemW, h = _rows * _itemH;
            float below = anchor.Y + anchor.H;
            bool fitsBelow = viewH <= 0f || below + h <= viewH + 0.01f;
            float roomAbove = anchor.Y;
            Above = !fitsBelow && (roomAbove >= h - 0.01f || roomAbove > (viewH - below));
            float y = Above ? Math.Max(0f, anchor.Y - h) : below;
            float x = anchor.X;
            if (viewW > 0f && x + w > viewW) x = Math.Max(0f, viewW - w);
            ListRect = new UiRect(x, y, w, h);
        }

        public void Close() { OpenId = null; _count = 0; ListRect = default(UiRect); Above = false; }

        /// <summary>Items run down the columns: with 2 columns and 10 items the left one holds 0..4, the right one 5..9.</summary>
        public UiRect ItemRect(int i)
        {
            if (i < 0 || i >= _count) return default(UiRect);
            int col = i / _rows, row = i % _rows;
            return new UiRect(ListRect.X + col * _itemW, ListRect.Y + row * _itemH, _itemW, _itemH);
        }

        /// <summary>The item under the point, -1 = outside the list (or in the empty cell of the last column).</summary>
        public int HitItem(float x, float y)
        {
            if (OpenId == null || _count == 0) return -1;
            if (x < ListRect.X || y < ListRect.Y || x >= ListRect.X + ListRect.W || y >= ListRect.Y + ListRect.H) return -1;
            int col = (int)((x - ListRect.X) / _itemW), row = (int)((y - ListRect.Y) / _itemH);
            if (col >= _columns) col = _columns - 1;
            if (row >= _rows) row = _rows - 1;
            int i = col * _rows + row;
            return i < _count ? i : -1;
        }

        public bool Contains(float x, float y) { return OpenId != null && x >= ListRect.X && y >= ListRect.Y && x < ListRect.X + ListRect.W && y < ListRect.Y + ListRect.H; }

        public static string SelfTest()
        {
            var m = new UiDropdownModel();
            var anchor = new UiRect(10, 20, 100, 18);
            if (m.OpenId != null || m.HitItem(11, 41) != -1) return "closed model";
            m.Open("a", anchor, 5, 1, 100, 18, 400);
            if (!m.IsOpen("a") || m.IsOpen("b") || m.Above) return "open below";
            if (Math.Abs(m.ListRect.Y - 38) > 0.01f || Math.Abs(m.ListRect.H - 90) > 0.01f) return "list below the button: " + m.ListRect.Y + "," + m.ListRect.H;
            if (m.HitItem(20, 39) != 0 || m.HitItem(20, 38 + 18 * 4 + 1) != 4) return "hit first and last";
            if (m.HitItem(5, 39) != -1 || m.HitItem(20, 20) != -1 || m.HitItem(20, 38 + 91) != -1) return "hit outside is -1";
            // no room below: above
            m.Open("a", new UiRect(10, 150, 100, 18), 5, 1, 100, 18, 200);
            if (!m.Above || Math.Abs(m.ListRect.Y - 60) > 0.01f) return "no room below: above (" + m.ListRect.Y + ")";
            if (m.HitItem(20, 61) != 0) return "hit in the list above";
            // neither fits: the roomier side
            m.Open("a", new UiRect(10, 20, 100, 18), 30, 1, 100, 18, 100);
            if (m.Above) return "tight window: below (more room)";
            // 10 items x 2 columns
            m.Open("b", anchor, 10, 2, 80, 18, 400);
            if (m.IsOpen("a") || !m.IsOpen("b")) return "one open list per window: opening b closes a";
            if (Math.Abs(m.ListRect.W - 160) > 0.01f || Math.Abs(m.ListRect.H - 90) > 0.01f) return "2 columns x 5 rows: " + m.ListRect.W + "x" + m.ListRect.H;
            UiRect r0 = m.ItemRect(0), r4 = m.ItemRect(4), r5 = m.ItemRect(5), r9 = m.ItemRect(9);
            if (r0.X != 10 || r4.X != 10 || r5.X != 90 || r9.X != 90 || r5.Y != r0.Y || r9.Y != r4.Y) return "columns run top to bottom";
            for (int i = 0; i < 10; i++) { var r = m.ItemRect(i); if (m.HitItem(r.X + r.W / 2, r.Y + r.H / 2) != i) return "hit centre of " + i; }
            if (m.ItemRect(10).W != 0 || m.ItemRect(-1).W != 0) return "item out of range";
            // odd count: the empty cell is no item
            m.Open("c", anchor, 7, 2, 80, 18, 400);
            if (m.HitItem(90 + 40, m.ListRect.Y + 18 * 3 + 5) != -1) return "empty cell of the last column";
            if (m.HitItem(90 + 40, m.ListRect.Y + 18 * 2 + 5) != 6) return "last item";
            // clamped to the view width
            m.Open("d", new UiRect(150, 20, 60, 18), 3, 1, 100, 18, 400, 200);
            if (m.ListRect.X + m.ListRect.W > 200.01f) return "list stays inside the width";
            m.Close();
            if (m.OpenId != null || m.IsOpen("d") || m.HitItem(160, 41) != -1) return "closed";
            return null;
        }
    }
}
