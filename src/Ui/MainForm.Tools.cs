// MainForm.Tools.cs — 主窗体（partial）：去除 / 倾斜 / 调色翻转 / 自动纠偏与透视
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PicTool
{
    public partial class MainForm : Form
    {
        // ================= 去除 =================

        void StrokeTo(float x, float y, float r, bool add)
        {
            Doc d = Cur;
            if (d == null || d.Current == null || busy) return;
            if (d.EnsureMask()) canvas.SetOverlay(d.Overlay);
            Imaging.MaskCircle(d.Mask, x, y, r, add);
            Imaging.OverlayCircle(d.Overlay, x, y, r, add);
        }

        void RectToMask(Rectangle r)
        {
            Doc d = Cur;
            if (d == null || d.Current == null || busy) return;
            if (d.EnsureMask()) canvas.SetOverlay(d.Overlay);
            Imaging.MaskRect(d.Mask, r, true);
            Imaging.OverlayRect(d.Overlay, r, true);
            canvas.Invalidate();
            RefreshInfoLine();
        }

        public void ClearMask()
        {
            Doc d = Cur;
            if (d == null) return;
            d.ClearMask();
            canvas.SetOverlay(null);
            Status("已清除涂抹");
        }

        public void DoInpaint()
        {
            // 忙碌时再点同一个按钮 = 要求取消（按钮文字已变成「取消修复」）
            if (busy) { fixCancel = true; Status("正在取消…"); return; }
            Doc d = Cur;
            if (d == null) { Status("请先打开图片"); return; }
            FlattenIfNeeded(d);   // 有未烙印文字先落像素：所见即所修
            if (d.Mask == null) { Status("请先圈出去除区域（笔刷涂抹或框选）"); return; }
            long sw = Environment.TickCount;
            Bitmap src = SafeCopy(d.Current);
            Bitmap mask = SafeCopy(d.Mask);
            if (src == null || mask == null) { Status("无法准备图片数据"); return; }
            int mode = segFix == null ? 1 : segFix.SelectedIndex;   // 0=普通 1=增强 2=混合 3=纸面 4=AI
            // 大面积的精细修复在 2010 年的机器上要几分钟（实测 200 万像素≈1 分钟/开发机），先问一句。
            // 纸面重建与 AI 不受此限：纸面重建按分块算（毫秒级），AI 按掩码外框裁剪后缩到固定尺寸推理一次。
            int maskArea = Imaging.MaskArea(mask);
            int warnPixels = (mode == 4) ? SysInfo.LargeAreaWarnPixels * 3 : SysInfo.LargeAreaWarnPixels;
            if (mode != 0 && mode != 3 && maskArea > warnPixels)
            {
                string ask = mode == 4
                    ? "圈出的区域约 " + (maskArea / 10000) + " 万像素。AI 修复每次推理都是固定开销（老机器十几秒），区域大小影响不大。\n\n继续用 AI 修复吗？"
                    : "圈出的区域约 " + (maskArea / 10000) + " 万像素。精细/混合修复在这个面积上可能要几分钟（老机器更久），普通模式只要几秒。\n\n要用精细修复吗？（选“否”改用普通模式）";
                if (MessageBox.Show(this, ask, "大面积修复", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    mode = 0;
            }
            string modeName = mode == 0 ? "普通模式" : mode == 2 ? "混合模式"
                : (mode == 3 ? "纸面重建" : (mode == 4 ? "AI 修复" : "增强模式"));
            // 按钮临时变成「取消修复」，忙碌结束恢复；转圈让"正在算"一眼可见
            string btnText = btnInpaint.Text;
            fixCancel = false;
            btnInpaint.Text = "取消修复";
            btnInpaint.BusyClickable = true;    // 转圈时仍可点 = 取消
            btnInpaint.Busy = true;
            RunBusy("正在修复...", delegate
                {
                    Bitmap result = null;
                    bool wasCancelled = false;
                    try
                    {
                        CancelCheck check = delegate() { return fixCancel; };
                        if (mode == 4)
                            result = AiFill.Run(src, mask, delegate(int p) { progressNote = "AI 修复中... " + p + "%"; }, check);
                        else if (mode == 3)
                            result = PaperFill.Run(src, mask, delegate(int p) { progressNote = "纸面重建中... " + p + "%"; });
                        else if (mode == 0)
                            result = Imaging.Inpaint(src, mask, 5, delegate(int p) { progressNote = "正在修复... " + p + "%"; }, check);
                        else if (mode == 2)
                            result = PatchFill.InpaintHybrid(src, mask, true, delegate(int p) { progressNote = "正在修复... " + p + "%"; }, check);
                        else
                            result = PatchFill.Inpaint(src, mask, true, delegate(int p) { progressNote = "正在修复... " + p + "%"; }, check);
                    }
                    catch (OpCancelledException)
                    {
                        wasCancelled = true;      // 用户取消不是错误
                    }
                    finally
                    {
                        src.Dispose();
                        mask.Dispose();
                    }
                    BeginInvoke((MethodInvoker)delegate
                    {
                        btnInpaint.Busy = false;
                        btnInpaint.Text = btnText;
                        if (wasCancelled)
                        {
                            Status("已取消修复，图片没有被改动");
                            return;
                        }
                        PushUndo(d, d.Current);
                        d.Current = result;
                        AfterEdit(true);
                        double sec = (Environment.TickCount - sw) / 1000.0;
                        Status("修复完成（" + modeName + "），耗时 " + sec.ToString("0.0") + " 秒");
                        Toast.Show(this, "修复完成（" + modeName + "）", Toast.Kinds.Ok,
                            "耗时 " + sec.ToString("0.0") + " 秒");
                    });
                }, null);
        }

        // ================= 倾斜 =================

        void OnPreviewTimer()
        {
            previewTimer.Stop();
            if (busy || pendingPreview == 0) return;
            int kind = pendingPreview;
            pendingPreview = 0;
            Doc d = Cur;
            if (d == null || d.Current == null) return;
            if (kind == 1)
            {
                double ang = sAngle.DoubleValue;
                Bitmap rbase = EnsureBase(ref rotBaseFull, ref rotBaseDoc, d);
                if (rbase == null) return;
                if (Math.Abs(ang) < 0.01)
                {
                    CancelRotatePreview();
                    canvas.SetImage(d.Current, d.Overlay, false);
                    return;
                }
                EnsurePreviewBase(d, rbase);
                if (previewBase == null) return;
                CancelRotatePreview();
                Rectangle roi;
                bool roiMode = RoiRect(d, out roi);
                if (roiMode)
                {
                    double f = previewBase.Width / (double)rbase.Width;
                    roi = new Rectangle((int)(roi.X * f), (int)(roi.Y * f), Math.Max(8, (int)(roi.Width * f)), Math.Max(8, (int)(roi.Height * f)));
                }
                previewRot = roiMode ? Imaging.RotateRegion(previewBase, roi, ang, swCrop.Checked)
                                     : Imaging.RotateFine(previewBase, ang, swCrop.Checked);
                canvas.SetImage(previewRot, null, false);
            }
            else if (kind == 2)
            {
                Bitmap cbase = EnsureBase(ref colorBaseFull, ref colorBaseDoc, d);
                if (cbase == null) return;
                EnsurePreviewBase(d, cbase);
                if (previewBase == null) return;
                if (previewBuf == null || previewBuf.Width != previewBase.Width || previewBuf.Height != previewBase.Height)
                {
                    if (previewBuf != null) previewBuf.Dispose();
                    previewBuf = new Bitmap(previewBase.Width, previewBase.Height,
                        System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                }
                using (Graphics g = Graphics.FromImage(previewBuf))
                {
                    g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                    g.DrawImage(previewBase, new Rectangle(0, 0, previewBuf.Width, previewBuf.Height));
                }
                Imaging.ColorAdjustInPlace(previewBuf, sBright.Value, sContr.Value, sSat.Value, sTemp.Value);
                // 色阶 + 锐化并入同一份预览：所见即所提交（全分辨率提交走 Enhance.Apply）
                Enhance.ApplyInPlace(previewBuf, CurrentAdjustParams());
                canvas.SetImage(previewBuf, d.Overlay, false);
            }
        }

        // 预览用的缩略基准图：必须与提交时用的全分辨率基准是同一张图，
        // 否则"预览看着对、一点应用就变样"（例如滑条保留值后再拖一次的情况）。
        Bitmap previewSrc;

        void EnsurePreviewBase(Doc d, Bitmap fullSrc)
        {
            if (previewSrc == fullSrc && previewBase != null &&
                Math.Max(previewBase.Width, previewBase.Height) <= SysInfo.PreviewMaxSide + 50 &&
                previewBase.Width * previewBase.Height >= fullSrc.Width * fullSrc.Height / 4)
                return;
            if (previewBase != null) previewBase.Dispose();
            previewBase = Imaging.ScaleToFit(fullSrc, SysInfo.PreviewMaxSide);
            previewSrc = fullSrc;
            if (previewBuf != null) { previewBuf.Dispose(); previewBuf = null; }
            previewDoc = d;
        }

        void CancelRotatePreview()
        {
            if (previewRot != null) { previewRot.Dispose(); previewRot = null; }
        }

        public void InvalidatePreview()
        {
            CancelRotatePreview();
            if (previewBase != null) { previewBase.Dispose(); previewBase = null; }
            if (previewBuf != null) { previewBuf.Dispose(); previewBuf = null; }
            previewSrc = null;
            previewDoc = null;
            pendingPreview = 0;
            previewTimer.Stop();
        }

        // 只纠正框选范围要用的区域：掩码的外接矩形
        bool RoiRect(Doc d, out Rectangle roi)
        {
            roi = Rectangle.Empty;
            if (swRoiOnly == null || !swRoiOnly.Checked) return false;
            if (d == null || d.Mask == null || !Imaging.Alive(d.Mask)) return false;
            roi = Imaging.MaskBounds(d.Mask);
            return roi.Width >= 8 && roi.Height >= 8;
        }

        public void CommitRotate()
        {
            Doc d = Cur;
            if (!GuardAction(d)) return;   // 统一守卫
            FlattenIfNeeded(d);   // 有未烙印文字先落像素，保证旋转的是“看到的画面”
            double ang = sAngle.DoubleValue;
            bool crop = swCrop.Checked;
            previewTimer.Stop();
            if (Math.Abs(ang) < 0.01)
            {
                // 拖回 0：退回本次旋转前的基准图
                CancelRotatePreview();
                InvalidatePreview();
                if (rotBaseFull != null && rotBaseDoc == d && Imaging.Alive(rotBaseFull))
                {
                    Bitmap back = rotBaseFull;
                    rotBaseFull = null; rotBaseDoc = null;
                    sliderCommit = true;
                    try
                    {
                        PushUndo(d, d.Current);
                        d.Current = back;
                    }
                    finally { sliderCommit = false; }
                    AfterEdit(false);
                    Status("已还原到本次旋转前的状态");
                }
                else
                {
                    canvas.SetImage(d.Current, d.Overlay, false);
                }
                return;
            }
            Rectangle roi;
            bool roiMode = RoiRect(d, out roi);
            if (roiMode && (roi.Width < 8 || roi.Height < 8))
            {
                Status("请先在画面上用【框选】画出要纠正的范围");
                Msg("已打开「只纠正框选范围」。请先用画布上的【框选】画出要纠正的那块区域（比如歪掉的证件、表格），再拖角度滑条。");
                return;
            }
            Bitmap baseImg = EnsureBase(ref rotBaseFull, ref rotBaseDoc, d);
            if (baseImg == null) { Status("无法准备图片数据"); return; }
            // 这里不主动释放预览图：画布仍指向它，等编辑结果落地后再由 InvalidatePreview 释放
            Bitmap work = SafeCopy(baseImg);        // 先在 UI 线程拷贝：后台线程与界面绘制共享位图会冲突
            if (work == null) { Status("无法准备图片数据"); return; }
            RunBusy("正在旋转...", delegate
            {
                Bitmap n;
                try { n = roiMode ? Imaging.RotateRegion(work, roi, ang, crop) : Imaging.RotateFine(work, ang, crop); }
                finally { work.Dispose(); }
                BeginInvoke((MethodInvoker)delegate
                {
                    sliderCommit = true;
                    try
                    {
                        PushUndo(d, d.Current);
                        d.Current = n;
                    }
                    finally { sliderCommit = false; }
                    // 滑条保持不动：它表示相对基准图的角度，再拖是"重新算"而不是"接着转"
                    InvalidatePreview();
                    AfterEdit(true);
                    Status((roiMode ? "已把框内内容纠正 " : "已旋转 ") + ang.ToString("0.#") + "°");
                });
            }, null);
        }

        public void DoRotate90(bool clockwise)
        {
            Doc d = Cur;
            if (!GuardAction(d)) return;   // 统一守卫
            FlattenIfNeeded(d);
            Bitmap work = SafeCopy(d.Current);      // 先在 UI 线程拷贝：后台线程与界面绘制共享位图会冲突
            if (work == null) { Status("无法准备图片数据"); return; }
            RunBusy("正在旋转...", delegate
            {
                Bitmap n;
                try { n = Imaging.Rotate90(work, clockwise); }
                finally { work.Dispose(); }
                BeginInvoke((MethodInvoker)delegate
                {
                    PushUndo(d, d.Current);
                    d.Current = n;
                    if (d.Mask != null) d.Mask.RotateFlip(clockwise ? RotateFlipType.Rotate90FlipNone : RotateFlipType.Rotate270FlipNone);
                    if (d.Overlay != null) d.Overlay.RotateFlip(clockwise ? RotateFlipType.Rotate90FlipNone : RotateFlipType.Rotate270FlipNone);
                    AfterEdit(false);
                    Status(clockwise ? "已右转 90°" : "已左转 90°");
                });
            }, null);
        }

        // ================= 调色 / 翻转 =================

        // ---------------- 区域上色（调色页） ----------------
        // 涂抹/框选出区域（与去除页同一个掩码机制，顶栏笔刷/框选/橡皮全局可用），
        // 把圈内"墨迹"按墨量染成所选标准色 —— 纸张不动、浓淡保留。
        // 算法 = SealTool.Colorize 泛化版（印章上色同一条，避免两套判据漂移）。
        public void DoRegionColorize()
        {
            Doc d = Cur;
            if (d == null) { Status("请先打开图片"); return; }
            if (busy) return;
            if (d.Mask == null || Imaging.MaskArea(d.Mask) < 20)
            { Status("请先圈出上色区域（顶栏【笔刷】涂抹或【框选】）"); return; }
            FlattenIfNeeded(d);     // 有未烙印文字先落像素：所见即所染
            long t0 = Environment.TickCount;
            Bitmap src = SafeCopy(d.Current);
            if (src == null) { Status("无法准备图片数据"); return; }
            Bitmap mk = Imaging.CopyMask(d.Mask);
            Color target = pickRecolor != null ? pickRecolor.SelectedColor : Color.Black;
            int strength = sRecolor != null ? sRecolor.Value : 100;
            RunBusy("正在上色...", delegate
            {
                try { SealTool.Colorize(src, mk, target, strength); }
                finally { mk.Dispose(); }
                Bitmap result = src;
                BeginInvoke((MethodInvoker)delegate
                {
                    PushUndo(d, d.Current);
                    d.Current = result;
                    AfterEdit(true);
                    double sec = (Environment.TickCount - t0) / 1000.0;
                    Status("区域上色完成，耗时 " + sec.ToString("0.0") + " 秒（Ctrl+Z 可撤销）");
                });
            }, null);
        }

        // 汇总当前调色页全部滑条（含色阶/锐化）为一份参数
        AdjustParams CurrentAdjustParams()
        {
            AdjustParams p = new AdjustParams();
            if (sBright != null) p.Bright = sBright.Value;
            if (sContr != null) p.Contr = sContr.Value;
            if (sSat != null) p.Sat = sSat.Value;
            if (sTemp != null) p.Temp = sTemp.Value;
            if (sBlack != null) p.Black = sBlack.Value;
            if (sWhite != null) p.White = sWhite.Value;
            if (sGamma != null) p.Gamma = sGamma.Value / 100.0;
            if (sSharp != null) p.Sharpen = sSharp.Value / 100.0;
            return p;
        }

        public void CommitColor()
        {
            Doc d = Cur;
            if (!GuardAction(d)) return;   // 统一守卫
            FlattenIfNeeded(d);
            previewTimer.Stop();
            AdjustParams p = CurrentAdjustParams();
            if (p.IsNeutral())
            {
                // 全部拖回默认值：这就是"还原"，直接把基准图交还给文档
                if (colorBaseFull != null && colorBaseDoc == d && Imaging.Alive(colorBaseFull))
                {
                    Bitmap back = colorBaseFull;
                    colorBaseFull = null; colorBaseDoc = null;
                    sliderCommit = true;
                    try
                    {
                        PushUndo(d, d.Current);
                        d.Current = back;
                    }
                    finally { sliderCommit = false; }
                    InvalidatePreview();
                    AfterEdit(false);
                    Status("已还原到本次调色前的状态");
                }
                else
                {
                    CancelColorPreview();
                    InvalidatePreview();
                    canvas.SetImage(d.Current, d.Overlay, false);
                }
                return;
            }
            Bitmap baseImg = EnsureBase(ref colorBaseFull, ref colorBaseDoc, d);
            if (baseImg == null) { Status("无法准备图片数据"); return; }
            Bitmap work = SafeCopy(baseImg);        // 先在 UI 线程拷贝：后台线程与界面绘制共享位图会冲突
            if (work == null) { Status("无法准备图片数据"); return; }
            RunBusy("正在调色...", delegate
            {
                Bitmap n;
                try { n = Enhance.Apply(work, p); }
                finally { work.Dispose(); }
                BeginInvoke((MethodInvoker)delegate
                {
                    sliderCommit = true;
                    try
                    {
                        PushUndo(d, d.Current);
                        d.Current = n;
                    }
                    finally { sliderCommit = false; }
                    // 滑条保持不动：它表示相对基准图的量，再拖是"重新算"而不是"叠加"
                    InvalidatePreview();
                    AfterEdit(false);
                    Status("已应用调色（亮度 " + SgnVal((int)p.Bright) + "，对比度 " + SgnVal((int)p.Contr) +
                           "，饱和度 " + SgnVal((int)p.Sat) + "，色温 " + SgnVal((int)p.Temp) +
                           "，黑场 " + p.Black + "，白场 " + p.White + "）");
                });
            }, null);
        }

        void CancelColorPreview()
        {
            if (previewBuf != null) { previewBuf.Dispose(); previewBuf = null; }
        }

        public void ResetColorSliders(bool redraw)
        {
            sBright.ResetValue(0); vBright.Text = "0";
            sContr.ResetValue(0); vContr.Text = "0";
            sSat.ResetValue(0); vSat.Text = "0";
            sTemp.ResetValue(0); vTemp.Text = "0";
            if (sBlack != null) { sBlack.ResetValue(0); vBlack.Text = "0"; }
            if (sWhite != null) { sWhite.ResetValue(255); vWhite.Text = "255"; }
            if (sGamma != null) { sGamma.ResetValue(100); vGamma.Text = "1.00"; }
            if (sSharp != null) { sSharp.ResetValue(0); vSharp.Text = "0%"; }
            DropAdjustBases();     // 图像被别的操作改过了，调色/旋转的基准作废
            if (redraw && Cur != null && Cur.Current != null)
                canvas.SetImage(Cur.Current, Cur.Overlay, false);
        }

        public void DoAutoEnhance()
        {
            Doc d = Cur;
            if (!GuardAction(d)) return;   // 统一守卫
            FlattenIfNeeded(d);
            Bitmap work = SafeCopy(d.Current);      // 先在 UI 线程拷贝：后台线程与界面绘制共享位图会冲突
            if (work == null) { Status("无法准备图片数据"); return; }
            RunBusy("正在增强...", delegate
            {
                Bitmap n;
                try { n = Imaging.AutoEnhance(work); }
                finally { work.Dispose(); }
                BeginInvoke((MethodInvoker)delegate
                {
                    PushUndo(d, d.Current);
                    d.Current = n;
                    ResetColorSliders(false);
                    AfterEdit(false);
                    Status("已完成自动增强");
                });
            }, null);
        }

        // ================= 自动纠偏 / 透视校正 =================

        // 一键转正：投影法检测文字行角度 → 按检测角度旋转并自动裁掉空白边。
        // 检测不出（空白页/照片）就明说，绝不瞎转。
        public void DoAutoDeskew()
        {
            Doc d = Cur;
            if (!GuardAction(d)) return;   // 统一守卫
            FlattenIfNeeded(d);
            Bitmap work = SafeCopy(d.Current);      // 先在 UI 线程拷贝：后台线程与界面绘制共享位图会冲突
            if (work == null) { Status("无法准备图片数据"); return; }
            bool crop = swCrop != null && swCrop.Checked;
            RunBusy("正在检测倾斜角度...", delegate
            {
                double ang = 0;
                Bitmap n = null;
                try { ang = Enhance.DetectSkewAngle(work); }
                catch { }
                if (Math.Abs(ang) >= 0.08)
                {
                    try { n = Imaging.RotateFine(work, ang, crop); }
                    catch { n = null; }
                }
                work.Dispose();
                Bitmap result = n;
                double detected = ang;
                BeginInvoke((MethodInvoker)delegate
                {
                    if (result == null)
                    {
                        Status("画面本来就是正的（或检测不到文字行），未做纠偏");
                        return;
                    }
                    PushUndo(d, d.Current);
                    d.Current = result;
                    ClearAncillary(d);
                    AfterEdit(true);
                    canvas.Fit();
                    Status("已自动纠偏 " + (detected > 0 ? "+" : "") + detected.ToString("0.0") + "°（Ctrl+Z 可撤销）");
                });
            }, null);
        }

        public void StartPerspective()
        {
            Doc d = Cur;
            if (d == null) { Status("请先打开图片"); return; }
            canvas.ClearPersPoints();
            canvas.Tool = CanvasTool.Perspective;
            canvas.NotifyToolChanged();
            Status("透视取点：在画面上依次点文件的 左上 → 右上 → 右下 → 左下（Esc 重取）");
        }

        public void DoPerspective()
        {
            Doc d = Cur;
            if (!GuardAction(d)) return;   // 统一守卫
            FlattenIfNeeded(d);
            if (canvas.PersCount < 4)
            {
                Msg("请先点【取四个角点】，然后在画面上依次点击文件的四个角：\n左上 → 右上 → 右下 → 左下。\n\n拍歪的文件用四点拉平，比框选裁剪干净得多。");
                return;
            }
            PointF[] quad = new PointF[4];
            Array.Copy(canvas.PersPoints, quad, 4);
            Bitmap work = SafeCopy(d.Current);      // 先在 UI 线程拷贝：后台线程与界面绘制共享位图会冲突
            if (work == null) { Status("无法准备图片数据"); return; }
            RunBusy("正在透视校正...", delegate
            {
                Bitmap n = Enhance.PerspectiveWarp(work, quad);
                work.Dispose();
                BeginInvoke((MethodInvoker)delegate
                {
                    PushUndo(d, d.Current);
                    d.Current = n;
                    ClearAncillary(d);
                    canvas.ClearPersPoints();
                    canvas.Tool = CanvasTool.Rect;      // 校正完顺势切到框选，方便接着裁剪
                    canvas.NotifyToolChanged();
                    AfterEdit(true);
                    canvas.Fit();
                    Status("已按四点做透视校正：" + n.Width + "×" + n.Height + " 像素（Ctrl+Z 可撤销）");
                });
            }, null);
        }

        public void DoFlip(bool horizontal)
        {
            Doc d = Cur;
            if (!GuardAction(d)) return;   // 统一守卫
            FlattenIfNeeded(d);
            Bitmap work = SafeCopy(d.Current);      // 先在 UI 线程拷贝：后台线程与界面绘制共享位图会冲突
            if (work == null) { Status("无法准备图片数据"); return; }
            RunBusy("正在翻转...", delegate
            {
                Bitmap n;
                try { n = Imaging.Flip(work, horizontal); }
                finally { work.Dispose(); }
                BeginInvoke((MethodInvoker)delegate
                {
                    PushUndo(d, d.Current);
                    d.Current = n;
                    if (d.Mask != null) d.Mask.RotateFlip(horizontal ? RotateFlipType.RotateNoneFlipX : RotateFlipType.RotateNoneFlipY);
                    if (d.Overlay != null) d.Overlay.RotateFlip(horizontal ? RotateFlipType.RotateNoneFlipX : RotateFlipType.RotateNoneFlipY);
                    AfterEdit(false);
                    Status(horizontal ? "已水平翻转" : "已垂直翻转");
                });
            }, null);
        }

    }
}
