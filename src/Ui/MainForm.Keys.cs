// MainForm.Keys.cs — 主窗体（partial）：快捷键 + 拖放
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PicTool
{
    public partial class MainForm : Form
    {
        // ---- 快捷键：自定义映射（Shortcuts.cs）+ 几条固定的顺手表盘 ----
        // 焦点在输入框里时，字母键要留给打字。
        // 原来靠 ContainerControl.ActiveControl 一路往下走链，只在"焦点控件本身就是 TextBox、且它是某个
        // ContainerControl 的直接子控件"时报 true。但本程序有两类**壳里包着真 TextBox**的输入面：
        //     · FlatTextBox —— 一个普通 Control 壳，里面装真 TextBox（文件页四个输入框）；
        //     · 画布文字输入卡片 —— Panel 里装真 TextBox。
        //   壳本身不是 ContainerControl，链走到壳就断了 → 误判为"没在打字" →
        //   在输入框里敲 b/r/e/t、空格、1~4 会被全局快捷键抢走（切工具 / 弹修复 / 切页）。
        //   改成直接扫"当前是否有获得键盘焦点的 TextBox"，不再依赖容器的嵌套方式。
        bool IsTypingInTextBox() { return HasFocusedTextBox(this); }

        static bool HasFocusedTextBox(Control root)
        {
            if (root == null) return false;
            if (root is TextBox && root.Focused) return true;
            for (int i = 0; i < root.Controls.Count; i++)
                if (HasFocusedTextBox(root.Controls[i])) return true;
            return false;
        }
        void OpenShortcutSettings()
        {
            using (ShortcutSettingsForm f = new ShortcutSettingsForm())
            {
                f.ShowDialog(this);
                Status("快捷键设置已保存（" + Shortcuts.FilePath + "）");
            }
        }
        // 空格键临时平移（专业修图软件通用手势）：按住空格+左键拖动=平移画面，松开恢复工具
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space && canvas != null && canvas.HasImage && !IsTypingInTextBox())
            {
                canvas.SetSpacePan(true);
                e.Handled = true;       // 别让空格再触发焦点按钮
                return;
            }
            base.OnKeyDown(e);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space && canvas != null)
            {
                canvas.SetSpacePan(false);
                e.Handled = true;
                return;
            }
            base.OnKeyUp(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            bool ctrl = (keyData & Keys.Control) == Keys.Control;
            // 正在输入框里打字：只放行 Ctrl 组合，其余交给输入框
            if (!ctrl && IsTypingInTextBox()) return base.ProcessCmdKey(ref msg, keyData);
            // 自定义快捷键（导航轨底部的「快捷键设置」里可随意改）
            if (Shortcuts.Hit(keyData, "tool.brush")) { SetTool(0); return true; }
            if (Shortcuts.Hit(keyData, "tool.rect")) { SetTool(1); return true; }
            if (Shortcuts.Hit(keyData, "tool.erase")) { SetTool(2); return true; }
            if (Shortcuts.Hit(keyData, "tool.text")) { SetTool(3); return true; }
            // Delete 删除选中的文字（正在输入框里打字时上面已放行，不会走到这里）
            if (keyData == Keys.Delete && toolIndex == 3 && canvas.SelText >= 0) { DeleteSelText(); return true; }
            // 左侧栏：F2 重命名 / Delete 删除到回收站
            if (keyData == Keys.F2 && ws.Selected >= 0 && ws.Selected < items.Count) { RenameItem(ws.Selected); return true; }
            if (keyData == Keys.Delete && ws.Selected >= 0 && ws.Selected < items.Count) { DeleteItem(ws.Selected); return true; }
            // 空格（默认绑定「开始修复」）有两层含义，按《用户使用手册》：
            //   有修复动作（已涂抹/框选）时 = 开始修复；没有修复动作时 = 按住空格拖动平移。
            // 以前这里无条件吃掉空格 → 总是走 DoInpaint（没有掩膜时只弹一句
            //   "请先圈出去除区域"），下面 OnKeyDown 的 SetSpacePan(true) 永远执行不到，
            //   "按住空格平移"成了死手势（手册与实际不符）。判断逻辑抽成 DecideInpaintKey 供自检覆盖。
            if (Shortcuts.Hit(keyData, "tool.inpaint"))
            {
                bool somethingToFix = busy || (Cur != null && Cur.Mask != null);
                InpaintKeyAction act = DecideInpaintKey(somethingToFix, keyData, canvas != null && canvas.HasImage);
                if (act == InpaintKeyAction.Pan) { canvas.SetSpacePan(true); return true; }  // KeyUp 里恢复
                DoInpaint();        // Fix：开修；busy 时 DoInpaint 内部即"取消修复"；Hint：给出提示
                return true;
            }
            if (Shortcuts.Hit(keyData, "crop.do")) { SwitchSectionTo(1); DoCropToSelection(); return true; }
            if (Shortcuts.Hit(keyData, "crop.trim")) { SwitchSectionTo(1); DoTrimBlank(); return true; }
            if (Shortcuts.Hit(keyData, "crop.a4")) { SwitchSectionTo(1); DoToA4(true); return true; }
            // 印章页是第 5 项（index 4）；若错写成 2（方向页）会导致 Ctrl+D 跳错页。
            // 页序定义见 MainForm.UiKit.cs 的 SwitchSection。
            if (Shortcuts.Hit(keyData, "seal.detect")) { SwitchSectionTo(4); DoSealDetect(); return true; }
            if (Shortcuts.Hit(keyData, "seal.color")) { SwitchSectionTo(4); DoSealColorize(); return true; }
            if (Shortcuts.Hit(keyData, "edit.undo")) { DoUndo(); return true; }
            if (Shortcuts.Hit(keyData, "edit.revert")) { DoRevert(); return true; }
            if (Shortcuts.Hit(keyData, "file.save")) { SaveCurrent(); return true; }
            if (Shortcuts.Hit(keyData, "file.saveas")) { SaveCurrentAs(); return true; }
            if (Shortcuts.Hit(keyData, "view.fit")) { canvas.Fit(); UpdateZoomLabel(); return true; }
            if (Shortcuts.Hit(keyData, "view.actual")) { canvas.SetZoom(1.0); UpdateZoomLabel(); return true; }
            if (Shortcuts.Hit(keyData, "view.zoomIn")) { canvas.SetZoom(canvas.Zoom * 1.25); UpdateZoomLabel(); return true; }
            if (Shortcuts.Hit(keyData, "view.zoomOut")) { canvas.SetZoom(canvas.Zoom / 1.25); UpdateZoomLabel(); return true; }
            if (Shortcuts.Hit(keyData, "nav.prev")) { SelectDoc(cur - 1); return true; }
            if (Shortcuts.Hit(keyData, "nav.next")) { SelectDoc(cur + 1); return true; }
            if (Shortcuts.Hit(keyData, "brush.plus")) { NudgeBrush(+4); return true; }
            if (Shortcuts.Hit(keyData, "brush.minus")) { NudgeBrush(-4); return true; }
            for (int n = 0; n < 9; n++)
                if (Shortcuts.Hit(keyData, "section." + (n + 1))) { SwitchSectionTo(n); return true; }
            // 固定顺手表：数字键/小键盘切工具、Ctrl+O 打开、0 适应窗口
            if (keyData == Keys.D1 || keyData == Keys.NumPad1) { SetTool(0); return true; }
            if (keyData == Keys.D2 || keyData == Keys.NumPad2) { SetTool(1); return true; }
            if (keyData == Keys.D3 || keyData == Keys.NumPad3) { SetTool(2); return true; }
            if (keyData == Keys.D4 || keyData == Keys.NumPad4) { SetTool(3); return true; }
            if (keyData == Keys.D0 || keyData == Keys.NumPad0) { canvas.Fit(); UpdateZoomLabel(); return true; }
            if (ctrl && (keyData & Keys.KeyCode) == Keys.O) { OpenFiles(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // 「开始修复」这个动作的键（默认空格）按下时到底该做什么。抽成纯函数是为了让自检
        // 能直接覆盖这个判断 —— 它曾经因为"无条件当修复"而让"按住空格平移"成了死手势。
        internal enum InpaintKeyAction { Fix, Pan, Hint }
        internal static InpaintKeyAction DecideInpaintKey(bool somethingToFix, Keys keyData, bool hasImage)
        {
            if (somethingToFix) return InpaintKeyAction.Fix;                       // 有东西可修（或正在修=取消）
            if (keyData == Keys.Space && hasImage) return InpaintKeyAction.Pan;     // 空格退化成"按住平移"
            return InpaintKeyAction.Hint;                                          // 其它键：给"请先圈出区域"提示
        }

        // 「开始修复」按钮的可用性：有图 + 已圈出区域才可点。
        // 修复进行中，这个按钮会变成「取消修复」（按钮上写着"点我取消"），此时**必须仍然可点**。
        // 300ms 心跳 RefreshInfoLine 里原来写着 `btnInpaint.Enabled = ... && !busy`，修复一开始
        // （≤300ms 内）就把按钮置灰，于是"点按钮取消修复"成了死交互 —— 只能靠再按一次空格取消。
        // 抽成纯函数：忙碌中仅当"这次忙碌就是修复自己"才保持可点（可点即取消）。
        internal static bool DecideInpaintEnabled(bool hasDoc, bool hasMask, bool busy, bool inpaintRunning)
        {
            if (!hasDoc || !hasMask) return false;
            return !busy || inpaintRunning;
        }

        // ---------------- 输入框打字判定自检（--uityping）----------------
        // 证明"焦点在输入框里时，全局快捷键不会把按键抢走"这条交互判定。新旧两套判定并排打印：
        // 旧判定（链式）在 FlatTextBox / 画布文字卡片这类"壳+真 TextBox"上会漏判。
        public string UiTypingProbe()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine(AppInfo.Name + " 输入框打字判定自检   " + DateTime.Now.ToString("HH:mm:ss"));
            sb.AppendLine("问题：焦点在输入框里时，按 b/r/e/t、空格、1~4 必须归输入框，不能被工具/分区快捷键抢走。");
            sb.AppendLine("每项都并排打印 旧判定（沿 ContainerControl 走链）与 新判定（扫全树找获得焦点的 TextBox）。");
            sb.AppendLine();
            SelectSection(7);                       // 文件页：四个 FlatTextBox 都在这一页
            Application.DoEvents();
            ProbeTyping(sb, "文件页·文件名前缀（FlatTextBox 壳内 TextBox）", txtRenamePrefix == null ? null : txtRenamePrefix.Inner);
            ProbeTyping(sb, "文件页·起始编号（FlatTextBox 壳内 TextBox）", txtRenameStart == null ? null : txtRenameStart.Inner);
            segRenameMode.SelectedIndex = 1;        // 切到"查找替换"模式，让下面两个框真正可见（否则 Focus 不生效）
            Application.DoEvents();
            ProbeTyping(sb, "文件页·查找（FlatTextBox 壳内 TextBox）", txtFind == null ? null : txtFind.Inner);
            ProbeTyping(sb, "文件页·替换为（FlatTextBox 壳内 TextBox）", txtReplace == null ? null : txtReplace.Inner);
            segRenameMode.SelectedIndex = 0;
            SelectSection(0);
            Application.DoEvents();

            // 关键对照样本：**非 Container 的壳**里放一个 Container、Container 里放真 TextBox。
            // 这正是"画布文字输入卡片"的形状（canvas 不是 ContainerControl，textCard 是 Panel）。
            // 旧判定走 ContainerControl 链，从 Form 出发到 canvas 就断了（canvas 不是 ContainerControl），
            // 够不到里面的 TextBox —— 预期旧判定在这里为 False。
            Panel probeHost = new Panel();
            probeHost.Bounds = new Rectangle(2, 2, 132, 28);
            TextBox probeBox = new TextBox();
            probeBox.Bounds = new Rectangle(2, 2, 110, 20);
            probeHost.Controls.Add(probeBox);
            canvas.Controls.Add(probeHost);
            probeHost.Visible = true;
            Application.DoEvents();
            ProbeTyping(sb, "合成样本·非Container壳→Panel→TextBox（同画布文字卡片）", probeBox);
            probeBox.Dispose();
            probeHost.Dispose();
            Application.DoEvents();
            return sb.ToString();
        }

        void ProbeTyping(System.Text.StringBuilder sb, string label, Control target)
        {
            if (target == null) { sb.AppendLine(label + "：控件不存在"); sb.AppendLine(); return; }
            target.Focus();
            Application.DoEvents();
            bool now = IsTypingInTextBox();
            bool oldWay = LegacyTypingVerdict();
            sb.AppendLine(label);
            sb.AppendLine("  取到焦点      : " + target.Focused);
            sb.AppendLine("  旧判定(链式)  : " + oldWay);
            sb.AppendLine("  新判定(扫树)  : " + now);
            sb.AppendLine("  结论          : " + (now ? "打字安全（按键不会被抢）"
                                              : "★ 打字不安全：字母键会被全局快捷键抢走"));
            sb.AppendLine();
        }

        // 旧实现（诊断留档，已不再用于判定）：沿 ContainerControl.ActiveControl 走链。
        bool LegacyTypingVerdict()
        {
            Control c = this;
            while (c is ContainerControl)
            {
                Control next = ((ContainerControl)c).ActiveControl;
                if (next == null || next == c) break;
                c = next;
            }
            return c is TextBox;
        }

        void SwitchSectionTo(int i)
        {
            if (i < 0 || i > 8) return;
            SelectSection(i);
        }
        void NudgeBrush(int delta)
        {
            if (sBrush == null) return;
            double v = sBrush.DoubleValue + delta;
            if (v < 2) v = 2;
            if (v > 200) v = 200;
            sBrush.SetDouble(v, true);
            vBrush.Text = ((int)v).ToString();
        }

        protected override void OnDragEnter(DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
            base.OnDragEnter(e);
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            HandleDrop(e.Data);
            base.OnDragDrop(e);
        }

    }
}
