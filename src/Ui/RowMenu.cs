// RowMenu.cs — 工作区右键菜单（实测"这个 UI 很丑"重构）：
// 旧实现用 ListBox 纯文字行（行高 22、无图标、无分组），观感像调试输出。
// 新实现全自绘：每项带图标、分组之间画分隔线、危险项红色、悬停圆角高亮、
// 支持键盘上下/Enter/Esc。尺寸与配色全部走 Theme 令牌。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PicTool
{
    public sealed class RowMenu : Form
    {
        public sealed class Item
        {
            public string Text, Icon;
            public Color? Fg;        // null = 正常文字色；Danger 红用于破坏性动作
            public bool Sep;         // 分组分隔线
        }

        readonly List<Item> items = new List<Item>();
        int hover = -1, keyIdx = -1;    // hover=鼠标所在项；keyIdx=键盘当前项
        public int Picked = -1;

        const int RowH = 36;            // 行高：图标 20 + 上下留白，比旧版 22 舒展
        const int SepH = 9;             // 分隔线占用高度
        const int Pad = 6;              // 菜单上下内边距

        public void Add(string text, string icon) { items.Add(new Item { Text = text, Icon = icon }); }
        public void AddDanger(string text, string icon) { items.Add(new Item { Text = text, Icon = icon, Fg = Theme.Danger }); }
        public void AddSep() { items.Add(new Item { Sep = true }); }

        // 按当前项数算菜单总高（调用 ShowAt 前用）
        public int HeightFor()
        {
            int h = Pad * 2;
            foreach (Item it in items) h += it.Sep ? SepH : RowH;
            return h;
        }

        public RowMenu()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Panel;
            DoubleBuffered = true;
            KeyPreview = true;
            Deactivate += delegate { Close(); };
            MouseMove += delegate(object s, MouseEventArgs e) { Track(e.Y); };
            MouseLeave += delegate { if (hover != -1) { hover = -1; Invalidate(); } };
            MouseClick += delegate(object s, MouseEventArgs e)
            {
                int idx = HitRow(e.Y);
                if (idx >= 0 && !items[idx].Sep) { Picked = idx; Close(); }
                else Close();   // 点分隔线/内边距空白也关掉（：以前点"菜单里却不是项"的位置没反应，要点到菜单外才关）
            };
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; return; }
                if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down)
                {
                    int d = e.KeyCode == Keys.Down ? 1 : -1;
                    int start = keyIdx < 0 ? (d > 0 ? -1 : items.Count) : keyIdx;
                    int j = start;
                    for (int k = 0; k < items.Count; k++)
                    {
                        j = (j + d + items.Count) % items.Count;
                        if (!items[j].Sep) { keyIdx = j; hover = j; break; }
                    }
                    Invalidate();
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.Enter)
                {
                    if (keyIdx >= 0 && !items[keyIdx].Sep) { Picked = keyIdx; Close(); }
                    e.Handled = true;
                }
            };
        }

        // 屏幕 y → 项下标（分隔线返回 -1）
        int HitRow(int y)
        {
            int yy = Pad;
            for (int i = 0; i < items.Count; i++)
            {
                int h = items[i].Sep ? SepH : RowH;
                if (y >= yy && y < yy + h) return items[i].Sep ? -1 : i;
                yy += h;
            }
            return -1;
        }

        void Track(int y)
        {
            int idx = HitRow(y);
            if (idx != hover) { hover = idx; keyIdx = idx; Invalidate(); }
        }

        public void ShowAt(Point screenPos, int width)
        {
            int h = HeightFor();
            // ★ 屏幕边界夹取：右键行贴近屏幕底边/右边时，菜单会超出可视区、下面的项点不到。
            //   按菜单自身尺寸把 X/Y 夹回工作区。
            Rectangle wa = Screen.FromPoint(screenPos).WorkingArea;
            int x = screenPos.X, y = screenPos.Y;
            if (x + width > wa.Right) x = wa.Right - width;
            if (y + h > wa.Bottom) y = wa.Bottom - h;
            if (x < wa.Left) x = wa.Left;
            if (y < wa.Top) y = wa.Top;
            Bounds = new Rectangle(x, y, width, h);
            // ★ 圆角 Region：与 Toast 同款修法——方形窗体四角露白太廉价，
            //   把窗形裁成 OnPaint 描的圆角，四角彻底透明
            int r = Theme.Radius;
            using (System.Drawing.Drawing2D.GraphicsPath gp = new System.Drawing.Drawing2D.GraphicsPath())
            {
                Rectangle b = new Rectangle(0, 0, Width - 1, Height - 1);
                gp.AddArc(b.X, b.Y, 2 * r, 2 * r, 180, 90);
                gp.AddArc(b.Right - 2 * r, b.Y, 2 * r, 2 * r, 270, 90);
                gp.AddArc(b.Right - 2 * r, b.Bottom - 2 * r, 2 * r, 2 * r, 0, 90);
                gp.AddArc(b.X, b.Bottom - 2 * r, 2 * r, 2 * r, 90, 90);
                gp.CloseFigure();
                Region = new Region(gp);
            }
            Show();
            Focus();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Theme.StrokeRound(g, new Rectangle(0, 0, Width - 1, Height - 1), Theme.Radius, Theme.BorderDeep, 1f);

            int yy = Pad;
            for (int i = 0; i < items.Count; i++)
            {
                Item it = items[i];
                if (it.Sep)
                {
                    using (Pen p = new Pen(Theme.Border, 1f))
                        g.DrawLine(p, 10, yy + SepH / 2, Width - 10, yy + SepH / 2);
                    yy += SepH;
                    continue;
                }
                bool hot = i == hover || i == keyIdx;
                if (hot)
                {
                    // 悬停/键盘选中：圆角浅色底，左右各留 4px
                    using (SolidBrush b = new SolidBrush(Theme.AccentSoft))
                        Theme.FillRound(g, new Rectangle(4, yy + 1, Width - 8, RowH - 2), 6, Theme.AccentSoft);
                }
                int tx = 14;
                if (it.Icon != null && it.Icon.Length > 0)
                {
                    Color ic = it.Fg.HasValue ? it.Fg.Value : (hot ? Theme.Accent : Theme.TextSub);
                    Icons.Draw(g, it.Icon, new Rectangle(12, yy + (RowH - 20) / 2, 20, 20), ic, 1.4f);
                    tx = 42;
                }
                Color fg = it.Fg.HasValue ? it.Fg.Value : (hot ? Theme.Accent : Theme.Text);
                Theme.DrawText(g, it.Text, Theme.F12,
                    new Rectangle(tx, yy, Width - tx - 10, RowH), fg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                yy += RowH;
            }
        }
    }
}
