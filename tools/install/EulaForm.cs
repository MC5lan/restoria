// EulaForm.cs — 安装前的许可协议页（模态对话框，不接受则退出安装）。
//
// 为什么做成独立对话框而不是塞进向导的步骤条：
//   向导的步骤条是「检查环境 → 选择位置 → 正在安装 → 完成」四步，
//   协议不是安装步骤，它是安装的**前提**。独立一页，用户不接受就整个流程结束，
//   语义干净，也不用动向导的状态机。
//
// 视觉：与向导同一套（白底、主题字体、自绘扁平按钮）。
using System;
using System.Drawing;
using System.Windows.Forms;
using PicTool;

namespace PicToolSetup
{
    class EulaForm : Form
    {
        const int W = 656;
        const int H = 486;

        public EulaForm()
        {
            Text = "Restoria 安装程序";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(W, H);
            BackColor = Color.White;
            Font = Theme.F12;
            ShowInTaskbar = true;

            // 图标（尽量取自身图标；取不到就不放）
            try
            {
                PictureBox pic = new PictureBox();
                pic.Location = new Point(24, 20);
                pic.Size = new Size(28, 28);
                pic.SizeMode = PictureBoxSizeMode.StretchImage;
                Icon ico = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (ico != null) pic.Image = ico.ToBitmap();
                Controls.Add(pic);
            }
            catch { }

            Label t = new Label();
            t.AutoSize = false;
            t.Location = new Point(64, 22);
            t.Size = new Size(W - 88, 26);
            t.Font = Theme.F15B;
            t.ForeColor = Theme.Text;
            t.Text = "安装 — Restoria";
            Controls.Add(t);

            Label sub = new Label();
            sub.AutoSize = false;
            sub.Location = new Point(24, 62);
            sub.Size = new Size(W - 48, 20);
            sub.Font = Theme.F13B;
            sub.ForeColor = Theme.Text;
            sub.Text = EulaText.Title;
            Controls.Add(sub);

            Label tip = new Label();
            tip.AutoSize = false;
            tip.Location = new Point(24, 84);
            tip.Size = new Size(W - 48, 20);
            tip.Font = Theme.F11;
            tip.ForeColor = Theme.TextMuted;
            tip.Text = EulaText.Summary;
            Controls.Add(tip);

            Panel box = new Panel();
            box.Location = new Point(24, 110);
            box.Size = new Size(W - 48, H - 110 - 78);
            box.BackColor = Color.White;
            box.Paint += delegate (object s, PaintEventArgs e)
            {
                Theme.StrokeRound(e.Graphics, new Rectangle(0, 0, box.Width - 1, box.Height - 1),
                                  6, Theme.BorderDeep, 1f);
            };
            Controls.Add(box);

            // 只读多行文本框：系统控件，但这里必须用 —— 要能用鼠标滚轮、能 Ctrl+C、
            // 能键盘 PgDn，自绘富文本是负收益。
            TextBox view = new TextBox();
            view.Multiline = true;
            view.ReadOnly = true;
            view.ScrollBars = ScrollBars.Vertical;
            view.BorderStyle = BorderStyle.None;
            view.BackColor = Color.White;
            view.ForeColor = Theme.TextSub;
            view.Font = Theme.F12;
            view.WordWrap = true;
            view.TabStop = false;
            view.Text = EulaText.Text.Replace("\r\n", "\n").Replace("\n", "\r\n");
            view.Location = new Point(12, 10);
            view.Size = new Size(box.Width - 24, box.Height - 20);
            box.Controls.Add(view);

            FlatButton no = new FlatButton();
            no.Text = "我不接受";
            no.Size = new Size(104, 34);
            no.Location = new Point(W - 24 - 140 - 8 - 104, H - 24 - 34);
            no.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(no);

            FlatButton yes = new FlatButton();
            yes.Primary = true;
            yes.Text = "我接受并安装";
            yes.Size = new Size(140, 34);
            yes.Location = new Point(W - 24 - 140, H - 24 - 34);
            yes.Click += delegate { DialogResult = DialogResult.OK; Close(); };
            Controls.Add(yes);

            AcceptButton = yes;
            CancelButton = no;
        }
    }
}
