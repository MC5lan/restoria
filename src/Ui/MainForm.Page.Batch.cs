// MainForm.Page.Batch.cs - 主窗体（partial）：归档页（原"批量处理"页演进）
// P5 页面演进：原批量处理（翻转/旋转/调色应用/纠偏/黑白化）
// 整组删除；一键归档区从去除页搬到这里 —— "批量化操作"集中在一页收尾。
// 容器与类名沿用 pnlBatch/BatchPage（避免牵连切页机制），页面标题与导航已改「归档」。
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
        void BuildBatchPage()
        {
            pageBatch = new BatchPage(this);        // 构造器内完成 NewPage 的等价注册
            pnlBatch = pageBatch;                   // 切页/滚动机制仍通过 pnlBatch 走
            pageBatch.Build();
        }

        // 归档页：一个 Panel 子类，自己管自己的控件。要加控件改这里，不要回主窗体。
        public sealed class BatchPage : Panel
        {
            readonly IPageHost _mf;

            // ★ 归档档位：普通 / 极致。极致 = 准确性优先，慢一点、脏点留一点
            internal Segmented segArchiveMode;
            internal FlatProgress progress;              // 归档进度
            internal Label lblBatchResult;               // 归档结果
            internal Label lblOutDir;
            internal FlatButton btnPickOut, btnOpenOut, btnArchive, btnArchiveCancel;
            internal FlatButton btnArchiveOne;           // 归档当前图片

            internal BatchPage(IPageHost mf)
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
                y = _mf.PageHead(this, "归档", "一键处理整个文件夹：方向 / 去污 / 色阶 / 二值化 / A4 全自动", x, y, _mf.Cw);

                // ---------- 一键归档 ----------
                // ★ 归档档位（"宁愿放弃消除小黑点，也要把字保留全了，
                //   我需要最大限度的准确性"）—— 极致档单独给一格，代价写在说明里。
                _mf.SubLabel(this, "一键归档（含子文件夹，走完整流水线）", x, y, _mf.Cw);
                y += 26;
                segArchiveMode = new Segmented();
                segArchiveMode.Items = new string[] { "普通归档", "极致归档" };
                segArchiveMode.Bounds = new Rectangle(x, y, _mf.Cw, 34);
                // ★ 档位记住选择：ui.ini 里存上次选的档，启动时恢复 ——
                //   之前每次启动都回普通档，偏好极致的用户得天天手动切。
                segArchiveMode.SelectedIndex = UiSettings.ArchiveThorough ? 1 : 0;
                Controls.Add(segArchiveMode);
                Label lblArchiveMode = new Label();
                lblArchiveMode.AutoSize = false;
                lblArchiveMode.ForeColor = Theme.TextMuted;
                lblArchiveMode.Font = Theme.F11;
                lblArchiveMode.Bounds = new Rectangle(x, y + 38, _mf.Cw, 34);
                lblArchiveMode.Text = "普通：速度与干净度平衡；方向只认装订孔证据。";
                Controls.Add(lblArchiveMode);
                segArchiveMode.SelectedChanged += delegate
                {
                    _mf.archiveThorough = segArchiveMode.SelectedIndex == 1;
                    UiSettings.ArchiveThorough = _mf.archiveThorough;
                    UiSettings.Save();      // 小文件整写，切换频率极低，不必防抖
                    lblArchiveMode.Text = _mf.archiveThorough
                        ? "极致：只删最确定的尘点，删过头自动撤销；无装订孔的页用 OCR 判断方向。慢一些，字更不容易掉。" +
                          (Ocr.Available ? "" : "（本机缺 OCR 素材，方向退化为只认装订孔）")
                        : "普通：速度与干净度平衡；方向只认装订孔证据。";
                };
                // 启动恢复初值时也要把说明文案对上（事件只在用户点击时触发）
                if (UiSettings.ArchiveThorough)
                {
                    _mf.archiveThorough = true;
                    lblArchiveMode.Text = "极致：只删最确定的尘点，删过头自动撤销；无装订孔的页用 OCR 判断方向。慢一些，字更不容易掉。" +
                        (Ocr.Available ? "" : "（本机缺 OCR 素材，方向退化为只认装订孔）");
                }
                y += 78;
                btnArchive = _mf.Btn(this, "归档本文件夹（全部）", x, y, _mf.Cw, 40, "wand", FlatButton.Kinds.Primary, delegate { _mf.RunArchive(); });
                y += 46;
                btnArchiveOne = _mf.Btn(this, "归档当前图片", x, y, _mf.Cw, 34, "page", FlatButton.Kinds.Ghost, delegate { _mf.ArchiveCurrentImage(); });
                y += 40;
                btnArchiveCancel = _mf.Btn(this, "取消归档", x, y, _mf.Cw, 34, null, FlatButton.Kinds.Danger, delegate { _mf.archiveCancel = true; });
                btnArchiveCancel.Visible = false;
                y += 40;
                // 进度条：只在跑的时候显示 —— 不跑时它的空轨道就是一条多余的横线（实测"别扭"）
                progress = new FlatProgress();
                progress.Bounds = new Rectangle(x, y, _mf.Cw, 6);
                progress.Visible = false;
                Controls.Add(progress);
                y += 14;                       // 占位保留，显隐不跳版
                lblBatchResult = new Label();
                lblBatchResult.AutoSize = false;
                lblBatchResult.ForeColor = Theme.TextMuted;
                lblBatchResult.Font = Theme.F11;
                lblBatchResult.Bounds = new Rectangle(x, y, _mf.Cw, 34);
                lblBatchResult.Text = "归档后自动切到输出文件夹，可直接查看结果";
                Controls.Add(lblBatchResult);
                y += 46;

                _mf.Divider(this, x, y, _mf.Cw);
                y += 14;
                _mf.SubLabel(this, "输出文件夹", x, y, _mf.Cw);
                y += 26;
                lblOutDir = new Label();
                lblOutDir.AutoSize = false;
                lblOutDir.AutoEllipsis = true;
                lblOutDir.ForeColor = Theme.TextSub;
                lblOutDir.Font = Theme.F11;
                lblOutDir.Bounds = new Rectangle(x, y, _mf.Cw, 16);
                lblOutDir.Text = "（打开文件夹后自动设为 " + MainForm.ArchivedDirName + "）";
                Controls.Add(lblOutDir);
                y += 22;
                btnPickOut = _mf.Btn(this, "选择文件夹", x, y, (_mf.Cw - 8) / 2, 30, null, FlatButton.Kinds.Ghost, delegate { _mf.PickOutDir(); });
                btnOpenOut = _mf.Btn(this, "打开", x + (_mf.Cw - 8) / 2 + 8, y, (_mf.Cw - 8) / 2, 30, "external", FlatButton.Kinds.Subtle, delegate { _mf.OpenOutDir(); });
                y += 38;
                _mf.PageEnd(this);
            }
        }

        // 归档"当前打开的那张图片"（左侧栏选中的也行：取画布当前文档）
        public void ArchiveCurrentImage()
        {
            if (busy) { Msg("正在处理中，请稍候。"); return; }
            string path = null;
            if (cur >= 0 && cur < docs.Count) path = docs[cur].FilePath;
            else if (ws.Selected >= 0 && ws.Selected < items.Count && !items[ws.Selected].IsFolder)
                path = items[ws.Selected].FullPath;
            if (path == null || !File.Exists(path)) { Status("先打开一张图片（或点左侧栏选中一张）"); return; }
            string outFolder = outDir;
            if (outFolder == null)
            {
                try { outFolder = Path.Combine(Path.GetDirectoryName(path), ArchivedDirName); } catch { }
            }
            ArchiveFiles(new string[] { path }, outFolder);
        }
    }
}
