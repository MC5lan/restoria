// MainForm.Text.cs — 主窗体（partial）：添加/编辑文字（文字元素层）
// 由「文字元素层 + 显式烙印」方案引入（见 docs/添加文字功能方案.md）
// 提交点模型：textSnapshot 永远保存"上一次提交后"的文字快照；
// 任何文字编辑落地时调 CommitTexts() —— 把旧快照压进撤销栈，再刷新快照。
// P5 第三刀：构建类化进 TextPage（与 BatchPage 同模式）；
// 业务方法（ApplyTextStyle/RefreshTextInspector/CommitTexts/TextPlaceAt 等）
// 引用主窗体状态密集，留在本文件下半段的窗体侧。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Windows.Forms;

namespace PicTool
{
    public partial class MainForm : Form
    {
        // ★ P5 第三刀：页容器字段行已搬回 MainForm.cs（7 个 pnlXxx 是
        //   各页壳的赋值目标，物理位置应在主文件，不该寄居在 Text.cs）
        // ★ P5 第三刀：文字页类化——控件移入 TextPage，这里留只读别名
        TextPage pageText;              // 文字页（控件在类内）
        FlatDropDown ddFont { get { return pageText.ddFont; } }
        ColorPicker pickTextColor { get { return pageText.pickTextColor; } }
        FlatToggle tgBold { get { return pageText.tgBold; } }
        FlatToggle tgItalic { get { return pageText.tgItalic; } }
        FlatToggle tgUnderline { get { return pageText.tgUnderline; } }
        FlatToggle tgAlignL { get { return pageText.tgAlignL; } }
        FlatToggle tgAlignC { get { return pageText.tgAlignC; } }
        FlatToggle tgAlignR { get { return pageText.tgAlignR; } }
        Label textStyleLabel { get { return pageText.textStyleLabel; } }
        FlatButton btnTextBurn { get { return pageText.btnTextBurn; } }
        FlatButton btnTextUp { get { return pageText.btnTextUp; } }
        FlatButton btnTextDown { get { return pageText.btnTextDown; } }
        FlatButton btnTextDel { get { return pageText.btnTextDel; } }
        FlatSlider sTextSize { get { return pageText.sTextSize; } }
        Label vTextSize { get { return pageText.vTextSize; } }
        bool syncingText;               // 检查器同步时抑制控件事件回路（窗体侧与类内共用）
        List<TextItem> textSnapshot;    // 文字提交点快照：上一次提交后的文字状态（撤销栈的对照基准）

        // ================= 文字页 =================

        void BuildTextPage()
        {
            pageText = new TextPage(this);          // 构造器内完成 NewPage 的等价注册
            pnlText = pageText;                     // 切页/滚动机制仍通过 pnlText 走
            pageText.Build();
        }

        // 文字页：一个 Panel 子类，自己管自己的控件。要加控件改这里，不要回主窗体。
        public sealed class TextPage : Panel
        {
            readonly IPageHost _mf;

            internal FlatDropDown ddFont;
            internal ColorPicker pickTextColor;
            internal FlatToggle tgBold, tgItalic, tgUnderline, tgAlignL, tgAlignC, tgAlignR;
            internal Label textStyleLabel;
            internal FlatButton btnTextBurn, btnTextUp, btnTextDown, btnTextDel;
            internal FlatSlider sTextSize; internal Label vTextSize;

            internal TextPage(IPageHost mf)
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
                y = _mf.PageHead(this, "文字", "画面上单击即可加字；选中后可改样式", x, y, _mf.Cw);

                // 1) 样式区（选中文字才能改；未选中时整块变灰，并在下面提示怎么选中）
                textStyleLabel = _mf.Lbl(this, "文字样式（先点选画面上的文字）", x, y, _mf.Cw);
                y += 22;
                ddFont = new FlatDropDown();
                ddFont.Bounds = new Rectangle(x, y, _mf.Cw, 32);
                ddFont.ItemFont = delegate(string name)
                {
                    try { return new Font(name, 13f); }      // 列表里用自己的字体预览
                    catch { return Theme.F12; }
                };
                try
                {
                    FontFamily[] fams = new InstalledFontCollection().Families;
                    List<string> names = new List<string>();
                    for (int i = 0; i < fams.Length; i++)
                    {
                        // 只列至少有一种常规/粗体样式的族，过滤掉只带竖排等特殊样式的字体
                        if (fams[i].IsStyleAvailable(FontStyle.Regular) || fams[i].IsStyleAvailable(FontStyle.Bold))
                            names.Add(fams[i].Name);
                    }
                    names.Sort(StringComparer.OrdinalIgnoreCase);
                    ddFont.Items = names.ToArray();
                }
                catch { }
                if (ddFont.Items.Length == 0) ddFont.Items = new string[] { "宋体" };
                ddFont.SelectedChanged += delegate { if (!_mf.syncingText) { _mf.ApplyTextStyle(); _mf.CommitTexts(); } };
                Controls.Add(ddFont);
                y += 40;

