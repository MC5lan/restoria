// MainForm.Lifecycle.cs — 主窗体（partial）：关窗生命周期 / 截图 / 布局导出
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PicTool
{
    public partial class MainForm : Form
    {
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            bool exitSaveFailed = false;   // 退出时保存失败：恢复区必须保留，否则未保存的修改永久丢失
            if (busy)
            {
                if (MessageBox.Show(this, "批量处理正在进行，确定退出吗？", "退出",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                // busy 可能来自扫描、修复或一键归档：三个取消标志都要置位。
                // 漏掉任何一个，那条路径的后台线程就会在窗口销毁后继续跑，
                // 完成时 BeginInvoke 打到已销毁的窗体上（补了 scanCancel；
                // 补上漏掉的 fixCancel —— 修复期间关窗正是走这条路径；
                // 补上漏掉的 archiveCancel —— 归档线程若不收到取消，会在
                // OnFormClosed→Ocr.Shutdown() 释放原生运行时之后继续调 OCR = 原生崩溃）。
                // ：删掉 batchCancel —— 旧「批量处理」流程已并入归档，它赋值后
                // 从没人读过（编译器 CS0414 一直报），留着只会误导"还有一条取消路径"。
                scanCancel = true;
                fixCancel = true;
                archiveCancel = true;
            }
            if (HasUnsavedEdits())
            {
                DialogResult r = MessageBox.Show(this,
                    "还有修改没有保存，退出会丢失这些修改。\n\n要保存到“" + ArchivedDirName + "”文件夹后再退出吗？",
                    "退出", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (r == DialogResult.Cancel) { e.Cancel = true; return; }
                if (r == DialogResult.Yes)
                {
                    bool old = overwriteMode;
                    overwriteMode = false;
                    int failed = 0;
                    for (int i = 0; i < docs.Count; i++)
                    {
                        if (docs[i].Edits && docs[i].Current != null)
                        {
                            try
                            {
                                if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);
                                // 有未烙印文字：烙到副本上保存（此处不能碰界面状态，也不能弹确认框）
                                Bitmap toSave = docs[i].Current;
                                bool tmp = false;
                                if (docs[i].Texts.Count > 0)
                                {
                                    toSave = SafeCopy(docs[i].Current);
                                    if (toSave == null) throw new Exception("无法准备图片数据");
                                    TextStamp.Rasterize(toSave, docs[i].Texts);
                                    tmp = true;
                                }
                                try { Imaging.SaveImage(toSave, Path.Combine(outDir, docs[i].Name), 92); }
                                finally { if (tmp) toSave.Dispose(); }
                                docs[i].Texts.Clear();
                                docs[i].Edits = false;
                                Recovery.Clear(docs[i].FilePath);
                            }
                            catch { failed++; }   // 磁盘满/文件被占用：记下来，绝不能当"已保存"处理
                        }
                    }
                    overwriteMode = old;
                    if (failed > 0)
                    {
                        exitSaveFailed = true;
                        MessageBox.Show(this,
                            "有 " + failed + " 张图片保存失败（磁盘空间不足或文件被占用）。\n\n" +
                            "未保存的修改已保留在异常恢复区，下次启动程序时可以找回。",
                            "退出", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            }
            // 只有全部保存成功才清恢复区；有失败必须留下快照，下次启动可恢复
            Recovery.Shutdown(!exitSaveFailed);
            base.OnFormClosing(e);
        }

        bool HasUnsavedEdits()
        {
            for (int i = 0; i < docs.Count; i++)
                if (docs[i].Edits) return true;
            return false;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            previewTimer.Dispose();
            uiTimer.Dispose();
            AiFill.Shutdown();
            Ocr.Shutdown();
            InvalidatePreview();
            for (int i = 0; i < docs.Count; i++) docs[i].Dispose();
            docs.Clear();
            try { if (tempDir != null && Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            base.OnFormClosed(e);
        }

        // 模拟鼠标悬停（--shot 截图用，）：让自绘光标圈出现在静态截图里，
        // 消除"光标只有真机才看得到"的目检盲区（历次审查都没发现光标丑的原因）
        public void SimulateCursor(System.Drawing.Point p) { canvas.SimulateCursor(p); }

        // 加载样本目录（--wheeltest=<dir> 用，）：让左栏有列表，三区自检才完整
        public void OpenFolderForProbe(string dir)
        {
            try { if (Directory.Exists(dir)) OpenFolderPath(dir); }
            catch { }
        }

        // 光标圈自检（--cursortest，实测"笔刷/橡皮指针太丑"重画后加）：
        // 无头建窗 → 载入合成测试图 → 模拟悬停 → 把画布截成 PNG（%TEMP%\PicTool-cursor.png）。
        // 注意 OnPaint 里空图状态提前 return（DrawEmptyState），光标圈只在有图时绘制——
        // 所以本探针必须先 SetImage；报告落 %TEMP%\PicTool-cursortest.txt。
        public string CursorProbe()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine(AppInfo.Name + " 光标圈自检   " + DateTime.Now.ToString("HH:mm:ss"));
            try
            {
                Doc d = new Doc();
                d.FilePath = Path.Combine(Path.GetTempPath(), "cursortest-src.png");
                d.Original = new Bitmap(600, 450, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(d.Original))
                {
                    using (System.Drawing.Drawing2D.LinearGradientBrush bg = new System.Drawing.Drawing2D.LinearGradientBrush(
                        new Rectangle(0, 0, 600, 450), Color.FromArgb(245, 242, 235), Color.FromArgb(205, 200, 190), 45f))
                        g.FillRectangle(bg, 0, 0, 600, 450);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(70, 90, 130))) g.FillRectangle(b, 60, 50, 260, 190);
                }
                d.Current = Imaging.Ensure24bpp(new Bitmap(d.Original));
                d.WsIndex = -1;
                docs.Add(d);
                cur = docs.Count - 1;
                canvas.SetImage(d.Current, null, true);
                canvas.Tool = CanvasTool.Brush;
                canvas.SimulateCursor(new System.Drawing.Point(400, 300));
                Application.DoEvents();
                System.Threading.Thread.Sleep(150);
                Application.DoEvents();
                using (Bitmap shot = new Bitmap(canvas.Width, canvas.Height))
                {
                    canvas.DrawToBitmap(shot, new Rectangle(0, 0, canvas.Width, canvas.Height));
                    string png = Path.Combine(Path.GetTempPath(), "PicTool-cursor.png");
                    Imaging.SaveImage(shot, png, 95);
                    sb.AppendLine("  画布截图: " + png);
                    sb.AppendLine("  条件: mouseInside=true（模拟）、Tool=Brush、光标位于画布客户区 (400,300)");
                    sb.AppendLine("  判据: 截图 (400,300) 处应有 —— 白衬深线圆圈（半径=笔刷半径，最小 8px）+ 中心 2px 小点，无十字");
                }
                // 缩放下限判据（实测"缩小能缩太多了、指示标太小"）：
                // 连续向下缩 30 格后，zoom 必须停在允许下限上（min(fit,100%)），不能再小
                canvas.Fit();
                Application.DoEvents();
                string fitZoom = canvas.ZoomProbe();
                double expectMin = canvas.MinZoomProbe();
                for (int i = 0; i < 30; i++) canvas.HandleWheel(-120);
                Application.DoEvents();
                string minZoom = canvas.ZoomProbe();
                bool limitOk = Math.Abs(canvas.CurrentZoomProbe() - expectMin) < 1e-9;
                sb.AppendLine("  缩放下限: Fit=" + fitZoom + "  连续缩 30 格后=" + minZoom +
                              "  允许下限=" + Math.Round(expectMin * 100) + "%" +
                              (limitOk ? "   [通]" : "   [不通] 缩过头了"));
                sb.AppendLine();
                sb.AppendLine("结论: 截图已生成，人工/目检确认光标样式（新设计=单圈+白衬+中心点）；缩放下限见上");
            }
            catch (Exception ex)
            {
                sb.AppendLine("结论: 探针异常 —— " + ex.Message);
            }
            return sb.ToString();
        }

        // 撤销链自检（--undotest，实测"撤销只能撤销两次"后加，根因 UNDO_MAX=2；
        // 同日判据细化——：不只看"栈减一"，还逐帧比对**画面指纹**必须精确回到
        // 对应的历史状态。翻转序列的往返特性（水平↔垂直交替）天然能抓"弹错记录/弹错位图"）。
        // 无头开窗，走**真实编辑路径**（PushUndo/DoUndo，与界面点按钮完全同一条代码）：
        // 12 次交替翻转 → 断言撤销栈保有 10 步 → 逐次撤销 10 次每帧指纹比对 → 撤光后再撤有提示。
        // 以后谁把撤销预算改回小步数、或 DoUndo 弹错记录，跑一下立刻红。报告落 %TEMP%\PicTool-undotest.txt。
        static string Fingerprint(Bitmap b)
        {
            PixelBuffer buf = BitmapBridge.ToBuffer(b);
            using (System.Security.Cryptography.MD5 md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] h = md5.ComputeHash(buf.Data);
                System.Text.StringBuilder sb2 = new System.Text.StringBuilder(h.Length * 2);
                for (int i = 0; i < 8; i++) sb2.Append(h[i].ToString("x2"));   // 前 8 字节足够区分测试图
                return sb2.ToString();
            }
        }

        public string UndoProbe()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine(AppInfo.Name + " 撤销链自检   " + DateTime.Now.ToString("HH:mm:ss"));
            sb.AppendLine("判据：连续编辑 12 次后撤销栈保有 " + UNDO_MAX + " 步；逐次撤销全部成功且**画面指纹精确回到对应历史状态**；撤光后再撤有提示。");
            sb.AppendLine();
            System.Text.StringBuilder bad = new System.Text.StringBuilder();
            try
            {
                // 造一张测试图，走与"打开文件"相同的入列路径（不碰工作区 UI 行）
                Doc d = new Doc();
                d.FilePath = Path.Combine(Path.GetTempPath(), "undotest-src.png");
                d.Original = new Bitmap(600, 450, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(d.Original))
                {
                    g.Clear(Color.FromArgb(240, 236, 228));
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(90, 110, 150))) g.FillRectangle(b, 40, 40, 200, 140);
                }
                d.Current = Imaging.Ensure24bpp(new Bitmap(d.Original));
                WsItem it = new WsItem();
                it.FullPath = d.FilePath; it.Doc = d; it.DocIndex = docs.Count;
                d.WsIndex = items.Count;
                docs.Add(d); items.Add(it);
                cur = docs.Count - 1;
                canvas.SetImage(d.Current, null, true);

                // 12 次真实编辑：交替水平/垂直翻转（走 PushUndo + TrimUndo，与界面同路径）。
                // 每步编辑后的画面指纹记下来——撤销时逐帧比对（的细化判据）
                string[] fps = new string[12];
                for (int i = 0; i < 12; i++)
                {
                    Bitmap old = d.Current;
                    Bitmap n = Imaging.Flip(old, i % 2 == 0);
                    PushUndo(d, old);
                    d.Current = n;
                    AfterEdit(false);
                    fps[i] = Fingerprint(d.Current);
                }
                sb.AppendLine("  编辑 12 次后，撤销栈 = " + d.Undo.Count + " 步（应 = " + UNDO_MAX + "）");
                if (d.Undo.Count != UNDO_MAX) bad.Append("撤销栈不是 " + UNDO_MAX + " 步，是 " + d.Undo.Count + "；");

                // 全量诊断（定位指纹异常用）：fps 全表 + 栈内每条记录的指纹（尾→头）
                sb.Append("  fps[0..11]：");
                for (int i = 0; i < 12; i++) sb.Append(fps[i]).Append(i < 11 ? ' ' : '\n');
                sb.Append("  栈内记录指纹（尾→头，尾=下一次撤销要恢复的）：");
                for (int i = d.Undo.Count - 1; i >= 0; i--)
                    if (d.Undo[i].Img != null) sb.Append(Fingerprint(d.Undo[i].Img)).Append(' ');
                sb.AppendLine();

                // 逐次撤销 10 次：栈减一 **且** 画面指纹 == 第 (11-i) 次编辑后的历史状态
                //（12 步被裁成 10 步：最旧 2 步已丢，第 i 次撤销应回到第 11-i 次编辑后的画面）
                int undone = 0;
                System.Text.StringBuilder stackLine = new System.Text.StringBuilder("  撤销过程栈长：");
                for (int i = 0; i < UNDO_MAX; i++)
                {
                    int before = d.Undo.Count;
                    DoUndo();
                    stackLine.Append(d.Undo.Count).Append(i < UNDO_MAX - 1 ? " → " : "");
                    if (d.Undo.Count != before - 1) { bad.Append("第 " + (i + 1) + " 次撤销没弹栈；"); break; }
                    // 判据（修正 off-by-one）：第 k 次编辑的 undo 记录存的是
                    // S_{k-1}（编辑前状态）。撤第 i+1 次弹栈尾 → 画面应回到 S_{11-i} = fps[10-i]。
                    // （首版写成 fps[11-i] 多退一步，被周期 4 的翻转序列当场抓出——判据自身也要测。）
                    string expect = fps[10 - i];
                    string now = Fingerprint(d.Current);
                    if (now != expect)
                    {
                        int hit = Array.IndexOf(fps, now);
                        bad.Append("第 " + (i + 1) + " 次撤销画面指纹不符（应为 fps[" + (10 - i) + "]=S" + (11 - i) +
                                   " " + expect + "，实为 " + now + (hit >= 0 ? "，实际=fps[" + hit + "]=S" + (hit + 1) : "，不等于任何历史态") + "）；");
                        break;
                    }
                    undone++;
                }
                sb.AppendLine(stackLine.ToString());
                sb.AppendLine("  连续撤销成功（含指纹比对）" + undone + " / " + UNDO_MAX + " 次");
                if (undone != UNDO_MAX) bad.Append("连续撤销只成功 " + undone + " 次；");

                // 撤光后的提示
                DoUndo();
                sb.AppendLine("  撤光后再撤，状态栏 = " + (lastStatus ?? "(空)"));
                if (lastStatus == null || lastStatus.IndexOf("没有更早") < 0) bad.Append("撤光后没有提示；");

                sb.AppendLine();
                sb.AppendLine(bad.Length == 0 ? "结论: 撤销链通过（" + UNDO_MAX + " 步完整可用，逐帧指纹比对通过）"
                                              : "结论: 有问题 —— " + bad.ToString());
            }
            catch (Exception ex)
            {
                bad.Append("探针异常: " + ex.Message);
                sb.AppendLine("结论: 探针异常 —— " + ex.Message);
            }
            return sb.ToString();
        }

        // 拖放接收自检（--droptest，）：拖放事件不冒泡，接收方必须自己
        // AllowDrop=true 且挂了 DragEnter/DragDrop。之前只有主窗体开了
        // AllowDrop（画布挂了事件却没开口，其余区域连事件都没挂），实际拖到窗口
        // 任何位置都进不去。这里无头建窗后逐个核"主要区域都已开口"——
        // 以后谁重构 BuildUi 把开口删了，跑一下立刻红。
        public string DropProbe()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine(AppInfo.Name + " 拖放接收自检   " + DateTime.Now.ToString("HH:mm:ss"));
            sb.AppendLine("判据：画布 / 工作区 / 右栏 / 导航轨 / 分隔条 / 主窗体 必须 AllowDrop=true");
            sb.AppendLine("（拖放事件不冒泡：接收方没开口 = 拖到那里显示禁止光标，什么都不会发生）。");
            sb.AppendLine();
            Control[] areas = new Control[] { canvas, ws, content, navRail, splitter };
            string[] names = new string[] { "画布", "工作区", "右栏", "导航轨", "分隔条" };
            int bad = 0;
            for (int i = 0; i < areas.Length; i++)
            {
                bool ok = areas[i] != null && areas[i].AllowDrop;
                sb.AppendLine("  " + names[i] + "  AllowDrop=" + (areas[i] == null ? "控件不存在" : areas[i].AllowDrop.ToString())
                    + (ok ? "" : "   ★ 没开口"));
                if (!ok) bad++;
            }
            sb.AppendLine("  主窗体  AllowDrop=" + AllowDrop + (AllowDrop ? "" : "   ★ 没开口"));
            if (!AllowDrop) bad++;
            sb.AppendLine();
            sb.AppendLine(bad == 0 ? "结论: 全部开口，拖放链路接收侧就绪（实际拖放请人工拖一个文件夹验证）。"
                                   : "结论: 有 " + bad + " 处没开口 —— 拖到那些区域会被拒。");
            return sb.ToString();
        }

        // 诊断用：输出控件实际布局
        // 诊断用：切到第 N 页并让布局/滚动立即结算，供 --shot=N 截图
        public void ShotPage(int i)
        {
            try
            {
                SelectSection(i);
                ApplyContentScroll();
                Refresh();
                Application.DoEvents();
            }
            catch { }
        }

        public void DumpLayout()
        {
            // 诊断模式固定客户区尺寸：正常启动是最大化（尺寸随屏幕变），
            // 而布局门禁的基准必须与环境无关，所以这里先恢复标准尺寸再导出。
            WindowState = FormWindowState.Normal;
            ClientSize = new Size(1080, 680);
            Show();
            PerformLayout();
            Application.DoEvents();
            // ：细滚动条的摆位挂在 content.Resize 上，而 Resize 是否来得及时
            // 取决于消息队列时机（同一个二进制时而摆位时而没摆位，快照就不稳定）。
            // 这里显式再收一次，让 --layout 成为确定性的门禁，不再假报警。
            ApplyContentScroll();
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("Form client size = " + ClientSize.Width + "x" + ClientSize.Height);
            // 环境行**不能进快照的可比较部分**：RAM、核数、CLR 版本每台机器都不同，
            // 写进去就等于让基线与生成它的那台机器绑定，换机器（包括 CI）必然假报警。
            // 之前它确实被写进去了，于是 CI 上的布局门禁从来没有真正绿过。
            // 具体的值不用另存：Main 开头的 WriteEnvReport() 已经写到
            // %TEMP%\PicTool-env.txt，内容比这一行详细得多。
            sb.AppendLine("环境: (随机器变化，详见 PicTool-env.txt)");
            WriteLayoutDiag();
            DumpCtrl(this, sb, 0);
            string p = Path.Combine(Path.GetTempPath(), "PicTool-layout.txt");
            try { File.WriteAllText(p, sb.ToString(), System.Text.Encoding.UTF8); } catch { }
            Close();
        }

        // 布局诊断：把"会随机器变的东西"单独写一份，不放进快照。
        // 存在的理由：CI 上布局快照与基线不一致时，最可能的原因是这台机器
        // 缺少界面字体（Theme.PickFace 按 微软雅黑 → Segoe UI 回退），
        // 而 PageHead 的说明标签是 AutoSize —— 字体度量一变，它下面所有控件
        // 都跟着下移。没有这份记录，只能对着几百行差异猜。
        void WriteLayoutDiag()
        {
            try
            {
                System.Text.StringBuilder d = new System.Text.StringBuilder();
                d.AppendLine("界面字体 = " + Theme.FaceName + "   （候选: Microsoft YaHei UI / Microsoft YaHei / Segoe UI）");
                d.AppendLine("系统DPI  = " + this.DeviceDpi);
                d.AppendLine();
                d.AppendLine("AutoSize 控件（高度随字体度量变化，是快照跨机器不一致的唯一来源）：");
                CollectAutoSize(this, d, 0);
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "PicTool-layout-diag.txt"),
                                  d.ToString(), System.Text.Encoding.UTF8);
            }
            catch { }
        }

        void CollectAutoSize(Control parent, System.Text.StringBuilder d, int depth)
        {
            for (int i = 0; i < parent.Controls.Count; i++)
            {
                Control c = parent.Controls[i];
                if (c.AutoSize)
                {
                    string txt = "";
                    try { txt = c.Text; } catch { }
                    if (txt.Length > 20) txt = txt.Substring(0, 20);
                    d.AppendLine("  " + new string(' ', depth * 2) + c.GetType().Name +
                                 " height=" + c.Height + " width=" + c.Width +
                                 " text='" + txt + "'");
                }
                if (depth < 4) CollectAutoSize(c, d, depth + 1);
            }
        }

        void DumpCtrl(Control parent, System.Text.StringBuilder sb, int depth)        {
            string pad = new string(' ', depth * 2);
            for (int i = 0; i < parent.Controls.Count; i++)
            {
                Control c = parent.Controls[i];
                string txt = "";
                try { txt = c.Text; } catch { }
                if (txt.Length > 16) txt = txt.Substring(0, 16);
                sb.AppendLine(pad + c.GetType().Name + " '" + txt + "' bounds=" + c.Bounds +
                    " dock=" + c.Dock + " vis=" + c.Visible + " z=" + i);
                if (depth < 3) DumpCtrl(c, sb, depth + 1);
            }
        }
    }
}
