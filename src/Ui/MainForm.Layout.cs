// MainForm.Layout.cs — 主窗体（partial）：界面构建：BuildUi / 分区标题 / 顶部工具栏
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PicTool
{
    public partial class MainForm : Form
    {
        // ================= 界面构建 =================

        void BuildUi()
        {
            // 主题必须在建任何控件之前定下来：Label / 面板的颜色是**创建那一刻**
            //   从 Theme 取的值，之后再切主题要靠 RemapColors 追一遍（见 ApplyTheme）。
            UiSettings.Load();
            Theme.SetMode(UiSettings.Dark ? Theme.Modes.Dark : Theme.Modes.Light, false);
            Theme.Changed += ApplyTheme;

            // 停靠顺序很重要：后加入者先占位 -> 最后加入的工具栏/状态栏占满整宽
            tips = new ToolTip();
            tips.ShowAlways = true;

            canvas = new CanvasControl();
            canvas.Dock = DockStyle.Fill;
            canvas.StrokeHandler = StrokeTo;
            canvas.RectHandler = RectToMask;
            canvas.TextPlaceHandler = TextPlaceAt;      // 文字工具：空白处单击新建
            canvas.TextsCommitted = OnTextsCommitted;   // 拖动/缩放/输入结束的统一提交点
            canvas.SelChanged = delegate { RefreshTextInspector(); };
            canvas.BrushRadiusAdjusted += delegate(float r) { sBrush.ResetValue(r); vBrush.Text = ((int)r).ToString(); };
            // 缩放到下限（整页适合窗口）时的提示：不给提示用户会以为滚轮坏了
            canvas.ZoomLimitHit += delegate { Status("已缩到最小（整页适合窗口）——再缩就看不清了"); };
            // 拖放不在这里单独挂：统一走下面 BuildUi 末尾的 EnableDrop（以前挂在这却忘了
            // canvas.AllowDrop=true，事件永远不触发 —— 见 EnableDrop 注释）

            splitter = new FlatSplitter();
            splitter.Dock = DockStyle.Left;
            splitter.Visible = false;   // 目标面板稍后绑定
            splitter.ToggleRequested += delegate { ToggleWorkspace(); };

            // 画布浮动工具胶囊：笔刷 / 框选 / 橡皮（替代散在各个页里的"工具"分段）

            ws = new WorkspacePanel();
            ws.Dock = DockStyle.Left;
            ws.Width = lastWsWidth;
            ws.OpenPath += delegate(string p) { OpenFolderPath(p); };
            ws.ImageClicked += delegate(WsItem it) { if (it.DocIndex >= 0) SelectDoc(it.DocIndex); };
            ws.FolderEntered += delegate(WsItem it) { OpenFolderPath(it.FullPath); };
            ws.ChooseFolder += delegate { OpenFolder(); };
            ws.RefreshClicked += delegate { if (currentFolder != null) BuildWorkspace(currentFolder, true); };
            ws.RowMenu += delegate(int idx, Point screenPt) { ShowRowMenu(idx, screenPt); };

            splitter.Target = ws;
            splitter.Visible = true;

            // 左侧导航轨：9 个功能入口（文字上到第 6 位、归档页挪到最后——
            // 去除页只管去污，批量化操作集中收尾），激活指示条带滑动动画。
            navRail = new NavRail(
                new string[] { "eraser", "crop", "rotate", "palette", "stamp", "text", "scan", "folder", "wand" },
                new string[] { "去除", "裁剪", "方向", "调色", "印章", "文字", "扫描", "文件", "归档" },
                new string[] { "去除污点、划痕（Ctrl+1）", "裁剪框选 / 透视 / A4 画板（Ctrl+2）",
                               "旋转 90°/任意角度 / 自动纠偏 / 镜像（Ctrl+3）",
                               "亮度对比 / 色阶 / 锐化（Ctrl+4）", "印章识别与上色（Ctrl+5）",
                               "添加 / 编辑文字（Ctrl+6）",
                               "扫描仪取图（Ctrl+7）", "批量重命名 / 新建文件夹（Ctrl+8）",
                               "一键归档：普通 / 极致两档，整个文件夹自动走完整流水线（Ctrl+9）" });
            navRail.Dock = DockStyle.Left;
            navRail.Width = Theme.RailW;
            navRail.SelectedIndexChanged += RailChanged;
            btnKeys = new FlatButton();
            btnKeys.Icon = "keyboard"; btnKeys.IconOnly = true; btnKeys.Kind = FlatButton.Kinds.Subtle;
            btnKeys.Size = new Size(40, 36);
            btnKeys.Click += delegate { OpenShortcutSettings(); };
            tips.SetToolTip(btnKeys, "快捷键设置（可随意改成自己顺手的键）");
            navRail.Footer = btnKeys;

            Panel right = new Panel();
            right.Dock = DockStyle.Right;
            right.Width = Theme.PanelWide;
            right.BackColor = Theme.Panel;
            right.Paint += delegate(object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(Theme.Border))
                    e.Graphics.DrawLine(p, 0, 0, 0, right.Height);
            };

            Panel status = new Panel();
            status.Dock = DockStyle.Bottom;
            status.Height = Theme.StatusH;
            status.BackColor = Theme.BarBg;
            status.Paint += delegate(object s, PaintEventArgs e)
            {
                Theme.HLine(e.Graphics, 0, status.Width, 0, Theme.Border);
            };
            lblInfo = new Label();
            lblInfo.ForeColor = Theme.TextMuted;
            lblInfo.Font = Theme.F11;
            lblInfo.AutoSize = false;
            lblInfo.AutoEllipsis = true;
            lblInfo.Bounds = new Rectangle(16, 6, 520, 18);
            status.Controls.Add(lblInfo);
            lblStatus = new Label();
            lblStatus.ForeColor = Theme.TextSub;
            lblStatus.Font = Theme.F11;
            lblStatus.AutoSize = false;
            lblStatus.TextAlign = ContentAlignment.MiddleRight;
            lblStatus.Bounds = new Rectangle(status.Width - 480, 6, 464, 18);
            lblStatus.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            lblStatus.Text = "就绪";
            status.Controls.Add(lblStatus);

            Panel toolbar = new Panel();
            toolbar.Dock = DockStyle.Top;
            toolbar.Height = Theme.ToolbarH;
            toolbar.BackColor = Theme.BarBg;
            toolbar.Paint += delegate(object s, PaintEventArgs e)
            {
                Theme.HLine(e.Graphics, 0, toolbar.Width, toolbar.Height - 1, Theme.Border);
            };

            // 加入控件树（顺序决定停靠层级）
            Controls.Add(canvas);
            Controls.Add(splitter);
            Controls.Add(ws);
            Controls.Add(navRail);
            Controls.Add(right);
            Controls.Add(status);
            Controls.Add(toolbar);

            BuildToolbar(toolbar);
            BuildInspector(right);

            // 拖放接收统一开口：WinForms 的拖放事件**不冒泡**，而且按"鼠标下最深的那层控件"裁决 ——
            // 只有那一层自己 AllowDrop=true 且挂了自己的 DragEnter/DragDrop 才收得到。
            // 第一版只开了 7 个大容器，--droptest 立刻抓出漏网：拖到右栏命中的是它
            // 里面的页面 Panel、拖到工具栏命中的是按钮 —— 容器开了照样被拒。
            // 所以这里对整棵控件树递归开口（TextBox 除外：保持系统默认，不抢
            // 输入框的文本拖放语义），全部转 HandleDrop（文件夹→打开，图片→打开单张）。
            EnableDropDeep(this);
        }

        // 递归给一棵控件树开拖放接收（含 root 自身——主窗体原来只有 AllowDrop 没挂事件，
        // 裸区拖放显示禁止）。重复调用会双挂事件，只调一次。
        void EnableDropDeep(Control root)
        {
            if (root == null) return;
            if (!(root is TextBox))
            {
                root.AllowDrop = true;
                root.DragEnter += delegate(object s, DragEventArgs e)
                {
                    if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
                };
                root.DragDrop += delegate(object s, DragEventArgs e) { HandleDrop(e.Data); };
            }
            foreach (Control c in root.Controls) EnableDropDeep(c);
        }

        void Sep(Panel host, int x)
        {
            Panel l = new Panel();
            l.Bounds = new Rectangle(x, 14, 1, 26);
            l.BackColor = Theme.Border;
            host.Controls.Add(l);
        }

        // ---------------- 顶栏（精简：入口 / 撤销还原 / 保存方式与保存） ----------------

        void BuildToolbar(Panel bar)
        {
            int y = 10, h = 32;
            int x = 14;
            btnOpenFolder = Btn(bar, "打开文件夹", x, y, 118, h, "folder", FlatButton.Kinds.Ghost, delegate { OpenFolder(); });
            tips.SetToolTip(btnOpenFolder, "打开一个图片文件夹（也可以直接把文件夹拖进窗口）");
            x += 124;
            btnOpenFiles = Btn(bar, "打开图片", x, y, 100, h, "image", FlatButton.Kinds.Ghost, delegate { OpenFiles(); });
            tips.SetToolTip(btnOpenFiles, "打开单张或多张图片");
            x += 108;
            Sep(bar, x); x += 10;
            // 撤销 / 还原：高频操作图标化，悬停有文字提示
            btnUndo = BtnIcon(bar, "undo", x, y, delegate { DoUndo(); });
            tips.SetToolTip(btnUndo, "撤销上一步编辑（Ctrl+Z）");
            x += 34;
            btnRevert = BtnIcon(bar, "refresh", x, y, delegate { DoRevert(); });
            tips.SetToolTip(btnRevert, "恢复到刚打开这张图片时的样子");
            x += 34;

            // 工具组：笔刷 / 框选 / 橡皮
            Sep(bar, x); x += 10;
            btnToolBrush = BtnIcon(bar, "brush", x, y, delegate { SetTool(0); });
            tips.SetToolTip(btnToolBrush, "笔刷：涂抹要去除/标记的区域（1）");
            x += 30;
            btnToolRect = BtnIcon(bar, "marquee", x, y, delegate { SetTool(1); });
            tips.SetToolTip(btnToolRect, "框选：拉矩形圈出范围（2）");
            x += 30;
            btnToolErase = BtnIcon(bar, "eraser", x, y, delegate { SetTool(2); });
            tips.SetToolTip(btnToolErase, "橡皮：擦掉涂抹范围（3）");
            x += 30;
            btnToolText = BtnIcon(bar, "text", x, y, delegate { SetTool(3); });
            tips.SetToolTip(btnToolText, "文字：单击添加文字、点选拖动、双击编辑（4）");
            x += 30;
            Sep(bar, x); x += 10;
            UpdateToolButtons();

            // 缩放组：适应窗口 / 百分比 / 缩小 / 放大
            // 锚定右侧、顶在保存组之前；百分比点一下 = 回到 100%
            btnFit = new FlatButton();
            btnFit.Icon = "fit"; btnFit.IconOnly = true; btnFit.Kind = FlatButton.Kinds.Ghost;
            btnFit.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnFit.Bounds = new Rectangle(bar.Width - 588, y, 30, 32);
            btnFit.Click += delegate { canvas.Fit(); UpdateZoomLabel(); };
            bar.Controls.Add(btnFit);
            tips.SetToolTip(btnFit, "适应窗口");

            lblZoom = new Label();
            lblZoom.AutoSize = false;
            lblZoom.TextAlign = ContentAlignment.MiddleCenter;
            lblZoom.ForeColor = Theme.TextSub;
            lblZoom.Font = Theme.F12;
            lblZoom.BackColor = Theme.WindowBg;
            lblZoom.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            lblZoom.Bounds = new Rectangle(bar.Width - 558, y + 7, 44, 18);
            lblZoom.Text = "100%";
            lblZoom.Cursor = Cursors.Hand;
            lblZoom.Click += delegate { canvas.SetZoom(1.0); UpdateZoomLabel(); };
            bar.Controls.Add(lblZoom);
            tips.SetToolTip(lblZoom, "当前缩放比例；点一下回到 100%");

            btnZoomOut = new FlatButton();
            btnZoomOut.Icon = "zoomOut"; btnZoomOut.IconOnly = true; btnZoomOut.Kind = FlatButton.Kinds.Ghost;
            btnZoomOut.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnZoomOut.Bounds = new Rectangle(bar.Width - 514, y, 30, 32);
            btnZoomOut.Click += delegate { canvas.SetZoom(canvas.Zoom / 1.25); UpdateZoomLabel(); };
            bar.Controls.Add(btnZoomOut);
            tips.SetToolTip(btnZoomOut, "缩小");

            btnZoomIn = new FlatButton();
            btnZoomIn.Icon = "zoomIn"; btnZoomIn.IconOnly = true; btnZoomIn.Kind = FlatButton.Kinds.Ghost;
            btnZoomIn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnZoomIn.Bounds = new Rectangle(bar.Width - 484, y, 30, 32);
            btnZoomIn.Click += delegate { canvas.SetZoom(canvas.Zoom * 1.25); UpdateZoomLabel(); };
            bar.Controls.Add(btnZoomIn);
            tips.SetToolTip(btnZoomIn, "放大（Ctrl+滚轮可调笔刷大小）");

            // 右侧：保存方式 / 另存为 / 保存（唯一主按钮）/ 工作区开关
            segSave = new Segmented();
            segSave.Items = new string[] { "另存到 " + ArchivedDirName, "覆盖原图" };
            segSave.Bounds = new Rectangle(bar.Width - 448, y, 196, h);
            segSave.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            segSave.SelectedChanged += delegate { OnSaveModeChanged(segSave.SelectedIndex == 1, segSave); };
            bar.Controls.Add(segSave);
            tips.SetToolTip(segSave, "选择【保存】和批量处理时：输出到“" + ArchivedDirName + "”文件夹，还是直接覆盖原图");

            btnSaveAs = new FlatButton();
            btnSaveAs.Icon = "external"; btnSaveAs.IconOnly = true; btnSaveAs.Kind = FlatButton.Kinds.Ghost;
            btnSaveAs.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnSaveAs.Bounds = new Rectangle(bar.Width - 242, y, 34, h);
            btnSaveAs.Click += delegate { SaveCurrentAs(); };
            bar.Controls.Add(btnSaveAs);
            tips.SetToolTip(btnSaveAs, "另存为：保存到其它位置或其它格式");

            btnSave = Btn(bar, "保存", bar.Width - 202, y, 92, h, "save", FlatButton.Kinds.Primary, delegate { SaveCurrent(); });
            btnSave.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnSave.Radius = Theme.RadiusPill;
            tips.SetToolTip(btnSave, "保存当前图片（Ctrl+S）");

            btnWorkspace = new FlatButton();
            btnWorkspace.Icon = "layers"; btnWorkspace.IconOnly = true; btnWorkspace.Kind = FlatButton.Kinds.Ghost;
            btnWorkspace.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnWorkspace.Bounds = new Rectangle(bar.Width - 96, y, 34, h);
            btnWorkspace.Click += delegate { ToggleWorkspace(); };
            bar.Controls.Add(btnWorkspace);
            tips.SetToolTip(btnWorkspace, "显示 / 隐藏左侧工作区（双击分隔条也可以）");

            // 主题切换：翻扫描件时深色工作台更护眼、也更衬图片；
            // 浅色（墨蓝·纸感）适合白天和需要看清纸张细节的时候。点一下就换，会记住。
            btnTheme = new FlatButton();
            btnTheme.IconOnly = true;
            btnTheme.Kind = FlatButton.Kinds.Ghost;
            btnTheme.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnTheme.Bounds = new Rectangle(bar.Width - 56, y, 34, h);
            btnTheme.Click += delegate { ToggleTheme(); };
            bar.Controls.Add(btnTheme);
            UpdateThemeButton();
        }

    }
}