                vTextSize = _mf.LblVal(this, x + _mf.Cw - 76, y, 76);
                _mf.Lbl(this, "字号（像素）", x, y, _mf.Cw - 84);
                y += 20;
                sTextSize = new FlatSlider();
                sTextSize.Bounds = new Rectangle(x, y, _mf.Cw, 26);
                sTextSize.Min = 8; sTextSize.Max = 200; sTextSize.Step = 1;
                sTextSize.ResetValue(36);
                vTextSize.Text = "36";
                sTextSize.ValueChanged += delegate
                {
                    vTextSize.Text = sTextSize.Value.ToString();
                    if (!_mf.syncingText) _mf.ApplyTextStyle();     // 拖动实时预览，松手才提交
                };
                sTextSize.ValueCommitted += delegate { _mf.ApplyTextStyle(); _mf.CommitTexts(); };
                Controls.Add(sTextSize);
                y += 40;

                _mf.Lbl(this, "颜色", x, y, _mf.Cw);
                y += 20;
                pickTextColor = new ColorPicker();
                pickTextColor.Colors = new Color[] {
                    Color.FromArgb(32, 30, 28),      // 墨黑
                    Color.FromArgb(28, 50, 120),     // 蓝黑墨水
                    Color.FromArgb(200, 30, 40),     // 印章红
                    Color.White                      // 纯白
                };
                pickTextColor.Names = new string[] { "墨黑", "蓝黑", "印章红", "纯白" };
                pickTextColor.Bounds = new Rectangle(x, y, _mf.Cw, 34);
                pickTextColor.SelectedChanged += delegate { if (!_mf.syncingText) { _mf.ApplyTextStyle(); _mf.CommitTexts(); } };
                Controls.Add(pickTextColor);
                _mf.tips.SetToolTip(pickTextColor, "墨黑 / 蓝黑墨水 / 印章红 / 纯白（点色块选择）");
                y += 42;

                // 加粗/斜体/下划线做成字样式切换格（原来三个开关又占地方又不像"字样式"）
                int tw = 46;
                tgBold = Toggle(this, "B", null, new Font("Segoe UI", 10f, FontStyle.Bold), x, y, tw);
                tgItalic = Toggle(this, "I", null, new Font("Segoe UI", 10f, FontStyle.Italic), x + tw + 8, y, tw);
                tgUnderline = Toggle(this, "U", null, new Font("Segoe UI", 10f, FontStyle.Underline), x + (tw + 8) * 2, y, tw);
                _mf.Lbl(this, "加粗 / 斜体 / 下划线", x + (tw + 8) * 3 + 6, y + 6, _mf.Cw - (tw + 8) * 3 - 6);
                y += 40;

                _mf.Lbl(this, "对齐", x, y, _mf.Cw);
                y += 20;
                int aw = (_mf.Cw - 16) / 3;
                tgAlignL = Toggle(this, null, "alignLeft", null, x, y, aw);
                tgAlignC = Toggle(this, null, "alignCenter", null, x + aw + 8, y, aw);
                tgAlignR = Toggle(this, null, "alignRight", null, x + (aw + 8) * 2, y, aw);
                y += 42;

                btnTextBurn = _mf.Btn(this, "烙印到图片", x, y, _mf.Cw, 40, "stamp", FlatButton.Kinds.Primary, delegate { _mf.DoBurnTexts(); });
                _mf.tips.SetToolTip(btnTextBurn, "把画面上所有文字永久写进图片（之后可 Ctrl+Z 整步撤销）；保存前没烙印也会自动烙");
                y += 48;
                btnTextUp = _mf.Btn(this, "上移一层", x, y, (_mf.Cw - 8) / 2, 32, "layers", FlatButton.Kinds.Ghost, delegate { _mf.MoveSelText(-1); });
                btnTextDown = _mf.Btn(this, "下移一层", x + (_mf.Cw - 8) / 2 + 8, y, (_mf.Cw - 8) / 2, 32, "layers", FlatButton.Kinds.Ghost, delegate { _mf.MoveSelText(1); });
                y += 40;
                btnTextDel = _mf.Btn(this, "删除选中文字", x, y, _mf.Cw, 32, "close", FlatButton.Kinds.Danger, delegate { _mf.DeleteSelText(); });
                y += 40;
                _mf.PageH(this, y + 12);
                _mf.RefreshTextInspector();
            }

