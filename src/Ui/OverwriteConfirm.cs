// OverwriteConfirm.cs — 覆盖原图前的确认对话框（从 MainForm.cs 摘出的独立类）。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PicTool
{
    // ---------------- 覆盖原图确认对话框 ----------------
    public class OverwriteConfirm : Form
    {
        public bool DontAskAgain;
        bool dontAsk;

        public OverwriteConfirm(string message)
        {
            Text = "确认覆盖原图";
            Font = Theme.F12;
            BackColor = Theme.Panel;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(440, 216);

            Panel icon = new Panel();
            icon.Bounds = new Rectangle(20, 24, 28, 28);
            icon.Paint += delegate(object s, PaintEventArgs e)
            {
                Icons.Draw(e.Graphics, "warning", new Rectangle(2, 2, 24, 24), Theme.Danger, 1.8f);
            };
            Controls.Add(icon);

            Label title = new Label();
            title.Text = "覆盖原图";
            title.Font = Theme.F15B;
            title.ForeColor = Theme.Text;
            title.AutoSize = false;
            title.Bounds = new Rectangle(58, 22, 360, 24);
            Controls.Add(title);

            Label body = new Label();
            body.Text = message;
            body.Font = Theme.F12;
            body.ForeColor = Theme.TextSub;
            body.AutoSize = false;
            body.Bounds = new Rectangle(58, 52, 364, 96);
            Controls.Add(body);

            FlatSwitch ask = new FlatSwitch();
            ask.Text = "以后不再提醒";
            ask.Bounds = new Rectangle(20, 152, 200, 30);
            ask.CheckedChanged += delegate { dontAsk = ask.Checked; };
            Controls.Add(ask);

            FlatButton cancel = new FlatButton();
            cancel.Text = "取消";
            cancel.Kind = FlatButton.Kinds.Ghost;
            cancel.Bounds = new Rectangle(224, 152, 96, 32);
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancel);

            FlatButton ok = new FlatButton();
            ok.Text = "覆盖原图";
            ok.Kind = FlatButton.Kinds.Danger;
            ok.Bounds = new Rectangle(328, 152, 96, 32);
            ok.Click += delegate { DontAskAgain = dontAsk; DialogResult = DialogResult.OK; Close(); };
            Controls.Add(ok);

            // 不设 AcceptButton："覆盖原图"是破坏性动作，
            // 做默认键意味着误触 Enter 就覆盖。Esc 走 CancelButton 照样能取消。
            CancelButton = cancel;
        }
    }
}
