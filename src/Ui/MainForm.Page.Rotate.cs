// MainForm.Page.Rotate.cs - 主窗体（partial）：方向页（旋转/纠偏/镜像）
// 方向页被拆分为独立的 RotatePage 类：控件全部自治（字段在类内），构建与布局移入类内；
// delegate 里的主窗体成员经 _mf 前缀访问；主窗体侧保留只读别名属性，保证外部引用零改动。
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
        // ================= 方向页 =================
        // 原来寄居在"裁剪方向"页底部，需要滚动才看得到；现在左侧栏"方向"直接进入。
        void BuildRotatePage()
        {
            pageRotate = new RotatePage(this);      // 构造器内完成 NewPage 的等价注册
            pnlRotate = pageRotate;                 // 切页/滚动机制仍通过 pnlRotate 走
            int x = Theme.Pad, y = 12;
            y = PageHead(pnlRotate, "方向", "90° 快转、任意角度纠偏、镜像翻转", x, y, Cw);
            y = pageRotate.BuildControls(y);
            PageEnd(pnlRotate);
        }

        // 方向页：一个 Panel 子类，自己管自己的控件。要加控件改这里，不要回主窗体。
        public sealed class RotatePage : Panel
        {
            readonly IPageHost _mf;

            internal FlatSlider sAngle; internal Label vAngle;
            internal FlatSwitch swCrop; internal FlatSwitch swRoiOnly;
            internal FlatButton btnApplyRotate, btnRotL, btnRotR, btnDeskew;
            internal FlatButton btnFlipH, btnFlipV;

            internal RotatePage(IPageHost mf)
            {
                _mf = mf;
                // 与 NewPage() 相同的页面注册（Bounds/锚定/背景/默认隐藏/挂进内容区）
                Bounds = new Rectangle(0, 0, _mf.content.Width, _mf.content.Height);
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
                BackColor = Theme.Panel;
                Visible = false;
                _mf.content.Controls.Add(this);
            }

            // 构建方向页控件
            internal int BuildControls(int y)
            {
                int x = Theme.Pad;
                // 此处原有一个"方向与翻转"小标题，已删：与页头标题+说明完全重复。
                // 排版约定：一页只保留一个标题层级。
                vAngle = _mf.LblVal(this, x + _mf.Cw - 76, y, 76);
                _mf.Lbl(this, "角度", x, y, _mf.Cw - 84);
                y += 20;
                sAngle = new FlatSlider();
                sAngle.Bounds = new Rectangle(x, y, _mf.Cw, 26);
                sAngle.Min = -45; sAngle.Max = 45; sAngle.Step = 0.5;
                sAngle.ResetValue(0);
                vAngle.Text = "0°";
                sAngle.ValueChanged += delegate
                {
                    double v = sAngle.DoubleValue;
                    vAngle.Text = (v > 0 ? "+" : "") + v.ToString("0.#") + "°";
                    _mf.pendingPreview = 1;
                    _mf.previewTimer.Stop(); _mf.previewTimer.Start();
                };
                sAngle.ValueCommitted += delegate { _mf.CommitRotate(); };
                // 角度数值可点击输入：±45°、支持 0.5 步进的小数
                _mf.MakeValueEditable(vAngle, sAngle,
                    delegate(double v) { return v.ToString("0.#"); },
                    delegate(string t) { double d; if (!double.TryParse(t.Trim().TrimEnd('°'), out d)) return null; return d; });
                Controls.Add(sAngle);
                y += 34;
                swCrop = new FlatSwitch();
                swCrop.Text = "自动裁掉空白边";
                swCrop.Checked = true;
                swCrop.Bounds = new Rectangle(x, y, _mf.Cw, 30);
                Controls.Add(swCrop);
                y += 34;
                // 只纠正框选范围：先用画布上的【框选】画出范围，再拖角度，只把框内内容转正
                swRoiOnly = new FlatSwitch();
                swRoiOnly.Text = "只纠正框选范围";
                swRoiOnly.Checked = false;
                swRoiOnly.Bounds = new Rectangle(x, y, _mf.Cw, 30);
                swRoiOnly.CheckedChanged += delegate
                {
                    if (swRoiOnly.Checked)
                    {
                        _mf.SetTool(1);   // 打开就自动切到【框选】，省得用户找不到
                        // SetTool 会顺手写通用提示"拉一个矩形圈出去除区域"——在方向页说"去除"会
                        // 误导（裁剪页/印章页也各自覆盖过，同一条默认提示），这里按页覆盖成方向页的说法。
                        _mf.Status("方向：先用画布上的【框选】画出要纠正的范围，再拖角度滑条");
                    }
                    _mf.InvalidatePreview();
                };
                Controls.Add(swRoiOnly);
                y += 38;
                // 一键自动纠偏：投影法检测文字行倾斜角，检测到几度转几度，不用手动试滑条
                btnDeskew = _mf.Btn(this, "自动纠偏（一键转正）", x, y, _mf.Cw, 38, "wand", FlatButton.Kinds.Primary, delegate { _mf.DoAutoDeskew(); });
                y += 46;
                btnApplyRotate = _mf.Btn(this, "应用旋转（按上面角度）", x, y, _mf.Cw, 38, "rotate", FlatButton.Kinds.Ghost, delegate { _mf.CommitRotate(); });
                y += 46;
                btnRotL = _mf.Btn(this, "左转 90°", x, y, (_mf.Cw - 8) / 2, 32, null, FlatButton.Kinds.Ghost, delegate { _mf.DoRotate90(false); });
                btnRotR = _mf.Btn(this, "右转 90°", x + (_mf.Cw - 8) / 2 + 8, y, (_mf.Cw - 8) / 2, 32, null, FlatButton.Kinds.Ghost, delegate { _mf.DoRotate90(true); });
                y += 46;

                _mf.Divider(this, x, y, _mf.Cw);
                y += 14;
                _mf.SubLabel(this, "镜像翻转", x, y, _mf.Cw);
                y += 26;
                btnFlipH = _mf.Btn(this, "水平镜像", x, y, (_mf.Cw - 8) / 2, 36, "flipH", FlatButton.Kinds.Ghost, delegate { _mf.DoFlip(true); });
                btnFlipV = _mf.Btn(this, "垂直翻转", x + (_mf.Cw - 8) / 2 + 8, y, (_mf.Cw - 8) / 2, 36, "flipV", FlatButton.Kinds.Ghost, delegate { _mf.DoFlip(false); });
                y += 48;
                return y;
            }
        }
    }
}