            // 文字页的小切换格（B / I / U、左中右对齐）
            FlatToggle Toggle(Panel host, string glyph, string icon, Font glyphFont, int x, int y, int w)
            {
                FlatToggle t = new FlatToggle();
                t.Glyph = glyph;
                t.Icon = icon;
                t.GlyphFont = glyphFont;
                t.Bounds = new Rectangle(x, y, w, 32);
                t.CheckedChanged += delegate { if (!_mf.syncingText) { _mf.ApplyTextStyle(); _mf.CommitTexts(); } };
                host.Controls.Add(t);
                return t;
            }
        }

        static Color TextColorFor(int idx)
        {
            switch (idx)
            {
                case 1: return Color.FromArgb(28, 50, 120);     // 蓝黑墨水
                case 2: return Color.FromArgb(200, 30, 40);     // 印章红
                case 3: return Color.White;
                default: return Color.FromArgb(32, 30, 28);     // 墨黑
            }
        }

        static int TextColorIndexOf(Color c)
        {
            if (c.ToArgb() == Color.FromArgb(28, 50, 120).ToArgb()) return 1;
            if (c.ToArgb() == Color.FromArgb(200, 30, 40).ToArgb()) return 2;
            if (c.ToArgb() == Color.White.ToArgb()) return 3;
            return 0;
        }

        // 把检查器上的样式套到选中的文字元素（无选中时不做任何事）
        public void ApplyTextStyle()
        {
            Doc d = Cur;
            if (d == null || canvas.SelText < 0 || canvas.SelText >= d.Texts.Count) return;
            TextItem t = d.Texts[canvas.SelText];
            if (ddFont.SelectedIndex >= 0 && ddFont.SelectedText.Length > 0) t.FontName = ddFont.SelectedText;
            t.SizeEm = TextStamp.ClampSize(sTextSize.Value);
            t.Bold = tgBold.Checked;
            t.Italic = tgItalic.Checked;
            t.Underline = tgUnderline.Checked;
            t.Align = tgAlignC.Checked ? 1 : (tgAlignR.Checked ? 2 : 0);
            t.Color = pickTextColor.SelectedColor;
            TextStamp.UpdateBox(t);
            canvas.Invalidate();
        }

        // 检查器 ↔ 选中元素 双向同步；syncingText 防止改控件时又触发事件回路
        public void RefreshTextInspector()
        {
            if (pnlText == null || ddFont == null) return;
            Doc d = Cur;
            TextItem t = (d != null && canvas.SelText >= 0 && canvas.SelText < d.Texts.Count) ? d.Texts[canvas.SelText] : null;
            bool has = t != null;
            // ★ busy 时一并置灰（）：烙印跑着的时候改字体/字号/颜色，
            //   会直接写 d.Texts，与正在光栅化的画面竞争。以前只有烙印按钮看 !busy。
            bool on = has && !busy;
            ddFont.Enabled = sTextSize.Enabled = pickTextColor.Enabled = on;
            tgBold.Enabled = tgItalic.Enabled = tgUnderline.Enabled = on;
            tgAlignL.Enabled = tgAlignC.Enabled = tgAlignR.Enabled = on;
            btnTextDel.Enabled = btnTextUp.Enabled = btnTextDown.Enabled = on;
            btnTextBurn.Enabled = d != null && d.Texts.Count > 0 && !busy;
            if (textStyleLabel != null)
            {
                // 未选中文字时把"为什么全是灰的"说清楚，别让人以为界面坏了
                textStyleLabel.Text = has ? "文字样式" : (d == null ? "文字样式（先打开一张图片）" : "文字样式（先点选画面上的文字）");
                textStyleLabel.ForeColor = has ? Theme.TextSub : Theme.TextMuted;
            }
            if (!has || syncingText) return;
            syncingText = true;
            try
            {
                int fi = Array.IndexOf(ddFont.Items, t.FontName);
                if (fi >= 0) ddFont.SelectedIndex = fi;
                sTextSize.ResetValue((int)t.SizeEm);
                vTextSize.Text = ((int)t.SizeEm).ToString();
                tgBold.Checked = t.Bold;
                tgItalic.Checked = t.Italic;
                tgUnderline.Checked = t.Underline;
                tgAlignL.Checked = t.Align == 0;
                tgAlignC.Checked = t.Align == 1;
                tgAlignR.Checked = t.Align == 2;
                pickTextColor.SelectedIndex = TextColorIndexOf(t.Color);
            }
            finally { syncingText = false; }
        }

        // ---------------- 提交点 / 撤销快照 ----------------

