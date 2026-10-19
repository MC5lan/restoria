// MainForm.UiKit.cs — 主窗体（partial）：小控件工厂 + 分区/工具切换
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PicTool
{
    public partial class MainForm : Form
    {
        // ---------------- 小控件工厂 ----------------

        public Label Lbl(Panel host, string text, int x, int y, int w)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = Theme.F12;
            l.ForeColor = Theme.TextSub;
            l.AutoSize = false;
            l.Bounds = new Rectangle(x, y, w, 18);
            host.Controls.Add(l);
            return l;
        }

        public Label LblVal(Panel host, int x, int y, int w)
        {
            Label l = new Label();
            l.Text = "";
            l.Font = Theme.F12B;
            l.ForeColor = Theme.Text;
            l.TextAlign = ContentAlignment.MiddleRight;
            l.AutoSize = false;
            l.Bounds = new Rectangle(x, y, w, 18);
            host.Controls.Add(l);
            return l;
        }

        // ---------------- 数值行"点击可输入" ----------------

        // 点滑条右侧的数值 → 原地变输入框（右对齐、全选）；Enter/失焦提交、Esc 取消；
        // 输入值经 FlatSlider.SetDouble 自动钳到 [Min,Max]（上下限天然由滑条定义），
        // 再走与拖动松手同一条 ValueCommitted 提交链；非法输入提示并还原显示。
        // TextBox **首次点击时才创建**——构建期控件树零变化，不影响现有布局。
        // toEditor：内部值 → 编辑框初值文本（如 Gamma 100 → "1.00"）；fromEditor：输入文本 →
        // 内部值（返回 null = 非法）。显示格式不需要传——Label 文本由既有 ValueChanged 链负责。
        public void MakeValueEditable(Label value, FlatSlider s,
            Func<double, string> toEditor, Func<string, double?> fromEditor)
        {
            TextBox ed = null;
            // C#5 无局部函数：三个动作先声明为委托（闭包捕获变量本身），Click 注册放其后
            //（否则 CS0841 使用未赋值局部变量）
            Action ShowEdit = null, HideEdit = null, CommitEdit = null;
            ShowEdit = delegate
            {
                ed.Bounds = new Rectangle(value.Bounds.X, value.Bounds.Y - 2, value.Bounds.Width, 20);
                ed.Text = toEditor(s.DoubleValue);
                value.Visible = false;
                ed.Visible = true;
                ed.BringToFront();
                ed.Focus();
                ed.SelectAll();
            };
            HideEdit = delegate
            {
                if (ed != null) ed.Visible = false;
                value.Visible = true;
            };
            CommitEdit = delegate
            {
                if (ed == null || !ed.Visible) return;
                if (busy) { Status("正在处理中，请稍候"); HideEdit(); return; }
                double? v = fromEditor(ed.Text);
                if (v == null) { Status("输入无效——可输入范围 " + s.Min + " ~ " + s.Max); HideEdit(); return; }
                s.SetDouble(v.Value, true);   // clamp 到 [Min,Max] + 触发 ValueChanged（Label 文本 + 防抖预览）
                s.CommitFromEditor();         // 与拖动松手同一条提交链（应用调色/旋转）
                HideEdit();
            };
            value.Cursor = Cursors.IBeam;
            value.Click += delegate
            {
                if (busy) { Status("正在处理中，请稍候"); return; }
                if (ed == null)
                {
                    ed = new TextBox();
                    ed.BorderStyle = BorderStyle.None;
                    ed.BackColor = Theme.FieldBg;
                    ed.ForeColor = Theme.Text;
                    ed.Font = Theme.F12;
                    ed.TextAlign = HorizontalAlignment.Right;
                    ed.Visible = false;
                    value.Parent.Controls.Add(ed);
                    ed.KeyDown += delegate(object s2, KeyEventArgs e2)
                    {
                        if (e2.KeyCode == Keys.Enter) { e2.SuppressKeyPress = true; CommitEdit(); }
                        else if (e2.KeyCode == Keys.Escape) { e2.SuppressKeyPress = true; HideEdit(); }
                    };
                    ed.LostFocus += delegate { CommitEdit(); };
                }
                ShowEdit();
            };
        }

        public FlatButton Btn(Panel host, string text, int x, int y, int w, int h, string icon, FlatButton.Kinds kind, EventHandler onClick)
        {
            FlatButton b = new FlatButton();
            b.Text = text;
            b.Icon = icon;
            b.Kind = kind;
            b.Bounds = new Rectangle(x, y, w, h);
            b.Click += onClick;
            host.Controls.Add(b);
            return b;
        }

        public FlatSwitch Switch(Panel host, string text, int x, int y, int w, bool chk, int idx)
        {
            FlatSwitch s = new FlatSwitch();
            s.Text = text;
            s.Checked = chk;
            s.Bounds = new Rectangle(x, y, w, 30);
            host.Controls.Add(s);
            return s;
        }

        // 圆角提示卡片
        public Panel Hint(Panel host, int x, int y, int w, int h, string text)
        {
            Panel p = new Panel();
            p.Bounds = new Rectangle(x, y, w, h);
            p.BackColor = Theme.Panel;
            p.Paint += delegate(object s, PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                Rectangle r = new Rectangle(0, 0, p.Width - 1, p.Height - 1);
                Theme.FillRound(e.Graphics, r, Theme.Radius, Theme.WindowBg);
                Theme.StrokeRound(e.Graphics, r, Theme.Radius, Theme.Border, 1f);
                Theme.DrawText(e.Graphics, text, Theme.F11, new Rectangle(10, 8, p.Width - 20, p.Height - 16),
                    Theme.TextMuted, TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            };
            host.Controls.Add(p);
            return p;
        }

        // 切换功能页（导航轨点击 / 快捷键都走这里）
        void SelectSection(int i)
        {
            if (i < 0 || i > 8) i = 0;
            if (navRail != null && navRail.SelectedIndex != i)
            {
                // 导航轨滑到目标项（触发动画），但不要让它再回调回来
                navRail.SelectedIndexChanged -= RailChanged;
                navRail.SelectedIndex = i;
                navRail.SelectedIndexChanged += RailChanged;
            }
            sectionIndex = i;
            SwitchSection();
        }

        void RailChanged(object s, EventArgs e) { SelectSection(navRail.SelectedIndex); }

        void SwitchSection()
        {
            int i = sectionIndex;
            // 9 个页面：去除（去污）/ 裁剪 / 方向 / 调色 / 印章 / 文字 / 扫描 / 文件 / 归档 —— 每页只做一件事
            pnlRemove.Visible = i == 0;
            pnlCrop.Visible = i == 1;
            pnlRotate.Visible = i == 2;
            pnlColor.Visible = i == 3;
            pnlSeal.Visible = i == 4;
            pnlText.Visible = i == 5;
            pnlScan.Visible = i == 6;
            pnlFiles.Visible = i == 7;
            pnlBatch.Visible = i == 8;   // pnlBatch 容器名保留，内容已是「归档」页

            // 首次进扫描页：后台枚举一次扫描设备（省得启动时白等）
            if (i == 6 && !scanDevQueried)
            {
                scanDevQueried = true;
                RefreshDevices(false);
            }

            // 进去除页默认切回【笔刷】：这页第一件事就是涂（不然会残留上一页的文字/框选工具）
            if (i == 0) SetTool(0);

            // 进裁剪页默认切到【框选】：先圈范围是第一件事
            // （SetTool 给的是通用提示"圈出去除区域"——在裁剪页说"去除"会误导，按页覆盖掉）
            if (i == 1)
            {
                SetTool(1);
                Status("裁剪：先用画布上的【框选】拉出要保留的范围，再点【裁剪到选框】");
            }

            // 进印章页默认切到【框选】：印章第一步是圈区域
            if (i == 4)
            {
                SetTool(1);
                Status("印章：先用画布上的【框选】圈出印章区域，再点【识别印章】");
            }

            // 进文字页默认切到【文字】工具：第一件事就是在画面上加字
            // （文字页是 index 5；若错写成 8 会被归档页误触发）
            if (i == 5) SetTool(3);
            contentScroll = 0;
            ApplyContentScroll();
            if (canvas.HasImage) canvas.NotifyToolChanged();
        }

        // 工具选择的唯一入口（画布工具胶囊 / 快捷键 / 各页联动都走这里）
        public void SetTool(int i)
        {
            if (i < 0 || i > 3) i = 0;
            toolIndex = i;
            UpdateToolButtons();
            ApplyTool();
        }

        // 顶栏工具组的选中态：当前工具高亮
        void UpdateToolButtons()
        {
            if (btnToolBrush == null) return;
            btnToolBrush.Kind = toolIndex == 0 ? FlatButton.Kinds.Primary : FlatButton.Kinds.Ghost;
            btnToolRect.Kind = toolIndex == 1 ? FlatButton.Kinds.Primary : FlatButton.Kinds.Ghost;
            btnToolErase.Kind = toolIndex == 2 ? FlatButton.Kinds.Primary : FlatButton.Kinds.Ghost;
            btnToolText.Kind = toolIndex == 3 ? FlatButton.Kinds.Primary : FlatButton.Kinds.Ghost;
        }

        void ApplyTool()
        {
            int i = toolIndex;
            canvas.Tool = i == 0 ? CanvasTool.Brush
                       : i == 1 ? CanvasTool.Rect
                       : i == 3 ? CanvasTool.Text
                       : CanvasTool.Erase;
            canvas.NotifyToolChanged();
            UpdateFixHint();
            if (i == 0) Status("笔刷：涂抹要去除的区域");
            else if (i == 1) Status("框选：拉一个矩形圈出去除区域");
            else if (i == 3) Status("文字：单击添加，点中拖动，双击编辑，右下角手柄缩放");
            else Status("橡皮擦：擦掉涂抹范围");
        }

    }
}
