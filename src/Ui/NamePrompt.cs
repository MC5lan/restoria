// NamePrompt.cs — 主窗体外的小对话框：输入名称（左侧栏重命名用）
//
// 设计要点：
// - 与项目 UI 规范一致：不裸用原生控件做主体外观，输入框与按钮都用 Flat* 系列；
// - 只做一件事（改一个名字），所以只有 1 个输入框 + 2 个按钮；
// - ★ 校验在窗内完成（Win7 实测反馈"弹窗要好几次才能关掉"）：
//   调用方通过 Validator 委托给校验规则（非法字符、重名），校验失败**原地行内红字提示**
//   不关窗 —— 旧实现"确认关窗 → 调用方发现重名 → 弹失败框 → 重新右键重走全流程"，
//   用户感知就是同一个改名操作要反复弹好几次窗。
using System;
using System.Drawing;
using System.Windows.Forms;

namespace PicTool
{
    public sealed class NamePrompt : Form
    {
        FlatTextBox box;
        Label err;
        public string Value = "";

        // 校验委托：输入候选名 → 返回错误消息（null = 通过）。由调用方设置（非法字符、重名等）。
        public Func<string, string> Validator;

        // title：标题（如"重命名图片"）；label：说明；init：初始文本（图片只给主名，扩展名不参与）
        public NamePrompt(string title, string label, string init)
        {
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(380, 192);
            BackColor = Theme.Panel;
            Font = Theme.F12;
            Text = title;

            Label t = new Label();
            t.Text = title;
            t.Font = Theme.F15B;
            t.ForeColor = Theme.Text;
            t.AutoSize = false;
            t.Bounds = new Rectangle(18, 14, 344, 24);
            Controls.Add(t);

            Label s = new Label();
            s.Text = label;
            s.Font = Theme.F11;
            s.ForeColor = Theme.TextMuted;
            s.AutoSize = false;
            s.Bounds = new Rectangle(18, 42, 344, 34);
            Controls.Add(s);

            box = new FlatTextBox();
            box.Bounds = new Rectangle(18, 80, 344, 34);
            box.Text = init == null ? "" : init;
            box.Inner.SelectAll();
            Controls.Add(box);

            // 行内错误提示：校验失败红字显示在这里，不关窗、不弹原生框
            err = new Label();
            err.Text = "";
            err.Font = Theme.F11;
            err.ForeColor = Theme.Danger;
            err.AutoSize = false;
            err.Bounds = new Rectangle(18, 118, 344, 18);
            err.Visible = false;
            Controls.Add(err);

            FlatButton ok = new FlatButton();
            ok.Text = "确定";
            ok.Kind = FlatButton.Kinds.Primary;
            ok.Bounds = new Rectangle(18, 146, 166, 34);
            ok.Click += delegate { Accept(); };
            Controls.Add(ok);

            FlatButton cancel = new FlatButton();
            cancel.Text = "取消";
            cancel.Kind = FlatButton.Kinds.Ghost;
            cancel.Bounds = new Rectangle(196, 146, 166, 34);
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancel);

            AcceptButton = null;
            KeyPreview = true;
            KeyDown += delegate(object o, KeyEventArgs e)
            {
                // SuppressKeyPress：只 e.Handled 挡不住键继续进焦点控件，
                // Win7 + 输入法场景下 Enter 会穿透造成"确认两次"的错觉
                if (e.KeyCode == Keys.Enter) { Accept(); e.Handled = true; e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
            };
            Shown += delegate { box.Focus(); box.Inner.SelectAll(); };
        }

        void Accept()
        {
            string v = (box.Text == null ? "" : box.Text).Trim();
            if (v.Length == 0) { SetErr("名称不能为空。"); return; }
            if (Validator != null)
            {
                string problem = Validator(v);
                if (problem != null) { SetErr(problem); return; }
            }
            Value = v;
            DialogResult = DialogResult.OK;
            Close();
        }

        void SetErr(string msg)
        {
            err.Text = msg;
            err.Visible = true;
            box.Focus();            // 只聚焦不全选：用户多半只想微调，别把他输的字全抹了
        }
    }
}

namespace PicTool
{
    // 通用确认框（危险操作前用）：只有"确认 / 取消"两个扁平按钮，
    // 与 OverwriteConfirm 同风格；确认文案可定制（如"删除"）。
    // 长列表场景（如批量重命名确认）用可调高度重载，正文区自动滚动，信息一条不砍。
    public sealed class ConfirmBox : Form
    {
        public ConfirmBox(string title, string message, string okText)
            : this(title, message, okText, 210)
        {
        }

        public ConfirmBox(string title, string message, string okText, int height)
        {
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Theme.Panel;
            Font = Theme.F12;
            Text = title;
            ClientSize = new Size(440, height);

            Panel icon = new Panel();
            icon.Bounds = new Rectangle(20, 24, 28, 28);
            icon.Paint += delegate(object s, PaintEventArgs e)
            {
                Icons.Draw(e.Graphics, "warning", new Rectangle(2, 2, 24, 24), Theme.Danger, 1.8f);
            };
            Controls.Add(icon);

            Label t = new Label();
            t.Text = title;
            t.Font = Theme.F15B;
            t.ForeColor = Theme.Text;
            t.AutoSize = false;
            t.Bounds = new Rectangle(58, 22, 362, 24);
            Controls.Add(t);

            // 正文放进可滚动容器：内容超高一屏内滚着看完，信息一条不砍
            Panel bodyHost = new Panel();
            bodyHost.Bounds = new Rectangle(58, 52, 362, height - 118);
            bodyHost.AutoScroll = true;
            bodyHost.BackColor = Theme.Panel;
            Controls.Add(bodyHost);

            Label body = new Label();
            body.Text = message;
            body.Font = Theme.F12;
            body.ForeColor = Theme.TextSub;
            body.AutoSize = true;
            body.Bounds = new Rectangle(0, 0, 350, 18);
            bodyHost.Controls.Add(body);

            int btnY = height - 54;
            FlatButton ok = new FlatButton();
            ok.Text = okText;
            ok.Kind = FlatButton.Kinds.Danger;
            ok.Bounds = new Rectangle(20, btnY, 196, 36);
            ok.Click += delegate { DialogResult = DialogResult.OK; Close(); };
            Controls.Add(ok);

            FlatButton cancel = new FlatButton();
            cancel.Text = "取消";
            cancel.Kind = FlatButton.Kinds.Ghost;
            cancel.Bounds = new Rectangle(224, btnY, 196, 36);
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancel);

            KeyPreview = true;
            KeyDown += delegate(object o, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
            };
        }
    }
}
