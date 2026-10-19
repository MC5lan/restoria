// CanvasControl.cs — 图像画布：缩放/平移/涂抹/框选/笔刷光标
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PicTool
{
    public enum CanvasTool { Brush, Rect, Erase, Perspective, Text }

    public delegate void StrokeDelegate(float x, float y, float r, bool add);
    public delegate void RectDelegate(Rectangle imgRect);
    public delegate void TextPointDelegate(float x, float y);
    public delegate void VoidDelegate();

    public class CanvasControl : Control
    {
        // .NET 2.0 没有 Cursors.None，这里合成一个全透明光标（失败则退回十字光标）
        static Cursor invisibleCursor;
        static bool invisibleTried;
        static Cursor InvisibleCursor()
        {
            if (!invisibleTried)
            {
                invisibleTried = true;
                try
                {
                    int size = 32;
                    int xorSize = size * size * 4;
                    int andSize = (size / 8) * size;
                    int imgSize = 40 + xorSize + andSize;
                    using (System.IO.MemoryStream ms = new System.IO.MemoryStream())
                    using (System.IO.BinaryWriter w = new System.IO.BinaryWriter(ms))
                    {
                        w.Write((short)0); w.Write((short)2); w.Write((short)1);
                        w.Write((byte)size); w.Write((byte)size); w.Write((byte)0); w.Write((byte)0);
                        w.Write((short)0); w.Write((short)0);
                        w.Write(imgSize);
                        w.Write(22);
                        w.Write(40); w.Write(size); w.Write(size * 2);
                        w.Write((short)1); w.Write((short)32);
                        w.Write(0); w.Write(xorSize); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
                        w.Write(new byte[xorSize]);
                        byte[] and = new byte[andSize];
                        for (int i = 0; i < and.Length; i++) and[i] = 0xFF;
                        w.Write(and);
                        ms.Position = 0;
                        invisibleCursor = new Cursor(ms);
                    }
                }
                catch { invisibleCursor = null; }
            }
            return invisibleCursor != null ? invisibleCursor : Cursors.Cross;
        }

        Bitmap image;
        Bitmap overlay;
        double zoom = 1.0;
        float ox, oy;
        bool panning, painting;
        Point lastMouse, panLast;
        float lastStrokeX = -1, lastStrokeY = -1;
        bool mouseInside;

        // 框选
        bool rectSelecting;
        PointF rectStart;
        Rectangle rectPreview;
        Timer ants;
        float antsOffset;

        // 透视校正：四点取点（左上→右上→右下→左下），图像坐标
        PointF[] persPts = new PointF[4];
        int persCount;

        // 空格键临时平移（专业修图软件的通用手势）
        bool spacePan;

        // 光标位置（图像坐标）——状态栏坐标/取色指示用
        bool cursorInside;
        float curIx, curIy;

        public StrokeDelegate StrokeHandler;
        public RectDelegate RectHandler;
        public TextPointDelegate TextPlaceHandler;    // 文字工具：空白处单击（图像坐标）
        public VoidDelegate TextsCommitted;           // 一次文字编辑落地（拖动/缩放结束、编辑器提交）
        public VoidDelegate SelChanged;               // 选中文字变化（检查器同步用）
        public event Action<float> BrushRadiusAdjusted;

        // ---------------- 文字元素 ----------------
        // Texts 由主窗体随当前文档换绑（SyncCanvasTexts）；SelText 是画布上的选中项。
        public List<TextItem> Texts;
        public int SelText = -1;

        // 拖动状态：0 无 / 1 移动 / 2 右下角缩放
        int textDrag;
        PointF textDragStart;
        RectangleF textDragBox0;
        float textDragSize0;
        bool textDragMoved;

        // 文字输入窗：一张浮在画布上的小卡片（标题 + 多行输入 + 提示 + 确定/取消）。
        // 中文输入交给卡片里的真 TextBox，系统 IME 全接管；
        // 输入框字号固定、不随画布缩放，看到的字永远清楚；打完立刻在图上实时预览。
        Panel textCard;
        TextBox textEd;
        FlatButton textOk, textCancel;
        int textEdIndex = -1;
        string textEdOrig;
        bool textEdCommitting;
        RectangleF textEdAnchor;      // 正在编辑的文字在画面上的位置（图像坐标），用于摆卡片

        // 仿制图章

        // 当前 zoom 下图像的预缩放缓存：GDI+ 每次 DrawImage 都对全图重采样，大图高倍缩放平移时
        // 每帧全图插值，老机器掉帧。zoom/图不变时平移只 blit。
        Bitmap scaledCache;
        Bitmap cacheSrc;
        double cacheZoom = -1;
        int cacheW, cacheH;

        public float BrushRadius = 24;
        public CanvasTool Tool = CanvasTool.Brush;
        public int PersCount { get { return persCount; } }
        public PointF[] PersPoints { get { return persPts; } }

        public void ClearPersPoints() { persCount = 0; Invalidate(); }
        public string EmptyTitle = "把文件夹拖进来，或点左上角【打开文件夹】";
        public string EmptySub = "支持 JPG / PNG / BMP / TIFF，可对整个文件夹批量处理";
        public string EmptyHint = "去除水印 · 倾斜纠正 · 调色 · 翻转";

        // 位图可能已被其它路径释放（例如预览图在编辑结束后回收），
        // 鼠标命中测试、适应窗口等路径也必须判存活，否则会抛“参数无效”
        bool ImgAlive()
        {
            if (image != null && !Imaging.Alive(image)) { image = null; overlay = null; }
            return image != null;
        }

        public Bitmap DisplayImage { get { return image; } }
        public double Zoom { get { return zoom; } }
        public bool HasImage { get { return image != null; } }

        public CanvasControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.CanvasBg;
            TabStop = false;

            ants = new Timer();
            ants.Interval = 90;
            ants.Tick += delegate
            {
                antsOffset += 0.7f;
                if (antsOffset > 8f) antsOffset = 0f;
                InvalidateRectRegion();
            };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && ants != null) { ants.Stop(); ants.Dispose(); ants = null; }
            if (disposing) DropScaledCache();
            base.Dispose(disposing);
        }

        // ---------------- 图像绑定 ----------------

        public void SetImage(Bitmap img, Bitmap overlayLayer, bool fitView)
        {
            image = img;
            overlay = overlayLayer;
            DropScaledCache();      // 换图：旧 zoom 的预缩放缓存立即作废（及时释放，不等下次重建）
            lastStrokeX = -1;
            persCount = 0;      // 换图后旧取点坐标失效
            CancelTextEditor();       // 换图：还原未确认的输入，并关掉输入卡片
            SelText = -1;
            if (fitView) Fit();
            Invalidate();
        }

        public void SetOverlay(Bitmap overlayLayer)
        {
            overlay = overlayLayer;
            Invalidate();
        }

        // 缩放上下限：以前 Fit 兜底与 ZoomAt 下限各一套值，滚轮缩到下限以下被夹回、与"适应窗口"
        // 行为不一致，抽成常量一处收口
        public const double ZoomMin = 0.05;
        public const double ZoomMax = 16.0;   // 上限 16x：档案修字、验印需要看清单个像素

        // 缩到下限时的提示：下限改为"不低于整页适合窗口"后，滚轮继续往下会被静默夹住，用户以为
        // 滚轮失灵。到达下限时给一次状态栏提示，明确告知到底了。
        public event Action ZoomLimitHit;

        // 当前允许的最小缩放（实测"缩小能缩太多了、指示标太小"）：
        // 下限 = min(整页适合窗口 fit, 100%)——
        //   · 大图（fit < 1）：不能缩到比"整页可见"更小（没意义，只会把图缩成一小块）；
        //   · 小图（fit > 1，如 600×450 放小画布）：可缩到 100% 原尺寸为止。
        // 首版上限误写成 0.5，反而允许缩到比 fit 更小——被 --cursortest 的判据当场抓出。
        double MinZoomNow()
        {
            if (!ImgAlive() || Width < 10 || Height < 10) return ZoomMin;
            double f = Math.Min((Width - 64.0) / image.Width, (Height - 64.0) / image.Height);
            return Math.Max(ZoomMin, Math.Min(1.0, f));
        }

        // 只读探针（供 --cursortest 自检读取）
        public double MinZoomProbe() { return MinZoomNow(); }

        public void Fit()
        {
            PlaceTextCard();          // 缩放不打断输入：输入窗跟着文字走
            if (!ImgAlive() || Width < 10 || Height < 10) return;
            double zx = (Width - 64.0) / image.Width;
            double zy = (Height - 64.0) / image.Height;
            zoom = Math.Min(zx, zy);
            if (zoom > 1) zoom = 1;
            if (zoom < ZoomMin) zoom = ZoomMin;
            ox = (float)((Width - image.Width * zoom) / 2.0);
            oy = (float)((Height - image.Height * zoom) / 2.0);
            Invalidate();
        }

        public void SetZoom(double factor) { ZoomAt(Width / 2, Height / 2, factor); }

        void ZoomAt(int cx, int cy, double nz)
        {
            PlaceTextCard();          // 缩放不打断输入：输入窗跟着文字走
            if (image == null) return;
            double lo = MinZoomNow();
            if (nz < lo)
            {
                nz = lo;
                if (zoom > lo && ZoomLimitHit != null) ZoomLimitHit();   // 只在"从可缩到不可缩"的边界提示一次
            }
            if (nz > ZoomMax) nz = ZoomMax;
            float imgX = (float)((cx - ox) / zoom);
            float imgY = (float)((cy - oy) / zoom);
            zoom = nz;
            ox = cx - (float)(imgX * zoom);
            oy = cy - (float)(imgY * zoom);
            Invalidate();
        }

        public bool ClientToImage(Point p, out float ix, out float iy)
        {
            if (!ImgAlive()) { ix = 0; iy = 0; return false; }
            ix = (float)((p.X - ox) / zoom);
            iy = (float)((p.Y - oy) / zoom);
            return ix >= 0 && iy >= 0 && ix < image.Width && iy < image.Height;
        }

        public string ZoomText() { return Math.Round(zoom * 100) + "%"; }

        // 只读探针（供 --wheeltest / --cursortest 自检读取）
        public bool HasImageProbe() { return ImgAlive(); }
        public string ZoomProbe() { return ZoomText(); }
        public double CurrentZoomProbe() { return zoom; }

        // ---------------- 鼠标 ----------------

        // 滚轮由主窗体统一按"鼠标停在哪"裁决后调进来（见 MainForm.RouteWheel）。
        // 这里绝不自行响应 WM_MOUSEWHEEL：焦点在画布上而鼠标在左侧列表时，
        // 一旦画布也处理，列表滚动的同时画面会跟着缩放。
        public void HandleWheel(int delta)
        {
            if (image == null) return;
            bool ctrl = (Control.ModifierKeys & Keys.Control) == Keys.Control;
            bool shift = (Control.ModifierKeys & Keys.Shift) == Keys.Shift;
            if (ctrl)
            {
                float r = BrushRadius + (delta > 0 ? 1 : -1) * Math.Max(1f, BrushRadius * 0.12f);
                if (r < 4) r = 4;
                if (r > 300) r = 300;
                BrushRadius = r;
                if (BrushRadiusAdjusted != null) BrushRadiusAdjusted(r);
                Invalidate();
            }
            else if (shift)
            {
                // Shift+滚轮：水平平移（大图横向浏览不用拖滚动条）
                ox += delta > 0 ? 60 : -60;
                Invalidate();
            }
            else
            {
                Point p = PointToClient(Cursor.Position);
                ZoomAt(p.X, p.Y, zoom * (delta > 0 ? 1.25 : 0.8));
            }
        }

        // 兼容保留：WinForms 事件路径（焦点在画布上时消息先到这里），统一交给主窗体路由
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            // 输入窗开着时，任何手势都先把文字编辑收个尾（交互修：
            // 以前只有 Text 分支会提交 —— 编辑中切到框选/笔刷/透视再点画面，文字卡悬着
            // 既不提交也不取消。CommitTextEditor 幂等（卡片没开时直接返回），放最前面一处收口）
            if (TextCardOpen) CommitTextEditor();
            if (spacePan && e.Button == MouseButtons.Left && image != null)
            {
                panning = true; panLast = e.Location;
                Cursor = Cursors.SizeAll;
                base.OnMouseDown(e);
                return;
            }
            // 无图时右中键也不改状态：以前漏了 image!=null 判断，无图右键拖动会写 ox/oy，
            // 之后载入的图带着偏移
            if ((e.Button == MouseButtons.Right || e.Button == MouseButtons.Middle) && image != null)
            {
                panning = true; panLast = e.Location;
                Cursor = Cursors.SizeAll;
            }
            else if (e.Button == MouseButtons.Left && image != null)
            {
                float ix, iy;
                if (Tool == CanvasTool.Perspective)
                {
                    // 依次取四个角：左上 → 右上 → 右下 → 左下；点满四个即可到裁剪页应用
                    if (persCount < 4 && ClientToImageClamped(e.Location, out ix, out iy))
                    {
                        persPts[persCount] = new PointF(ix, iy);
                        persCount++;
                        Invalidate();
                    }
                }
                else if (Tool == CanvasTool.Rect)
                {
                    if (RectHandler != null && ClientToImageClamped(e.Location, out ix, out iy))
                    {
                        rectSelecting = true;
                        rectStart = new PointF(ix, iy);
                        rectPreview = Rectangle.Empty;
                        ants.Start();
                        Invalidate();
                    }
                }
                else if (Tool == CanvasTool.Text)
                {
                    // 文字工具：空白处单击 = 新建；点中文字 = 选中并可拖动；右下角手柄 = 缩放。
                    // 输入窗已在 OnMouseDown 顶部统一提交，这里不再重复。
                    // 图像外不单独取消选中：ClientToImageClamped 在 ImgAlive 时恒返回 true（旧分支是死代码），
                    // 点画布空白（含图像外边距）统一夹回图内坐标新建文字。
                    ClientToImageClamped(e.Location, out ix, out iy);
                    bool handled = false;
                    if (SelText >= 0 && Texts != null && SelText < Texts.Count && TextResizeGripAt(e.Location))
                    {
                        textDrag = 2;
                        textDragStart = new PointF(ix, iy);
                        textDragBox0 = Texts[SelText].Box;
                        textDragSize0 = Texts[SelText].SizeEm;
                        textDragMoved = false;
                        handled = true;
                    }
                    if (!handled)
                    {
                        int hit = TextStamp.HitTest(Texts, ix, iy);
                        if (hit >= 0)
                        {
                            SelText = hit;
                            textDrag = 1;
                            textDragStart = new PointF(ix, iy);
                            textDragBox0 = Texts[hit].Box;
                            textDragMoved = false;
                            RaiseSelChanged();
                        }
                        else
                        {
                            SelText = -1;
                            RaiseSelChanged();
                            if (TextPlaceHandler != null) TextPlaceHandler(ix, iy);
                        }
                    }
                    Invalidate();
                }
                else if (StrokeHandler != null && ClientToImage(e.Location, out ix, out iy))
                {
                    painting = true;
                    StrokeHandler(ix, iy, BrushRadius, Tool == CanvasTool.Brush);
                    lastStrokeX = ix; lastStrokeY = iy;
                    InvalidateBrushSpan(e.Location, e.Location);
                }
            }
            base.OnMouseDown(e);
        }

        bool ClientToImageClamped(Point p, out float ix, out float iy)
        {
            ix = 0; iy = 0;
            if (!ImgAlive()) return false;
            ix = (float)((p.X - ox) / zoom);
            iy = (float)((p.Y - oy) / zoom);
            if (ix < 0) ix = 0; if (iy < 0) iy = 0;
            if (ix > image.Width - 1) ix = image.Width - 1;
            if (iy > image.Height - 1) iy = image.Height - 1;
            return true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Point prevMouse = lastMouse;
            lastMouse = e.Location;
            cursorInside = true;
            if (ImgAlive())
            {
                // 图像坐标随动（状态栏坐标/取色指示），越界也要夹回图内，方便看边缘像素
                float cx = (float)((e.X - ox) / zoom), cy = (float)((e.Y - oy) / zoom);
                if (cx < 0) cx = 0; if (cy < 0) cy = 0;
                if (cx > image.Width - 1) cx = image.Width - 1;
                if (cy > image.Height - 1) cy = image.Height - 1;
                curIx = cx; curIy = cy;
            }
            if (panning)
            {
                ox += e.X - panLast.X;
                oy += e.Y - panLast.Y;
                panLast = e.Location;
                PlaceTextCard();      // 输入窗跟着画面走
                Invalidate();
            }
            else if (rectSelecting)
            {
                float ix, iy;
                ClientToImageClamped(e.Location, out ix, out iy);
                rectPreview = MakeRect(rectStart.X, rectStart.Y, ix, iy);
                Invalidate();
            }
            else if (textDrag != 0 && Tool == CanvasTool.Text &&
                     Texts != null && SelText >= 0 && SelText < Texts.Count)
            {
                float ix, iy;
                ClientToImageClamped(e.Location, out ix, out iy);
                TextItem t = Texts[SelText];
                if (textDrag == 1)
                {
                    t.Box = new RectangleF(textDragBox0.X + (ix - textDragStart.X),
                                           textDragBox0.Y + (iy - textDragStart.Y),
                                           textDragBox0.Width, textDragBox0.Height);
                }
                else
                {
                    // 右下角手柄：按位移比例改字号（锚定左上角），等比改写包围盒
                    float sx = textDragBox0.Width > 0 ? (ix - textDragBox0.X) / textDragBox0.Width : 1f;
                    float sy = textDragBox0.Height > 0 ? (iy - textDragBox0.Y) / textDragBox0.Height : 1f;
                    float f = Math.Max(sx, sy);
                    if (f < 0.1f) f = 0.1f;
                    if (f > 8f) f = 8f;
                    t.SizeEm = TextStamp.ClampSize(textDragSize0 * f);
                    TextStamp.UpdateBox(t);
                }
                textDragMoved = true;
                Invalidate();
            }
            else if (painting && StrokeHandler != null)
            {
                float ix, iy;
                if (ClientToImage(e.Location, out ix, out iy))
                {
                    float dx = ix - lastStrokeX, dy = iy - lastStrokeY;
                    float dist = (float)Math.Sqrt(dx * dx + dy * dy);
                    float stepLen = Math.Max(1.5f, BrushRadius * 0.35f);
                    if (dist > stepLen)
                    {
                        int steps = (int)(dist / stepLen);
                        for (int s = 1; s <= steps; s++)
                        {
                            float f = s / (float)steps;
                            StrokeHandler(lastStrokeX + dx * f, lastStrokeY + dy * f, BrushRadius, Tool == CanvasTool.Brush);
                        }
                        lastStrokeX = ix; lastStrokeY = iy;
                    }
                    InvalidateBrushSpan(prevMouse, lastMouse);
                }
            }
            else if (image != null)
            {
                if (Tool == CanvasTool.Rect || Tool == CanvasTool.Perspective) Cursor = Cursors.Cross;
                else if (Tool == CanvasTool.Text) Cursor = Cursors.IBeam;
                else if (spacePan) Cursor = Cursors.SizeAll;
                else Cursor = InvisibleCursor();
                // ⚠️ 悬停（未按住）时也必须重绘：光标圈画在 OnPaint 里，只换 Cursor 不重绘，
                // 圈会停留在上次重绘的位置 —— 表现就是"看不到指示标，除非进行操作"。
                // 新旧两个位置都invalidate：旧位置那圈要有人重绘覆盖，否则成残印。
                if (Tool == CanvasTool.Brush || Tool == CanvasTool.Erase)
                    InvalidateBrushSpan(prevMouse, lastMouse);
            }
            base.OnMouseMove(e);
        }

        static Rectangle MakeRect(float x1, float y1, float x2, float y2)
        {
            int x = (int)Math.Floor(Math.Min(x1, x2));
            int y = (int)Math.Floor(Math.Min(y1, y2));
            int w = (int)Math.Ceiling(Math.Abs(x2 - x1));
            int h = (int)Math.Ceiling(Math.Abs(y2 - y1));
            if (w < 1) w = 1;
            if (h < 1) h = 1;
            return new Rectangle(x, y, w, h);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right || e.Button == MouseButtons.Middle)
            {
                if (panning) { panning = false; ApplyCursor(); }
                base.OnMouseUp(e);
                return;
            }
            if (e.Button != MouseButtons.Left) { base.OnMouseUp(e); return; }
            // ★ 左键松手也要结束平移并恢复光标（）：空格+左键
            //   平移走后 panning 一直挂着不复位 —— 松手后鼠标一动画面还在跑，直到用户
            //   无意中按一次右键/中键才复位。右键分支上面已复位，这里补左键。
            if (panning) { panning = false; ApplyCursor(); }
            if (rectSelecting)
            {
                rectSelecting = false;
                ants.Stop();
                if (rectPreview.Width >= 2 && rectPreview.Height >= 2 && RectHandler != null)
                    RectHandler(rectPreview);
                rectPreview = Rectangle.Empty;
                Invalidate();
            }
            if (textDrag != 0)
            {
                bool moved = textDragMoved;
                textDrag = 0;
                if (moved && TextsCommitted != null) TextsCommitted();   // 拖动/缩放结束 = 一次提交
            }
            painting = false; lastStrokeX = -1;
            base.OnMouseUp(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape && rectSelecting)
            {
                rectSelecting = false;
                ants.Stop();
                rectPreview = Rectangle.Empty;
                Invalidate();
            }
            if (e.KeyCode == Keys.Escape && persCount > 0)
            {
                persCount = 0;      // 重新取点：Esc 一键清空
                Invalidate();
            }
            base.OnKeyDown(e);
        }

        // 空格键临时平移：主窗体（KeyPreview）与画布自身焦点两条路都会走到这里
        public void SetSpacePan(bool on)
        {
            if (spacePan == on) return;
            spacePan = on;
            if (mouseInside && image != null && !panning)
            {
                if (on) Cursor = Cursors.SizeAll;
                else ApplyCursor();
            }
        }

        // 状态栏指示：光标处的图像坐标（已夹回图内）
        public bool TryCursorImagePos(out float ix, out float iy)
        {
            ix = curIx; iy = curIy;
            return cursorInside && ImgAlive();
        }

        // 模拟鼠标悬停（供 --shot 截图使用）：自绘光标圈出现在静态截图里
        public void SimulateCursor(System.Drawing.Point p)
        {
            mouseInside = true;
            lastMouse = p;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            mouseInside = false;
            cursorInside = false;   // 光标离开画面：状态栏坐标/取色指示随之隐藏
            if (image != null) Cursor = Cursors.Default;
            if (image != null) Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            mouseInside = true;
            ApplyCursor();
            base.OnMouseEnter(e);
        }

        void ApplyCursor()
        {
            if (image == null) { Cursor = Cursors.Default; return; }
            if (spacePan) { Cursor = Cursors.SizeAll; return; }
            if (Tool == CanvasTool.Rect || Tool == CanvasTool.Perspective) Cursor = Cursors.Cross;
            else if (Tool == CanvasTool.Text) Cursor = Cursors.IBeam;
            else Cursor = InvisibleCursor();
        }

        public void NotifyToolChanged()
        {
            if (!mouseInside) return;
            ApplyCursor();
            Invalidate();
        }

        // ---------------- 文字编辑 ----------------

        void RaiseSelChanged() { if (SelChanged != null) SelChanged(); }

        public bool TextCardOpen { get { return textCard != null && textCard.Visible; } }

        // 选中框右下角的缩放手柄（屏幕坐标，8×8）
        bool TextResizeGripAt(Point p)
        {
            if (Texts == null || SelText < 0 || SelText >= Texts.Count) return false;
            Rectangle r = BoxToClient(Texts[SelText].Box);
            Rectangle grip = new Rectangle(r.Right - 4, r.Bottom - 4, 9, 9);
            return grip.Contains(p);
        }

        Rectangle BoxToClient(RectangleF imgBox)
        {
            return new Rectangle(
                (int)Math.Floor(ox + imgBox.X * zoom) - 2,
                (int)Math.Floor(oy + imgBox.Y * zoom) - 2,
                Math.Max(6, (int)Math.Ceiling(imgBox.Width * zoom) + 4),
                Math.Max(6, (int)Math.Ceiling(imgBox.Height * zoom) + 4));
        }

        // 打开输入卡片：贴在文字旁边的小窗口，输入即实时预览
        public void BeginTextEdit(int idx)
        {
            if (Texts == null || idx < 0 || idx >= Texts.Count || !ImgAlive()) return;
            CommitTextEditor();
            SelText = idx;
            RaiseSelChanged();
            TextItem t = Texts[idx];
            textEdIndex = idx;
            textEdOrig = t.Text == null ? "" : t.Text;
            textEdCommitting = false;
            textEdAnchor = t.Box;

            BuildTextCard();
            textEd.Text = textEdOrig;
            textEd.ForeColor = t.Color;
            textEd.Font = new Font(Theme.F12.FontFamily, 11f);   // 输入框字号固定：不随画布缩放变小
            PlaceTextCard();
            textCard.Visible = true;
            textCard.BringToFront();
            textEd.Focus();
            textEd.SelectAll();
            Invalidate();
        }

        // 卡片只建一次，之后复用（避免反复建控件闪一下）
        void BuildTextCard()
        {
            if (textCard != null) return;
            textCard = new Panel();
            textCard.Size = new Size(300, 176);
            textCard.Visible = false;
            textCard.BackColor = Theme.CanvasBg;         // 圆角外的四角由卡片自绘背景覆盖
            textCard.Paint += PaintTextCard;

            textEd = new TextBox();
            textEd.Multiline = true;
            textEd.BorderStyle = BorderStyle.None;
            textEd.BackColor = Theme.FieldBg;
            textEd.ForeColor = Theme.Text;
            textEd.ScrollBars = ScrollBars.Vertical;
            textEd.Bounds = new Rectangle(14, 44, 272, 84);
            textEd.TextChanged += delegate
            {
                // 边打边在图上预览：所见即所得（这是"打字出来的字不好"的根治办法）
                if (textEdCommitting) return;
                if (Texts == null || textEdIndex < 0 || textEdIndex >= Texts.Count) return;
                TextItem it = Texts[textEdIndex];
                it.Text = textEd.Text;
                TextStamp.UpdateBox(it);
                textEdAnchor = it.Box;
                Invalidate();
            };
            textEd.KeyDown += TextEditorKeyDown;
            textCard.Controls.Add(textEd);

            textOk = new FlatButton();
            textOk.Text = "确定";
            textOk.Kind = FlatButton.Kinds.Primary;
            textOk.Bounds = new Rectangle(190, 136, 96, 30);
            textOk.Click += delegate { CommitTextEditor(); };
            textCard.Controls.Add(textOk);

            textCancel = new FlatButton();
            textCancel.Text = "取消";
            textCancel.Kind = FlatButton.Kinds.Ghost;
            textCancel.Bounds = new Rectangle(96, 136, 86, 30);
            textCancel.Click += delegate { CancelTextEditor(); };
            textCard.Controls.Add(textCancel);

            // 点卡片空白处（标题/提示区）不该丢焦点
            textCard.MouseDown += delegate { textEd.Focus(); };

            Controls.Add(textCard);
        }

        // 卡片外观：标题栏 + 白底圆角 + 细边 + 投影 + 输入框衬底
        void PaintTextCard(object s, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle body = new Rectangle(4, 8, textCard.Width - 9, textCard.Height - 12);
            // 投影
            for (int i = 4; i >= 1; i--)
                Theme.FillRound(g, new Rectangle(body.X - i / 2, body.Y + i, body.Width + i, body.Height), Theme.RadiusCard,
                    Color.FromArgb(16, 0, 0, 0));
            Theme.FillRound(g, body, Theme.RadiusCard, Theme.Panel);
            Theme.StrokeRound(g, body, Theme.RadiusCard, Theme.Accent, 1.4f);

            // 标题
            Theme.DrawText(g, "输入文字", Theme.F12B,
                new Rectangle(body.X + 14, body.Y + 8, body.Width - 28, 20), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            string hint = "Enter 换行 · Ctrl+Enter 确定 · Esc 取消";
            Theme.DrawText(g, hint, Theme.F10,
                new Rectangle(body.X + 14, body.Y + 26, body.Width - 28, 16), Theme.TextMuted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

            // 输入框衬底（真 TextBox 是无边框的，这里给它画一圈浅底 + 细边）
            Rectangle eb = new Rectangle(textEd.Left - 6, textEd.Top - 5, textEd.Width + 12, textEd.Height + 10);
            Theme.FillRound(g, eb, Theme.Radius, Theme.FieldBg);
            Theme.StrokeRound(g, eb, Theme.Radius, textEd.Focused ? Theme.Accent : Theme.Border, textEd.Focused ? 1.4f : 1f);
        }

        // 卡片摆位：贴在文字下方；下方放不下就放到上方；再夹进画布可视区
        void PlaceTextCard()
        {
            if (textCard == null || Texts == null) return;
            int idx = textEdIndex;
            if (idx < 0 || idx >= Texts.Count) return;
            Rectangle r = BoxToClient(Texts[idx].Box);
            int cw = textCard.Width, chh = textCard.Height;
            int cx = r.X;                       // 左对齐文字
            int cy = r.Bottom + 10;             // 默认放文字下方
            if (cy + chh > Height - 8) cy = r.Y - chh - 10;      // 下方不够 → 放上方
            if (cy < 8) cy = Math.Max(8, Height - chh - 8);      // 上下都紧张 → 贴着底
            if (cx + cw > Width - 8) cx = Width - cw - 8;
            if (cx < 8) cx = 8;
            textCard.Bounds = new Rectangle(cx, cy, cw, chh);
        }

        void TextEditorKeyDown(object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                CancelTextEditor();            // Esc = 放弃本次输入（还原成打开前的文字）
                return;
            }
            if (e.KeyCode == Keys.Enter && (e.Control || e.Shift))
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                CommitTextEditor();            // Ctrl/Shift+Enter = 确定（单独 Enter 是换行）
            }
        }

        public void CommitTextEditor()
        {
            if (textCard == null || !textCard.Visible || textEdCommitting) return;
            textEdCommitting = true;
            try
            {
                if (textEdIndex >= 0 && Texts != null && textEdIndex < Texts.Count)
                {
                    TextItem t = Texts[textEdIndex];
                    string now = textEd.Text == null ? "" : textEd.Text;
                    if (t.Text != now)
                    {
                        t.Text = now;
                        TextStamp.UpdateBox(t);
                        if (TextsCommitted != null) TextsCommitted();
                    }
                }
            }
            finally
            {
                HideTextCard();
                DropEmptyEditText();     // 空文字（点了画面但什么都没输入）不留在画面上
                textEdCommitting = false;
            }
        }

        // 编辑结束后如果该条仍是空文本（或索引已失效），把它清掉，避免幽灵空项堆积
        void DropEmptyEditText()
        {
            if (Texts == null) return;
            if (SelText < 0 || SelText >= Texts.Count) return;
            if (Texts[SelText].Text.Length > 0) return;
            Texts.RemoveAt(SelText);
            SelText = -1;
            RaiseSelChanged();
            if (TextsCommitted != null) TextsCommitted();   // 主窗体快照要知道列表变了
            Invalidate();
        }

        public void CancelTextEditor()
        {
            if (textCard != null && textCard.Visible && textEdIndex >= 0 &&
                Texts != null && textEdIndex < Texts.Count)
            {
                // 实时预览已经把改动写进了元素：取消要还原回打开前的样子
                TextItem t = Texts[textEdIndex];
                t.Text = textEdOrig;
                TextStamp.UpdateBox(t);
            }
            CloseTextEditor(false);
            DropEmptyEditText();
            Invalidate();
        }

        // commit=true 提交内容；false 直接丢弃（当前文本由 CancelTextEditor 负责还原）
        void CloseTextEditor(bool commit)
        {
            if (textCard == null || !textCard.Visible) return;
            if (commit) CommitTextEditor();
            else HideTextCard();
        }

        void HideTextCard()
        {
            if (textCard == null) return;
            textCard.Visible = false;
            textEdIndex = -1;
            textEdOrig = null;
            Invalidate();
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            if (Tool == CanvasTool.Text && SelText >= 0 && Texts != null && SelText < Texts.Count)
            {
                BeginTextEdit(SelText);
                return;
            }
            Fit();
            base.OnDoubleClick(e);
        }

        // 笔刷扫过的区域：必须把"上一个位置"也算进来。
        // 只重画新位置的话，旧位置那圈光标没人重绘覆盖，就留在画面上成了"残印"。
        void InvalidateBrushSpan(Point from, Point to)
        {
            int rr = Math.Max(3, (int)(BrushRadius * zoom)) + 8;
            Invalidate(new Rectangle(
                Math.Min(from.X, to.X) - rr, Math.Min(from.Y, to.Y) - rr,
                Math.Abs(from.X - to.X) + rr * 2, Math.Abs(from.Y - to.Y) + rr * 2));
        }

        void InvalidateRectRegion()
        {
            if (rectPreview.IsEmpty) { Invalidate(); return; }
            Rectangle r = ImageToClient(rectPreview);
            Invalidate(new Rectangle(r.X - 4, r.Y - 4, r.Width + 8, r.Height + 8));
        }

        Rectangle ImageToClient(Rectangle imgRect)
        {
            return new Rectangle(
                (int)(ox + imgRect.X * zoom),
                (int)(oy + imgRect.Y * zoom),
                Math.Max(1, (int)(imgRect.Width * zoom)),
                Math.Max(1, (int)(imgRect.Height * zoom)));
        }

        // ---------------- 绘制 ----------------

        // 取当前 zoom 下的预缩放视图（命中缓存直接返回；失效则重建）。
        // 判据用"图引用 + zoom + 目标尺寸"：image 编辑后是整体换新 Bitmap 对象，引用一变即重建。
        // 总像素上限 12M（dev-expert 复审）：只有单边 8192 上限时，大图高倍下
        // 缓存可膨胀到近百 MB；加总量帽后超限走原图直画。构建失败（极端尺寸/内存紧）
        // 也降级直画，绝不让 OnPaint 抛异常。
        Bitmap ScaledView(int w, int h)
        {
            if (scaledCache != null && !Imaging.Alive(scaledCache)) { scaledCache = null; cacheSrc = null; }
            if (scaledCache != null && cacheSrc == image && Math.Abs(cacheZoom - zoom) < 1e-9
                && cacheW == w && cacheH == h) return scaledCache;
            if (w > 8192 || h > 8192 || (long)w * h > 12000000) return null;
            if (scaledCache != null) { try { scaledCache.Dispose(); } catch { } scaledCache = null; }
            Bitmap b;
            try { b = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb); }
            catch { return null; }
            try
            {
                using (Graphics g2 = Graphics.FromImage(b))
                {
                    g2.InterpolationMode = zoom >= 1.0 ? InterpolationMode.NearestNeighbor : InterpolationMode.Bilinear;
                    g2.PixelOffsetMode = zoom >= 1.0 ? PixelOffsetMode.Half : PixelOffsetMode.HighQuality;
                    g2.CompositingMode = CompositingMode.SourceCopy;
                    g2.DrawImage(image, new Rectangle(0, 0, w, h));
                }
            }
            catch { try { b.Dispose(); } catch { } return null; }
            scaledCache = b; cacheSrc = image; cacheZoom = zoom; cacheW = w; cacheH = h;
            return b;
        }

        void DropScaledCache()
        {
            if (scaledCache != null) { try { scaledCache.Dispose(); } catch { } scaledCache = null; }
            cacheSrc = null; cacheZoom = -1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            if (!Imaging.Alive(image))
            {
                image = null;
                overlay = null;
                DrawEmptyState(g);
                return;
            }
            if (overlay != null && !Imaging.Alive(overlay)) overlay = null;

            Rectangle imgRect = new Rectangle((int)ox, (int)oy,
                Math.Max(1, (int)(image.Width * zoom)), Math.Max(1, (int)(image.Height * zoom)));

            // 轻微投影
            for (int i = 3; i >= 1; i--)
            {
                using (Pen p = new Pen(Color.FromArgb(10 * (4 - i), 0, 0, 0), 1f))
                    g.DrawRectangle(p, imgRect.X - i, imgRect.Y - i, imgRect.Width + i * 2, imgRect.Height + i * 2);
            }

            g.InterpolationMode = zoom >= 1.0 ? InterpolationMode.NearestNeighbor : InterpolationMode.Bilinear;
            g.PixelOffsetMode = zoom >= 1.0 ? PixelOffsetMode.Half : PixelOffsetMode.HighQuality;
            g.CompositingMode = CompositingMode.SourceCopy;
            // 预缩放缓存：命中时 1:1 blit（平移/悬停重绘不再全图重采样）；未命中（首次/缩放后）
            // 走原图直画，下一帧起就有缓存
            Bitmap view = ScaledView(imgRect.Width, imgRect.Height);
            if (view != null) g.DrawImage(view, imgRect);
            else g.DrawImage(image, imgRect);
            if (overlay != null)
            {
                g.CompositingMode = CompositingMode.SourceOver;
                g.DrawImage(overlay, imgRect);
            }
            g.CompositingMode = CompositingMode.SourceCopy;

            using (Pen p = new Pen(Color.FromArgb(60, 0, 0, 0)))
                g.DrawRectangle(p, imgRect.X - 1, imgRect.Y - 1, imgRect.Width + 1, imgRect.Height + 1);

            g.SmoothingMode = SmoothingMode.AntiAlias;

            // 文字元素（未烙印的实时渲染）：任意缩放下直接按屏幕字号画，与烙印结果视觉一致
            if (Texts != null && Texts.Count > 0)
            {
                g.CompositingMode = CompositingMode.SourceOver;
                System.Drawing.Text.TextRenderingHint oldHint = g.TextRenderingHint;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                for (int i = 0; i < Texts.Count; i++)
                {
                    TextItem t = Texts[i];
                    RectangleF cr = new RectangleF(
                        (float)(ox + t.Box.X * zoom), (float)(oy + t.Box.Y * zoom),
                        (float)(t.Box.Width * zoom), (float)(t.Box.Height * zoom));
                    using (Font f = TextStamp.MakeFont(t, Math.Max(1f, t.SizeEm * (float)zoom)))
                    using (SolidBrush b = new SolidBrush(t.Color))
                    using (StringFormat sf = TextStamp.FormatOf(t))
                    {
                        g.DrawString(t.Text == null ? "" : t.Text, f, b, cr, sf);
                    }
                }
                g.TextRenderingHint = oldHint;
                g.CompositingMode = CompositingMode.SourceCopy;

                // 选中框（虚线）+ 右下角缩放手柄
                if (Tool == CanvasTool.Text && SelText >= 0 && SelText < Texts.Count && !TextCardOpen)
                {
                    Rectangle r = BoxToClient(Texts[SelText].Box);
                    using (Pen p = new Pen(Color.FromArgb(230, 80, 160, 255), 1.2f))
                    {
                        p.DashPattern = new float[] { 4f, 3f };
                        g.DrawRectangle(p, r);
                    }
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(230, 80, 160, 255)))
                        g.FillRectangle(b, r.Right - 4, r.Bottom - 4, 8, 8);
                }
            }

            // 框选预览（蚂蚁线）—— 配色按主题分两套：浅色画布上白线看不见，深底用白、浅底用深灰
            // （尺寸提示条仍是黑底白字，两边都可读）
            if (rectSelecting && !rectPreview.IsEmpty)
            {
                bool dark = Theme.IsDark;
                Color antsMain = dark ? Color.FromArgb(220, 255, 255, 255) : Color.FromArgb(210, 60, 66, 74);
                Color antsDim = dark ? Color.FromArgb(180, 0, 0, 0) : Color.FromArgb(150, 255, 255, 255);
                Color veil = dark ? Color.FromArgb(30, 255, 255, 255) : Color.FromArgb(16, 60, 66, 74);
                Rectangle pr = ImageToClient(rectPreview);
                using (SolidBrush b = new SolidBrush(veil))
                    g.FillRectangle(b, pr);
                using (Pen p = new Pen(antsMain, 1f))
                {
                    p.DashPattern = new float[] { 4f, 4f };
                    p.DashOffset = antsOffset;
                    g.DrawRectangle(p, pr);
                }
                using (Pen p = new Pen(antsDim, 1f))
                {
                    p.DashPattern = new float[] { 4f, 4f };
                    p.DashOffset = antsOffset + 4f;
                    g.DrawRectangle(p, pr);
                }
                // 尺寸提示
                string sz = rectPreview.Width + " × " + rectPreview.Height;
                Size ts = TextRenderer.MeasureText(sz, Theme.F11, new Size(200, 20), TextFormatFlags.NoPadding);
                Rectangle lb = new Rectangle(pr.X + 2, pr.Bottom + 4, ts.Width + 10, 20);
                if (lb.Bottom > Height - 4) lb.Y = pr.Y - 24;
                Theme.FillRound(g, lb, 4, Color.FromArgb(210, 0, 0, 0));
                Theme.DrawText(g, sz, Theme.F11, lb, Theme.TextOnDark,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }

            // 笔刷/橡皮光标（实测"太丑"重画）：专业修图软件的标准画法——
            // 深色细圈 + 白色衬边（任何底色上都只呈现"一个干净的圈"），中心 2px 小点对位。
            // 旧版"黑圈+白圈+十字"三件套废弃：十字遮住涂刷中心的细节，双圈叠在图上显脏。
            // 橡皮保留虚线圈以示区分。视觉验证提示：自绘圈只在真实鼠标悬停时出现，
            // --shot 静态截图里没有——目检盲区，需真机确认。
            if (mouseInside && !panning && !rectSelecting &&
                (Tool == CanvasTool.Brush || Tool == CanvasTool.Erase))
            {
                // 图像 blit 段把 SmoothingMode 关了、PixelOffsetMode 设成了 Half（SourceCopy 提速）——
                // 光标段必须显式恢复两者，否则细椭圆出不对称像素阶梯（--cursortest 验证）
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                int r = Math.Max(8, (int)(BrushRadius * zoom));   // 最小半径 8px（实测缩小后指示标太小）
                bool erase = Tool == CanvasTool.Erase;
                Rectangle ring = new Rectangle(lastMouse.X - r, lastMouse.Y - r, r * 2, r * 2);
                using (Pen p = new Pen(Color.FromArgb(190, 255, 255, 255), 3.2f))      // 白衬边：压住底色
                    g.DrawEllipse(p, ring);
                using (Pen p = new Pen(Color.FromArgb(245, 26, 30, 38), 1.6f))         // 深色主线
                {
                    if (erase) p.DashPattern = new float[] { 4f, 3f };
                    g.DrawEllipse(p, ring);
                }
                // 中心小点（白衬 + 深点）：对位用，不遮挡涂刷中心
                using (SolidBrush b = new SolidBrush(Color.FromArgb(140, 255, 255, 255)))
                    g.FillEllipse(b, lastMouse.X - 2.4f, lastMouse.Y - 2.4f, 4.8f, 4.8f);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(215, 28, 32, 38)))
                    g.FillEllipse(b, lastMouse.X - 1.3f, lastMouse.Y - 1.3f, 2.6f, 2.6f);
            }


            // 透视校正：四点取点可视化（连线 + 序号 + 点满后闭合半透明四边形）
            // 注意：GDI+ 的 DrawPolygon 在少于 3 个点时会抛"参数无效"（OnPaint 里炸=每帧弹异常框），
            // 所以 1 个点只画点标、2 个点画线段、3~4 个点才画闭合多边形。
            if (persCount > 0)
            {
                PointF[] c = new PointF[persCount];
                bool finite = true;
                for (int i = 0; i < persCount; i++)
                {
                    float px = (float)(ox + persPts[i].X * zoom);
                    float py = (float)(oy + persPts[i].Y * zoom);
                    if (float.IsNaN(px) || float.IsInfinity(px) || float.IsNaN(py) || float.IsInfinity(py))
                    {
                        finite = false;
                        break;
                    }
                    c[i] = new PointF(px, py);
                }

                if (!finite)
                {
                    persCount = 0;      // 坐标已经不可信（理论到不了这里），丢弃防连环炸
                }
                else
                {
                    if (persCount == 4)
                    {
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(26, 80, 160, 255)))
                            g.FillPolygon(b, c);
                    }
                    if (persCount >= 3)
                    {
                        using (Pen p = new Pen(Color.FromArgb(230, 80, 160, 255), 1.6f))
                        {
                            if (persCount < 4) p.DashPattern = new float[] { 5f, 4f };
                            g.DrawPolygon(p, c);
                        }
                    }
                    else if (persCount == 2)
                    {
                        using (Pen p = new Pen(Color.FromArgb(230, 80, 160, 255), 1.6f))
                        {
                            p.DashPattern = new float[] { 5f, 4f };
                            g.DrawLine(p, c[0], c[1]);
                        }
                    }
                    for (int i = 0; i < persCount; i++)
                    {
                        Rectangle dot = new Rectangle((int)c[i].X - 5, (int)c[i].Y - 5, 10, 10);
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(235, 80, 160, 255)))
                            g.FillEllipse(b, dot);
                        string n = (i + 1).ToString();
                        Size ns = TextRenderer.MeasureText(n, Theme.F11, new Size(20, 20), TextFormatFlags.NoPadding);
                        Rectangle lb = new Rectangle((int)c[i].X + 8, (int)c[i].Y - 20, ns.Width + 6, 18);
                        Theme.FillRound(g, lb, 4, Color.FromArgb(210, 0, 0, 0));
                        Theme.DrawText(g, n, Theme.F11, lb, Theme.TextOnDark,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                    }
                }
            }
        }

        void DrawEmptyState(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // 空状态配色按主题分两套：浅色主题画布改成纸感暖白后，原来给深底设计的浅色文字/白虚线
            // 全部不可见，这里深底=浅字、浅底=深字
            bool dark = Theme.IsDark;
            Color lineC = dark ? Color.FromArgb(70, 255, 255, 255) : Color.FromArgb(160, 122, 112, 96);
            Color titleC = dark ? Color.FromArgb(190, 200, 208, 216) : Color.FromArgb(92, 86, 76);
            Color subC = dark ? Color.FromArgb(130, 150, 160, 172) : Color.FromArgb(122, 116, 106);
            Color hintC = dark ? Color.FromArgb(110, 130, 140, 152) : Color.FromArgb(146, 140, 130);
            int cw = Math.Min(460, Width - 60);
            int ch = 210;
            int cx = (Width - cw) / 2, cy = (Height - ch) / 2;
            Rectangle card = new Rectangle(cx, cy, cw, ch);
            using (GraphicsPath p = Theme.Round(card, 12))
            using (Pen pen = new Pen(lineC, 1.4f))
            {
                pen.DashPattern = new float[] { 5f, 4f };
                g.DrawPath(pen, p);
            }

            // "纸 + 印"组合图：替代通用图标，一张带文字线的白纸、右下角压一枚朱砂印，作为产品第一眼视觉标识。
            int ox0 = cx + cw / 2 - 26, oy0 = cy + 36;
            Color paperFill = dark ? Color.FromArgb(0xE9, 0xE7, 0xE2) : Color.White;
            Color paperLine = dark ? Color.FromArgb(200, 0x55, 0x59, 0x5E) : Color.FromArgb(210, 0x8A, 0x84, 0x78);
            Color paperEdge = dark ? Color.FromArgb(120, 0x45, 0x4B, 0x54) : Color.FromArgb(170, 0xD8, 0xD2, 0xC6);
            Theme.FillRound(g, new Rectangle(ox0 + 1, oy0 + 2, 30, 40), 2, Color.FromArgb(dark ? 60 : 26, 0, 0, 0));   // 纸的落影
            Theme.FillRound(g, new Rectangle(ox0, oy0, 30, 40), 2, paperFill);
            Theme.StrokeRound(g, new Rectangle(ox0, oy0, 30, 40), 2, paperEdge, 1f);
            foreach (Rectangle lr in new Rectangle[] {
                new Rectangle(ox0 + 5, oy0 + 7, 20, 2),
                new Rectangle(ox0 + 5, oy0 + 13, 20, 2),
                new Rectangle(ox0 + 5, oy0 + 19, 13, 2) })
                Theme.FillRound(g, lr, 1, paperLine);
            Rectangle seal = new Rectangle(ox0 + 22, oy0 + 26, 20, 20);
            Theme.FillRound(g, new Rectangle(seal.X + 1, seal.Y + 2, seal.Width, seal.Height), 3, Color.FromArgb(dark ? 70 : 30, 0, 0, 0));
            Theme.FillRound(g, seal, 3, Theme.Cinnabar);
            Theme.StrokeRound(g, new Rectangle(seal.X + 3, seal.Y + 3, seal.Width - 6, seal.Height - 6), 2, Color.FromArgb(215, 255, 255, 255), 1.2f);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(225, 255, 255, 255)))
                g.FillRectangle(b, seal.X + seal.Width / 2 - 3, seal.Y + seal.Height / 2 - 3, 6, 6);   // 印文（抽象）
            Theme.DrawText(g, EmptyTitle, Theme.F13B,
                new Rectangle(cx, cy + 92, cw, 24), titleC,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine);
            Theme.DrawText(g, EmptySub, Theme.F12,
                new Rectangle(cx, cy + 120, cw, 20), subC,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine);
            Theme.DrawText(g, EmptyHint, Theme.F11,
                new Rectangle(cx, cy + 148, cw, 20), hintC,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine);
        }
    }
}
