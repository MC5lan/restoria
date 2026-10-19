// MainForm.Misc.cs — 主窗体（partial）：杂项：工作区开合 / 状态栏 / 直方图 / 缩略图
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PicTool
{
    public partial class MainForm : Form
    {
        // ================= 杂项 =================

        void ToggleWorkspace()
        {
            if (ws.Visible && ws.Width > 40)
            {
                lastWsWidth = ws.Width;
                ws.Visible = false;
                splitter.Visible = false;
            }
            else
            {
                ws.Visible = true;
                splitter.Visible = true;
                ws.Width = lastWsWidth < 160 ? 244 : lastWsWidth;
                ws.Invalidate();
            }
            canvas.Fit();
        }

        void RefreshHeader()
        {
            Doc d = Cur;
            if (d == null)
            {
                lblDocName.Text = "未打开图片";
                lblDocMeta.Text = "";
                if (dotEdited != null) dotEdited.Visible = false;
                return;
            }
            lblDocName.Text = d.Name;
            string meta = d.Current != null ? (d.Current.Width + " × " + d.Current.Height + " 像素") : "";
            if (d.Spilled) meta += "   · 已转入后台缓存";
            lblDocMeta.Text = meta;
            if (dotEdited != null) dotEdited.Visible = d.Edits;
        }

        void RefreshInfoLine()
        {
            Doc d = Cur;
            if (busy) lblStatus.Text = progressNote;   // 后台进度由这里统一显示
            UpdateZoomLabel();
            if (d == null) lblInfo.Text = "";
            else
            {
                string dims = d.Current != null ? (d.Current.Width + "×" + d.Current.Height) : "";
                lblInfo.Text = (cur + 1) + " / " + docs.Count + "    " + d.Name +
                    (dims.Length > 0 ? "    " + dims + "    " + canvas.ZoomText() : "");
                // 鼠标在画面上时追加 坐标 + 取色（修字、验印、对齐用的工程级辅助）
                if (canvas.HasImage && !busy)
                {
                    float ix, iy;
                    if (canvas.TryCursorImagePos(out ix, out iy))
                    {
                        lblInfo.Text += "    X:" + (int)ix + " Y:" + (int)iy;
                        try
                        {
                            int px = (int)ix, py = (int)iy;
                            if (px >= 0 && py >= 0 && px < d.Current.Width && py < d.Current.Height &&
                                d.Current == canvas.DisplayImage)
                            {
                                Color c = d.Current.GetPixel(px, py);
                                lblInfo.Text += "  #" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
                            }
                        }
                        catch { }
                    }
                }
            }
            UpdateHistogram(d);
            if (adjustStale) { adjustStale = false; ResetAdjustSessions(true); }   // 别的编辑动过图像：滑条归零并作废基准
            SyncPanelScroll(ContentMax());   // 300ms 心跳顺带校正滚动条（窗口缩放后也能立刻对上）
            btnRevert.Enabled = d != null && (d.Edits || d.Undo.Count > 0) && !busy;
            btnSave.Enabled = btnSaveAs.Enabled = d != null && !busy;
            // 修复按钮：有图 + 已圈出区域才可点；修复进行中它就是「取消修复」，必须保持可点
            //（判断逻辑在纯函数 DecideInpaintEnabled，见 MainForm.Keys.cs；曾因写成 `... && !busy`
            // 而把「点击取消修复」变成死交互）
            btnInpaint.Enabled = DecideInpaintEnabled(d != null, d != null && d.Mask != null, busy, btnInpaint.Busy);
            // 长任务（自动增强 / 自动纠偏 / 修复…）进行中禁用调色与角度滑条：
            // busy 时防抖预览直接跳过、松手提交又被 Commit 的 busy 早退挡掉 —— 滑条数字变了、
            // 图没变、也没提示，用户以为坏了。禁用后 OnMouseUp 不触发，提交自然不会丢；
            // busy 一清这里就恢复，异常路径也安全（比在每个 action 里各写一遍靠谱）。
            bool slidersOn = !busy;
            if (sBright != null) { sBright.Enabled = slidersOn; sContr.Enabled = slidersOn; sSat.Enabled = slidersOn; sTemp.Enabled = slidersOn; }
            if (sBlack != null) { sBlack.Enabled = slidersOn; sWhite.Enabled = slidersOn; sGamma.Enabled = slidersOn; sSharp.Enabled = slidersOn; }
            if (sAngle != null) sAngle.Enabled = slidersOn;
        }

        // 直方图按文档归属惰性重算：300ms 心跳里只做一次归属比对，
        // 真正的重算只发生在换图（d 变化）或编辑落地（AfterEdit 里置 histDoc=null）之后
        void UpdateHistogram(Doc d)
        {
            if (histView == null) return;
            if (histDoc == d) return;
            histDoc = d;
            histData = null;
            histView.Data = null;
            if (d == null || d.Current == null || !Imaging.Alive(d.Current)) return;
            Bitmap small = null;
            try
            {
                small = Imaging.ScaleToFit(d.Current, 320);
                if (small != null) histData = Enhance.LuminanceHistogram(small);
            }
            catch { }
            finally { if (small != null) small.Dispose(); }
            histView.Data = histData;
        }

        string lastStatus;   // 最近一条状态栏文本（--undotest 等自检口断言用）

        public void Status(string s)
        {
            lastStatus = s;
            lblStatus.Text = s;
            // 处理中用主色高亮：一眼看出"有活在跑"；空闲回到灰色。
            // 每次写状态都重设，不需要专门记"进入/退出忙碌"。
            lblStatus.ForeColor = busy ? Theme.Accent : Theme.TextSub;
        }

        // 交给后台线程前先拷贝一份：GDI+ 位图不是线程安全的，
        // UI 线程可能正在绘制同一个对象，直接共享会抛“对象当前正在其他地方使用”
        static Bitmap SafeCopy(Bitmap src)
        {
            if (src == null) return null;
            try { return src.Clone(new Rectangle(0, 0, src.Width, src.Height), src.PixelFormat) as Bitmap; }
            catch { return null; }
        }

        void Msg(string s)
        {
            MessageBox.Show(this, s, "Restoria", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void UpdateZoomLabel()
        {
            if (lblZoom != null && canvas != null) lblZoom.Text = canvas.ZoomText();
        }

        int thumbGen;   // 代数计数：换文件夹/刷新后让旧的后台缩略图线程自行退出（防写脏已换掉的列表）

        void StartThumbWorker()
        {
            // 必须做快照：items 会被 UI 线程 Clear 后重新填充（换文件夹、刷新），
            // 后台线程直接遍历活列表会抛越界异常，而后台线程的未捕获异常会直接终止进程
            List<WsItem> list = new List<WsItem>(items);
            int gen = ++thumbGen;
            System.Threading.Thread t = new System.Threading.Thread(delegate()
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (gen != thumbGen) return;   // 列表已换代：本线程立即收工
                    try
                    {
                    WsItem it = list[i];
                    if (it == null || it.IsFolder || it.IsParent) continue;
                    if (it.Thumb != null) continue;
                        // 后台线程只从磁盘读，绝不碰内存里的位图：
                        // GDI+ 位图非线程安全，和界面绘制共享同一对象会抛“对象当前正在其他地方使用”
                        // temp 用 try/finally 释放：MakeThumb 抛异常时原来会泄漏整张图
                        Bitmap temp = null;
                        try
                        {
                            temp = Imaging.LoadFile(it.FullPath);

                            FileInfo fi = null;
                            try { fi = new FileInfo(it.FullPath); } catch { }
                            string meta = temp.Width + "×" + temp.Height + (fi != null ? "    " + Imaging.FormatBytes(fi.Length) : "");
                            Bitmap thumb = Imaging.MakeThumb(temp, 240, 170);
                            // 赋值回主线程：后台线程写 it.Thumb/it.Meta、
                            // UI 线程 OnPaint 读，无同步属数据竞争。生成在后台，赋值回前台。
                            try
                            {
                                BeginInvoke((MethodInvoker)delegate
                                {
                                    it.Meta = meta;
                                    it.Thumb = thumb;
                                    if (it.Doc != null && it.Doc.Thumb == null) it.Doc.Thumb = thumb;
                                    ws.Invalidate();
                                });
                            }
                            catch { try { if (thumb != null) thumb.Dispose(); } catch { } }   // 窗体已销毁：别漏了这张缩略图
                        }
                        finally { try { if (temp != null) temp.Dispose(); } catch { } }
                    }
                    catch { try { BeginInvoke((MethodInvoker)delegate { list[i].Meta = "无法读取"; }); } catch { } }
                    try { BeginInvoke((MethodInvoker)delegate { ws.Invalidate(); }); } catch { }
                    System.Threading.Thread.Sleep(8);
                }
                try { BeginInvoke((MethodInvoker)delegate { ws.SetLoading(""); }); } catch { }
            });
            t.IsBackground = true;
            if (list.Count > 0) ws.SetLoading("正在读取缩略图…");
            t.Start();
        }

    }
}
