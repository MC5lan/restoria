// MainForm.Page.FilePage.cs - 主窗体（partial）：文件页（批量重命名/建目录）
// P5 第三刀：页面类化——控件与构建逻辑自治（与 BatchPage 同模式）；
// 业务方法（RenamePlan/DoRename/UndoRename/DoMkFolders）引用主窗体状态密集，
// 留在本文件下半段的窗体侧 partial，控件经别名属性访问，外部引用零改动。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace PicTool
{
    public partial class MainForm
    {
        void BuildFilePage()
        {
            pageFiles = new FilePage(this);         // 构造器内完成 NewPage 的等价注册
            pnlFiles = pageFiles;                   // 切页/滚动机制仍通过 pnlFiles 走
            pageFiles.Build();
        }

        // 文件页：一个 Panel 子类，自己管自己的控件。要加控件改这里，不要回主窗体。
        public sealed class FilePage : Panel
        {
            readonly IPageHost _mf;

            internal Segmented segRenameMode;
            internal Label lblRenamePrefix, lblRenameStart, lblFind, lblReplace;
            internal FlatTextBox txtRenamePrefix, txtRenameStart, txtFind, txtReplace;
            internal Label lblRenamePreview;
            internal FlatButton btnRenameRun, btnRenameUndo;
            internal Label lblRenameLast;
            internal Label lblMkBase, lblMkCount;
            internal FlatTextBox txtMkBase, txtMkCount;
            internal FlatButton btnMkRun; internal Label lblMkResult;

            internal FilePage(IPageHost mf)
            {
                _mf = mf;
                // 与 NewPage() 相同的页面注册（Bounds/锚定/背景/默认隐藏/挂进内容区）
                Bounds = new Rectangle(0, 0, _mf.content.Width, _mf.content.Height);
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
                BackColor = Theme.Panel;
                Visible = false;
                _mf.content.Controls.Add(this);
            }

            internal void Build()
            {
                int x = Theme.Pad, y = 12;
                y = _mf.PageHead(this, "文件", "批量重命名图片、批量新建文件夹", x, y, _mf.Cw);
                y = BuildFilesControls(y);
                _mf.PageEnd(this);
            }

            int BuildFilesControls(int y)
            {
                int x = Theme.Pad;
                y += 24;

                _mf.SubLabel(this, "批量重命名（当前文件夹的图片）", x, y, _mf.Cw);
                y += 22;
                segRenameMode = new Segmented();
                segRenameMode.Items = new string[] { "编号重命名", "查找替换" };
                segRenameMode.Bounds = new Rectangle(x, y, _mf.Cw, 32);
                segRenameMode.SelectedChanged += delegate { _mf.UpdateRenameRows(); _mf.UpdateRenamePreview(); };
                Controls.Add(segRenameMode);
                y += 40;

                // 编号模式
                lblRenamePrefix = _mf.Lbl(this, "文件名前缀", x, y, _mf.Cw - 96);
                txtRenamePrefix = Input(this, x + _mf.Cw - 96, y - 3, 96);
                txtRenamePrefix.Text = "扫描";
                y += 27;
                lblRenameStart = _mf.Lbl(this, "起始编号", x, y, _mf.Cw - 96);
                txtRenameStart = Input(this, x + _mf.Cw - 96, y - 3, 96);
                txtRenameStart.Text = "1";
                y += 27;

                // 替换模式（默认隐藏）
                lblFind = _mf.Lbl(this, "查找（文件名里的文字）", x, y, _mf.Cw);
                y += 21;
                txtFind = Input(this, x, y, _mf.Cw);
                y += 27;
                lblReplace = _mf.Lbl(this, "替换为", x, y, _mf.Cw);
                y += 21;
                txtReplace = Input(this, x, y, _mf.Cw);
                y += 27;
                _mf.UpdateRenameRows();

                txtRenamePrefix.TextChanged += delegate { _mf.UpdateRenamePreview(); };
                txtRenameStart.TextChanged += delegate { _mf.UpdateRenamePreview(); };
                txtFind.TextChanged += delegate { _mf.UpdateRenamePreview(); };
                txtReplace.TextChanged += delegate { _mf.UpdateRenamePreview(); };

                _mf.Lbl(this, "预览", x, y, _mf.Cw);   // 预览框高度在下方设（已压矮）
                y += 20;
                // 预览区：给一个卡片底 + 内嵌文本，避免原来那一大块灰底空方块
                Panel prevCard = new Panel();
                prevCard.Bounds = new Rectangle(x, y, _mf.Cw, 72);   // 96 → 72：把"批量新建文件夹"拉进一屏
                prevCard.BackColor = Theme.Panel;
                prevCard.Paint += delegate(object s, PaintEventArgs e)
                {
                    Ui.Card(e.Graphics, new Rectangle(0, 0, prevCard.Width - 1, prevCard.Height - 1));
                };
                Controls.Add(prevCard);
                lblRenamePreview = new Label();
                lblRenamePreview.AutoSize = false;
                lblRenamePreview.ForeColor = Theme.TextSub;
                lblRenamePreview.Font = Theme.F11;
                lblRenamePreview.BackColor = Theme.Panel;
                lblRenamePreview.Bounds = new Rectangle(9, 7, _mf.Cw - 20, 58);
                prevCard.Controls.Add(lblRenamePreview);
                y += 102;

                btnRenameRun = _mf.Btn(this, "预览并重命名", x, y, (_mf.Cw - 8) / 2, 38, "save", FlatButton.Kinds.Primary, delegate { _mf.DoRename(); });
                btnRenameUndo = _mf.Btn(this, "撤销上次", x + (_mf.Cw - 8) / 2 + 8, y, (_mf.Cw - 8) / 2, 38, "undo", FlatButton.Kinds.Ghost, delegate { _mf.UndoRename(); });
                btnRenameUndo.Enabled = false;
                y += 44;
                lblRenameLast = new Label();
                lblRenameLast.AutoSize = false;
                lblRenameLast.ForeColor = Theme.TextMuted;
                lblRenameLast.Font = Theme.F11;
                lblRenameLast.Bounds = new Rectangle(x, y, _mf.Cw, 18);
                lblRenameLast.Text = "";
                Controls.Add(lblRenameLast);
                y += 26;

                _mf.Divider(this, x, y, _mf.Cw);
                y += 12;
                _mf.SubLabel(this, "批量新建文件夹（当前文件夹下）", x, y, _mf.Cw);
                y += 24;
                lblMkBase = _mf.Lbl(this, "文件夹基础名", x, y, _mf.Cw - 96);
                txtMkBase = Input(this, x + _mf.Cw - 96, y - 3, 96);
                txtMkBase.Text = "档案";
                y += 27;
                lblMkCount = _mf.Lbl(this, "数量（如 10 → 档案01…档案10）", x, y, _mf.Cw - 96);
                txtMkCount = Input(this, x + _mf.Cw - 96, y - 3, 96);
                txtMkCount.Text = "10";
                y += 30;
                btnMkRun = _mf.Btn(this, "创建文件夹", x, y, _mf.Cw, 36, "folder", FlatButton.Kinds.Ghost, delegate { _mf.DoMkFolders(); });
                y += 42;
                lblMkResult = new Label();
                lblMkResult.AutoSize = false;
                lblMkResult.ForeColor = Theme.TextSub;
                lblMkResult.Font = Theme.F11;
                lblMkResult.Bounds = new Rectangle(x, y, _mf.Cw, 18);
                lblMkResult.Text = "";
                Controls.Add(lblMkResult);
                y += 26;
                _mf.UpdateRenamePreview();
                return y;
            }

            // 扁平输入框（原来是原生 FixedSingle 灰边方框，和整体界面完全不是一个风格）
            FlatTextBox Input(Panel host, int x, int y, int w)
            {
                FlatTextBox t = new FlatTextBox();
                t.Bounds = new Rectangle(x, y, w, 26);
                host.Controls.Add(t);
                return t;
            }
        }

        // ================= 文件页逻辑（窗体侧，引用主窗体状态密集故不进类）=================

        public void UpdateRenameRows()
        {
            bool numbered = segRenameMode.SelectedIndex == 0;
            lblRenamePrefix.Visible = numbered; txtRenamePrefix.Visible = numbered;
            lblRenameStart.Visible = numbered; txtRenameStart.Visible = numbered;
            lblFind.Visible = !numbered; txtFind.Visible = !numbered;
            lblReplace.Visible = !numbered; txtReplace.Visible = !numbered;
        }

        // 当前的重命名计划（预览与执行共用同一份规则，保证“看到的”就是“要做的”）
        List<RenameOp> RenamePlan()
        {
            if (currentFolder == null) return null;
            string[] files;
            try { files = BatchProcessor.ListImages(currentFolder, MAX_FILES); }
            catch { return null; }
            if (files.Length == 0) return null;
            if (segRenameMode.SelectedIndex == 0)
            {
                int start;
                if (!int.TryParse(txtRenameStart.Text.Trim(), out start) || start < 0) return null;
                return FileTools.PlanNumbered(files, txtRenamePrefix.Text.Trim(), start);
            }
            if (txtFind.Text.Length == 0) return null;
            return FileTools.PlanReplace(files, txtFind.Text, txtReplace.Text);
        }

        public void UpdateRenamePreview()
        {
            if (lblRenamePreview == null) return;
            if (currentFolder == null) { lblRenamePreview.Text = "（先打开一个文件夹）"; return; }
            List<RenameOp> plan = RenamePlan();
            if (plan == null || plan.Count == 0)
            {
                lblRenamePreview.Text = segRenameMode.SelectedIndex == 1 && txtFind.Text.Length == 0
                    ? "（填好“查找”后这里会显示改名结果）"
                    : "（当前文件夹里没有可改名的图片）";
                return;
            }
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            int shown = Math.Min(6, plan.Count);
            for (int i = 0; i < shown; i++)
                sb.AppendLine(Path.GetFileName(plan[i].OldPath) + "  →  " + Path.GetFileName(plan[i].NewPath));
            if (plan.Count > shown) sb.AppendLine("……共 " + plan.Count + " 项");
            lblRenamePreview.Text = sb.ToString();
        }

        public void DoRename()
        {
            if (busy) return;
            if (currentFolder == null) { Status("请先打开一个文件夹"); return; }
            List<RenameOp> plan = RenamePlan();
            if (plan == null || plan.Count == 0)
            {
                Status(segRenameMode.SelectedIndex == 0 ? "编号要填非负数字" : "没有文件名包含要查找的文字");
                return;
            }

            // 确认框列出全部改名结果（用户拍板 B：信息一条不砍）——
            // 项目自绘 ConfirmBox + 可滚动正文 + 按条数自适应高度（替换原生 MessageBox）
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("将对 " + plan.Count + " 个文件执行重命名：\n");
            for (int i = 0; i < plan.Count; i++)
                sb.AppendLine(Path.GetFileName(plan[i].OldPath) + "  →  " + Path.GetFileName(plan[i].NewPath));
            int dlgH = Math.Min(760, 210 + Math.Max(0, plan.Count - 5) * 15);
            using (ConfirmBox cb = new ConfirmBox("确认批量重命名", sb.ToString(), "确认重命名", dlgH))
            {
                if (cb.ShowDialog(this) != DialogResult.OK) return;
            }

            RenameResult r = FileTools.Apply(plan);
            lastRenameOps = r.Done.Count > 0 ? r.Done : null;
            btnRenameUndo.Enabled = lastRenameOps != null;
            lblRenameLast.Text = lastRenameOps != null ? "刚重命名了 " + r.Done.Count + " 个文件（可撤销）" : "";
            if (r.Errors.Count > 0)
            {
                string log = Path.Combine(currentFolder, "重命名日志.txt");
                try { File.WriteAllLines(log, r.Errors.ToArray(), System.Text.Encoding.UTF8); } catch { }
                Msg("完成 " + r.Ok + " 个，跳过 " + r.Skip + " 个。\n\n跳过原因见：" + log);
            }
            else Status("重命名完成：" + r.Ok + " 个文件");
            if (r.Ok > 0) BuildWorkspace(currentFolder, false);
            else UpdateRenamePreview();
        }

        public void UndoRename()
        {
            if (busy) return;
            if (lastRenameOps == null || lastRenameOps.Count == 0)
            {
                Status("没有可撤销的重命名（只记最近一次，换过文件夹后失效）");
                return;
            }
            int ok = 0, fail = 0;
            for (int i = lastRenameOps.Count - 1; i >= 0; i--)
            {
                try
                {
                    if (File.Exists(lastRenameOps[i].OldPath))
                    {
                        File.Move(lastRenameOps[i].OldPath, lastRenameOps[i].NewPath);
                        ok++;
                    }
                    else fail++;
                }
                catch { fail++; }
            }
            lastRenameOps = null;
            btnRenameUndo.Enabled = false;
            lblRenameLast.Text = "";
            Status("已撤销 " + ok + " 个" + (fail > 0 ? "（" + fail + " 个失败）" : ""));
            if (ok > 0 && currentFolder != null) BuildWorkspace(currentFolder, false);
        }

        public void DoMkFolders()
        {
            if (busy) return;
            if (currentFolder == null) { Status("请先打开一个文件夹"); return; }
            int n;
            if (!int.TryParse(txtMkCount.Text.Trim(), out n)) { Msg("数量要填数字（1 ~ 500）。"); return; }
            try
            {
                int created = FileTools.CreateNumberedFolders(currentFolder, txtMkBase.Text.Trim(), n);
                lblMkResult.Text = "已创建 " + created + " 个文件夹。";
                Status("已创建 " + created + " 个文件夹");
                if (created > 0) BuildWorkspace(currentFolder, false);
            }
            catch (Exception ex) { Msg(ex.Message); }
        }
    }
}
