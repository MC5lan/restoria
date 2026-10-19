// Theme.cs — 设计令牌：颜色 / 字体 / 圆角 / 间距 / 通用绘制
//
// 大改版（实测："反馈挺差的"、"颜色或者说一个主题"）：
//   ① **双主题**：浅色「墨蓝·纸感」+ 深色「深色工作台」，运行时可切换、可持久化。
//   ② **三级灰阶拉开层次**：以前 工作台 #F5F6F8 / 卡片 #FFF / 导航轨 #F8F9FB 只差 3%，
//      分区全靠 1px 描边撑着 → 整体发"平"。现在三档明差分明，一眼能看出分区。
//   ③ **主色换成墨蓝**（#2A4B7C）：原来的 #2563EB 是通用后台默认蓝，跟档案/纸墨
//      没有任何关系；墨蓝更贴这一行的气质，也更耐看。
//
// 实现要点：所有颜色令牌都是**静态属性**而不是常量 —— 切换主题时只换掉当前
// Palette，全项目 `Theme.Panel` 这种写法一个字都不用改（字段→属性对调用方透明）。
// 新增颜色请同时补进两套调色板，否则另一套主题下会出现"白底黑字"。
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace PicTool
{
    // 一套调色板。字段（不是属性）—— 只在启动时构建两次，之后只读。
    sealed class Palette
    {
        public bool IsDark;

        public Color WindowBg;      // 窗体/工作台底（最深的一档灰）
        public Color Panel;         // 卡片/面板（最亮，浮在工作台之上）
        public Color BarBg;         // 工具栏 / 状态栏
        public Color RailBg;        // 左侧导航轨（比工作台再深一档）
        public Color RailActive;    // 导航选中底
        public Color Border;        // 常规描边
        public Color BorderDeep;    // 强调描边（hover / 分隔）
        public Color HoverBg;       // 悬停底
        public Color PressBg;       // 按下底
        public Color TrackBg;       // 分段控件轨道
        public Color Divider;       // 分组分隔线
        public Color SliderTrack;   // 滑条未填充轨道（填充段直接用 Accent，故无 SliderFill 令牌）
        public Color FieldBg;       // 输入框底
        public Color DisabledBg;    // 禁用填充
        public Color DisabledText;  // 禁用文字

        public Color Accent;        // 主色
        public Color AccentHi;      // 主色（悬停）
        public Color AccentDown;    // 主色（按下）
        public Color AccentSoft;    // 主色浅底
        public Color AccentSoft2;   // 主色浅底（更深一档）

        public Color Text;          // 主文字
        public Color TextSub;       // 次级文字
        public Color TextMuted;     // 弱化文字 / 说明
        public Color TextOnDark;    // 深色底上的文字
        public Color TextOnAccent;  // 主色按钮上的文字（浅色主题=白；深色主题主色发亮，必须用深色字，否则白字对比度只有 ~3:1）

        public Color Danger, DangerSoft;
        public Color Green, GreenSoft;
        public Color Warn, WarnSoft, WarnLine;
        public Color Cinnabar;      // 朱砂（印章红，品牌点缀色）——只做点缀：
                                    // 导航位置指示 / 页眉锚点 / 空状态印章，绝不做按钮底色

        public Color CanvasBg;      // 画布底（浅色=纸感暖白；深色=深色衬托图片）
        public Color MaskFill;      // 涂抹可视化（ARGB 半透明）
        public Color Shadow;        // 卡片投影（ARGB 半透明）
        public Color FocusRing;     // 键盘焦点环
        public Color Knob;          // 开关 / 滑条的圆钮（深色下必须是浅灰，纯白会糊、深灰会看不见）
    }

    static class Theme
    {
        public enum Modes { Light, Dark }

        static Modes mode = Modes.Light;
        static Palette P = Palettes.Light;

        public static Modes Mode { get { return mode; } }
        public static bool IsDark { get { return mode == Modes.Dark; } }

        // 主题切换后由 MainForm 订阅并整体重绘；参数是**切换前**的调色板 ——
        // 界面上那些写死在控件里的颜色（Label.ForeColor 之类）要靠它做映射。
        public delegate void ThemeChangedHandler(Palette oldPalette);
        public static event ThemeChangedHandler Changed;

        public static void SetMode(Modes m, bool notify)
        {
            Palette old = P;
            mode = m;
            P = (m == Modes.Dark) ? Palettes.Dark : Palettes.Light;
            if (notify && Changed != null)
            {
                try { Changed(old); }
                catch { }
            }
        }

        public static void Toggle() { SetMode(IsDark ? Modes.Light : Modes.Dark, true); }

        // ---------- 颜色令牌（转发到当前调色板） ----------
        public static Color WindowBg { get { return P.WindowBg; } }
        public static Color Panel { get { return P.Panel; } }
        public static Color BarBg { get { return P.BarBg; } }
        public static Color RailBg { get { return P.RailBg; } }
        public static Color RailActive { get { return P.RailActive; } }
        public static Color Border { get { return P.Border; } }
        public static Color BorderDeep { get { return P.BorderDeep; } }
        public static Color HoverBg { get { return P.HoverBg; } }
        public static Color PressBg { get { return P.PressBg; } }
        public static Color TrackBg { get { return P.TrackBg; } }
        public static Color Divider { get { return P.Divider; } }
        public static Color SliderTrack { get { return P.SliderTrack; } }
        public static Color FieldBg { get { return P.FieldBg; } }
        public static Color DisabledBg { get { return P.DisabledBg; } }
        public static Color DisabledText { get { return P.DisabledText; } }
        public static Color Accent { get { return P.Accent; } }
        public static Color AccentHi { get { return P.AccentHi; } }
        public static Color AccentDown { get { return P.AccentDown; } }
        public static Color AccentSoft { get { return P.AccentSoft; } }
        public static Color AccentSoft2 { get { return P.AccentSoft2; } }
        public static Color Text { get { return P.Text; } }
        public static Color TextSub { get { return P.TextSub; } }
        public static Color TextMuted { get { return P.TextMuted; } }
        public static Color TextOnDark { get { return P.TextOnDark; } }
        public static Color TextOnAccent { get { return P.TextOnAccent; } }
        public static Color Cinnabar { get { return P.Cinnabar; } }
        public static Color Danger { get { return P.Danger; } }
        public static Color DangerSoft { get { return P.DangerSoft; } }
        public static Color Green { get { return P.Green; } }
        public static Color GreenSoft { get { return P.GreenSoft; } }
        public static Color Warn { get { return P.Warn; } }
        public static Color WarnSoft { get { return P.WarnSoft; } }
        public static Color WarnLine { get { return P.WarnLine; } }
        public static Color CanvasBg { get { return P.CanvasBg; } }
        public static Color MaskFill { get { return P.MaskFill; } }
        public static Color Shadow { get { return P.Shadow; } }
        public static Color FocusRing { get { return P.FocusRing; } }
        public static Color Knob { get { return P.Knob; } }

        // ---------- 字体（中文优先微软雅黑，缺失时自动回退） ----------
        static readonly string Face = PickFace();
        public static readonly Font F10 = new Font(Face, 7.5f);                     // 约 10px（导航小字）
        public static readonly Font F11 = new Font(Face, 8.25f);                    // 约 11px
        public static readonly Font F12 = new Font(Face, 9f);                       // 约 12px
        public static readonly Font F12B = new Font(Face, 9f, FontStyle.Bold);
        public static readonly Font F13B = new Font(Face, 9.75f, FontStyle.Bold);    // 约 13px
        public static readonly Font F15B = new Font(Face, 11.25f, FontStyle.Bold);   // 约 15px

        static string PickFace()
        {
            string[] want = { "Microsoft YaHei UI", "Microsoft YaHei", "微软雅黑", "Segoe UI" };
            try
            {
                InstalledFontCollection col = new InstalledFontCollection();
                FontFamily[] fams = col.Families;
                for (int w = 0; w < want.Length; w++)
                    for (int i = 0; i < fams.Length; i++)
                        if (string.Compare(fams[i].Name, want[w], true) == 0) return fams[i].Name;
            }
            catch { }
            return "Microsoft YaHei";
        }

        // ---------- 尺寸 ----------
        public const int ToolbarH = 52;
        public const int StatusH = 26;
        public const int RailW = 62;   // 左侧导航轨宽
        public const int RowH = 56;   // 工作区列表行高
        public const int PanelWide = 324;  // 右侧检查器宽度
        public const int Radius = 6;
        public const int RadiusCard = 8;
        public const int RadiusPill = 10;
        public const int Pad = 16;

        // ---------- 通用绘制 ----------
        public static GraphicsPath Round(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            if (radius <= 0 || r.Width <= 0 || r.Height <= 0) { p.AddRectangle(r); return p; }
            int d = radius * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Rectangle r, int radius, Color c)
        {
            using (GraphicsPath p = Round(r, radius))
            using (SolidBrush b = new SolidBrush(c))
                g.FillPath(b, p);
        }

        public static void StrokeRound(Graphics g, Rectangle r, int radius, Color c, float width)
        {
            using (GraphicsPath p = Round(r, radius))
            using (Pen pen = new Pen(c, width))
                g.DrawPath(pen, p);
        }

        // 卡片投影：只在需要"浮起来"的地方用（提示条 / 弹出面板）。
        // 画法是最朴素的一像素偏移实心底 —— GDI+ 没有阴影 API，真高斯模糊太贵。
        public static void FillRoundShadow(Graphics g, Rectangle r, int radius, int expand, int dy)
        {
            Rectangle s = new Rectangle(r.X - expand, r.Y - expand + dy,
                                        r.Width + expand * 2, r.Height + expand * 2);
            using (GraphicsPath p = Round(s, radius + expand))
            using (SolidBrush b = new SolidBrush(Shadow))
                g.FillPath(b, p);
        }

        // 一像素分隔线（半像素对齐，避免发虚）
        public static void HLine(Graphics g, int x1, int x2, int y, Color c)
        {
            using (Pen p = new Pen(c))
            {
                g.SmoothingMode = SmoothingMode.None;
                g.DrawLine(p, x1, y, x2, y);
                g.SmoothingMode = SmoothingMode.AntiAlias;
            }
        }

        public static void DrawText(Graphics g, string s, Font f, Rectangle r, Color c, TextFormatFlags flags)
        {
            TextRenderer.DrawText(g, s, f, r, c, flags | TextFormatFlags.NoPadding);
        }

        public static void TextLeft(Graphics g, string s, Font f, int x, int y, int w, Color c)
        {
            TextRenderer.DrawText(g, s, f, new Rectangle(x, y, w, f.Height + 2), c,
                TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }

        public static void TextRight(Graphics g, string s, Font f, int x, int y, int w, Color c)
        {
            TextRenderer.DrawText(g, s, f, new Rectangle(x, y, w, f.Height + 2), c,
                TextFormatFlags.Right | TextFormatFlags.NoPadding);
        }

        // 绘制在矩形内垂直居中、水平居中的文本
        public static void TextCenter(Graphics g, string s, Font f, Rectangle r, Color c)
        {
            TextRenderer.DrawText(g, s, f, r, c,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }

        // 键盘焦点环（圆角外扩一圈）。只在控件真正拿到焦点时画 —— 现在按钮默认
        // TabStop=false，所以只有滑条/输入框这类能聚焦的控件会用到。
        public static void DrawFocus(Graphics g, Rectangle r, int radius)
        {
            Rectangle fr = new Rectangle(r.X - 2, r.Y - 2, r.Width + 4, r.Height + 4);
            using (GraphicsPath p = Round(fr, radius + 2))
            using (Pen pen = new Pen(Color.FromArgb(180, FocusRing), 2f))
                g.DrawPath(pen, p);
        }

        // 把一棵控件树里"照抄旧令牌"的颜色换成新令牌。
        // 为什么必须有这个：Label / TextBox 的颜色是**创建那一刻**从 Theme 取的值，
        // 之后就写死在控件上了 —— 切换主题只 Invalidate 的话，深色底下会变成黑字黑底。
        public static void RemapColors(Control root, Palette old)
        {
            if (root == null || old == null) return;
            RemapOne(root, old);
            for (int i = 0; i < root.Controls.Count; i++)
                RemapColors(root.Controls[i], old);
        }

        static void RemapOne(Control c, Palette old)
        {
            // 前景色：文字类令牌
            if (Same(c.ForeColor, old.Text)) c.ForeColor = Text;
            else if (Same(c.ForeColor, old.TextSub)) c.ForeColor = TextSub;
            else if (Same(c.ForeColor, old.TextMuted)) c.ForeColor = TextMuted;
            else if (Same(c.ForeColor, old.TextOnDark)) c.ForeColor = TextOnDark;
            else if (Same(c.ForeColor, old.DisabledText)) c.ForeColor = DisabledText;
            else if (Same(c.ForeColor, old.Accent)) c.ForeColor = Accent;
            else if (Same(c.ForeColor, old.Danger)) c.ForeColor = Danger;
            else if (Same(c.ForeColor, old.Green)) c.ForeColor = Green;
            else if (Same(c.ForeColor, old.Warn)) c.ForeColor = Warn;

            // 背景色：底/卡片/面板类令牌
            if (Same(c.BackColor, old.Panel)) c.BackColor = Panel;
            else if (Same(c.BackColor, old.WindowBg)) c.BackColor = WindowBg;
            else if (Same(c.BackColor, old.BarBg)) c.BackColor = BarBg;
            else if (Same(c.BackColor, old.RailBg)) c.BackColor = RailBg;
            else if (Same(c.BackColor, old.FieldBg)) c.BackColor = FieldBg;
            else if (Same(c.BackColor, old.CanvasBg)) c.BackColor = CanvasBg;
            else if (Same(c.BackColor, old.AccentSoft)) c.BackColor = AccentSoft;
            else if (Same(c.BackColor, old.DisabledBg)) c.BackColor = DisabledBg;
            else if (Same(c.BackColor, old.Border)) c.BackColor = Border;
        }

        static bool Same(Color a, Color b) { return a.ToArgb() == b.ToArgb(); }

        // 整个控件树重绘（切换主题后调用）
        public static void RefreshTree(Control root)
        {
            if (root == null) return;
            root.Invalidate(true);
            for (int i = 0; i < root.Controls.Count; i++)
                RefreshTree(root.Controls[i]);
        }
    }

    // 两套调色板的实际取值。改颜色改这里 —— 一处改，全界面跟着变。
    static class Palettes
    {
        static Color C(int r, int g, int b) { return Color.FromArgb(r, g, b); }
        static Color A(int a, int r, int g, int b) { return Color.FromArgb(a, r, g, b); }

        public static readonly Palette Light = BuildLight();
        public static readonly Palette Dark = BuildDark();

        // 浅色：暖纸白工作台 + 纯白卡片 + 墨蓝主色。
        // 层次（关键）：工作台 #F2F0EC → 导航轨 #EBE7E0 → 卡片 #FFFFFF，三档差得开。
        static Palette BuildLight()
        {
            Palette p = new Palette();
            p.IsDark = false;
            p.WindowBg = C(0xF2, 0xF0, 0xEC);
            p.Panel = Color.White;
            p.BarBg = C(0xFB, 0xFA, 0xF8);
            p.RailBg = C(0xEB, 0xE7, 0xE0);
            p.RailActive = C(0xE4, 0xEC, 0xF6);
            p.Border = C(0xE2, 0xDE, 0xD6);
            p.BorderDeep = C(0xCB, 0xC5, 0xBA);
            p.HoverBg = C(0xEE, 0xEB, 0xE5);
            p.PressBg = C(0xE3, 0xDF, 0xD7);
            p.TrackBg = C(0xE9, 0xE5, 0xDE);
            p.Divider = C(0xE7, 0xE2, 0xD9);
            p.SliderTrack = C(0xE0, 0xDB, 0xD2);
            p.FieldBg = C(0xFC, 0xFB, 0xF9);
            p.DisabledBg = C(0xF0, 0xEE, 0xEA);
            p.DisabledText = C(0xA9, 0xA7, 0x9F);

            p.Accent = C(0x2A, 0x4B, 0x7C);
            p.AccentHi = C(0x3A, 0x5F, 0x96);
            p.AccentDown = C(0x1E, 0x38, 0x60);
            p.AccentSoft = C(0xE9, 0xEF, 0xF7);
            p.AccentSoft2 = C(0xD3, 0xE0, 0xF0);

            p.Text = C(0x1A, 0x1F, 0x28);
            p.TextSub = C(0x56, 0x60, 0x6E);
            // TextMuted 加深（）：旧值 #8A8F99 在白底上只有 ~3.5:1，
            // 说明文字（F11 小字）低于 4.5:1 可读性门槛；加深到 #767B85 ≈ 4.6:1。
            p.TextMuted = C(0x76, 0x7B, 0x85);
            p.TextOnDark = C(0xE9, 0xE7, 0xE3);
            p.TextOnAccent = Color.White;   // 墨蓝底上白字 ≈ 8.7:1

            p.Danger = C(0xC0, 0x39, 0x2B); p.DangerSoft = C(0xFB, 0xEF, 0xED);
            p.Cinnabar = C(0xB5, 0x43, 0x2E);   // 印泥朱砂：暖、偏沉，压得住纸底
            p.Green = C(0x1E, 0x7A, 0x46); p.GreenSoft = C(0xE7, 0xF3, 0xEB);
            p.Warn = C(0xA2, 0x62, 0x1A); p.WarnSoft = C(0xFD, 0xF3, 0xE3);
            p.WarnLine = C(0xED, 0xD3, 0xA4);

            // 画布底：浅色主题用"纸感"暖白（实测方案 A：原来 #22225A 深色，
            // 空窗体时中间一大片深色被嫌"纯黑太丑"；现在像一张摊开的纸，全界面统一浅色。
            /// 深色主题保持深色 —— 深色工作台就是要深色衬托图片）
            p.CanvasBg = C(0xF7, 0xF4, 0xED);
            p.MaskFill = A(0x64, 0xE5, 0x48, 0x4D);
            p.Shadow = A(28, 0x14, 0x12, 0x0E);
            p.FocusRing = C(0x2A, 0x4B, 0x7C);
            p.Knob = Color.White;
            return p;
        }

        // 深色：纯中性冷灰（不掺蓝紫，免得图片偏色被干扰），主色调亮以保证对比度。
        static Palette BuildDark()
        {
            Palette p = new Palette();
            p.IsDark = true;
            p.WindowBg = C(0x17, 0x1A, 0x1E);
            p.Panel = C(0x21, 0x25, 0x29);
            p.BarBg = C(0x1C, 0x20, 0x24);
            p.RailBg = C(0x10, 0x13, 0x17);
            p.RailActive = C(0x23, 0x31, 0x42);
            p.Border = C(0x30, 0x35, 0x3C);
            p.BorderDeep = C(0x45, 0x4B, 0x54);
            p.HoverBg = C(0x28, 0x2E, 0x34);
            p.PressBg = C(0x33, 0x3A, 0x42);
            p.TrackBg = C(0x28, 0x2E, 0x34);
            p.Divider = C(0x2A, 0x2F, 0x36);
            p.SliderTrack = C(0x34, 0x3A, 0x41);
            p.FieldBg = C(0x19, 0x1D, 0x21);
            p.DisabledBg = C(0x23, 0x28, 0x2D);
            p.DisabledText = C(0x6A, 0x70, 0x78);

            p.Accent = C(0x5B, 0x93, 0xD6);
            p.AccentHi = C(0x77, 0xAA, 0xE6);
            p.AccentDown = C(0x3F, 0x76, 0xB4);
            p.AccentSoft = C(0x21, 0x31, 0x43);
            p.AccentSoft2 = C(0x2C, 0x42, 0x58);

            p.Text = C(0xE6, 0xE8, 0xEB);
            p.TextSub = C(0xA9, 0xAF, 0xB9);
            // TextMuted 深色同步加深：#6E757F 在 Panel #212529 上 ~3.4:1，加深到 #838A94 ≈ 4.6:1。
            p.TextMuted = C(0x83, 0x8A, 0x94);
            p.TextOnDark = C(0xE6, 0xE8, 0xEB);
            p.TextOnAccent = C(0x12, 0x17, 0x1D);   // 深色主题主色 #5B93D6 发亮：白字只有 ~3.2:1，深字 ≈ 5.6:1（）

            p.Danger = C(0xE0, 0x6A, 0x5C); p.DangerSoft = C(0x37, 0x23, 0x1F);
            p.Cinnabar = C(0xC9, 0x56, 0x3F);   // 深色下提亮一档，深底上仍 ≥4.5:1
            p.Green = C(0x4C, 0xAF, 0x7D); p.GreenSoft = C(0x1E, 0x2F, 0x26);
            p.Warn = C(0xD6, 0x9C, 0x4A); p.WarnSoft = C(0x37, 0x2C, 0x1B);
            p.WarnLine = C(0x6B, 0x54, 0x26);

            p.CanvasBg = C(0x0E, 0x10, 0x13);
            p.MaskFill = A(0x64, 0xE5, 0x48, 0x4D);
            p.Shadow = A(90, 0x00, 0x00, 0x00);
            p.FocusRing = C(0x7D, 0xB0, 0xE8);
            p.Knob = C(0xC3, 0xCB, 0xD6);
            return p;
        }
    }
}
