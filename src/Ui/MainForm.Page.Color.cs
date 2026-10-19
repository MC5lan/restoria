// MainForm.Page.Color.cs - 主窗体（partial）：调色页（含直方图与色阶）
// P5 第三刀：页面类化——控件与构建逻辑自治，与 RotatePage/BatchPage 同模式。
// 两个滑条工厂（SliderRow/SliderRowAdv）只服务本页，一并移入；delegate 里的主窗体
// 成员经 _mf 前缀访问；主窗体侧留只读别名属性，外部引用零改动。
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
        void BuildColorPage()
        {
            pageColor = new ColorPage(this);        // 构造器内完成 NewPage 的等价注册
            pnlColor = pageColor;                   // 切页/滚动机制仍通过 pnlColor 走
            pageColor.Build();
        }

        // 调色页：一个 Panel 子类，自己管自己的控件。要加控件改这里，不要回主窗体。
        public sealed class ColorPage : Panel
        {
            readonly IPageHost _mf;

            internal FlatButton btnRecolor, btnRecolorClear;    // 区域上色（构建在印章页段，控件归本页）
            internal Label vBright, vContr, vSat, vTemp;
            internal FlatButton btnAuto, btnResetColor, btnApplyColor;
            internal FlatSlider sBright, sContr, sSat, sTemp;   // 从主窗体迁入（第二刀时判多页引用，第三刀随类收编）
            // 色阶 / 锐化 / 直方图（调色页工程级扩展）
            internal FlatSlider sBlack, sWhite, sGamma, sSharp;
            internal Label vBlack, vWhite, vGamma, vSharp;
            internal HistogramView histView;
            internal Doc histDoc;                    // 直方图归属的文档（换图/改图后重算）
            internal long[] histData;                // 当前直方图数据（256 级亮度）

            internal ColorPage(IPageHost mf)
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
                y = _mf.PageHead(this, "调色", "过暗用【自动增强】；数值可点击直接修改", x, y, _mf.Cw);
                // 直方图放最上面：拖黑场/白场时眼睛有依据，不用来回试
                histView = new HistogramView();
                histView.Bounds = new Rectangle(x, y, _mf.Cw, 50);
                Controls.Add(histView);
                y += 58;
                vBright = SliderRow(this, x, ref y, "亮度", -100, 100, 0, 1, 1);
                vContr = SliderRow(this, x, ref y, "对比度", -100, 100, 0, 1, 2);
                vSat = SliderRow(this, x, ref y, "饱和度", -100, 100, 0, 1, 3);
                vTemp = SliderRow(this, x, ref y, "色温", -100, 100, 0, 1, 4);

                _mf.Divider(this, x, y, _mf.Cw);
                y += 14;
                _mf.SubLabel(this, "色阶 · 白底黑字校准", x, y, _mf.Cw);
                y += 24;
                // 黑场/白场：把"该白的底"压到 255、"该黑的字"压到 0，扫描件灰蒙蒙的底色一次拉干净
                // （editFmt/parseEdit：数值点击可输入的编辑框进制——Gamma 显示 1.00 倍率、锐化带 %）
                sBlack = SliderRowAdv(this, x, ref y, "黑场", 0, 120, 0, 1,
                    delegate(double v) { return ((int)v).ToString(); }, out vBlack,
                    null, null);
                sWhite = SliderRowAdv(this, x, ref y, "白场", 130, 255, 255, 1,
                    delegate(double v) { return ((int)v).ToString(); }, out vWhite,
                    null, null);
                sGamma = SliderRowAdv(this, x, ref y, "Gamma", 10, 300, 100, 5,
                    delegate(double v) { return (v / 100.0).ToString("0.00"); }, out vGamma,
                    delegate(double v) { return (v / 100.0).ToString("0.##"); },
                    delegate(string t) { double d; if (!double.TryParse(t.Trim().TrimEnd('x', 'X'), out d) || d <= 0) return null; return d * 100; });
                sSharp = SliderRowAdv(this, x, ref y, "锐化", 0, 150, 0, 5,
                    delegate(double v) { return ((int)v) + "%"; }, out vSharp,
                    delegate(double v) { return Math.Round(v).ToString(); },
                    delegate(string t) { double d; if (!double.TryParse(t.Trim().TrimEnd('%'), out d)) return null; return d; });

                y += 4;
                btnAuto = _mf.Btn(this, "自动增强", x, y, (_mf.Cw - 8) / 2, 32, "wand", FlatButton.Kinds.Ghost, delegate { _mf.DoAutoEnhance(); });
                btnResetColor = _mf.Btn(this, "重置滑条", x + (_mf.Cw - 8) / 2 + 8, y, (_mf.Cw - 8) / 2, 32, null, FlatButton.Kinds.Subtle, delegate { _mf.ResetColorSliders(true); });
                y += 42;
                btnApplyColor = _mf.Btn(this, "应用调色", x, y, _mf.Cw, 38, "palette", FlatButton.Kinds.Primary, delegate { _mf.CommitColor(); });
                y += 44;

                _mf.PageEnd(this);
            }

            // 生成一行“标签 + 数值 + 滑条”，返回数值 Label（从 MainForm 移入：只服务本页）
            Label SliderRow(Panel host, int x, ref int y, string label, int min, int max, int val, double step, int idx)
            {
                Label value = _mf.LblVal(host, x + _mf.Cw - 76, y, 76);
                _mf.Lbl(host, label, x, y, _mf.Cw - 84);
                y += 17;
                FlatSlider s = new FlatSlider();
                s.Bounds = new Rectangle(x, y, _mf.Cw, 22);
                s.Min = min; s.Max = max; s.Step = step;
                s.ResetValue(val);
                value.Text = val > 0 ? "+" + val : val.ToString();
                s.ValueChanged += delegate
                {
                    int v = s.Value;
                    value.Text = v > 0 ? "+" + v : v.ToString();
                    _mf.pendingPreview = 2;
                    _mf.previewTimer.Stop(); _mf.previewTimer.Start();
                };
                s.ValueCommitted += delegate { _mf.CommitColor(); };
                // 数值可点击输入：亮度/对比度/饱和度/色温 = 整数
                _mf.MakeValueEditable(value, s,
                    delegate(double v) { return Math.Round(v).ToString(); },
                    delegate(string t) { double d; if (!double.TryParse(t.Trim().TrimStart('+'), out d)) return null; return d; });
                host.Controls.Add(s);
                if (idx == 1) sBright = s;
                else if (idx == 2) sContr = s;
                else if (idx == 3) sSat = s;
                else sTemp = s;
                y += 28;
                return value;
            }

            // 带自定义数值格式的滑条行（色阶组用：黑场/白场是绝对值，Gamma 是倍率）
            // editFmt/parseEdit（数值可输入）：编辑框初值与解析；null = 默认整数
            internal FlatSlider SliderRowAdv(Panel host, int x, ref int y, string label, int min, int max, int val, double step,
                                    FormatValueDelegate fmt, out Label value,
                                    Func<double, string> editFmt, Func<string, double?> parseEdit)
            {
                value = _mf.LblVal(host, x + _mf.Cw - 76, y, 76);
                _mf.Lbl(host, label, x, y, _mf.Cw - 84);
                y += 17;
                FlatSlider s = new FlatSlider();
                s.Bounds = new Rectangle(x, y, _mf.Cw, 22);
                s.Min = min; s.Max = max; s.Step = step;
                s.ResetValue(val);
                value.Text = fmt(val);
                Label vl = value;       // 匿名方法里不能用 out 参数，拷到局部变量
                s.ValueChanged += delegate
                {
                    vl.Text = fmt(s.DoubleValue);
                    _mf.pendingPreview = 2;
                    _mf.previewTimer.Stop(); _mf.previewTimer.Start();
                };
                s.ValueCommitted += delegate { _mf.CommitColor(); };
                // 数值可点击输入：默认整数进制，Gamma/锐化由调用方给转换
                Func<double, string> toEd = editFmt != null ? editFmt : (Func<double, string>)delegate(double v) { return Math.Round(v).ToString(); };
                Func<string, double?> fromEd = parseEdit != null ? parseEdit : (Func<string, double?>)delegate(string t) { double d; if (!double.TryParse(t.Trim(), out d)) return null; return d; };
                _mf.MakeValueEditable(value, s, toEd, fromEd);
                host.Controls.Add(s);
                y += 28;
                return s;
            }
        }
    }
}
