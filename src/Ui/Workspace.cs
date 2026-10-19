// Workspace.cs — 文档模型（含内存溢出机制）+ 左侧工作区面板
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace PicTool
{
    // 统一撤销记录（文字功能）：位图编辑与文字编辑共用一条栈，
    // 记录里 Img 与 Texts 至少一个非空；烙印操作两者都填 —— 一次 Ctrl+Z 整步退回。
    public class EditRecord
    {
        public Bitmap Img;               // 位图快照；null = 本次不涉及位图
        public List<TextItem> Texts;     // 文字快照；null = 本次不涉及文字

        public void Dispose()
        {
            if (Img != null) { try { Img.Dispose(); } catch { } Img = null; }
            if (Texts != null) Texts = null;   // 纯数据，直接丢弃
        }
    }

    // 一个打开的图片。全分辨率位图占用大，超过内存预算时自动“溢出”到临时文件，
    // 切回时再恢复 —— 保证 2GB 内存的机器也不崩。
    public class Doc : IDisposable
    {
        public string FilePath;
        public Bitmap Current;
        public Bitmap Original;
        public Bitmap Mask;         // 8bpp 修复遮罩
        public Bitmap Overlay;      // 32bpp 遮罩可视化
        public Bitmap Thumb;
        public List<EditRecord> Undo = new List<EditRecord>();
        public List<TextItem> Texts = new List<TextItem>();   // 未烙印的文字元素（层级 = 列表序）
        public bool Spilled;
        public string TempPath;
        public bool Edits;
        public long LastUse;
        public int WsIndex = -1;    // 在工作区列表里的行号

        public string Name { get { return FilePath == null ? "" : Path.GetFileName(FilePath); } }

        public void Dispose()
        {
            if (Current != null) { Current.Dispose(); Current = null; }
            if (Original != null) { Original.Dispose(); Original = null; }
            if (Mask != null) { Mask.Dispose(); Mask = null; }
            if (Overlay != null) { Overlay.Dispose(); Overlay = null; }
            if (Thumb != null) { Thumb.Dispose(); Thumb = null; }
            for (int i = 0; i < Undo.Count; i++) Undo[i].Dispose();
            Undo.Clear();
            Texts.Clear();
            try { if (TempPath != null && File.Exists(TempPath)) File.Delete(TempPath); } catch { }
            TempPath = null;
        }

        public long MemBytes
        {
            get
            {
                long b = 0;
                if (Current != null) b += (long)Current.Width * Current.Height * 3;
                if (Original != null) b += (long)Original.Width * Original.Height * 3;
                if (Mask != null) b += (long)Mask.Width * Mask.Height;            // 8bpp 遮罩
                if (Overlay != null) b += (long)Overlay.Width * Overlay.Height * 4;   // 32bpp 可视化层
                for (int i = 0; i < Undo.Count; i++) b += Undo[i].Img != null ? (long)Undo[i].Img.Width * Undo[i].Img.Height * 3 : 0;
                return b;
            }
        }

        public void Spill(string tempDir)
        {
            if (Spilled || Current == null) return;
            // 未烙印的文字先落进位图再溢出：临时文件是位图快照，恢复后不会带回 Texts，
            // 不在这里烙印，用户切几张图回来文字就凭空消失了
            if (Texts.Count > 0)
            {
                try { TextStamp.Rasterize(Current, Texts); Texts.Clear(); Edits = true; } catch { }
            }
            TempPath = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".jpg");
            Imaging.SaveImage(Current, TempPath, 92);
            FreeMemory();
            Spilled = true;
        }

        void FreeMemory()
        {
            if (Current != null) { Current.Dispose(); Current = null; }
            if (Original != null) { Original.Dispose(); Original = null; }
            if (Mask != null) { Mask.Dispose(); Mask = null; }
            if (Overlay != null) { Overlay.Dispose(); Overlay = null; }
            for (int i = 0; i < Undo.Count; i++) Undo[i].Dispose();
            Undo.Clear();
            Texts.Clear();      // 文字快照很便宜，但清空最稳（恢复时 Texts 会随位图一起重算语义）
        }

        public void Restore()
        {
            if (!Spilled || TempPath == null || !File.Exists(TempPath)) { Spilled = false; return; }
            Bitmap loaded = Imaging.LoadFile(TempPath);
            Current = loaded;
            // “原始基准”优先从源文件重新读取：临时文件是有损 JPEG，不能拿来当还原依据
            Bitmap orig = null;
            if (FilePath != null) { try { orig = Imaging.LoadFile(FilePath); } catch { orig = null; } }
            Original = orig != null ? orig : Imaging.Ensure24bpp(new Bitmap(loaded));
            Spilled = false;
        }

        public bool EnsureMask()
        {
            if (Current == null) return false;
            if (Mask != null && Mask.Width == Current.Width && Mask.Height == Current.Height) return false;
            if (Mask != null) Mask.Dispose();
            if (Overlay != null) Overlay.Dispose();
            Mask = Imaging.MakeMask(Current.Width, Current.Height);
            Overlay = new Bitmap(Current.Width, Current.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            return true;
        }

        public void ClearMask()
        {
            if (Mask != null) { Mask.Dispose(); Mask = null; }
            if (Overlay != null) { Overlay.Dispose(); Overlay = null; }
        }
    }

    // 工作区列表条目
    public class WsItem
    {
        public bool IsParent;
        public bool IsFolder;
        public string FullPath;
        public string Name;
        public string Meta;
        public Bitmap Thumb;
        public bool Loading;
        public int DocIndex = -1;
        public Doc Doc;
    }

    public delegate void WsVoidDelegate();

    // ---------------- 左侧工作区面板 ----------------
    // 布局是"固定框"结构：页眉（文件夹名/路径栏/按钮）和页脚（统计）是独立的子控件——
    // 真实窗口互相遮挡，列表永远只能画在中间剩余区域，结构上不可能再与页眉页脚重叠。
    public class WorkspacePanel : Control
    {
        public List<WsItem> Items = new List<WsItem>();
        public int Selected = -1;
        public string Folder = "";
        public string FolderTitle = "未打开文件夹";

        public event Action<string> OpenPath;          // 回车/确认路径
        public event Action<WsItem> ImageClicked;      // 单击图片
        public event Action<WsItem> FolderEntered;     // 双击（或单击上级）
        public event WsVoidDelegate ChooseFolder;      // 点"更换"按钮
        public event WsVoidDelegate RefreshClicked;
        // 行右键菜单：行号 + 屏幕坐标（主窗体负责弹菜单并执行操作）
        public event Action<int, Point> RowMenu;

        Control header, footer;
        TextBox pathBox;
        int scrollY, hoverRow = -1;
        bool barDragging;
        int barGrabY, barGrabScroll;
        Rectangle barThumb = Rectangle.Empty;
        bool barVisible;
        int hoverBtn = -1;      // 0=更换 1=刷新（页眉内）
        string loadingText = "";

        public const int HeaderH = 86;
        public const int FooterH = 30;
        const int PadX = 12;

        public WorkspacePanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Panel;

            // 页眉固定框
            header = new Control();
            header.Dock = DockStyle.Top;
            header.Height = HeaderH;
            header.BackColor = Theme.Panel;
            header.Paint += DrawHeader;
            header.MouseMove += HeaderMouseMove;
            header.MouseLeave += delegate { if (hoverBtn != -1) { hoverBtn = -1; header.Invalidate(); } };
            header.MouseDown += HeaderMouseDown;
            header.Resize += delegate { PlacePathBox(); };
            Controls.Add(header);

            pathBox = new TextBox();
            pathBox.BorderStyle = BorderStyle.None;
            pathBox.BackColor = Theme.FieldBg;
            pathBox.ForeColor = Theme.TextSub;
            pathBox.Font = Theme.F11;
            pathBox.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    if (OpenPath != null) OpenPath(pathBox.Text.Trim());
                }
            };
            pathBox.Enter += delegate { header.Invalidate(); };
            pathBox.Leave += delegate { header.Invalidate(); };
            header.Controls.Add(pathBox);

            // 页脚固定框
            footer = new Control();
            footer.Dock = DockStyle.Bottom;
            footer.Height = FooterH;
            footer.BackColor = Theme.Panel;
            footer.Paint += DrawFooter;
            Controls.Add(footer);

            Cursor = Cursors.Default;
        }

        void PlacePathBox()
        {
            pathBox.Bounds = new Rectangle(PadX + 8, 52, Math.Max(40, header.Width - PadX * 2 - 16), 18);
        }

        public void SetFolder(string folderTitle, string path, List<WsItem> items)
        {
            FolderTitle = folderTitle;
            Folder = path;
            Items = items;
            Selected = -1;
            scrollY = 0;
            if (pathBox.Text != path) pathBox.Text = path;
            header.Invalidate();
            footer.Invalidate();
            Invalidate();
        }

        public void SetLoading(string text) { loadingText = text ?? ""; Invalidate(); }

        public void SelectIndex(int i)
        {
            Selected = i;
            EnsureVisible(i);
            Invalidate();
        }

        public void InvalidateRow(int i) { Invalidate(RowRect(i)); }

        // 某一行的屏幕区域（只重画这一行，别整块重画）
        Rectangle RowRect(int i)
        {
            int y = ListTop + 4 + i * Theme.RowH - scrollY;
            return new Rectangle(0, y, Width, Theme.RowH);
        }

        // 供主窗体按鼠标在哪分发滚轮
        public void WheelBy(int delta)
        {
            scrollY -= Math.Sign(delta) * Theme.RowH * 3;
            ClampScroll();
            Invalidate();
        }

        // 只读探针（--wheeltest 三区自检用，）
        public int ScrollYProbe() { return scrollY; }
        public int MaxScrollProbe() { return MaxScroll; }
        public void RestoreScroll(int y) { scrollY = y; ClampScroll(); Invalidate(); }

        // 列表区 = 页眉/页脚两个子控件之间的全部客户区（子控件自己占住上下边缘）
        // 页眉/页脚是真实子控件（Dock Top/Bottom），列表只能画中间那条带。
        // 之前这两个值写死 0 / Height：行从面板顶端开画，于是首行的名字被 86px 页眉盖掉上半截
        // （界面上表现为首行"文字缺一块/和页眉打架"），可滚动范围也少了页眉+页脚的高度，
        // 末尾几张永远滚不到。列表区域的几何必须跟页眉页脚的实际高度走。
        int ListTop { get { return header == null ? 0 : header.Height; } }
        int ListBottom { get { return Height - (footer == null ? 0 : footer.Height); } }
        int ListH { get { return Math.Max(1, ListBottom - ListTop); } }
        int ContentH { get { return Items.Count * Theme.RowH + 8; } }
        int MaxScroll { get { return Math.Max(0, ContentH - ListH); } }

        int RowAt(int y)
        {
            int ly = y - ListTop + scrollY - 4;
            if (ly < 0) return -1;
            int i = ly / Theme.RowH;
            return (i >= 0 && i < Items.Count) ? i : -1;
        }

        void EnsureVisible(int i)
        {
            if (i < 0) return;
            int top = i * Theme.RowH + 4;
            if (top < scrollY) scrollY = top;
            else if (top + Theme.RowH > scrollY + ListH) scrollY = top + Theme.RowH - ListH;
            ClampScroll();
        }

        void ClampScroll()
        {
            if (scrollY < 0) scrollY = 0;
            if (scrollY > MaxScroll) scrollY = MaxScroll;
        }

        // ---- 滚动位置存取（用户：返回文件夹不要回到顶部）----
        // 主窗体在"离开一个文件夹"时把这里的位置记下来，回到这个文件夹时再恢复。
        // 行高是固定的（Theme.RowH），所以恢复是精确的，不会因为缩略图异步加载而漂移。
        public int GetScrollY() { return scrollY; }

        public void SetScrollY(int y)
        {
            scrollY = y;
            ClampScroll();
            Invalidate();
        }

        // 滚轮不再由本面板处理：主窗体按"鼠标停在哪"统一路由到 WheelBy（见 MainForm.RouteWheel）。

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int row = RowAt(e.Y);
            if (barDragging)
            {
                if (barThumb.Height < ListH)
                {
                    int span = ListH - barThumb.Height;
                    int maxScroll = MaxScroll;
                    if (span > 0)
                    {
                        double f = (e.Y - barGrabY) / (double)span;
                        if (f < 0) f = 0; if (f > 1) f = 1;
                        scrollY = barGrabScroll + (int)(maxScroll * f);
                        if (scrollY < 0) scrollY = 0;
                        if (scrollY > maxScroll) scrollY = maxScroll;
                        Invalidate();
                    }
                }
            }
            else if (row != hoverRow)
            {
                int oldRow = hoverRow;
                hoverRow = row;
                if (oldRow >= 0) Invalidate(RowRect(oldRow));
                if (row >= 0) Invalidate(RowRect(row));
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hoverRow = -1;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            // 右键 = 行菜单：先选中该行，再把坐标交给主窗体弹菜单
            if (e.Button == MouseButtons.Right)
            {
                int r = RowAt(e.Y);
                if (r >= 0)
                {
                    Selected = r;
                    Invalidate();
                    if (RowMenu != null) RowMenu(r, PointToScreen(e.Location));
                }
                base.OnMouseDown(e);
                return;
            }
            if (e.Button != MouseButtons.Left) { base.OnMouseDown(e); return; }

            // 滚动条拖动
            if (barVisible && e.X >= Width - 12)
            {
                if (barThumb.Contains(e.X, e.Y))
                {
                    barDragging = true; barGrabY = e.Y; barGrabScroll = scrollY;
                }
                else
                {
                    scrollY += (e.Y < barThumb.Y ? -ListH : ListH);
                    ClampScroll();
                    Invalidate();
                }
                return;
            }

            int row = RowAt(e.Y);
            if (row < 0) return;
            WsItem it = Items[row];
            Selected = row;
            Invalidate();
            if (it.IsParent || it.IsFolder)
            {
                // 文件夹：单击进入（学生用起来更直接）
                if (FolderEntered != null) FolderEntered(it);
            }
            else
            {
                if (ImageClicked != null) ImageClicked(it);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e) { barDragging = false; base.OnMouseUp(e); }

        // ---------------- 绘制 ----------------
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush b = new SolidBrush(Theme.Panel))
                g.FillRectangle(b, ClientRectangle);
            DrawList(g);
        }

        // ---- 页眉（独立子控件，坐标全部相对页眉） ----
        void DrawHeader(object s, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush b = new SolidBrush(Theme.Panel))
                g.FillRectangle(b, e.ClipRectangle);

            // 文件夹名称：folder 图标位换成 3px 朱砂竖条——"你在这个文件夹"的位置锚点；
            // 语义由标题文字本身承担，图标不再重复
            Theme.FillRound(g, new Rectangle(PadX, 12, 3, 18), 1, Theme.Cinnabar);
            Rectangle tr = new Rectangle(PadX + 12, 11, header.Width - PadX - 12 - 76, 20);
            Theme.DrawText(g, FolderTitle, Theme.F13B, tr, Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

            // 右侧两个小按钮
            DrawIconBtn(g, new Rectangle(header.Width - 68, 9, 24, 24), "folder", hoverBtn == 0);
            DrawIconBtn(g, new Rectangle(header.Width - 38, 9, 24, 24), "refresh", hoverBtn == 1);

            // 路径框
            Rectangle pr = new Rectangle(PadX, 46, header.Width - PadX * 2, 28);
            Theme.FillRound(g, pr, Theme.Radius, Theme.FieldBg);
            Theme.StrokeRound(g, pr, Theme.Radius, pathBox.Focused ? Theme.Accent : Theme.Border, 1f);
            Icons.Draw(g, "chevronRight", new Rectangle(PadX + 7, 50, 12, 12), Theme.TextMuted, 1.4f);

            Theme.HLine(g, 0, header.Width, HeaderH - 1, Theme.Border);
        }

        void DrawIconBtn(Graphics g, Rectangle r, string icon, bool hover)
        {
            if (hover) Theme.FillRound(g, r, 5, Theme.HoverBg);
            Icons.Draw(g, icon, new Rectangle(r.X + 4, r.Y + 4, 16, 16), hover ? Theme.Text : Theme.TextSub, 1.5f);
        }

        int HeaderBtnAt(int x, int y)
        {
            if (y > 34) return -1;
            if (x >= header.Width - 38 && x <= header.Width - 14) return 1;   // 刷新
            if (x >= header.Width - 68 && x <= header.Width - 44) return 0;   // 更换文件夹
            return -1;
        }

        void HeaderMouseMove(object s, MouseEventArgs e)
        {
            int btn = HeaderBtnAt(e.X, e.Y);
            if (btn != hoverBtn) { hoverBtn = btn; header.Invalidate(); }
        }

        void HeaderMouseDown(object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            int btn = HeaderBtnAt(e.X, e.Y);
            if (btn == 0) { if (ChooseFolder != null) ChooseFolder(); }
            else if (btn == 1) { if (RefreshClicked != null) RefreshClicked(); }
        }

        void DrawList(Graphics g)
        {
            int top = ListTop, bottom = ListBottom;
            // 列表行裁剪在列表区域内（scrollY 停在非整行位置时首末行会越界，必须裁）
            System.Drawing.Drawing2D.GraphicsState clip = g.Save();
            g.SetClip(new Rectangle(0, top, Width, ListH));

            if (Items.Count == 0)
            {
                Rectangle er = new Rectangle(0, top, Width, ListH);
                if (loadingText.Length > 0)
                    Theme.DrawText(g, loadingText, Theme.F12, er, Theme.TextMuted,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                else
                {
                    bool noFolder = (Folder == null || Folder.Length == 0);
                    Icons.Draw(g, noFolder ? "folder" : "image",
                        new Rectangle(Width / 2 - 11, top + ListH / 2 - 26, 22, 22), Theme.TextMuted, 1.5f);
                    Theme.DrawText(g, noFolder ? "还没有打开文件夹" : "这个文件夹里没有图片", Theme.F12,
                        new Rectangle(0, top + ListH / 2 + 4, Width, 20), Theme.TextMuted,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine);
                }
                barVisible = false; barThumb = Rectangle.Empty;   // 清掉陈旧状态，避免空列表仍响应滚动条拖拽
                g.Restore(clip);
                return;
            }

            int first = Math.Max(0, (scrollY - 4) / Theme.RowH);
            int last = Math.Min(Items.Count - 1, (scrollY + ListH) / Theme.RowH + 1);
            for (int i = first; i <= last; i++)
            {
                int y = ListTop + 4 + i * Theme.RowH - scrollY;
                DrawRow(g, Items[i], i, y);
            }

            // 自定义细滚动条
            barVisible = MaxScroll > 0;
            if (barVisible)
            {
                int trackTop = top + 4, trackH = ListH - 8;
                int thumbH = Math.Max(30, (int)((double)trackH * ListH / ContentH));
                int maxScroll = MaxScroll;
                int thumbY = trackTop + (int)((double)(trackH - thumbH) * scrollY / Math.Max(1, maxScroll));
                barThumb = new Rectangle(Width - 9, thumbY, 5, thumbH);
                Theme.FillRound(g, new Rectangle(Width - 9, trackTop, 5, trackH), 3, Theme.TrackBg);
                Theme.FillRound(g, barThumb, 3, barDragging ? Theme.Accent : Theme.BorderDeep);
            }
            else barThumb = Rectangle.Empty;

            g.Restore(clip);
        }

        void DrawRow(Graphics g, WsItem it, int i, int y)
        {
            bool on = i == Selected;
            Rectangle row = new Rectangle(6, y, Width - 18, Theme.RowH - 4);
            if (on) Theme.FillRound(g, row, 6, Theme.AccentSoft);
            else if (i == hoverRow) Theme.FillRound(g, row, 6, Theme.HoverBg);

            if (on)
            {
                Rectangle bar = new Rectangle(row.X + 1, row.Y + 8, 3, row.Height - 16);
                Theme.FillRound(g, bar, 2, Theme.Accent);
            }

            // 缩略图 / 图标磁贴
            Rectangle tile = new Rectangle(row.X + 10, y + 6, 60, 40);
            Theme.FillRound(g, tile, 4, on ? Theme.Panel : Theme.WindowBg);
            if (it.IsFolder || it.IsParent)
            {
                Icons.Draw(g, it.IsParent ? "up" : "folder",
                    new Rectangle(tile.X + 20, tile.Y + 10, 20, 20), on ? Theme.Accent : Theme.TextSub, 1.5f);
            }
            else if (Imaging.Alive(it.Thumb))
            {
                double sz = Math.Min((double)(tile.Width - 2) / it.Thumb.Width, (double)(tile.Height - 2) / it.Thumb.Height);
                int dw = Math.Max(1, (int)(it.Thumb.Width * sz)), dh = Math.Max(1, (int)(it.Thumb.Height * sz));
                Rectangle dr = new Rectangle(tile.X + (tile.Width - dw) / 2, tile.Y + (tile.Height - dh) / 2, dw, dh);
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.DrawImage(it.Thumb, dr);
            }
            else
            {
                Icons.Draw(g, "image", new Rectangle(tile.X + 20, tile.Y + 10, 20, 20), Theme.TextMuted, 1.5f);
            }
            using (Pen p = new Pen(on ? Theme.AccentSoft2 : Theme.Border))
                g.DrawRectangle(p, tile.X, tile.Y, tile.Width - 1, tile.Height - 1);

            // 文字
            int tx = tile.Right + 12;
            int tw = Width - tx - 22;
            Rectangle nr = new Rectangle(tx, y + 9, tw, 20);
            Theme.DrawText(g, it.Name, on ? Theme.F12B : Theme.F12, nr, Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

            string meta = it.Meta ?? "";
            Rectangle mr = new Rectangle(tx, y + 29, tw, 18);
            Theme.DrawText(g, meta, Theme.F11, mr, Theme.TextMuted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

            // 已编辑：右侧一个小绿点
            if (it.Doc != null && it.Doc.Edits)
            {
                using (SolidBrush b = new SolidBrush(Theme.Green))
                    g.FillEllipse(b, row.Right - 14, y + Theme.RowH / 2 - 8, 7, 7);
            }
        }

        // ---- 页脚（独立子控件） ----
        void DrawFooter(object s, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (SolidBrush b = new SolidBrush(Theme.Panel))
                g.FillRectangle(b, e.ClipRectangle);
            Theme.HLine(g, 0, footer.Width, 0, Theme.Border);
            int imgs = 0, edited = 0;
            for (int i = 0; i < Items.Count; i++)
            {
                if (Items[i].IsFolder || Items[i].IsParent) continue;
                imgs++;
                if (Items[i].Doc != null && Items[i].Doc.Edits) edited++;
            }
            string txt = "共 " + imgs + " 张";
            if (edited > 0) txt += " · 已编辑 " + edited + " 张";
            Rectangle tr = new Rectangle(PadX, 0, footer.Width - PadX * 2, FooterH - 1);
            Theme.DrawText(g, txt, Theme.F11, tr, Theme.TextMuted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }
}
