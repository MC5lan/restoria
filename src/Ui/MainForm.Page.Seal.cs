// MainForm.Page.Seal.cs - 主窗体（partial）：印章页（印章识别上色 + 区域上色）
// P5 第三刀：页面类化——控件与构建逻辑自治（与 BatchPage 同模式）；
// 业务方法（DoSealDetect/RefreshSealOverlay/DoSealColorize）引用主窗体状态密集，
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
        void BuildSealPage()
        {
            pageSeal = new SealPage(this);          // 构造器内完成 NewPage 的等价注册
            pnlSeal = pageSeal;                     // 切页/滚动机制仍通过 pnlSeal 走
            pageSeal.Build();
        }

        // 印章页：一个 Panel 子类，自己管自己的控件。要加控件改这里，不要回主窗体。
        public sealed class SealPage : Panel
        {
            readonly IPageHost _mf;

            internal FlatButton btnSealDetect, btnSealColor, btnSealClear;
            internal Segmented segSealRange;
            internal Label vSealStrength;
            internal FlatSlider sSeal;
            // 区域上色（从调色页挪来，实测：上色类操作都放印章页）
            internal ColorPicker pickRecolor;
            internal FlatSlider sRecolor; internal Label vRecolor;
            internal FlatButton btnRecolor, btnRecolorClear;

            internal SealPage(IPageHost mf)
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
                y = _mf.PageHead(this, "印章", "① 框选公章 → ② 识别印章 → ③ 给印章上色", x, y, _mf.Cw);
                y = BuildSealControls(y);

                // ---------- 区域上色（从调色页挪来，实测：上色类操作都放印章页）----------
                // 框选/涂抹一个区域，把圈内文字染成标准色。掩码与去除页同一套
                // （顶栏笔刷/框选/橡皮全局可用），算法与印章上色同一条（Colorize 泛化版）。
                _mf.Divider(this, x, y, _mf.Cw);
                y += 14;
                _mf.SubLabel(this, "区域上色 · 给圈内的字换颜色", x, y, _mf.Cw);
                y += 26;
                _mf.Lbl(this, "先用顶栏【笔刷】涂抹或【框选】圈出区域，再选颜色上色", x, y, _mf.Cw);
                y += 20;
                pickRecolor = new ColorPicker();
                pickRecolor.Colors = new Color[] {
                    Color.FromArgb(30, 30, 30),      // 墨黑
                    Color.FromArgb(95, 95, 95),      // 灰（铅笔字）
                    Color.White,                     // 纯白
                    Color.FromArgb(200, 26, 32),     // 印章红
                    Color.FromArgb(25, 60, 150),     // 蓝墨水
                    Color.FromArgb(20, 110, 60),     // 绿
                    Color.FromArgb(205, 150, 0)      // 黄
                };
                pickRecolor.Names = new string[] { "黑", "灰", "白", "红", "蓝", "绿", "黄" };
                pickRecolor.Bounds = new Rectangle(x, y, _mf.Cw, 34);
                Controls.Add(pickRecolor);
                _mf.tips.SetToolTip(pickRecolor, "点色块选颜色（黑/灰/白/红/蓝/绿/黄）");
                y += 42;
                vRecolor = _mf.LblVal(this, x + _mf.Cw - 76, y, 76);
                _mf.Lbl(this, "浓淡", x, y, _mf.Cw - 84);
                y += 20;
                sRecolor = new FlatSlider();
                sRecolor.Bounds = new Rectangle(x, y, _mf.Cw, 26);
                sRecolor.Min = 20; sRecolor.Max = 100; sRecolor.Step = 5;
                sRecolor.ResetValue(100);
                vRecolor.Text = "100";
                sRecolor.ValueChanged += delegate { vRecolor.Text = sRecolor.Value.ToString(); };
                Controls.Add(sRecolor);
                y += 38;
                btnRecolor = _mf.Btn(this, "区域上色（Ctrl+Z 可撤销）", x, y, _mf.Cw, 38, "palette", FlatButton.Kinds.Primary, delegate { _mf.DoRegionColorize(); });
                y += 44;
                btnRecolorClear = _mf.Btn(this, "清除区域", x, y, _mf.Cw, 32, null, FlatButton.Kinds.Subtle, delegate { _mf.ClearMask(); });
                y += 42;

                _mf.PageEnd(this);
            }

            // 「印章」区控件（原 MainForm.BuildSealControls，逐行平移，仅加 _mf 前缀）
            int BuildSealControls(int y)
            {
                int x = Theme.Pad;
                // 工具选择已统一收进画布左上角的工具胶囊；进本页时自动切到【框选】
                btnSealDetect = _mf.Btn(this, "① 识别印章", x, y, _mf.Cw, 40, "wand", FlatButton.Kinds.Primary, delegate { _mf.DoSealDetect(); });
                _mf.tips.SetToolTip(btnSealDetect, "先在画面上【框选】圈出印章区域（可圈多处），再点这里自动找出公章");
                y += 50;
                _mf.Lbl(this, "上色范围", x, y, _mf.Cw);
                y += 20;
                // 两种上色范围：只描外框（章内文字、黑字一律不碰）/ 含内侧文字（连章内的字一起上红）
                segSealRange = new Segmented();
                segSealRange.Items = new string[] { "含内侧文字", "只描外框" };
                segSealRange.Bounds = new Rectangle(x, y, _mf.Cw, 32);
                segSealRange.SelectedChanged += delegate { _mf.RefreshSealOverlay(); };
                Controls.Add(segSealRange);
                segSealRange.SelectedIndex = 0;
                y += 40;
                vSealStrength = _mf.LblVal(this, x + _mf.Cw - 76, y, 76);
                _mf.Lbl(this, "上色浓度", x, y, _mf.Cw - 84);
                y += 20;
                sSeal = new FlatSlider();
                sSeal.Bounds = new Rectangle(x, y, _mf.Cw, 26);
                sSeal.Min = 20; sSeal.Max = 100; sSeal.Step = 5;
                sSeal.ResetValue(80);
                vSealStrength.Text = "80";
                sSeal.ValueChanged += delegate { vSealStrength.Text = sSeal.Value.ToString(); };
                Controls.Add(sSeal);
                y += 38;
                btnSealColor = _mf.Btn(this, "② 给印章上色", x, y, _mf.Cw, 40, "palette", FlatButton.Kinds.Ghost, delegate { _mf.DoSealColorize(); });
                y += 48;
                btnSealClear = _mf.Btn(this, "清除标记", x, y, _mf.Cw, 32, null, FlatButton.Kinds.Subtle, delegate { _mf.ClearMask(); });
                y += 42;
                return y;
            }
        }

        // ================= 印章逻辑（窗体侧，引用主窗体状态密集故不进类）=================

        // 识别印章：在标记区域内自动找出公章，结果替换为标记载荷（可再手动修补）
        public void DoSealDetect()
        {
            Doc d = Cur;
            if (!GuardAction(d)) return;   // 统一守卫
            if (d.Mask == null || Imaging.MaskArea(d.Mask) < 100)
            {
                Status("请先用【框选】圈出印章所在区域");
                Msg("请先用【框选】在画面上圈出印章所在的区域（可以圈多处），再点【识别印章】。");
                return;
            }
            if (busy) return;
            Bitmap src = SafeCopy(d.Current);
            Bitmap hint = Imaging.CopyMask(d.Mask);
            RunBusy("正在识别印章...", delegate
                {
                    int count;
                    Bitmap found;
                    Bitmap frame = null;
                    try { found = SealTool.Detect(src, hint, out count, out frame); }
                    finally { src.Dispose(); hint.Dispose(); }
                    BeginInvoke((MethodInvoker)delegate
                    {
                        int area = Imaging.MaskArea(found);
                        d.Mask.Dispose();
                        d.Mask = found;
                        if (sealFrameMask != null) { sealFrameMask.Dispose(); sealFrameMask = null; }
                        sealFrameMask = frame;
                        RefreshSealOverlay();   // 叠加层跟着上色范围走
                        canvas.Invalidate();
                        if (count == 0)
                            Status("没有找到印章。可以把区域圈得更贴合印章，或用【笔刷】直接涂出印章再上色");
                        else
                            Status("识别到 " + count + " 处疑似印章，共 " + area + " 像素。确认后点【给印章上色】");
                    });
                }, null);
        }

        // 上色范围的叠加显示：只描外框时只高亮外框，含内文时高亮整章
        public void RefreshSealOverlay()
        {
            Doc d = Cur;
            if (d == null || canvas == null) return;
            bool frameOnly = (segSealRange != null && segSealRange.SelectedIndex == 1);
            Bitmap srcMask = d.Mask;
            if (frameOnly && sealFrameMask != null && Imaging.Alive(sealFrameMask) && d.Mask != null &&
                sealFrameMask.Width == d.Mask.Width && sealFrameMask.Height == d.Mask.Height)
                srcMask = sealFrameMask;
            if (d.Overlay != null) { try { d.Overlay.Dispose(); } catch { } d.Overlay = null; }
            if (srcMask != null && Imaging.Alive(srcMask) && Imaging.MaskArea(srcMask) > 0)
                d.Overlay = Imaging.OverlayFromMask(srcMask, Color.FromArgb(255, 60, 40), 110);
            canvas.SetOverlay(d.Overlay);
            canvas.Invalidate();
        }

        // 上色：只改掩码范围内墨迹的颜色，保留浓淡（章内文字与五角星不变形）
        public void DoSealColorize()
        {
            Doc d = Cur;
            if (!GuardAction(d)) return;   // 统一守卫
            if (d.Mask == null || Imaging.MaskArea(d.Mask) < 20) { Status("还没有标记印章区域"); return; }
            long t0 = Environment.TickCount;
            Bitmap src = SafeCopy(d.Current);
            // 上色范围：只描外框时用识别出的"外框"掩码，章内文字与黑字一律不碰
            bool frameOnly = (segSealRange != null && segSealRange.SelectedIndex == 1);
            Bitmap srcMask = d.Mask;
            if (frameOnly && sealFrameMask != null && Imaging.Alive(sealFrameMask) &&
                sealFrameMask.Width == d.Mask.Width && sealFrameMask.Height == d.Mask.Height)
                srcMask = sealFrameMask;
            Bitmap mk = Imaging.CopyMask(srcMask);
            int strength = sSeal.Value;
            RunBusy("正在给印章上色...", delegate
                {
                    try { SealTool.Colorize(src, mk, strength); }
                    finally { mk.Dispose(); }
                    Bitmap result = src;
                    BeginInvoke((MethodInvoker)delegate
                    {
                        PushUndo(d, d.Current);
                        d.Current = result;
                        AfterEdit(true);
                        double sec = (Environment.TickCount - t0) / 1000.0;
                        Status("印章上色完成，耗时 " + sec.ToString("0.0") + " 秒（Ctrl+Z 可撤销）");
                    });
                }, null);
        }
    }
}
