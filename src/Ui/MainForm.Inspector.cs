// MainForm.Inspector.cs — 主窗体（partial）：检查器 + 页面高度与滚动辅助
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PicTool
{
    public partial class MainForm : Form
    {
        // ---------------- 右侧检查器 ----------------

        void BuildInspector(Panel right)
        {
            lblDocName = new Label();
            lblDocName.Font = Theme.F13B;
            lblDocName.ForeColor = Theme.Text;
            lblDocName.AutoSize = false;
            lblDocName.AutoEllipsis = true;
            lblDocName.Bounds = new Rectangle(Theme.Pad, 14, right.Width - Theme.Pad * 2 - 16, 22);
            lblDocName.Text = "未打开图片";
            right.Controls.Add(lblDocName);

            lblDocMeta = new Label();
            lblDocMeta.Font = Theme.F11;
            lblDocMeta.ForeColor = Theme.TextMuted;
            lblDocMeta.AutoSize = false;
            lblDocMeta.AutoEllipsis = true;
            lblDocMeta.Bounds = new Rectangle(Theme.Pad, 36, right.Width - Theme.Pad * 2 - 16, 18);
            right.Controls.Add(lblDocMeta);

            // 已编辑指示：小绿点（与左侧工作区列表一致）
            dotEdited = new FlatDot();
            dotEdited.Bounds = new Rectangle(right.Width - Theme.Pad - 9, 20, 9, 9);
            dotEdited.Visible = false;
            right.Controls.Add(dotEdited);
            dotEdited.BringToFront();
            tips.SetToolTip(dotEdited, "已编辑（尚未保存）");

            content = new Panel();
            content.Bounds = new Rectangle(0, 66, right.Width, right.Height - 66);
            content.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            content.BackColor = Theme.Panel;
            content.Paint += delegate(object s, PaintEventArgs e)
            {
                Theme.HLine(e.Graphics, 0, content.Width, 0, Theme.Border);
            };
            right.Controls.Add(content);

            // 这里只是「构建顺序」（决定控件 z 序），**不等于导航序号** —— 切页映射
            // 以 MainForm.UiKit.cs 的 SwitchSection 为准（当前 9 页：
            // 去除 / 裁剪 / 方向 / 调色 / 印章 / 文字 / 扫描 / 文件 / 归档，归档=Ctrl+9）。
            // 保持原构建顺序不动（改顺序会动 z 序，导致布局被无谓地重排）。
            BuildRemovePage();    // 去除页（手工去污）                          → pnlRemove
            BuildCropPage();      // 裁剪页（裁剪 + 透视 + A4 画板）              → pnlCrop
            BuildRotatePage();    // 方向页（旋转 / 纠偏 / 镜像）                 → pnlRotate
            BuildColorPage();     // 调色页（亮度/对比/饱和/色温 + 色阶）         → pnlColor
            BuildSealPage();      // 印章页                                      → pnlSeal
            BuildBatchPage();     // 归档页（容器名 pnlBatch 保留，一键归档在此） → pnlBatch
            BuildScanPage();      // 扫描页                                      → pnlScan
            BuildFilePage();      // 文件页（批量重命名 / 新建文件夹）            → pnlFiles
            BuildTextPage();      // 文字页                                      → pnlText

            // 细滚动条：页面比可视区高时出现（原来没有任何提示，用户以为"下面没东西了"）。
            // 放在所有页面创建之后再加，并置顶，避免被 NewPage 加进来的页面盖住。
            // 注意用显式摆位而不是 Dock：Dock=Right 在 content 尺寸还没定下来时会把高度算成 0。
            panelScroll = new FlatScroll();
            panelScroll.Visible = false;
            panelScroll.Scrolled += delegate
            {
                if (syncingScroll) return;          // 防回环：同步滚动条时会再设一次 Value
                contentScroll = panelScroll.Value;
                ApplyContentScroll();
            };
            content.Controls.Add(panelScroll);
            panelScroll.BringToFront();
            // 窗口尺寸变化后 content 才有真实高度：重算一次，否则滚动条停在 0 高度
            content.Resize += delegate { ApplyContentScroll(); };

            SelectSection(0);
            ApplyTool();          // 初始化画布工具状态
        }

        Panel NewPage()
        {
            Panel p = new Panel();
            p.Bounds = new Rectangle(0, 0, content.Width, content.Height);
            p.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            p.BackColor = Theme.Panel;
            p.Visible = false;
            content.Controls.Add(p);
            return p;
        }

        // 记录该页的自然高度（超出可视区域时用滚轮滚动查看）
        public void PageH(Panel p, int h) { p.Tag = h; }

        // 页面里"可见控件"的最大底部（有隐藏按钮时页面自然高度按它算）
        int VisibleBottom(Panel host)
        {
            int bottom = 0;
            for (int i = 0; i < host.Controls.Count; i++)
            {
                Control c = host.Controls[i];
                if (!c.Visible) continue;
                if (c.Bottom > bottom) bottom = c.Bottom;
            }
            return bottom;
        }

        // 页面构建收尾：按可见控件算自然高度（有折叠组时 y 会把收起部分的空白也算进去）
        public void PageEnd(Panel host)
        {
            PageH(host, VisibleBottom(host) + 14);
        }

        int ContentMax()
        {
            Panel p = VisiblePage();
            if (p == null) return 0;
            // 🔴 自然高**必须每次现算**（用 --wheeltest 定位的根因）：构建期窗体还没显示，
            //   父链不可见 → WinForms 子控件的 Visible **全部返回 false** →
            //   PageEnd 当时存进 Tag 的自然高全是 0+14=14，于是 ContentMax 恒为 0 ——
            //   右栏的滚动和滚动条**从来就没工作过**。窗体显示之后子控件 Visible 才可信，
            //   而且页内按钮/进度条随时在动态显隐（归档跑起来 progress 出现等），所以以
            //   "每次用时重算"为准，顺带把结果写回 Tag 供别处参考。
            int natural = VisibleBottom(p) + 14;
            p.Tag = natural;
            return Math.Max(0, natural - content.Height);
        }

        Panel VisiblePage()
        {
            // 9 页：去除 / 裁剪 / 方向 / 调色 / 印章 / 文字 / 扫描 / 文件 / 归档
            //（一键归档在「归档」页 pnlBatch，不在「去除」页）
            if (pnlRemove != null && pnlRemove.Visible) return pnlRemove;
            if (pnlCrop != null && pnlCrop.Visible) return pnlCrop;
            if (pnlRotate != null && pnlRotate.Visible) return pnlRotate;
            if (pnlColor != null && pnlColor.Visible) return pnlColor;
            if (pnlSeal != null && pnlSeal.Visible) return pnlSeal;
            if (pnlBatch != null && pnlBatch.Visible) return pnlBatch;
            if (pnlScan != null && pnlScan.Visible) return pnlScan;
            if (pnlFiles != null && pnlFiles.Visible) return pnlFiles;
            if (pnlText != null && pnlText.Visible) return pnlText;
            return null;
        }

        void ApplyContentScroll()
        {
            Panel p = VisiblePage();
            if (p == null) return;
            int max = ContentMax();
            if (contentScroll > max) contentScroll = max;
            if (contentScroll < 0) contentScroll = 0;
            p.Top = -contentScroll;
            p.Height = Math.Max(content.Height, p.Tag != null ? (int)p.Tag : content.Height);
            // 滚动条只有 10px，页面右侧本来就有 16px 内边距：直接叠在上面，不改页宽
            //（改页宽会和 Anchor Right 打架）
            SyncPanelScroll(max);
        }

        // 同步检查器滚动条（长页面才有；顺带告诉用户"下面还有内容"）
        void SyncPanelScroll(int max)
        {
            if (panelScroll == null) return;
            Rectangle want = new Rectangle(content.Width - 10, 0, 10, content.Height);   // 显式摆位，别依赖 Dock
            if (panelScroll.Bounds != want) panelScroll.Bounds = want;
            if (max <= 0)
            {
                if (panelScroll.Visible) { panelScroll.Visible = false; panelScroll.SendToBack(); }
                return;
            }
            syncingScroll = true;
            try
            {
                panelScroll.PageSize = content.Height;
                panelScroll.Max = max;
                panelScroll.Value = contentScroll;
            }
            finally { syncingScroll = false; }
            if (!panelScroll.Visible) { panelScroll.Visible = true; panelScroll.BringToFront(); }
        }

    }
}
