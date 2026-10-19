// FlatButton.cs — 安装程序与卸载程序共用的两个自绘控件。
//
// 单独成文件的唯一原因：安装（setup-boot.exe）和卸载（uninst.exe）是两个独立的
// 编译产物，要共用同一套观感就得有同一个编译单元。视觉规范仍以 src\Ui\Theme.cs 为准，
// 这里不引入任何新颜色。
//
//   FlatButton  —— 扁平按钮（Primary=蓝底白字；否则白底描边）
//   FlatProgress—— 进度条（支持真进度与"不确定态"来回滑）
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using PicTool;

namespace PicToolSetup
{
    // ---------- 扁平按钮 ----------
    // 实现 IButtonControl 是为了能被窗体的 AcceptButton / CancelButton 接受，
    // 这样"回车 = 默认按钮 / Esc = 取消"这些系统级键盘习惯照旧成立。
    class FlatButton : Control, IButtonControl
    {
        public bool Primary;
        bool hover, down;
        DialogResult dlgResult = DialogResult.None;

        public FlatButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Font = Theme.F12;
            Size = new Size(96, 32);
            Cursor = Cursors.Hand;
        }

        // ----- IButtonControl -----
        public DialogResult DialogResult
        {
            get { return dlgResult; }
            set { dlgResult = value; }
        }
        public void NotifyDefault(bool value) { }
        public void PerformClick()
        {
            if (Enabled) OnClick(EventArgs.Empty);
        }
        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Space || keyData == Keys.Enter) return true;
            return base.IsInputKey(keyData);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                down = true; Invalidate();
            }
            base.OnKeyDown(e);
        }
        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                down = false; Invalidate();
                PerformClick();
            }
            base.OnKeyUp(e);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent == null ? Color.White : Parent.BackColor);
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Color bg, fg;
            if (!Enabled) { bg = Theme.DisabledBg; fg = Theme.DisabledText; }
            else if (Primary)
            {
                bg = down ? Theme.AccentDown : (hover ? Theme.AccentHi : Theme.Accent);
                fg = Color.White;
            }
            else
            {
                bg = down ? Theme.PressBg : (hover ? Theme.HoverBg : Theme.Panel);
                fg = Theme.Text;
            }
            Theme.FillRound(g, r, 6, bg);
            if (!Primary || !Enabled) Theme.StrokeRound(g, r, 6, Theme.BorderDeep, 1f);
            Theme.TextCenter(g, Text, Font, new Rectangle(0, 0, Width, Height), fg);
        }
    }

    // ---------- 进度条（支持"不确定态"来回滑） ----------
    class FlatProgress : Control
    {
        int val;
        int march;
        bool marquee;

        public FlatProgress()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 8;
            Enabled = false;   // 纯展示，不吃鼠标
        }

        public int Value
        {
            get { return val; }
            set { if (val != value) { val = value; Invalidate(); } }
        }

        public bool Marquee
        {
            get { return marquee; }
            set { if (marquee != value) { marquee = value; Invalidate(); } }
        }

        public void Advance()
        {
            if (!marquee) return;
            march += 3;
            if (march > 100) march = 0;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent == null ? Color.White : Parent.BackColor);
            int rad = Height / 2;
            Theme.FillRound(g, new Rectangle(0, 0, Width, Height), rad, Theme.SliderTrack);
            if (marquee)
            {
                int bw = Math.Max(32, Width / 4);
                int span = Width - bw;
                int x = span * march / 100;
                Theme.FillRound(g, new Rectangle(x, 0, bw, Height), rad, Theme.Accent);
            }
            else
            {
                int w = (int)((long)Width * val / 100);
                if (w > 0)
                {
                    if (w < Height) w = Height;
                    int rr = rad;
                    if (w < rad * 2) rr = w / 2;
                    Theme.FillRound(g, new Rectangle(0, 0, w, Height), rr, Theme.Accent);
                }
            }
        }
    }
}
