// ShortcutSettings.cs — 快捷键设置对话框：点一下按键框，按下新键即改（Esc 取消，Backspace 清除）
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PicTool
{
    delegate void RowArmedHandler(KeyRow row);

    // 一行：动作名 + 按键框
    sealed class KeyRow : Control
    {
        readonly ShortcutAction act;
        bool armed, hover;

        public KeyRow(ShortcutAction a)
        {
            act = a;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = 30;
            Cursor = Cursors.Hand;
            TabStop = false;
        }

        public ShortcutAction Action { get { return act; } }
        public bool Armed { get { return armed; } }
        public void Arm(bool on) { armed = on; Invalidate(); }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && !armed && ActionArmed != null) ActionArmed(this);
            base.OnMouseDown(e);
        }

        public event RowArmedHandler ActionArmed;

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (hover && !armed)
                Theme.FillRound(g, new Rectangle(0, 2, Width, Height - 4), 6, Theme.HoverBg);

            Rectangle nameR = new Rectangle(8, 0, Width - 130, Height);
            Theme.DrawText(g, act.Name, Theme.F12, nameR, Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);

            Rectangle box = new Rectangle(Width - 118, 4, 110, Height - 8);
            bool conflict = false;
            if (!armed)
            {
                ShortcutAction other = Shortcuts.WhoHas(act.Key, act);
                conflict = other != null;
            }
            Theme.FillRound(g, box, 6, armed ? Theme.AccentSoft : Theme.FieldBg);
            Theme.StrokeRound(g, box, 6, armed ? Theme.Accent : (conflict ? Theme.Danger : Theme.Border), 1f);

            string txt = armed ? "按下新键…" : Shortcuts.Text(act.Key);
            Color fg = armed ? Theme.Accent : (conflict ? Theme.Danger : Theme.Text);
            if (act.Key == Keys.None && !armed) fg = Theme.TextMuted;
            if (conflict && !armed) txt += "（冲突）";
            Theme.DrawText(g, txt, Theme.F12, box, fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    public sealed class ShortcutSettingsForm : Form
    {
        List<KeyRow> rows = new List<KeyRow>();
        KeyRow armedRow;
        Label hint;

        public ShortcutSettingsForm()
        {
            Text = "快捷键设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.WindowBg;
            Font = Theme.F12;
            KeyPreview = true;

            List<ShortcutAction> all = Shortcuts.All;
            int colW = 322, top = 46;
            const int rowPitch = 30, headPitch = 26;   // 行间距 / 分组标题占位

            // 按 Group 归组（保持动作表里的出现顺序：工具 / 操作 / 编辑 / 视图 / 分区）
            List<string> groups = new List<string>();
            Dictionary<string, List<ShortcutAction>> byGroup = new Dictionary<string, List<ShortcutAction>>();
            for (int i = 0; i < all.Count; i++)
            {
                string g = all[i].Group;
                if (g == null) g = "";
                if (!byGroup.ContainsKey(g)) { byGroup[g] = new List<ShortcutAction>(); groups.Add(g); }
                byGroup[g].Add(all[i]);
            }

            // 两栏贪心均衡：每栏总高 = 该栏各「组标题 + 组内行高」之和；
            // 若把整组塞进左栏会明显超出均衡线，就换到右栏（整组不拆栏，免得看着乱）
            int totalPx = 0;
            for (int gi = 0; gi < groups.Count; gi++)
                totalPx += headPitch + byGroup[groups[gi]].Count * rowPitch;
            int halfPx = (totalPx + 1) / 2;

            int[] colY = new int[] { top, top };
            int curCol = 0;
            for (int gi = 0; gi < groups.Count; gi++)
            {
                string g = groups[gi];
                List<ShortcutAction> gs = byGroup[g];
                int gh = headPitch + gs.Count * rowPitch;
                if (curCol == 0 && colY[0] > top && (colY[0] - top) + gh > halfPx + rowPitch) curCol = 1;

                int gx = 16 + curCol * (colW + 8);
                Label cap = new Label();
                cap.Text = g;
                cap.Font = Theme.F12B;
                cap.ForeColor = Theme.TextMuted;
                cap.AutoSize = false;
                cap.Bounds = new Rectangle(gx, colY[curCol] + 4, colW, 18);
                Controls.Add(cap);
                colY[curCol] += headPitch;

                for (int k = 0; k < gs.Count; k++)
                {
                    KeyRow r = new KeyRow(gs[k]);
                    r.Bounds = new Rectangle(gx, colY[curCol], colW, 30);
                    r.ActionArmed += delegate(KeyRow sender) { ArmRow(sender); };
                    Controls.Add(r);
                    rows.Add(r);
                    colY[curCol] += rowPitch;
                }
            }

            // 高度按"最高的那一栏"算：列底 + 间隔 + 固定按键说明(三行) + 按钮行高 32 + 下边距 14
            // （固定高度在动作条目增加后会挤爆最后一行，底部按钮会压到按键框上）
            int rowsBottom = Math.Max(colY[0], colY[1]);
            const int fixedH = 54;                  // 「固定按键」只读说明占位：补了 Enter/Tab 说明后为三行，原 36 会裁掉第三行
            int h = rowsBottom + 8 + fixedH + 32 + 14;
            ClientSize = new Size(680, h);

            // 固定顺手表：这些键写死在 ProcessCmdKey 里、不在上面的可改列表中。
            // 不写出来的话，用户会以为"这份列表就是全部按键"（设置面板在骗人）。
            // ★ ：文案与拒绝判据共用 Shortcuts.FixedKeysNote —— 以前这里把
            //   "空格 开始修复"也列进"不可修改"，可上方"开始修复=空格"就是可改行，自相矛盾；
            //   同时补上 Enter/Tab 两个不可绑的骨架键说明。
            Label fixedKeys = new Label();
            fixedKeys.Text = Shortcuts.FixedKeysNote;
            fixedKeys.Font = Theme.F11;
            fixedKeys.ForeColor = Theme.TextMuted;
            fixedKeys.AutoSize = false;
            fixedKeys.Bounds = new Rectangle(16, rowsBottom + 8, 648, fixedH);
            Controls.Add(fixedKeys);

            Label title = new Label();
            title.Text = "快捷键设置";
            title.Font = Theme.F15B;
            title.ForeColor = Theme.Text;
            title.AutoSize = false;
            title.Bounds = new Rectangle(16, 14, 300, 24);
            Controls.Add(title);

            hint = new Label();
            hint.Text = "点右边按键框即可修改（Esc 取消，Backspace 清除）";
            hint.Font = Theme.F11;
            hint.ForeColor = Theme.TextMuted;
            hint.AutoSize = false;
            hint.Bounds = new Rectangle(16, h - 38, 420, 20);
            Controls.Add(hint);

            FlatButton reset = new FlatButton();
            reset.Text = "恢复默认"; reset.Kind = FlatButton.Kinds.Subtle;
            reset.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            reset.Bounds = new Rectangle(ClientSize.Width - 218, h - 46, 96, 32);
            reset.Click += delegate { Shortcuts.ResetDefaults(true); Refresh2(); hint.Text = "已恢复默认快捷键"; };
            Controls.Add(reset);

            FlatButton done = new FlatButton();
            done.Text = "完成"; done.Kind = FlatButton.Kinds.Primary;
            done.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            done.Bounds = new Rectangle(ClientSize.Width - 114, h - 46, 98, 32);
            done.Click += delegate { Close(); };
            Controls.Add(done);
            CancelButton = null;
        }

        void Refresh2() { for (int i = 0; i < rows.Count; i++) rows[i].Invalidate(); }

        void ArmRow(KeyRow r)
        {
            if (armedRow != null) armedRow.Arm(false);
            armedRow = r;
            r.Arm(true);
            hint.Text = "请按下新键（Esc 取消，Backspace 清除）";
        }

        void Disarm()
        {
            if (armedRow != null) armedRow.Arm(false);
            armedRow = null;
            hint.Text = "点右边按键框即可修改（Esc 取消，Backspace 清除）";
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (armedRow == null) return base.ProcessCmdKey(ref msg, keyData);

            Keys body = keyData & Keys.KeyCode;
            if (body == Keys.Escape) { Disarm(); return true; }
            if (body == Keys.Back || body == Keys.Delete)
            {
                armedRow.Action.Key = Keys.None;
                Shortcuts.Save();
                Disarm();
                Refresh2();
                return true;
            }
            if (body == Keys.ShiftKey || body == Keys.ControlKey || body == Keys.Menu) return true;   // 只按修饰键不算

            // 保留键（交互）：固定功能键绑了会双触发，Enter/Tab 绑了会坏掉
            // 对话框默认键与焦点遍历。以前这里只查可改集占用，这两类键能被直接写进去。
            string reject = Shortcuts.RejectReason(keyData);
            if (reject != null)
            {
                hint.Text = reject == "skeleton"
                    ? "【" + Shortcuts.Text(keyData) + "】是系统骨架键（对话框/焦点用），不能设为快捷键"
                    : "【" + Shortcuts.Text(keyData) + "】是固定按键（不可修改），请换一个键";
                return true;   // 保持等待状态，让用户重新按
            }

            ShortcutAction other = Shortcuts.WhoHas(keyData, armedRow.Action);
            if (other != null)
            {
                hint.Text = "【" + Shortcuts.Text(keyData) + "】已被「" + other.Name + "」占用，换一个键";
                return true;   // 保持等待状态，让用户重新按
            }
            armedRow.Action.Key = keyData;
            Shortcuts.Save();
            Disarm();
            Refresh2();
            return true;
        }

        // 自检钩子（--selftest 调用，无头）：验证"等待态守卫"——Enter/Tab/固定功能键必须
        // 被拒绝且保持等待，普通键必须能绑。测完把第一个动作的键还原原值，不污染 keys.ini。
        // 之前这段没有任何守卫：Enter/Tab 能被绑走（废掉对话框默认键/焦点遍历），
        // 固定功能键（1-4/0/Ctrl+O/F2/Delete）能被绑走（与写死的固定处理双触发）。
        internal string ProbeArmGuards()
        {
            if (rows.Count == 0) return "快捷键设置窗没有行（面板没建起来）";
            KeyRow r = rows[0];
            Keys orig = r.Action.Key;
            System.Text.StringBuilder bad = new System.Text.StringBuilder();
            try
            {
                ArmRow(r);
                if (armedRow != r) return "进入等待态失败";
                Keys[] mustReject = new Keys[] {
                    Keys.Enter, Keys.Tab,
                    Keys.D1, Keys.NumPad1, Keys.D4, Keys.D0, Keys.NumPad0,
                    Keys.F2, Keys.Control | Keys.O };
                for (int i = 0; i < mustReject.Length; i++)
                {
                    Keys k = mustReject[i];
                    Message m = new Message();
                    m.Msg = 0x100;   // WM_KEYDOWN（ProcessCmdKey 只看 keyData，这里给个像样的消息）
                    m.WParam = (IntPtr)k;
                    ProcessCmdKey(ref m, k);
                    if (armedRow == null)
                    {
                        bad.Append("按 " + Shortcuts.Text(k) + " 竟解除了等待态（应保持等待并拒绝）；");
                        ArmRow(r);
                    }
                    if (r.Action.Key == k)
                    {
                        bad.Append(Shortcuts.Text(k) + " 被写进动作（应拒绝）；");
                        r.Action.Key = Keys.None;
                    }
                }
                // 反证：一个没人占用的普通字母键必须能正常绑定（证明不是"一律拒绝"）
                Keys free = Keys.None;
                for (Keys k = Keys.A; k <= Keys.Z; k++)
                    if (Shortcuts.WhoHas(k, r.Action) == null && Shortcuts.RejectReason(k) == null) { free = k; break; }
                if (free != Keys.None)
                {
                    Message m = new Message();
                    m.Msg = 0x100;
                    m.WParam = (IntPtr)free;
                    ProcessCmdKey(ref m, free);
                    if (armedRow != null) bad.Append("普通键 " + Shortcuts.Text(free) + " 竟没进入赋值（等待态没解除）；");
                    else if (r.Action.Key != free) bad.Append("普通键 " + Shortcuts.Text(free) + " 没写进动作；");
                }
            }
            finally
            {
                bool changed = r.Action.Key != orig;
                r.Action.Key = orig;      // 还原，别把用户的键改掉
                if (changed) Shortcuts.Save();   // 只在真被改动过才落盘，自检平时不碰 keys.ini
                Disarm();
            }
            return bad.Length == 0 ? null : bad.ToString();
        }
    }
}
