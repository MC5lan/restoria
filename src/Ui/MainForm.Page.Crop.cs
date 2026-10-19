// MainForm.Page.Crop.cs - 主窗体（partial）：裁剪 / A4 画板页
// P5 第三刀：页面类化——控件与构建逻辑自治（与 BatchPage 同模式）；
// 业务方法（DoCropToSelection/DoTrimBlank/DoToA4）引用主窗体状态密集，留在窗体侧
//（本文件下半段的 partial 部分），控件经别名属性访问，外部引用零改动。
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
        void BuildCropPage()
        {
            pageCrop = new CropPage(this);          // 构造器内完成 NewPage 的等价注册
            pnlCrop = pageCrop;                     // 切页/滚动机制仍通过 pnlCrop 走
            pageCrop.Build();
        }

        // 裁剪 / A4 画板页：一个 Panel 子类，自己管自己的控件。要加控件改这里。
        public sealed class CropPage : Panel
        {
            readonly IPageHost _mf;

            internal Segmented segCropAspect, segA4Dpi, segA4Orient, segA4Mode;
            internal FlatButton btnCropDo, btnTrimBlank, btnA4Crop, btnA4Only;
            internal FlatButton btnPersPts, btnPersApply, btnPersClear;   // 透视校正（逻辑在 Tools.cs）

            internal CropPage(IPageHost mf)
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
                y = _mf.PageHead(this, "裁剪", "框选范围裁剪；拍歪的用四点透视拉正", x, y, _mf.Cw);

                _mf.SubLabel(this, "按选框裁剪", x, y, _mf.Cw);
                y += 22;
                _mf.Lbl(this, "比例", x, y, _mf.Cw);
                y += 20;
                segCropAspect = new Segmented();
                segCropAspect.Items = new string[] { "自由", "1:1", "4:3", "3:4" };
                segCropAspect.Bounds = new Rectangle(x, y, _mf.Cw, 32);
                Controls.Add(segCropAspect);
                segCropAspect.SelectedIndex = 0;
                y += 40;
                btnCropDo = _mf.Btn(this, "裁剪到选框", x, y, _mf.Cw, 40, "crop", FlatButton.Kinds.Primary, delegate { _mf.DoCropToSelection(); });
                y += 48;
                btnTrimBlank = _mf.Btn(this, "自动裁白边", x, y, _mf.Cw, 32, null, FlatButton.Kinds.Ghost, delegate { _mf.DoTrimBlank(); });
                y += 42;

                _mf.Divider(this, x, y, _mf.Cw);
                y += 12;
                // 透视校正：拍照/斜拍文件四角不成直角时，框选矩形裁不干净，四点拉平才能还原成"正视角"
                _mf.SubLabel(this, "透视校正（拍歪的文件）", x, y, _mf.Cw);
                y += 24;
                btnPersPts = _mf.Btn(this, "取四个角点", x, y, (_mf.Cw - 8) / 2, 32, null, FlatButton.Kinds.Ghost, delegate { _mf.StartPerspective(); });
                _mf.tips.SetToolTip(btnPersPts, "点【取四个角点】后，在画面上依次点文件的：左上 → 右上 → 右下 → 左下。Esc 可重取。");
                btnPersClear = _mf.Btn(this, "清除取点", x + (_mf.Cw - 8) / 2 + 8, y, (_mf.Cw - 8) / 2, 32, null, FlatButton.Kinds.Subtle, delegate { _mf.canvas.ClearPersPoints(); _mf.Status("已清除透视取点"); });
                y += 40;
                btnPersApply = _mf.Btn(this, "应用透视校正", x, y, _mf.Cw, 40, "crop", FlatButton.Kinds.Ghost, delegate { _mf.DoPerspective(); });
                y += 48;

                _mf.Divider(this, x, y, _mf.Cw);
                y += 12;
                _mf.SubLabel(this, "A4 画板", x, y, _mf.Cw);
                y += 24;
                _mf.Lbl(this, "分辨率", x, y, _mf.Cw);
                y += 20;
                segA4Dpi = new Segmented();
                segA4Dpi.Items = new string[] { "150dpi", "200dpi", "300dpi" };
                segA4Dpi.Bounds = new Rectangle(x, y, _mf.Cw, 32);
                Controls.Add(segA4Dpi);
                segA4Dpi.SelectedIndex = 2;      // 归档默认 300dpi
                y += 40;
                _mf.Lbl(this, "方向", x, y, _mf.Cw);
                y += 20;
                segA4Orient = new Segmented();
                segA4Orient.Items = new string[] { "竖版 A4", "横版 A4" };
                segA4Orient.Bounds = new Rectangle(x, y, _mf.Cw, 32);
                Controls.Add(segA4Orient);
                segA4Orient.SelectedIndex = 0;
                y += 40;
                _mf.Lbl(this, "放入方式", x, y, _mf.Cw);
                y += 20;
                segA4Mode = new Segmented();
                segA4Mode.Items = new string[] { "适应留白", "铺满裁切" };
                segA4Mode.Bounds = new Rectangle(x, y, _mf.Cw, 32);
                Controls.Add(segA4Mode);
                segA4Mode.SelectedIndex = 0;
                y += 40;
                btnA4Crop = _mf.Btn(this, "裁剪并做成 A4", x, y, _mf.Cw, 40, "page", FlatButton.Kinds.Primary, delegate { _mf.DoToA4(true); });
                y += 48;
                btnA4Only = _mf.Btn(this, "只做成 A4", x, y, _mf.Cw, 32, null, FlatButton.Kinds.Ghost, delegate { _mf.DoToA4(false); });
                y += 44;

                _mf.PageEnd(this);
            }
        }

        // ================= 裁剪辅助（窗体侧逻辑，引用主窗体状态密集故不进类）=================

        double SelectedAspect()
        {
            if (segCropAspect == null) return 0;
            switch (segCropAspect.SelectedIndex)
            {
                case 1: return 1.0;
                case 2: return 4.0 / 3.0;
                case 3: return 3.0 / 4.0;
                default: return 0;
            }
        }

        // 取当前框选范围（可选按比例取中间最大块）；没有框选返回空矩形
        Rectangle CropRectFromSelection(bool applyAspect)
        {
            Doc d = Cur;
            if (d == null || d.Mask == null || !Imaging.Alive(d.Mask)) return Rectangle.Empty;
            Rectangle r = Imaging.MaskBounds(d.Mask);
            if (r.Width < 4 || r.Height < 4) return Rectangle.Empty;
            double a = applyAspect ? SelectedAspect() : 0;
            if (a > 0) r = Imaging.FitAspect(r, a);
            return r;
        }

        // 裁剪/改尺寸之后掩码与叠加层尺寸对不上，必须一起清掉
        void ClearAncillary(Doc d)
        {
            if (d == null) return;
            if (d.Mask != null) { try { d.Mask.Dispose(); } catch { } d.Mask = null; }
            if (d.Overlay != null) { try { d.Overlay.Dispose(); } catch { } d.Overlay = null; }
            if (sealFrameMask != null) { try { sealFrameMask.Dispose(); } catch { } sealFrameMask = null; }
        }

        public void DoCropToSelection()
        {
            Doc d = Cur;
            if (d == null) { Status("请先打开图片"); return; }
            if (busy) return;
            FlattenIfNeeded(d);
            Rectangle r = CropRectFromSelection(true);
            if (r.Width < 4 || r.Height < 4)
            {
                Status("请先用【框选】画出要保留的范围");
                Msg("请先在画面上用【框选】拉一个矩形，圈出想保留的部分，再点【裁剪到选框】。");
                return;
            }
            RunBusy("正在裁剪...", delegate
            {
                Bitmap n;
                Bitmap work = SafeCopy(d.Current);
                try { n = Imaging.Crop(work, r); }
                finally { work.Dispose(); }
                BeginInvoke((MethodInvoker)delegate
                {
                    PushUndo(d, d.Current);
                    d.Current = n;
                    ClearAncillary(d);
                    AfterEdit(true);
                    canvas.Fit();
                    Status("已裁剪为 " + n.Width + "×" + n.Height + " 像素");
                });
            }, null);
        }

        public void DoTrimBlank()
        {
            Doc d = Cur;
            if (d == null) { Status("请先打开图片"); return; }
            if (busy) return;
            FlattenIfNeeded(d);
            RunBusy("正在裁掉白边...", delegate
            {
                Bitmap n;
                Rectangle content;
                int ow, oh;
                Bitmap work = SafeCopy(d.Current);
                try
                {
                    ow = work.Width; oh = work.Height;
                    n = Imaging.TrimBlank(work, 26, out content);
                }
                finally { work.Dispose(); }
                BeginInvoke((MethodInvoker)delegate
                {
                    if (n.Width == ow && n.Height == oh)
                    {
                        n.Dispose();
                        Status("四周没有找到可裁的白边");
                        return;
                    }
                    PushUndo(d, d.Current);
                    d.Current = n;
                    ClearAncillary(d);
                    AfterEdit(true);
                    canvas.Fit();
                    Status("已裁掉白边：" + ow + "×" + oh + " → " + n.Width + "×" + n.Height);
                });
            }, null);
        }

        public void DoToA4(bool cropFirst)
        {
            Doc d = Cur;
            if (!GuardAction(d)) return;   // 统一守卫（原分离式漏写 busy 风险更大）
            FlattenIfNeeded(d);
            Rectangle r = Rectangle.Empty;
            if (cropFirst)
            {
                r = CropRectFromSelection(false);
                if (r.Width < 4 || r.Height < 4)
                {
                    Status("请先用【框选】画出要保留的范围");
                    Msg("【裁剪并做成 A4】需要先用【框选】画出要保留的范围。\n\n只想把整张图放进 A4 页面，请用下面的【只做成 A4（不裁剪）】。");
                    return;
                }
            }
            int dpi = segA4Dpi.SelectedIndex == 0 ? 150 : (segA4Dpi.SelectedIndex == 1 ? 200 : 300);
            bool land = segA4Orient.SelectedIndex == 1;
            bool fill = segA4Mode.SelectedIndex == 1;
            RunBusy("正在生成 A4 页面...", delegate
            {
                Bitmap page;
                Bitmap work = SafeCopy(d.Current);
                Bitmap cut = null;
                try
                {
                    Bitmap baseImg = work;
                    if (cropFirst) { cut = Imaging.Crop(work, r); baseImg = cut; }
                    page = Imaging.ToA4(baseImg, dpi, land, fill);
                }
                finally { if (cut != null) cut.Dispose(); work.Dispose(); }
                BeginInvoke((MethodInvoker)delegate
                {
                    PushUndo(d, d.Current);
                    d.Current = page;
                    ClearAncillary(d);
                    AfterEdit(true);
                    canvas.Fit();
                    Status("已生成标准 A4 页面：" + page.Width + "×" + page.Height + " 像素（" + dpi + "dpi，" +
                           (land ? "横版" : "竖版") + (fill ? "，铺满裁切" : "，适应留白") + "）");
                });
            }, null);
        }
    }
}
