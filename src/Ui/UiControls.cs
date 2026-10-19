// UiControls.cs — 自绘控件库：按钮 / 分段 / 开关 / 滑条 / 进度 / 分隔 / 导航轨 / 工具胶囊
// 全部自定义绘制 + 双缓冲，不依赖系统视觉样式，Win7 经典主题下观感一致。
// 交互动效统一走 Anim 引擎（hover 渐变 / 选中滑块 / 开关旋钮），时长 120~180ms。
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PicTool
{
    public delegate string TextFilterDelegate(string text);   // .NET 2.0 没有 System.Func

    // ---------------- 按钮 ----------------
    public class FlatButton : Control, IButtonControl
    {
        public enum Kinds { Ghost, Primary, Danger, Subtle, Warn }
        Kinds kind = Kinds.Ghost;
        bool down;
        double hoverT;                       // hover 渐变系数 0..1（动效）
        string icon;
        DialogResult dlgResult = DialogResult.None;
        public int Radius = Theme.Radius;
        public int IconSize = 16;
        public bool IconOnly;

        // ★ 忙碌态：长任务进行中按钮显示转圈、忽略点击。
        //   外部照常设 Text（例如"处理中…"），转圈画在文字左边占图标的位置。
        bool busyFlag;
        Timer busyTimer;
        int busyAngle;

        // 忙碌中仍然可点（例如"取消修复"：转圈 + 点击=取消并存）
        public bool BusyClickable;

        public bool Busy
        {
            get { return busyFlag; }
            set
            {
                if (busyFlag == value) return;
                busyFlag = value;
                if (busyFlag)
                {
                    busyAngle = 0;
                    busyTimer = new Timer();
                    busyTimer.Interval = 50;
                    busyTimer.Tick += delegate { busyAngle = (busyAngle + 30) % 360; if (busyFlag) Invalidate(); };
                    busyTimer.Start();
                }
                else if (busyTimer != null)
                {
                    busyTimer.Stop(); busyTimer.Dispose(); busyTimer = null;
                }
                Invalidate();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && busyTimer != null) { busyTimer.Stop(); busyTimer.Dispose(); busyTimer = null; }
            base.Dispose(disposing);
        }

        // IButtonControl：使按钮可以作为对话框的默认/取消按钮
        public DialogResult DialogResult { get { return dlgResult; } set { dlgResult = value; } }
        public void NotifyDefault(bool value) { }
        public void PerformClick() { if (Enabled) OnClick(EventArgs.Empty); }

        public Kinds Kind { get { return kind; } set { kind = value; Invalidate(); } }
        public string Icon { get { return icon; } set { icon = value; Invalidate(); } }

        public FlatButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            Font = Theme.F12; Cursor = Cursors.Hand;
            Size = new Size(88, 32);
            BackColor = Color.Transparent;
            TabStop = false;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            Anim.Run(this, hoverT, 1, 130, AnimEase.Out, delegate(double v) { hoverT = v; Invalidate(); }, null);
            base.OnMouseEnter(e);
        }
        protected override void OnMouseLeave(EventArgs e)
        {
            down = false;
            Anim.Run(this, hoverT, 0, 160, AnimEase.Out, delegate(double v) { hoverT = v; Invalidate(); }, null);
            base.OnMouseLeave(e);
        }
        protected override void OnMouseDown(MouseEventArgs e) { if (Enabled) { down = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
        protected override void OnEnabledChanged(EventArgs e) { hoverT = 0; Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnClick(EventArgs e)
        {
            if (busyFlag && !BusyClickable) return;   // 忙碌中的点击一律忽略：防手抖把长任务点两遍
            base.OnClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // 按下时整体往里缩 1px：轻微"下压"的手感（比只变色多一层物理暗示）
            Rectangle r = down
                ? new Rectangle(1, 1, Width - 3, Height - 3)
                : new Rectangle(0, 0, Width - 1, Height - 1);
            Color fill, line = Color.Empty, txt;
            bool bordered = true;

            switch (kind)
            {
                case Kinds.Primary:
                    if (Enabled) { fill = PrimaryFill(); line = fill; txt = Theme.TextOnAccent; bordered = false; }
                    else { fill = Theme.DisabledBg; line = Theme.Border; txt = Theme.DisabledText; bordered = true; }
                    break;
                case Kinds.Danger:
                    fill = ColorLerp.Mix(Theme.Panel, Theme.DangerSoft, hoverT);
                    if (down) fill = Theme.DangerSoft;
                    line = Enabled ? ColorLerp.Mix(Theme.Danger, Theme.Danger, hoverT) : Theme.Border;
                    txt = Enabled ? Theme.Danger : Theme.TextMuted;
                    break;
                case Kinds.Warn:
                    fill = ColorLerp.Mix(Theme.WarnSoft, Theme.WarnLine, hoverT * 0.30);
                    line = Theme.WarnLine; txt = Theme.Warn;
                    break;
                case Kinds.Subtle:
                    fill = ColorLerp.Mix(Theme.Panel, Theme.HoverBg, hoverT);
                    if (down) fill = Theme.PressBg;
                    line = Color.Empty; bordered = false;
                    txt = Enabled ? Theme.TextSub : Theme.TextMuted;
                    break;
                default:
                    fill = ColorLerp.Mix(Theme.Panel, Theme.HoverBg, hoverT);
                    if (down) fill = Theme.PressBg;
                    line = (Enabled && hoverT > 0.01) ? ColorLerp.Mix(Theme.Border, Theme.BorderDeep, hoverT) : Theme.Border;
                    txt = Enabled ? Theme.Text : Theme.TextMuted;
                    break;
            }
            if (line.IsEmpty)
            {
                if (!fill.Equals(Theme.Panel) || down)
                    Theme.FillRound(g, r, Radius, fill);
            }
            else
            {
                using (GraphicsPath p = Theme.Round(r, Radius))
                using (SolidBrush b = new SolidBrush(fill))
                    g.FillPath(b, p);
                if (bordered)
                    Theme.StrokeRound(g, r, Radius, line, 1f);
            }

            // 图标 + 文字（整体居中）；IconOnly 时只画图标——否则外部 setText 会把
            // 窄按钮撑爆，裁出半截文字（工作区按钮曾因此显示成"工作"）
            // 忙碌时用转圈占图标的位置；IconOnly + 忙碌 = 只剩一个转圈
            string label = IconOnly ? "" : (Text ?? "");
            Size ts = label.Length > 0
                ? TextRenderer.MeasureText(label, Font, new Size(400, 40), TextFormatFlags.NoPadding)
                : Size.Empty;
            bool spinning = busyFlag;
            bool showIcon = icon != null && !busyFlag;
            int leadW = spinning || showIcon ? IconSize : 0;
            int gap = (leadW > 0 && label.Length > 0) ? 6 : 0;
            int contentW = leadW + gap + ts.Width;
            int cx = (Width - contentW) / 2;
            int cy = Height / 2;

            if (spinning)
            {
                Rectangle sb = new Rectangle(cx, cy - IconSize / 2, IconSize, IconSize);
                using (Pen sp = new Pen(txt, 1.8f))
                {
                    sp.StartCap = LineCap.Round;
                    sp.EndCap = LineCap.Round;
                    g.DrawArc(sp, sb, busyAngle, 270);
                }
                cx += IconSize + gap;
            }
            else if (showIcon)
            {
                Rectangle ib = new Rectangle(cx, cy - IconSize / 2, IconSize, IconSize);
                Icons.Draw(g, icon, ib, txt, 1.5f);
                cx += IconSize + gap;
            }
            if (label.Length > 0)
            {
                Rectangle tb = new Rectangle(cx, cy - ts.Height / 2 - 1, ts.Width + 2, ts.Height + 2);
                Theme.DrawText(g, label, Font, tb, txt,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
        }

        Color PrimaryFill()
        {
            // 禁用态：不是纯灰，而是"主色被冲淡"——一眼能认出这是个主按钮，只是现在点不了
            if (!Enabled) return ColorLerp.Mix(Theme.DisabledBg, Theme.Accent, 0.18);
            if (down) return Theme.AccentDown;
            return ColorLerp.Mix(Theme.Accent, Theme.AccentHi, hoverT);
        }
    }

    // ---------------- 分段控件（动画滑块版）----------------
    public class Segmented : Control
    {
        string[] items = new string[0];
        int sel = -1, hoverIdx = -1;
        double thumbX;                       // 滑块 X（动画值，像素）
        public int Radius = 8;
        public event EventHandler SelectedChanged;

        public string[] Items { get { return items; } set { items = value ?? new string[0]; sel = items.Length > 0 ? 0 : -1; SnapThumb(); Invalidate(); } }
        public int SelectedIndex
        {
            get { return sel; }
            set
            {
                if (value >= 0 && value < items.Length && sel != value)
                {
                    sel = value;
                    AnimateThumb();
                    Invalidate();
                    if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty);
                }
            }
        }

        public Segmented()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Font = Theme.F12;
            Height = 34;
            TabStop = false;
            Cursor = Cursors.Hand;
        }

        int ItemW() { return items.Length == 0 ? 0 : Width / items.Length; }

        // 滑块目标位置（即选项单元的起点）
        int ThumbTarget() { int w = ItemW(); return w <= 0 ? 0 : sel * w + 2; }

        void SnapThumb() { thumbX = ThumbTarget(); }

        void AnimateThumb()
        {
            Anim.Run(this, thumbX, ThumbTarget(), 160, AnimEase.Out,
                delegate(double v) { thumbX = v; Invalidate(); }, null);
        }

        int IndexAt(int x)
        {
            if (items.Length == 0) return -1;
            int w = ItemW();
            if (w <= 0) return -1;
            int i = x / w;
            return (i >= 0 && i < items.Length) ? i : -1;
        }

        protected override void OnResize(EventArgs e) { SnapThumb(); base.OnResize(e); }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = IndexAt(e.X);
            if (i != hoverIdx) { hoverIdx = i; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { hoverIdx = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) { base.OnMouseDown(e); return; }
            int i = IndexAt(e.X);
            if (i >= 0) SelectedIndex = i;
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int n = items.Length;
            if (n == 0) return;
            Rectangle all = new Rectangle(0, 0, Width - 1, Height - 1);
            Theme.FillRound(g, all, Radius, Theme.TrackBg);

            int w = ItemW();
            // 动画滑块：白色圆角块滑到当前项
            if (sel >= 0 && w > 0)
            {
                Rectangle th = new Rectangle((int)Math.Round(thumbX), 2, w - 4, Height - 5);
                Theme.FillRound(g, th, Radius - 2, Theme.Panel);
                Theme.StrokeRound(g, th, Radius - 2, Theme.BorderDeep, 1f);
            }
            for (int i = 0; i < n; i++)
            {
                Rectangle cell = new Rectangle(i * w, 2, w, Height - 5);
                bool on = i == sel;
                Color tc = on ? Theme.Accent : (i == hoverIdx ? Theme.Text : Theme.TextSub);
                Theme.DrawText(g, items[i], on ? Theme.F12B : Theme.F12, cell, tc,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
        }
    }

    // ---------------- 左侧导航轨（功能入口 = 一格抽屉）----------------
    public class NavRail : Control
    {
        string[] iconNames = new string[0];
        string[] labelTexts = new string[0];
        int sel = 0, hoverIdx = -1;
        double indY;                          // 指示条 Y（动画值）
        ToolTip tips;
        Control footer;

        public event EventHandler SelectedIndexChanged;

        public int SelectedIndex
        {
            get { return sel; }
            set
            {
                if (value >= 0 && value < iconNames.Length && sel != value)
                {
                    sel = value;
                    AnimateIndicator();
                    Invalidate();
                    if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
                }
            }
        }

        // 底部固定槽位（如"快捷键设置"）；由外部 Add 进来，导航自动让出底部空间
        public Control Footer
        {
            get { return footer; }
            set
            {
                footer = value;
                if (footer != null)
                {
                    if (!Controls.Contains(footer)) Controls.Add(footer);
                    PlaceFooter();
                }
                Invalidate();
            }
        }

        public NavRail(string[] icons, string[] labels, string[] tips)
        {
            this.iconNames = icons ?? new string[0];
            this.labelTexts = labels ?? new string[0];
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.RailBg;
            Cursor = Cursors.Hand;
            TabStop = false;
            if (tips != null && tips.Length > 0)
            {
                // 悬停到哪个入口就显示哪条提示（MouseMove 里动态换文案）
                this.tips = new ToolTip();
                this.tips.ShowAlways = true;
                this.tips.SetToolTip(this, tips[0]);
                hoverTips = tips;
            }
            indY = IndicatorTarget();
        }

        string[] hoverTips;

        const int ItemH = 52;
        const int TopPad = 10;
        int FooterReserve() { return footer != null ? 48 : 8; }

        int IndicatorTarget() { return TopPad + sel * ItemH + (ItemH - 22) / 2; }

        void AnimateIndicator()
        {
            Anim.Run(this, indY, IndicatorTarget(), 170, AnimEase.Out,
                delegate(double v) { indY = v; Invalidate(); }, null);
        }

        int IndexAt(int y)
        {
            int usable = Height - TopPad - FooterReserve();
            if (iconNames.Length == 0 || usable < ItemH) return -1;
            int i = (y - TopPad) / ItemH;
            if (i < 0 || i >= iconNames.Length) return -1;
            if (TopPad + (i + 1) * ItemH > Height - FooterReserve() + 6) return -1;
            return i;
        }

        void PlaceFooter()
        {
            if (footer == null || footer.IsDisposed) return;
            footer.Bounds = new Rectangle((Width - 40) / 2, Height - 44, 40, 36);
        }

        protected override void OnResize(EventArgs e) { PlaceFooter(); base.OnResize(e); }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = IndexAt(e.Y);
            if (i != hoverIdx)
            {
                hoverIdx = i;
                if (tips != null && hoverTips != null)
                    tips.SetToolTip(this, i >= 0 && i < hoverTips.Length ? hoverTips[i] : "");
                Invalidate();
            }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { hoverIdx = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) { base.OnMouseDown(e); return; }
            int i = IndexAt(e.Y);
            if (i >= 0) SelectedIndex = i;
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush b = new SolidBrush(Theme.RailBg))
                g.FillRectangle(b, ClientRectangle);

            int iw = 18;
            int w = Width;
            for (int i = 0; i < iconNames.Length; i++)
            {
                int y0 = TopPad + i * ItemH;
                if (y0 + ItemH > Height - FooterReserve() + 6) break;
                bool on = i == sel;
                bool hov = i == hoverIdx && !on;

                // 行底（激活/悬停）
                Rectangle row = new Rectangle(6, y0 + 2, w - 12, ItemH - 4);
                if (on) Theme.FillRound(g, row, 8, Theme.RailActive);
                else if (hov) Theme.FillRound(g, row, 8, ColorLerp.Mix(Theme.RailBg, Theme.HoverBg, 0.85));

                // 图标 + 小字
                Color c = on ? Theme.Accent : (hov ? Theme.Text : Theme.TextSub);
                Rectangle ib = new Rectangle((w - iw) / 2, y0 + 6, iw, iw);
                Icons.Draw(g, iconNames[i], ib, c, 1.5f);
                Theme.DrawText(g, labelTexts[i], Theme.F10,
                    new Rectangle(2, y0 + 6 + iw + 1, w - 4, 14), on ? Theme.Accent : Theme.TextMuted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.SingleLine);

                // 激活指示条（动画滑动的 3px 圆角竖条）——朱砂：印章红的位置指示，与主色蓝职责分离
                // （蓝只管可交互）
                if (on)
                {
                    int iy = (int)Math.Round(indY);
                    Theme.FillRound(g, new Rectangle(w - 5, iy, 3, 22), 2, Theme.Cinnabar);
                }
            }
        }
    }

    // ---------------- 画布浮动工具胶囊（笔刷/框选/橡皮/图章）----------------

    // ---------------- 开关（行内：左标签 + 右开关，旋钮动画）----------------
    public class FlatSwitch : Control
    {
        bool chk, hover;
        double knobT;                         // 0=关 1=开（动画值）
        public event EventHandler CheckedChanged;
        public bool Checked
        {
            get { return chk; }
            set { if (chk != value) { chk = value; AnimateKnob(); } }
        }

        public FlatSwitch()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Font = Theme.F12;
            Size = new Size(220, 30);
            Cursor = Cursors.Hand;
            TabStop = false;
            knobT = chk ? 1 : 0;
        }

        void AnimateKnob()
        {
            Anim.Run(this, knobT, chk ? 1 : 0, 150, AnimEase.Out,
                delegate(double v) { knobT = v; Invalidate(); }, null);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnClick(EventArgs e)
        {
            if (!Enabled) return;
            chk = !chk; AnimateKnob();
            if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            base.OnClick(e);
        }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int sw = 38, sh = 22;
            Rectangle track = new Rectangle(Width - sw - 1, (Height - sh) / 2, sw, sh);
            Color offC = hover ? Theme.TextMuted : Theme.BorderDeep;
            Color onC = ColorLerp.Mix(Theme.Accent, Theme.AccentHi, hover ? 1 : 0);
            Color trackColor = !Enabled ? Theme.DisabledBg
                : ColorLerp.Mix(offC, onC, knobT);
            Theme.FillRound(g, track, sh / 2, trackColor);

            int knob = sh - 4;
            double f = 2 + (track.Width - knob - 4) * knobT;
            Rectangle kr = new Rectangle(track.X + (int)Math.Round(f), track.Y + 2, knob, knob);
            using (SolidBrush b = new SolidBrush(Theme.Knob))
                g.FillEllipse(b, kr);
            using (Pen p = new Pen(Theme.Shadow))
                g.DrawEllipse(p, kr);

            string label = Text ?? "";
            if (label.Length > 0)
            {
                Rectangle tr = new Rectangle(0, 0, Width - sw - 12, Height);
                Theme.DrawText(g, label, Font, tr, Enabled ? Theme.Text : Theme.TextMuted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
        }
    }

    // ---------------- 滑条 ----------------
    public class FlatSlider : Control
    {
        int min = 0, max = 100, val = 0;
        double dblVal, step = 1.0;
        bool dragging, hover;
        public event EventHandler ValueChanged;
        public event EventHandler ValueCommitted;

        public int Min { get { return min; } set { min = value; if (val < min) { val = min; dblVal = min; } Invalidate(); } }
        public int Max { get { return max; } set { max = value; if (val > max) { val = max; dblVal = max; } Invalidate(); } }
        public double Step { get { return step; } set { step = value <= 0 ? 1.0 : value; } }
        public double DoubleValue { get { return dblVal; } }
        public int Value { get { return val; } set { SetD(value, false, true); } }

        public FlatSlider()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Size = new Size(180, 26);
            Cursor = Cursors.Hand;
            TabStop = true;              // 2026-09-24：可以聚焦（画焦点环），方向键仍不占用
        }

        // 拖动时跟随气泡显示的文本；不设就显示整数值
        //（特殊读法的滑条——Gamma/黑场之类——由创建方给个换算，见 MainForm 的滑条工厂）
        public Func<int, string> ValueLabel;

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        public void SetDouble(double v, bool raiseChanged) { SetD(v, raiseChanged, false); }
        public void ResetValue(double v) { dblVal = v; val = (int)Math.Round(v); Invalidate(); }

        // 数值输入框提交时调用（用户需求：滑条数值支持手动键入）——
        // 与拖动松手走同一条 ValueCommitted 提交链（调色/旋转的应用逻辑无差别）。
        public void CommitFromEditor()
        {
            Invalidate();
            if (ValueCommitted != null) ValueCommitted(this, EventArgs.Empty);
        }

        void SetD(double v, bool raiseChanged, bool roundToInt)
        {
            double nv = v;
            if (step > 0 && step != 1.0) nv = Math.Round(nv / step) * step;
            if (roundToInt) nv = Math.Round(nv);
            if (nv < min) nv = min;
            if (nv > max) nv = max;
            if (Math.Abs(nv - dblVal) < 1e-9) return;
            dblVal = nv; val = (int)Math.Round(nv);
            Invalidate();
            if (raiseChanged && ValueChanged != null) ValueChanged(this, EventArgs.Empty);
        }

        int ThumbX()
        {
            double span = max - min;
            if (span <= 0) return 9;
            int w = Width - 18;
            return (int)Math.Round(9 + w * (val - min) / span);
        }

        void FromMouse(int mx)
        {
            int w = Width - 18;
            if (w <= 1) return;
            double f = (mx - 9) / (double)w;
            if (f < 0) f = 0; if (f > 1) f = 1;
            SetD(min + f * (max - min), true, false);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) { base.OnMouseDown(e); return; }   // 只有左键才算拖动（右键不该改值）
            if (Enabled) { dragging = true; FromMouse(e.X); }
            base.OnMouseDown(e);
        }
        protected override void OnMouseMove(MouseEventArgs e) { if (dragging) FromMouse(e.X); base.OnMouseMove(e); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (dragging)
            {
                dragging = false; Invalidate();
                if (ValueCommitted != null) ValueCommitted(this, EventArgs.Empty);
            }
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int cy = Height / 2;
            int tx = ThumbX();
            Color accent = Enabled ? (dragging || hover ? Theme.AccentHi : Theme.Accent)
                                   : ColorLerp.Mix(Theme.DisabledBg, Theme.Accent, 0.18);
            Color track = Enabled ? Theme.SliderTrack : Theme.DisabledBg;

            using (Pen p = new Pen(track, 3f))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                g.DrawLine(p, 9, cy, Width - 9, cy);
            }
            using (Pen p = new Pen(accent, 3f))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                g.DrawLine(p, 9, cy, tx, cy);
            }

            int rad = (dragging || hover) && Enabled ? 8 : 7;
            Rectangle kr = new Rectangle(tx - rad, cy - rad, rad * 2, rad * 2);
            using (SolidBrush b = new SolidBrush(Theme.Knob))
                g.FillEllipse(b, kr);
            using (Pen p = new Pen(accent, 1.8f))
                g.DrawEllipse(p, kr);

            // ★ 键盘焦点环：Tab 走到滑条时能看出来"焦点在这"（以前焦点完全不可见）
            if (Focused && Enabled && ShowFocusCues)
                Theme.DrawFocus(g, new Rectangle(2, 2, Width - 5, Height - 5), Theme.Radius);

            // ★ 拖动/悬停时的数值气泡：以前调到多少只能看右栏那个小字标签，
            //   眼睛要在滑条和标签之间来回跳；现在数值直接跟着滑块走。
            //   控件只有 22px 高、气泡画出去会被裁剪，所以贴着顶边画（拖动时盖住半条轨道，可接受）。
            if ((dragging || hover) && Enabled)
            {
                string s = ValueLabel != null ? ValueLabel(val) : val.ToString();
                Size msz = TextRenderer.MeasureText(s, Theme.F10, new Size(120, 20), TextFormatFlags.NoPadding);
                int bw = Math.Max(26, msz.Width + 10), bh = 16;
                int bx = Math.Max(0, Math.Min(Width - bw, tx - bw / 2));
                Rectangle bb = new Rectangle(bx, 0, bw, bh);
                Theme.FillRound(g, bb, 5, Theme.Accent);
                Theme.DrawText(g, s, Theme.F10, bb, Theme.TextOnDark,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
        }
    }

    // ---------------- 进度条 ----------------
    public class FlatProgress : Control
    {
        int val;
        Timer anim;
        int phase;                     // 高光带位置（0..400 循环）

        public int Value { get { return val; } set { val = Math.Max(0, Math.Min(100, value)); Invalidate(); } }

        public FlatProgress()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = 6;
            TabStop = false;
            // ★ 流光：一段高光在填充条里来回跑 —— 进度条走到一半停住（大图处理慢）和"卡死了"
            //   看起来一模一样；有了流光，活着/卡住一眼分清。
            anim = new Timer();
            anim.Interval = 50;
            anim.Tick += delegate { phase = (phase + 4) % 400; if (Visible && val > 0 && val < 100) Invalidate(); };
            anim.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && anim != null) { anim.Stop(); anim.Dispose(); anim = null; }
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.FillRound(g, new Rectangle(0, 0, Width, Height), Height / 2, Theme.TrackBg);
            if (val > 0)
            {
                int w = (int)(Width * val / 100.0);
                if (w < Height) w = Height;
                Theme.FillRound(g, new Rectangle(0, 0, w, Height), Height / 2, Theme.Accent);

                if (val < 100)
                {
                    // 高光带：只在"还没走完"时跑
                    int gw = Math.Max(20, w / 5);
                    int span = w + gw;
                    int gx = span * phase / 400 - gw;
                    if (gx < 0) gx = 0;
                    if (gx > w - gw) gx = Math.Max(0, w - gw);
                    Theme.FillRound(g, new Rectangle(gx, 1, gw, Height - 2), (Height - 2) / 2,
                        Color.FromArgb(70, 255, 255, 255));
                }
            }
        }
    }

    // ---------------- 可拖动分隔条 ----------------
    public class FlatSplitter : Control
    {
        bool dragging, hover;
        int startX, startWidth;
        public Control Target;
        public int MinLeft = 0, MaxLeft = 460;
        public event EventHandler Moved;
        public event EventHandler ToggleRequested;

        public FlatSplitter()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Width = 6;
            Cursor = Cursors.SizeWE;
            BackColor = Theme.WindowBg;
            TabStop = false;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (Target != null) { dragging = true; startX = PointToScreen(new Point(e.X, 0)).X; startWidth = Target.Width; }
            base.OnMouseDown(e);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragging && Target != null)
            {
                int now = PointToScreen(new Point(e.X, 0)).X;
                int w = startWidth + (now - startX);
                if (w < MinLeft) w = MinLeft;
                if (w > MaxLeft) w = MaxLeft;
                Target.Width = w;
                if (Moved != null) Moved(this, EventArgs.Empty);
            }
            base.OnMouseMove(e);
        }
        protected override void OnMouseUp(MouseEventArgs e) { dragging = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (ToggleRequested != null) ToggleRequested(this, EventArgs.Empty);
            base.OnMouseDoubleClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (SolidBrush b = new SolidBrush(Theme.WindowBg))
                g.FillRectangle(b, ClientRectangle);
            Color c = (dragging || hover) ? Theme.Accent : Theme.Border;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int x = Width / 2;
            using (Pen p = new Pen(c, (dragging || hover) ? 2f : 1f))
                g.DrawLine(p, x, 0, x, Height);
        }
    }

    // ---------------- 小圆点（状态指示，如"已编辑"）----------------
    public class FlatDot : Control
    {
        Color dotColor = Theme.Green;
        public Color DotColor { get { return dotColor; } set { dotColor = value; Invalidate(); } }

        public FlatDot()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            Size = new Size(10, 10);
            BackColor = Color.Transparent;
            TabStop = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush b = new SolidBrush(dotColor))
                e.Graphics.FillEllipse(b, 1, 1, Width - 2, Height - 2);
        }
    }

    // ---------------- 扁平输入框 ----------------
    // 原生 TextBox 带系统 3D 边框，混在自绘界面里非常扎眼（文件页/文字页都是重灾区）。
    // 这里把无边框 TextBox 装进一个自绘圆角容器：聚焦时描主色边，未聚焦描浅边。
    public class FlatTextBox : Control
    {
        TextBox box;
        bool focused;

        public TextFilterDelegate Filter;        // 可选：输入过滤（返回过滤后的文本）
        public event EventHandler TextChanged;   // 转发内层 TextBox 的 TextChanged

        public TextBox Inner { get { return box; } }
        public bool ReadOnly { get { return box.ReadOnly; } set { box.ReadOnly = value; Invalidate(); } }
        public HorizontalAlignment TextAlign
        {
            get { return box.TextAlign; }
            set { box.TextAlign = value; }
        }
        public override string Text
        {
            get { return box.Text; }
            set { box.Text = value; }
        }

        public FlatTextBox()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Size = new Size(160, 30);

            box = new TextBox();
            box.BorderStyle = BorderStyle.None;
            box.Font = Theme.F12;
            box.BackColor = Theme.FieldBg;
            box.ForeColor = Theme.Text;
            box.GotFocus += delegate { focused = true; Invalidate(); };
            box.LostFocus += delegate { focused = false; Invalidate(); };
            box.TextChanged += delegate
            {
                if (Filter != null) { string t = Filter(box.Text); if (t != box.Text) box.Text = t; }
                Invalidate();
                if (TextChanged != null) TextChanged(this, EventArgs.Empty);
            };
            Controls.Add(box);
            LayoutBox();
        }

        void LayoutBox()
        {
            if (box == null) return;      // 构造函数里 Size 先于内层框创建，会走到这里
            box.Bounds = new Rectangle(9, (Height - box.PreferredHeight) / 2 + 1,
                Math.Max(10, Width - 18), box.PreferredHeight);
        }

        protected override void OnResize(EventArgs e) { LayoutBox(); Invalidate(); base.OnResize(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            box.Focus();          // 点容器任意处都能开始输入
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Theme.FillRound(e.Graphics, r, Theme.Radius, ReadOnly ? Theme.DisabledBg : Theme.FieldBg);
            Theme.StrokeRound(e.Graphics, r, Theme.Radius,
                focused ? Theme.Accent : Theme.Border, focused ? 1.6f : 1f);
        }
    }

    // ---------------- 切换按钮（加粗/斜体/下划线/对齐 这类"开关格"）----------------
    // Label 或 Icon 二选一；选中态 = 主色浅底 + 主色描边，与分段控件观感统一。
    public class FlatToggle : Control
    {
        bool chk, hover, down;
        public string Glyph;        // 显示字符（如 "B"），空则用 Icon
        public string Icon;
        public Font GlyphFont;
        public event EventHandler CheckedChanged;

        public bool Checked
        {
            get { return chk; }
            set { if (chk != value) { chk = value; Invalidate(); if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty); } }
        }

        public FlatToggle()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Font = Theme.F12;
            Size = new Size(38, 32);
            Cursor = Cursors.Hand;
            TabStop = false;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (Enabled) { down = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { hover = false; Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            if (!Enabled)
            {
                Theme.FillRound(g, r, Theme.Radius, Theme.DisabledBg);
                Theme.StrokeRound(g, r, Theme.Radius, Theme.Border, 1f);
            }
            else if (chk)
            {
                Theme.FillRound(g, r, Theme.Radius, down ? Theme.AccentSoft2 : Theme.AccentSoft);
                Theme.StrokeRound(g, r, Theme.Radius, Theme.Accent, 1.2f);
            }
            else
            {
                Theme.FillRound(g, r, Theme.Radius, down ? Theme.PressBg : (hover ? Theme.HoverBg : Theme.Panel));
                Theme.StrokeRound(g, r, Theme.Radius, hover ? Theme.BorderDeep : Theme.Border, 1f);
            }
            Color fg = !Enabled ? Theme.DisabledText : (chk ? Theme.Accent : Theme.Text);
            if (Glyph != null && Glyph.Length > 0)
            {
                Font f = GlyphFont != null ? GlyphFont : Font;
                Theme.DrawText(g, Glyph, f, r, fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
            else if (Icon != null && Icon.Length > 0)
            {
                Icons.Draw(g, Icon, new Rectangle(Width / 2 - 8, Height / 2 - 8, 16, 16), fg, 1.6f);
            }
        }
    }

    // ---------------- 颜色选择（一排色块）----------------
    // 文字颜色原来用四个文字按钮表示，看不出颜色本身 —— 这里直接画色块，选中有外圈 + 勾。
    public class ColorPicker : Control
    {
        Color[] colors = new Color[0];
        string[] names = new string[0];
        int sel, hoverIdx = -1;
        public event EventHandler SelectedChanged;

        public Color[] Colors { get { return colors; } set { colors = value ?? new Color[0]; Snap(); Invalidate(); } }
        public string[] Names { get { return names; } set { names = value ?? new string[0]; } }
        public int SelectedIndex
        {
            get { return sel; }
            set { if (value >= 0 && value < colors.Length && sel != value) { sel = value; Invalidate(); if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty); } }
        }
        public Color SelectedColor { get { return sel >= 0 && sel < colors.Length ? colors[sel] : Color.Black; } }

        void Snap() { if (sel >= colors.Length) sel = colors.Length > 0 ? 0 : -1; }

        public ColorPicker()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = 34;
            Cursor = Cursors.Hand;
            TabStop = false;
        }

        int CellW() { return colors.Length == 0 ? 0 : (Width - (colors.Length - 1) * 8) / colors.Length; }
        int IndexAt(int x)
        {
            int w = CellW();
            if (w <= 0) return -1;
            int i = x / (w + 8);
            return (i >= 0 && i < colors.Length) ? i : -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = IndexAt(e.X);
            if (i != hoverIdx) { hoverIdx = i; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { hoverIdx = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { int i = IndexAt(e.X); if (i >= 0) SelectedIndex = i; }
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int w = CellW();
            if (w <= 0) return;
            for (int i = 0; i < colors.Length; i++)
            {
                int x = i * (w + 8);
                Rectangle r = new Rectangle(x, 2, w, Height - 6);
                Theme.FillRound(g, r, Theme.Radius, colors[i]);
                bool on = i == sel;
                Theme.StrokeRound(g, r, Theme.Radius,
                    on ? Theme.Accent : (i == hoverIdx ? Theme.BorderDeep : Theme.Border), on ? 2f : 1f);
                if (on)
                {
                    // 勾：深色底画白勾、浅色底画深勾
                    int lum = (colors[i].R * 299 + colors[i].G * 587 + colors[i].B * 114) / 1000;
                    Color tick = lum > 160 ? Color.FromArgb(60, 65, 75) : Color.White;
                    using (Pen p = new Pen(tick, 1.8f))
                    {
                        int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                        g.DrawLines(p, new PointF[] {
                            new PointF(cx - 5, cy), new PointF(cx - 1.5f, cy + 3.5f), new PointF(cx + 5, cy - 3.5f) });
                    }
                }
            }
        }
    }

    // ---------------- 扁平下拉（字体选择）----------------
    // 原生 ComboBox 的边框与下拉箭头跟自绘界面格格不入；这里自绘按钮 + 无边框弹出列表。
    public class FlatDropDown : Control
    {
        string[] items = new string[0];
        int sel = -1, hoverIdx = -1;
        bool open, hover;
        DropList popup;
        public event EventHandler SelectedChanged;
        public delegate Font ItemFontDelegate(string item);
        public ItemFontDelegate ItemFont;        // 列表项用自己的字体渲染（字体选择器用）

        public string[] Items { get { return items; } set { items = value ?? new string[0]; Invalidate(); } }
        public int SelectedIndex
        {
            get { return sel; }
            set
            {
                if (value >= -1 && value < items.Length && sel != value)
                {
                    sel = value; Invalidate();
                    if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty);
                }
            }
        }
        public string SelectedText { get { return sel >= 0 && sel < items.Length ? items[sel] : ""; } }

        public FlatDropDown()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = 32;
            Cursor = Cursors.Hand;
            TabStop = false;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && Enabled && !open) ShowList();
            base.OnMouseDown(e);
        }

        void ShowList()
        {
            if (items.Length == 0) return;
            open = true;
            Invalidate();
            popup = new DropList(this, items, sel, ItemFont);
            popup.Closed += delegate
            {
                open = false;
                if (popup != null) { int p = popup.Picked; popup = null; if (p >= 0) SelectedIndex = p; }
                Invalidate();
            };
            Point sp = PointToScreen(new Point(0, Height + 2));
            popup.ShowAt(sp, Width, Math.Min(320, 22 * Math.Min(items.Length, 14) + 6));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Theme.FillRound(g, r, Theme.Radius, Enabled ? Theme.FieldBg : Theme.DisabledBg);
            Theme.StrokeRound(g, r, Theme.Radius, open ? Theme.Accent : (hover ? Theme.BorderDeep : Theme.Border), open ? 1.6f : 1f);
            Color fg = Enabled ? Theme.Text : Theme.DisabledText;
            string t = SelectedText;
            Theme.DrawText(g, t, Theme.F12, new Rectangle(10, 0, Width - 36, Height), fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            Icons.Draw(g, "chevronDown", new Rectangle(Width - 22, Height / 2 - 7, 14, 14), fg, 1.5f);
        }
    }

    // 下拉列表的弹出窗：无边框 Form + 自绘 ListBox
    public class DropList : Form
    {
        public int Picked = -1;
        ListBox list;

        public DropList(Control owner, string[] items, int sel, FlatDropDown.ItemFontDelegate itemFont)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Panel;
            SetStyle(ControlStyles.ResizeRedraw, true);

            list = new ListBox();
            list.BorderStyle = BorderStyle.None;
            list.DrawMode = DrawMode.OwnerDrawFixed;
            list.ItemHeight = 22;
            list.Font = Theme.F12;
            list.BackColor = Theme.Panel;
            list.IntegralHeight = false;
            list.Items.AddRange(items);
            if (sel >= 0 && sel < items.Length) list.SelectedIndex = sel;
            list.DrawItem += delegate(object s, DrawItemEventArgs e)
            {
                if (e.Index < 0) return;
                bool on = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
                using (SolidBrush b = new SolidBrush(on ? Theme.AccentSoft : Theme.Panel))
                    e.Graphics.FillRectangle(b, e.Bounds);
                string it = (string)list.Items[e.Index];
                Font f = Theme.F12;
                if (itemFont != null) { try { f = itemFont(it); } catch { f = Theme.F12; } }
                Color c = on ? Theme.Accent : Theme.Text;
                Theme.DrawText(e.Graphics, it, f,
                    new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height), c,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                if (itemFont != null && !ReferenceEquals(f, Theme.F12)) f.Dispose();
            };
            list.DoubleClick += delegate { Accept(); };
            list.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { Accept(); e.Handled = true; }
                else if (e.KeyCode == Keys.Escape) { Close(); }
            };
            list.Click += delegate { Accept(); };
            Controls.Add(list);
            Deactivate += delegate { Close(); };
        }

        public void ShowAt(Point screenPos, int width, int height)
        {
            Bounds = new Rectangle(screenPos.X, screenPos.Y, width, height);
            // ★ 圆角 Region（UI 统一）：下拉窗体四角不再露方形底色
            int r = Theme.Radius;
            using (System.Drawing.Drawing2D.GraphicsPath gp = new System.Drawing.Drawing2D.GraphicsPath())
            {
                Rectangle b = new Rectangle(0, 0, width - 1, height - 1);
                gp.AddArc(b.X, b.Y, 2 * r, 2 * r, 180, 90);
                gp.AddArc(b.Right - 2 * r, b.Y, 2 * r, 2 * r, 270, 90);
                gp.AddArc(b.Right - 2 * r, b.Bottom - 2 * r, 2 * r, 2 * r, 0, 90);
                gp.AddArc(b.X, b.Bottom - 2 * r, 2 * r, 2 * r, 90, 90);
                gp.CloseFigure();
                Region = new Region(gp);
            }
            Show();
            list.Bounds = new Rectangle(1, 1, width - 2, height - 2);
            list.Focus();
        }

        void Accept()
        {
            Picked = list.SelectedIndex;
            Close();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.StrokeRound(e.Graphics, new Rectangle(0, 0, Width - 1, Height - 1), Theme.Radius, Theme.BorderDeep, 1f);
        }
    }

    // ---------------- 细滚动条（检查器长页面用）----------------
    // 原来页面比可视区高就"看不出来下面还有内容"，用户根本不知道要滚。
    public class FlatScroll : Control
    {
        int val, max, page = 1;
        bool drag;
        int grabY, grabVal;
        Rectangle thumb = Rectangle.Empty;
        public event EventHandler Scrolled;

        public int Value { get { return val; } set { int v = value; if (v < 0) v = 0; if (v > max) v = max; if (v != val) { val = v; Invalidate(); Raise(); } } }
        public int Max { get { return max; } set { max = Math.Max(0, value); if (val > max) val = max; Invalidate(); } }
        public int PageSize { get { return page; } set { page = Math.Max(1, value); Invalidate(); } }
        public bool Active { get { return max > 0; } }

        void Raise() { if (Scrolled != null) Scrolled(this, EventArgs.Empty); }

        public FlatScroll()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Width = 10;
            Cursor = Cursors.Hand;
            TabStop = false;
        }

        int ThumbH()
        {
            int content = page + max;
            if (content <= 0) return Height;
            int h = (int)((double)Height * page / content);
            return Math.Max(36, Math.Min(Height, h));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || max <= 0) { base.OnMouseDown(e); return; }
            if (thumb.Contains(e.X, e.Y)) { drag = true; grabY = e.Y; grabVal = val; }
            else Value = val + (e.Y < thumb.Y ? -page : page);    // 点空白 = 翻页
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (drag && max > 0)
            {
                int span = Height - ThumbH();
                if (span > 0)
                {
                    double f = (e.Y - grabY) / (double)span;
                    Value = grabVal + (int)Math.Round(f * max);
                }
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e) { drag = false; base.OnMouseUp(e); }

        // 滚动条自身的滚轮（鼠标正好停在细条上时也能滚 —— 以前只有拖拽和翻页两种手势）
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (max <= 0) return;
            Value = val - Math.Sign(e.Delta) * Math.Max(40, page / 4);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (max <= 0) { thumb = Rectangle.Empty; return; }
            // 轨道（浅）：让"这里有滚动条、下面还有内容"一眼看得出
            Theme.FillRound(g, new Rectangle(3, 2, Width - 6, Height - 4), (Width - 6) / 2, Theme.TrackBg);
            int th = ThumbH();
            int span = Height - th;
            int ty = span <= 0 ? 0 : (int)Math.Round((double)span * val / max);
            thumb = new Rectangle(3, ty, Width - 6, th);
            Theme.FillRound(g, thumb, (Width - 6) / 2, drag ? Theme.Accent : Theme.BorderDeep);
        }
    }

    // ---------------- 通用绘制助手 ----------------
    static class Ui
    {
        // 卡片：白底 + 细描边 + 圆角
        public static void Card(Graphics g, Rectangle r)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.FillRound(g, r, Theme.RadiusCard, Theme.Panel);
            Theme.StrokeRound(g, r, Theme.RadiusCard, Theme.Border, 1f);
        }

        // 区块标题（12px 加粗）+ 可选说明（11px 次级色），返回占用高度
        public static int Section(Graphics g, string title, string sub, int x, int y, int w)
        {
            int h = 0;
            if (title != null && title.Length > 0)
            {
                Theme.TextLeft(g, title, Theme.F12B, x, y, w, Theme.Text);
                h += 20;
            }
            if (sub != null && sub.Length > 0)
            {
                Theme.TextLeft(g, sub, Theme.F11, x, y + h, w, Theme.TextMuted);
                h += 18;
            }
            return h;
        }

        // 小徽标（如"已编辑"）
        public static void Badge(Graphics g, string text, int x, int y, Color bg, Color fg)
        {
            Size s = TextRenderer.MeasureText(text, Theme.F11, new Size(200, 20), TextFormatFlags.NoPadding);
            Rectangle r = new Rectangle(x, y, s.Width + 12, 18);
            Theme.FillRound(g, r, 9, bg);
            Theme.DrawText(g, text, Theme.F11, r, fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }
}
