// MainForm.Page.Remove.cs - 主窗体（partial）：去除页（手工去污）
// 去除页：单一职责——只做手工去污（笔刷涂抹 + 修复方式 + 开始修复）。
// 一键归档区已搬到「归档」页（原批量页位置）。
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
        void BuildRemovePage()
        {
            pageRemove = new RemovePage(this);      // 构造器内完成 NewPage 的等价注册
            pnlRemove = pageRemove;                 // 切页/滚动机制仍通过 pnlRemove 走
            pageRemove.Build();
        }

        // 去除页：一个 Panel 子类，自己管自己的控件。要加控件改这里，不要回主窗体。
        public sealed class RemovePage : Panel
        {
            readonly IPageHost _mf;

            internal Segmented segFix;
            internal FlatButton btnInpaint, btnClearMask;
            internal FlatSlider sBrush; internal Label vBrush;      // 笔刷（画布工具的参数行）

            internal RemovePage(IPageHost mf)
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
                y = _mf.PageHead(this, "去除", "涂掉污点、水印、划痕，再点【开始修复】", x, y, _mf.Cw);

                vBrush = _mf.LblVal(this, x + _mf.Cw - 76, y, 76);
                _mf.Lbl(this, "笔刷大小（Ctrl+滚轮）", x, y, _mf.Cw - 84);
                y += 20;
                sBrush = new FlatSlider();
                sBrush.Bounds = new Rectangle(x, y, _mf.Cw, 26);
                sBrush.Min = 4; sBrush.Max = 300; sBrush.Step = 1;
                sBrush.ResetValue(24);
                vBrush.Text = "24";
                sBrush.ValueChanged += delegate
                {
                    _mf.canvas.BrushRadius = sBrush.Value;
                    vBrush.Text = sBrush.Value.ToString();
                };
                Controls.Add(sBrush);
                _mf.canvas.BrushRadius = 24;
                y += 34;
                _mf.Lbl(this, "修复方式", x, y, _mf.Cw);
                y += 20;
                segFix = new Segmented();
                // AI 修复是可选叠加：只有 ai\ 目录里备齐运行时与模型时才出现这一项
                // 顺序固定：0=普通 1=增强 2=混合 3=纸面 [4=AI]（DoInpaint 按序号分发）
                segFix.Items = AiFill.Available
                    ? new string[] { "普通", "增强", "混合", "纸面", "AI" }
                    : new string[] { "普通", "增强", "混合", "纸面" };
                segFix.Bounds = new Rectangle(x, y, _mf.Cw, 32);
                segFix.SelectedChanged += delegate { _mf.UpdateFixHint(); };
                Controls.Add(segFix);
                segFix.SelectedIndex = 3;      // 默认“纸面”：档案扫描件的污渍/霉斑/折痕上实测最干净
                y += 42;
                btnInpaint = _mf.Btn(this, "开始修复", x, y, _mf.Cw, 40, "wand", FlatButton.Kinds.Primary, delegate { _mf.DoInpaint(); });
                y += 48;
                btnClearMask = _mf.Btn(this, "清除涂抹", x, y, _mf.Cw, 32, null, FlatButton.Kinds.Subtle, delegate { _mf.ClearMask(); });
                y += 44;

                _mf.PageEnd(this);
                _mf.UpdateFixHint();
            }
        }
    }
}
