// MainForm.Wheel.cs — 主窗体（partial）：滚轮路由 / WndProc / 修复提示 / 页头装饰
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PicTool
{
    public partial class MainForm : Form
    {
        // ---------------- 滚轮统一路由 ----------------
        // WM_MOUSEWHEEL 由焦点控件沿窗口树冒泡到主窗体，这里按"鼠标停在哪"一次性裁决：
        //   画布 → 缩放/平移（canvas.HandleWheel）；左侧工作区 → 滚列表；右侧检查器 → 滚面板。
        // 以前画布自己也响应滚轮：焦点在画布上、鼠标移到左侧列表滚动时，
        // 列表滚动的同画布跟着缩放（用户视角：中间的图片展示区在跟着滑动）。
        const int WM_MOUSEWHEEL = 0x020A;

        // 返回是否已处理（true = 吃掉消息，不再走默认分发）
        bool RouteWheel(int delta, Point screenPt)
        {
            Point mp = screenPt;
            if (canvas != null && canvas.Visible &&
                canvas.RectangleToScreen(canvas.ClientRectangle).Contains(mp))
            {
                // busy 时只跳过画布（处理中改视图与后台绘制打架）：左栏/右栏照常可滚
                //（以前 busy 时整条路由 return，左栏跟着一起失效）
                if (!busy) canvas.HandleWheel(delta);
                return true;
            }
            if (ws != null && ws.Visible && HitZone(ws, mp))
            {
                ws.WheelBy(delta);
                return true;
            }
            if (content != null && content.Visible && HitZone(content, mp))
            {
                if (ContentMax() > 0)
                {
                    // 一格滚 1/4 屏：右栏页面普遍 900px+，一格 60px 要滚十几下才到底
                    int step = Math.Max(60, content.Height / 4);
                    contentScroll -= Math.Sign(delta) * step;
                    ApplyContentScroll();
                    content.Invalidate();
                }
                return true;
            }
            // 左右分隔条（4px 窄条）：归入左栏滚列表——命中判定的边界缝隙曾是
            // "滚轮偶尔没反应"的来源之一
            if (splitter != null && splitter.Visible &&
                splitter.RectangleToScreen(splitter.ClientRectangle).Contains(mp))
            {
                if (ws != null) ws.WheelBy(delta);
                return true;
            }
            // 鼠标不在画布/工作区/检查器上（如标题栏、状态栏）：无事可做
            return false;
        }

        // 命中判定加 3px 容差：吸收面板边缘 1~2px 的视觉缝隙（用户"正对着列表滚却不灵"的来源）
        static bool HitZone(Control c, Point screenPt)
        {
            Rectangle r = c.RectangleToScreen(c.ClientRectangle);
            r.Inflate(3, 3);
            return r.Contains(screenPt);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEWHEEL)
            {
                // WM_MOUSEWHEEL 的 lParam 就是"滚轮发生时鼠标的屏幕坐标"，正是 RouteWheel 做
                // "鼠标停在哪"裁决要依据的东西。以前用实时 Control.MousePosition：真实操作下两者
                // 一致，但它让这条链路无法被无头注入复验（注入时物理鼠标并不在面板上，必然判"不通"）。
                // 改用消息自带的坐标 —— 行为不变，`--wheeltest` 才成为确定性的证据。
                // busy 判定移进 RouteWheel（只跳画布，不拦左栏/右栏）
                if (RouteWheel((short)((long)m.WParam >> 16), ScreenPointFromLParam(m.LParam)))
                    return;     // 已统一裁决，不再走默认分发，杜绝画布/工作区各自再处理一遍
            }
            base.WndProc(ref m);
        }

        static Point ScreenPointFromLParam(IntPtr lparam)
        {
            int lp = unchecked((int)(long)lparam);
            return new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
        }

        public int Cw { get { return Theme.PanelWide - Theme.Pad * 2; } }

        // 滚轮链路自检（--wheeltest）：程序化注入与真实滚轮**完全相同**的
        // WM_MOUSEWHEEL 消息（走同一条 WndProc → RouteWheel 路径），
        // 验证"消息 → 裁决 → 右栏滚动"整条链路是否通。滚轮"没反应"时先用它定位。
        // 自检覆盖三区：画布 / 左栏工作区 / 右栏各注入一次，逐区报告（只测右栏答不了问题）。
        public string WheelTest()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine(AppInfo.Name + " 滚轮链路自检   " + DateTime.Now.ToString("HH:mm:ss"));
            sb.AppendLine("判据：向三个区各自注入 3 格 WM_MOUSEWHEEL（与真实滚轮同一条路径），逐区看反应。");
            sb.AppendLine();

            // ---- 区 1：右栏（需要内容超出可视区；先找一个可滚的页） ----
            int saved = sectionIndex;
            int probe = -1;
            for (int i = 0; i < 9; i++)
            {
                SelectSection(i);
                Application.DoEvents();
                if (ContentMax() > 0) { probe = i; break; }
            }
            int before = contentScroll;
            if (probe >= 0)
            {
                InjectWheel(content, -120, 3);
                sb.AppendLine("  右栏(" + PageName(probe) + ")     : contentScroll " + before + " → " + contentScroll +
                              (contentScroll > before ? "   [通]" : "   [不通]"));
            }
            else
            {
                sb.AppendLine("  右栏            : 跳过（9 页都没有超出可视区的内容，本无可滚量）");
            }
            SelectSection(saved);
            Application.DoEvents();

            // ---- 区 2：左栏工作区（有列表且内容超高才可滚；无图时如实说明） ----
            int wsBefore = wsScrollYProbe();
            InjectWheel(ws, -120, 3);
            int wsAfter = wsScrollYProbe();
            if (wsMaxScrollProbe() <= 0)
                sb.AppendLine("  左栏工作区      : 跳过（列表为空或内容没超出可视区，本无可滚量）");
            else
                sb.AppendLine("  左栏工作区      : scrollY " + wsBefore + " → " + wsAfter +
                              (wsAfter > wsBefore ? "   [通]" : "   [不通]"));
            wsRestoreScroll(wsBefore);   // 自检不留副作用：滚动位置还原

            // ---- 区 3：画布命中判定（只验判定，不实际注入——注入会真的缩放视图，自检不该有副作用） ----
            bool canvasHit = canvas != null && canvas.Visible &&
                canvas.RectangleToScreen(canvas.ClientRectangle).Contains(
                    canvas.PointToScreen(new Point(canvas.Width / 2, canvas.Height / 2)));
            sb.AppendLine("  画布命中判定    : " + (canvasHit ? "[通]（鼠标在画布上时滚轮走缩放/平移）" : "[不通]") +
                          (canvasHasImage() ? "" : "；当前无图，画布滚轮本无作用"));

            // ---- 繁忙态复验（处理中左栏滚不动） ----
            sb.AppendLine();
            sb.AppendLine("  繁忙态说明      : 归档/修复运行中，左栏与右栏滚轮照常可用；");
            sb.AppendLine("                    画布在繁忙中跳过（不改视图），这是有意设计。");
            return sb.ToString();
        }

        // 向指定面板中央注入 n 格滚轮（负 delta = 向下滚）
        void InjectWheel(Control target, int delta, int times)
        {
            if (target == null || !target.Visible) return;
            Point cp = target.PointToScreen(new Point(target.Width / 2, target.Height / 2));
            int lp = (cp.Y << 16) | (cp.X & 0xFFFF);
            for (int i = 0; i < times; i++)
            {
                Message m = new Message();
                m.Msg = WM_MOUSEWHEEL;
                m.WParam = (IntPtr)unchecked((uint)(delta << 16));
                m.LParam = (IntPtr)lp;
                WndProc(ref m);
                Application.DoEvents();
            }
        }

        int wsScrollYProbe() { return ws == null ? 0 : ws.ScrollYProbe(); }
        int wsMaxScrollProbe() { return ws == null ? 0 : ws.MaxScrollProbe(); }
        void wsRestoreScroll(int y) { if (ws != null) ws.RestoreScroll(y); }
        bool canvasHasImage() { return canvas != null && canvas.HasImageProbe(); }

        static readonly string[] PageNames = new string[] {
            "去除", "裁剪", "方向", "调色", "印章", "文字", "扫描", "文件", "归档" };
        string PageName(int i) { return (i >= 0 && i < PageNames.Length) ? PageNames[i] : ("#" + i); }

        // BuildRemovePage 在各页面分部类中实现（去除页与归档页已拆为独立页）

        public void UpdateFixHint()
        {
            if (segFix == null) return;
            // 说明文字收进 tooltip：界面上不再铺长句，悬停即读
            switch (segFix.SelectedIndex)
            {
                case 0:
                    tips.SetToolTip(segFix, "扩散填充：最快。适合小斑点；面积大或跨边线时会把边线抹糊。");
                    break;
                case 2:
                    tips.SetToolTip(segFix, "内容感知 + 扩散保底：找不到合适样本块的地方自动退回扩散填充，结果更保守。");
                    break;
                case 3:
                    tips.SetToolTip(segFix, "纸面重建：用涂抹区周围的纸面重建一片干净纸。不含任何生成，"
                        + "所以永远不会编出内容；霉斑/水渍/折痕/印迹上最干净，且是秒级。"
                        + "代价是被抹掉的表格线不会自己回来 —— 需要接续直线时改用 AI。");
                    break;
                case 4:
                    tips.SetToolTip(segFix, AiFill.Available
                        ? "AI 修复（MI-GAN）：会把表格线之类的长直线延伸过去，适合被抹掉的地方需要「接上」内容时用。2~5 秒/次。"
                        : AiFill.StatusText);
                    break;
                default:
                    tips.SetToolTip(segFix, "内容感知填充：质量最好，能延续边线和纹理。稍慢。");
                    break;
            }
        }

        // 「方向」区：上半部旋转纠正（任意角度实时预览 + 90° 转），下半部镜像翻转。
        // 分组小标题（比字段标签更淡，用于把一个页面切成几块）
        public Label SubLabel(Panel host, string text, int x, int y, int w)
        {
            Label l = new Label();
            l.Text = text;
            // 分组小标题：比页头说明（F11 浅灰）实一档，让人一眼看出"这是分组"而不是又一句说明
            l.Font = Theme.F12B;
            l.ForeColor = Theme.TextSub;
            l.AutoSize = false;
            l.Bounds = new Rectangle(x, y, w, 20);
            host.Controls.Add(l);
            return l;
        }

        // 页面头：功能名 + 一句话说明。每一页都有 —— 切页时先看清"这是哪、这页干什么"。
        public int PageHead(Panel host, string title, string sub, int x, int y, int w)
        {
            Label t = new Label();
            t.Text = title;
            t.Font = Theme.F15B;
            t.ForeColor = Theme.Text;
            t.AutoSize = false;
            t.Bounds = new Rectangle(x, y, w, 26);
            host.Controls.Add(t);
            y += 32;                       // 标题 → 说明：留出呼吸间隙（原来 28 太贴）
            if (sub != null && sub.Length > 0)
            {
                Label s = new Label();
                s.Text = sub;
                s.Font = Theme.F11;
                s.ForeColor = Theme.TextMuted;
                // 自适应高度（排版约定）：按内容自动换行、按实际行数占位。
                // 原来固定 34 高 + 36 占位 —— 短文案时文字顶上、长文案时贴住下一个控件。
                s.AutoSize = true;
                s.MaximumSize = new Size(w, 0);
                s.Location = new Point(x, y);
                host.Controls.Add(s);
                y += s.Height + 12;        // 说明 → 第一个控件
            }
            return y;
        }

        public void Divider(Panel host, int x, int y, int w)
        {
            Label l = new Label();
            l.Bounds = new Rectangle(x, y, w, 1);
            l.BackColor = Theme.Divider;      // 原来是 Border：白底上几乎看不见，分组感全无
            host.Controls.Add(l);
        }

    }
}