        // 任何文字编辑落地后调用：把"上一次提交"的快照压进撤销栈（一次编辑 = 一步撤销）
        public void CommitTexts()
        {
            Doc d = Cur;
            if (d == null) return;
            if (textSnapshot == null) { textSnapshot = CloneTexts(d.Texts); return; }
            if (textSnapshot.Count == d.Texts.Count && !TextsDiffer(textSnapshot, d.Texts)) return;
            EditRecord r = new EditRecord();
            r.Texts = textSnapshot;                 // 旧状态进栈
            d.Undo.Add(r);
            TrimUndo(d);
            textSnapshot = CloneTexts(d.Texts);     // 新状态成为下一次的"旧快照"
            d.Edits = true;
            d.LastUse = ++lru;
            RefreshHeader();
            RefreshTextInspector();
            ws.Invalidate();
            NoteRecoveryFlattened(d);               // 恢复快照里必须带字
        }

        static bool TextsDiffer(List<TextItem> a, List<TextItem> b)
        {
            for (int i = 0; i < a.Count; i++)
            {
                TextItem x = a[i], y = b[i];
                if (x.Text != y.Text || x.FontName != y.FontName || x.SizeEm != y.SizeEm ||
                    x.Bold != y.Bold || x.Italic != y.Italic || x.Underline != y.Underline ||
                    x.Align != y.Align || x.Color.ToArgb() != y.Color.ToArgb() || x.Box != y.Box) return true;
            }
            return false;
        }

        // ---------------- 文字工具动作 ----------------

        // 画布空白处单击（文字工具）：新建一段默认文字并直接弹出输入窗
        void TextPlaceAt(float ix, float iy)
        {
            Doc d = Cur;
            if (d == null || busy) { Status("请先打开图片"); return; }
            canvas.CommitTextEditor();              // 先把上一次输入落地（可能已由画布提交过，幂等）
            TextItem t = new TextItem();
            t.Text = "";
            // 样式取检查器上当前的选择，用户改过一次就沿用它
            t.FontName = ddFont != null && ddFont.SelectedText.Length > 0 ? ddFont.SelectedText : "宋体";
            t.SizeEm = sTextSize != null ? TextStamp.ClampSize(sTextSize.Value) : 36f;
            if (pickTextColor != null && pickTextColor.SelectedIndex >= 0) t.Color = pickTextColor.SelectedColor;
            if (tgAlignC != null && tgAlignC.Checked) t.Align = 1;
            else if (tgAlignR != null && tgAlignR.Checked) t.Align = 2;
            t.Box = new RectangleF(ix, iy, 12f, 12f);
            TextStamp.UpdateBox(t);
            d.Texts.Add(t);
            SyncCanvasTexts();
            canvas.SelText = d.Texts.Count - 1;
            // 这里不 CommitTexts：等输入窗确认了非空文字再记一步撤销，
            // 空文字会被画布自动清掉，不占撤销步
            canvas.BeginTextEdit(d.Texts.Count - 1);
            Status("在输入窗里打字，Ctrl+Enter 确定，Esc 取消");
        }

        void OnTextsCommitted()
        {
            CommitTexts();
        }

        public void DeleteSelText()
        {
            Doc d = Cur;
            if (!GuardAction(d)) return;   // 统一守卫
            canvas.CancelTextEditor();              // 删除时正在输入的内容一并放弃（空文字会被顺手清掉）
            int idx = canvas.SelText;               // CancelTextEditor 可能已改变选中状态，重新取
            if (idx < 0 || idx >= d.Texts.Count) { Status("先在画面上点选一段文字"); return; }
            d.Texts.RemoveAt(idx);
            canvas.SelText = -1;
            CommitTexts();
            SyncCanvasTexts();
            Status("已删除文字（Ctrl+Z 可撤销）");
        }

        public void MoveSelText(int delta)
        {
            Doc d = Cur;
            if (!GuardAction(d)) return;   // 统一守卫
            int i = canvas.SelText;
            if (i < 0 || i >= d.Texts.Count) { Status("先在画面上点选一段文字"); return; }
            int j = i + delta;
            if (j < 0 || j >= d.Texts.Count) { Status(delta < 0 ? "已经在最底层了" : "已经在最顶层了"); return; }
            TextItem t = d.Texts[i];
            d.Texts[i] = d.Texts[j];
            d.Texts[j] = t;
            canvas.SelText = j;
            CommitTexts();
            SyncCanvasTexts();
            Status(delta < 0 ? "已上移一层" : "已下移一层");
        }

        // 烙印：未烙印文字一次性落进位图（FlattenIfNeeded 里走合并撤销记录）
        public void DoBurnTexts()
        {
            Doc d = Cur;
            if (d == null || busy) { Status("请先打开图片"); return; }
            if (d.Texts.Count == 0) { Status("画面上还没有文字"); return; }
            canvas.CommitTextEditor();
            CommitTexts();
            int n = d.Texts.Count;
            FlattenIfNeeded(d);
            AfterEdit(false);
            Status("已把 " + n + " 段文字烙进图片（Ctrl+Z 可整步撤销）");
        }

    }
}
