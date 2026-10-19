// Toast.cs — 操作结果提示条
//
// 为什么需要这个：以前**所有**操作结果都只写底部状态栏一行 11px 灰字 ——
// 保存完了、归档完了、失败了，界面上几乎看不出变化，等于"干完活不吭声"。
// 现在成功/失败/警告/信息各有各的颜色与图标，右下角弹出、几秒后自己消失。
//
// 设计约束：
//   · 后台线程（归档/批量/扫描）也会弹提示 → Show 必须自己跨线程回到 UI 线程；
//   · 不抢焦点、不阻塞操作（不是 MessageBox）—— 提示条只是"吭一声"，点一下还能立刻关掉；
//   · WinForms 的控件没有 Opacity，所以动效只做**滑入/滑出**，不做淡入淡出
//     （GDI 的 TextRenderer 对带 alpha 的文字支持不可靠，硬做会出现文字不跟着淡的情况）。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PicTool
{
    public static class Toast
    {
        public enum Kinds { Ok, Error, Warn, Info }

        const int MaxWidth = 360;
        const int CardH = 42;
        const int Gap = 8;
        const int Edge = 18;                 // 距窗口边缘（不能叫 Margin：Control 里已有这个属性）
        const int BottomExtra = 30;      // 让开底部状态栏
        const int StayMs = 3600;         // 停留时长（文字越长停留越久）

        static readonly List<ToastCard> live = new List<ToastCard>();

        public static void Show(Control anchor, string msg, Kinds kind)
        {
            Show(anchor, msg, kind, 0);
        }

        // detail：可选的第二行小字（如"失败 3 张，详情见归档日志.txt"）
        public static void Show(Control anchor, string msg, Kinds kind, string detail)
        {
            Show(anchor, msg, kind, 0, detail);
        }

        static void Show(Control anchor, string msg, Kinds kind, int stayMs)
        {
            Show(anchor, msg, kind, stayMs, null);
        }

        static void Show(Control anchor, string msg, Kinds kind, int stayMs, string detail)
        {
            if (anchor == null || string.IsNullOrEmpty(msg)) return;
            Form host = null;
            try { host = anchor.FindForm(); } catch { }
            if (host == null) return;

            // 后台线程直接调 Show 是常态（归档/批量都在工作线程里跑），必须切回 UI 线程
            if (host.InvokeRequired)
            {
                try { host.BeginInvoke(new MethodInvoker(delegate { ShowOn(host, msg, kind, stayMs, detail); })); }
                catch { }
                return;
            }
            ShowOn(host, msg, kind, stayMs, detail);
        }

        static void ShowOn(Form host, string msg, Kinds kind, int stayMs, string detail)
        {
            try
            {
                // 同一句话还在屏幕上：不叠第二条，只把它的倒计时重置（避免刷屏）
                for (int i = live.Count - 1; i >= 0; i--)
                {
                    ToastCard c0 = live[i];
                    if (c0.Host == host && c0.Message == msg && !c0.Closing)
                    {
                        c0.Restart();
                        return;
                    }
                }
                // 最多同时 4 条，超了先挤掉最老的一条
                while (Count(host) >= 4)
                {
                    for (int i = 0; i < live.Count; i++)
                        if (live[i].Host == host) { live[i].Close(true); break; }
                }

                ToastCard c = new ToastCard(host, msg, kind, detail);
                c.StayMs = stayMs > 0 ? stayMs : Math.Min(9000, StayMs + msg.Length * 45);
                live.Add(c);
                host.Controls.Add(c);
                c.BringToFront();
                Relayout(host);
                c.PlayEnter();
            }
            catch { }
        }

        static int Count(Form host)
        {
            int n = 0;
            for (int i = 0; i < live.Count; i++) if (live[i].Host == host) n++;
            return n;
        }

        static void Relayout(Form host)
        {
            if (host == null) return;
            int bottom = host.ClientSize.Height - Edge -   BottomExtra;
            int y = bottom;
            // 从下往上排：最新的在最下面
            for (int i = live.Count - 1; i >= 0; i--)
            {
                ToastCard c = live[i];
                if (c.Host != host) continue;
                y -= c.Height;
                c.TargetY = y;
                y -= Gap;
            }
        }

        static void Remove(ToastCard c)
        {
            Form host = c.Host;
            live.Remove(c);
            if (host != null)
            {
                try { host.Controls.Remove(c); } catch { }
                Relayout(host);
            }
            try { c.Dispose(); } catch { }
        }

        // ---------------- 提示条本体 ----------------

        class ToastCard : Control
        {
            internal Form Host;
            internal string Message;
            internal bool Closing;
            internal int StayMs = StayMsDefault;
            internal int TargetY;
            internal const int StayMsDefault = 3600;

            Kinds kind;
            string detail;
            double slide = 16;              // 滑入动画：16 → 0
            Timer timer;
            EventHandler hostResize;
            bool hover;

            public ToastCard(Form host, string msg, Kinds kind, string detail)
            {
                Host = host;
                Message = msg;
                this.kind = kind;
                this.detail = detail;

                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.UserPaint | ControlStyles.ResizeRedraw |
                         ControlStyles.SupportsTransparentBackColor, true);
                Font = Theme.F12;
                Cursor = Cursors.Hand;
                TabStop = false;

                // 尺寸按文字量出来，最长不超过 MaxWidth
                Size s = TextRenderer.MeasureText(msg, Theme.F12, new Size(MaxWidth - 60, 40),
                    TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                int w = Math.Max(160, Math.Min(MaxWidth, s.Width + 56));
                int h = CardH;
                if (!string.IsNullOrEmpty(detail))
                {
                    Size sd = TextRenderer.MeasureText(detail, Theme.F11, new Size(MaxWidth - 60, 40),
                        TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                    w = Math.Max(w, Math.Min(MaxWidth, sd.Width + 56));
                    h = CardH + 20;
                }
                Size = new Size(w, h);
                Location = new Point(host.ClientSize.Width - Edge -   w, host.ClientSize.Height - Edge -   BottomExtra - h);
                SetRoundRegion();       // ★ 圆角裁剪（实测"外圈方形好廉价"）：
                                        //   卡片画的是圆角，但控件矩形是方的——四角露白一块，
                                        //   观感廉价。Region 把窗形裁成真正的圆角，四角彻底透明。

                hostResize = delegate { Relayout(Host); };
                host.Resize += hostResize;
            }

            // 圆角 Region：卡片矩形四角不再露白（Theme.RadiusCard 与 OnPaint 的圆角一致）
            void SetRoundRegion()
            {
                int r = Theme.RadiusCard;
                Rectangle b = new Rectangle(0, 0, Width, Height);
                using (System.Drawing.Drawing2D.GraphicsPath gp = new System.Drawing.Drawing2D.GraphicsPath())
                {
                    gp.AddArc(b.X, b.Y, 2 * r, 2 * r, 180, 90);
                    gp.AddArc(b.Right - 2 * r, b.Y, 2 * r, 2 * r, 270, 90);
                    gp.AddArc(b.Right - 2 * r, b.Bottom - 2 * r, 2 * r, 2 * r, 0, 90);
                    gp.AddArc(b.X, b.Bottom - 2 * r, 2 * r, 2 * r, 90, 90);
                    gp.CloseFigure();
                    Region = new Region(gp);
                }
            }

            public void PlayEnter()            {
                Anim.Run(this, 16, 0, 220, AnimEase.Out,
                    delegate(double v) { slide = v; Place(); }, null);
                timer = new Timer();
                timer.Interval = StayMs;
                timer.Tick += delegate { Close(false); };
                timer.Start();
            }

            public void Restart()
            {
                if (timer == null) return;
                timer.Stop();
                timer.Start();
                Anim.Run(this, slide, 0, 160, AnimEase.Out, delegate(double v) { slide = v; Place(); }, null);
            }

            void Place()
            {
                if (Host == null) return;
                int x = Host.ClientSize.Width - Edge -   Width;
                Left = x;
                Top = TargetY + (int)Math.Round(slide);
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                hover = true;
                // 悬停即停表（给用户读完的时间）：以前只在 MouseMove 里 Stop+Start（那是"重置倒计时"
                // 不是"暂停"），鼠标静止不动时倒计时照走到点，条照消失，"悬停暂停"名不副实。
                if (timer != null) timer.Stop();
                Invalidate();
                base.OnMouseEnter(e);
            }
            protected override void OnMouseLeave(EventArgs e)
            {
                hover = false;
                if (timer != null) timer.Start();   // 离开才继续倒计时
                Invalidate();
                base.OnMouseLeave(e);
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                // 点一下就关：提示条不该挡事
                Close(false);
                base.OnMouseDown(e);
            }
            // 计时只由 Enter/Leave 成对管理；不再 override OnMouseMove
            // （以前在里面 Stop+Start = 移动才重置，静止悬停仍会到点消失）。

            public void Close(bool now)
            {
                if (Closing) return;
                Closing = true;
                if (timer != null) { timer.Stop(); timer.Dispose(); timer = null; }
                if (now) { Remove(this); return; }
                Anim.Run(this, slide, 14, 180, AnimEase.Out,
                    delegate(double v) { slide = v; Place(); },
                    delegate { Remove(this); });
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    if (timer != null) { timer.Stop(); timer.Dispose(); timer = null; }
                    if (Host != null && hostResize != null) { try { Host.Resize -= hostResize; } catch { } }
                    // 宿主窗体关闭时子控件被整体 Dispose，不走 Remove —— 这里补摘静态引用
                    // （；正常关闭路径 Remove 已摘，这是宿主关闭的兜底）
                    try { live.Remove(this); } catch { }
                }
                base.Dispose(disposing);
            }

            Color AccentOf()
            {
                switch (kind)
                {
                    case Kinds.Ok: return Theme.Green;
                    case Kinds.Error: return Theme.Danger;
                    case Kinds.Warn: return Theme.Warn;
                    default: return Theme.Accent;
                }
            }

            string IconOf()
            {
                switch (kind)
                {
                    case Kinds.Ok: return "check";
                    case Kinds.Error: return "close";
                    case Kinds.Warn: return "warning";
                    default: return "info";
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);

                Theme.FillRoundShadow(g, r, Theme.RadiusCard, 3, 2);
                Theme.FillRound(g, r, Theme.RadiusCard, Theme.Panel);
                Theme.StrokeRound(g, r, Theme.RadiusCard,
                    hover ? Theme.BorderDeep : Theme.Border, 1f);

                // 左侧色条：扫一眼就知道是成功还是出错
                Color ac = AccentOf();
                Rectangle bar = new Rectangle(r.X + 1, r.Y + 8, 3, r.Height - 16);
                Theme.FillRound(g, bar, 2, ac);

                int ix = r.X + 14;
                Rectangle ib = new Rectangle(ix, Height / 2 - 8, 16, 16);
                Icons.Draw(g, IconOf(), ib, ac, 1.6f);

                Rectangle tb = new Rectangle(ix + 22, 0, Width - ix - 22 - 12, Height);
                if (string.IsNullOrEmpty(detail))
                {
                    Theme.DrawText(g, Message, Theme.F12, tb, Theme.Text,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                }
                else
                {
                    Rectangle t1 = new Rectangle(tb.X, 6, tb.Width, 18);
                    Rectangle t2 = new Rectangle(tb.X, 24, tb.Width, 16);
                    Theme.DrawText(g, Message, Theme.F12B, t1, Theme.Text,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                    Theme.DrawText(g, detail, Theme.F11, t2, Theme.TextMuted,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                }
            }
        }
    }
}
