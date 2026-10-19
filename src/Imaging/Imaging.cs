// Imaging.cs — 核心图像处理库（不依赖 UI，不依赖任何外部 DLL）
// 目标运行环境：.NET 2.0 CLR（Windows 7 出厂自带），x86
// 约束：C# 语法限制在 2.0 可编译范围（不用 LINQ/HashSet/async）
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Collections.Generic;

namespace PicTool
{
    public delegate void ProgressHandler(int percent);

    // 取消检查：返回 true 表示用户要求中止当前耗时操作
    public delegate bool CancelCheck();

    // 用户主动取消（不是错误：界面按"已取消"处理，不弹错误框）
    public class OpCancelledException : Exception
    {
        public OpCancelledException() : base("操作已取消。") { }
    }

    public static class Imaging
    {
        // 涂抹范围的可视化颜色（半透明红）——核心库不依赖 UI 层，颜色常量自持
        public static readonly Color MaskPaint = Color.FromArgb(0x64, 0xE5, 0x48, 0x4D);

        // ---------------- 文件读写 ----------------

        // 安全加载：读完即释放文件句柄；按 EXIF 方向自动转正；统一为 24bpp
        public static Bitmap LoadFile(string path)
        {
            Bitmap bmp;
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                using (MemoryStream ms = new MemoryStream(bytes))
                using (Bitmap raw = new Bitmap(ms))
                {
                    bmp = new Bitmap(raw); // 克隆一份，脱离流，原文件句柄即刻释放
                }
            }
            catch (Exception ex)
            {
                if (ex is ArgumentException || ex is OutOfMemoryException)
                    throw new Exception("无法打开该图片：文件可能已损坏，或图片过大。");
                throw;
            }
            bmp = Ensure24bpp(bmp);
            ApplyExifOrientation(bmp, path);
            return bmp;
        }

        // 按 EXIF Orientation(0x0112) 转正。Bitmap 必须是可写像素格式。
        public static void ApplyExifOrientation(Bitmap bmp, string path)
        {
            try
            {
                int orient = 1;
                foreach (PropertyItem pi in bmp.PropertyItems)
                {
                    if (pi.Id == 0x0112 && pi.Value != null && pi.Value.Length >= 2)
                    {
                        orient = BitConverter.ToUInt16(pi.Value, 0);
                        break;
                    }
                }
                switch (orient)
                {
                    case 2: bmp.RotateFlip(RotateFlipType.RotateNoneFlipX); break;
                    case 3: bmp.RotateFlip(RotateFlipType.Rotate180FlipNone); break;
                    case 4: bmp.RotateFlip(RotateFlipType.RotateNoneFlipY); break;
                    case 5: bmp.RotateFlip(RotateFlipType.Rotate90FlipX); break;
                    case 6: bmp.RotateFlip(RotateFlipType.Rotate90FlipNone); break;
                    case 7: bmp.RotateFlip(RotateFlipType.Rotate270FlipX); break;
                    case 8: bmp.RotateFlip(RotateFlipType.Rotate270FlipNone); break;
                }
            }
            catch { /* 没有 EXIF 或读取失败：保持原样 */ }
        }

        public static Bitmap MakeThumb(Bitmap src, int maxW, int maxH)
        {
            int sw = src.Width, sh = src.Height;
            double scale = Math.Min((double)maxW / sw, (double)maxH / sh);
            if (scale > 1.0) scale = 1.0;
            int dw = Math.Max(1, (int)Math.Round(sw * scale));
            int dh = Math.Max(1, (int)Math.Round(sh * scale));
            Bitmap dst = new Bitmap(dw, dh, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.Clear(Color.White);   // 透明像素按纸张白处理
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, dw, dh));
            }
            return dst;
        }

        // 缩放到不超过指定边长（用于实时预览）
        public static Bitmap ScaleToFit(Bitmap src, int maxSide)
        {
            int longest = Math.Max(src.Width, src.Height);
            if (longest <= maxSide) return Ensure24bpp(new Bitmap(src));
            return MakeThumb(src, maxSide, maxSide);
        }

        // ---------------- 裁剪 / A4 画板 ----------------

        // 按给定像素尺寸缩放（高质量）
        public static Bitmap ScaleTo(Bitmap src, int w, int h)
        {
            if (w < 1) w = 1;
            if (h < 1) h = 1;
            Bitmap dst = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(src, new Rectangle(0, 0, w, h));
            }
            return dst;
        }

        // 裁剪到指定矩形（矩形会被夹到图内）
        public static Bitmap Crop(Bitmap src, Rectangle rect)
        {
            Rectangle r = Rectangle.Intersect(rect, new Rectangle(0, 0, src.Width, src.Height));
            if (r.Width < 1 || r.Height < 1) return Ensure24bpp(new Bitmap(src));
            Bitmap dst = new Bitmap(r.Width, r.Height, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, r.Width, r.Height), r, GraphicsUnit.Pixel);
            }
            return dst;
        }

        // 在给定矩形内取一个指定宽高比的最大矩形（居中），用于"按 1:1 / 4:3 裁剪"
        public static Rectangle FitAspect(Rectangle outer, double aspect)
        {
            if (aspect <= 0) return outer;
            int w = outer.Width, h = outer.Height;
            double cur = w / (double)h;
            if (cur > aspect) w = (int)Math.Floor(h * aspect);       // 太宽：按高定宽
            else h = (int)Math.Floor(w / aspect);                     // 太高：按宽定高
            if (w < 1) w = 1;
            if (h < 1) h = 1;
            return new Rectangle(outer.X + (outer.Width - w) / 2, outer.Y + (outer.Height - h) / 2, w, h);
        }

        // 自动裁掉四周空白：以四角颜色为"纸色"，找出内容外接矩形。
        // tolerance 是允许的亮度差（0~255），扫描件一般 18~30 就够。
        public static Bitmap TrimBlank(Bitmap src, int tolerance, out Rectangle content)
        {
            int w = src.Width, h = src.Height;
            content = new Rectangle(0, 0, w, h);
            int minX = w, minY = h, maxX = -1, maxY = -1;
            try
            {
                BitmapData d = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);  // resource-ok: 解锁在 try 尾 + catch 兜底，本函数结构特殊
                unsafe
                {
                    // 四角取样求纸色（取最亮的那个角，避免角落里正好有内容）
                    int best = -1;
                    int[] xs = new int[] { 0, w - 1, 0, w - 1 };
                    int[] ys = new int[] { 0, 0, h - 1, h - 1 };
                    int pr = 255, pg = 255, pb = 255;
                    for (int k = 0; k < 4; k++)
                    {
                        byte* p = (byte*)d.Scan0 + ys[k] * d.Stride + xs[k] * 3;
                        int lum = (p[2] * 29 + p[1] * 150 + p[0] * 77) >> 8;
                        if (lum > best) { best = lum; pr = p[2]; pg = p[1]; pb = p[0]; }
                    }
                    for (int y = 0; y < h; y++)
                    {
                        byte* row = (byte*)d.Scan0 + y * d.Stride;
                        for (int x = 0; x < w; x++)
                        {
                            byte* p = row + x * 3;
                            int dr = p[2] - pr, dg = p[1] - pg, db = p[0] - pb;
                            if (dr < 0) dr = -dr;
                            if (dg < 0) dg = -dg;
                            if (db < 0) db = -db;
                            if (dr + dg + db <= tolerance * 3) continue;     // 还是纸色
                            if (x < minX) minX = x;
                            if (x > maxX) maxX = x;
                            if (y < minY) minY = y;
                            if (y > maxY) maxY = y;
                        }
                    }
                }
                src.UnlockBits(d);
            }
            catch { return Ensure24bpp(new Bitmap(src)); }
            if (maxX < 0) return Ensure24bpp(new Bitmap(src));               // 整张都是纸色：不动
            // 留一点点边距，别贴得太死
            int pad = Math.Max(2, Math.Min(w, h) / 200);
            minX = Math.Max(0, minX - pad); minY = Math.Max(0, minY - pad);
            maxX = Math.Min(w - 1, maxX + pad); maxY = Math.Min(h - 1, maxY + pad);
            content = new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
            if (content.Width == w && content.Height == h) return Ensure24bpp(new Bitmap(src));
            return Crop(src, content);
        }

        // 裁掉扫描仪在四周留下的黑框。
        //
        // 为什么需要它：Image_00015 / Image_00033 是彩色封面页，流水线会**原样跳过**
        //   它们（怕把彩色内容毁掉），于是这两张的扫描黑框一直没人管 —— 68 张成品里
        //   就这两张四周还留着一圈黑（实测左边/右边 97% 的像素是黑的）。
        //   普通页的黑框由 TrimBlank 顺带裁掉，但跳过页根本走不到那一步。
        //
        // 三条判据必须**同时**满足才算黑框（宁可不裁，也绝不误裁内容）：
        //   ① 像素够黑：max(R,G,B) < 60。
        //      ⚠️ 用 max 而不是亮度，这是本函数最容易踩的坑：红色封面的亮度只有 81
        //      （R=200,G=30,B=30），按亮度判会被当成黑框，整张封面被裁光。
        //   ② 整行 / 整列够连续：该行（列）里 ≥85% 的像素都黑 —— 文字、表格线、
        //      装订孔都不可能这么密（孔最多占一列的一小段）。
        //   ③ 单边不超过短边的 8%，裁完剩下的画面不小于原来的 50% ——
        //      超过就认定"这整张就是深色内容"，一刀不裁。
        //
        // 返回 null = 没发现黑框（调用方保持原图不动）。
        public static Bitmap TrimDarkFrame(Bitmap src, out Rectangle cut)
        {
            cut = Rectangle.Empty;
            if (src == null || src.Width < 64 || src.Height < 64) return null;

            int w = src.Width, h = src.Height;
            PixelBuffer buf = null;
            try { buf = BitmapBridge.ToBuffer(src); }
            catch { return null; }
            byte[] d = buf.Data;
            int stride = buf.Stride;
            int maxSide = Math.Max(4, (int)(Math.Min(w, h) * 0.08));

            int top = 0, bottom = 0, left = 0, right = 0;

            // 上边：逐行往里走，第一行不够黑就停
            for (int y = 0; y < maxSide; y++)
            {
                int off = y * stride, dark = 0;
                for (int x = 0; x < w; x++)
                {
                    int i = off + x * 3;
                    int mx = d[i]; if (d[i + 1] > mx) mx = d[i + 1]; if (d[i + 2] > mx) mx = d[i + 2];
                    if (mx < 60) dark++;
                }
                if (dark < w * 0.85) break;
                top = y + 1;
            }
            // 下边
            for (int y = 0; y < maxSide; y++)
            {
                int off = (h - 1 - y) * stride, dark = 0;
                for (int x = 0; x < w; x++)
                {
                    int i = off + x * 3;
                    int mx = d[i]; if (d[i + 1] > mx) mx = d[i + 1]; if (d[i + 2] > mx) mx = d[i + 2];
                    if (mx < 60) dark++;
                }
                if (dark < w * 0.85) break;
                bottom = y + 1;
            }
            // 左边
            for (int x = 0; x < maxSide; x++)
            {
                int dark = 0;
                for (int y = 0; y < h; y++)
                {
                    int i = y * stride + x * 3;
                    int mx = d[i]; if (d[i + 1] > mx) mx = d[i + 1]; if (d[i + 2] > mx) mx = d[i + 2];
                    if (mx < 60) dark++;
                }
                if (dark < h * 0.85) break;
                left = x + 1;
            }
            // 右边
            for (int x = 0; x < maxSide; x++)
            {
                int dark = 0;
                int xx = w - 1 - x;
                for (int y = 0; y < h; y++)
                {
                    int i = y * stride + xx * 3;
                    int mx = d[i]; if (d[i + 1] > mx) mx = d[i + 1]; if (d[i + 2] > mx) mx = d[i + 2];
                    if (mx < 60) dark++;
                }
                if (dark < h * 0.85) break;
                right = x + 1;
            }

            if (top == 0 && bottom == 0 && left == 0 && right == 0) return null;
            int cw = w - left - right, ch = h - top - bottom;
            if (cw < w * 0.5 || ch < h * 0.5) return null;      // ③ 裁掉的太多 → 是深色内容，不裁
            cut = new Rectangle(left, top, cw, ch);
            return Crop(src, cut);
        }

        // 裁掉图片边缘的"灰色阴影带"（配合 TrimDarkFrame 使用）。
        //
        // 为什么需要：红色封面页（Image_00015/33）排进 A4 后，四周贴着的是封面图像
        //   自身的扫描阴影带（灰黑渐变，亮度 100~200）—— 它**不够黑**（TrimDarkFrame
        //   的判据 max<60 抓不到），但视觉上就是"这张图四周一圈脏边"。
        //
        // 与 TrimDarkFrame 的关键区别：阴影带是**灰**的（低饱和度），彩色封面主体是
        //   **鲜艳**的（红封面 R-B≈170）。所以判"灰"而不是判"黑"：
        //   阴影像素 = (max-min) < 50 且 max < 215 —— 白纸（max=250）和红封面都不满足。
        //
        // 三条保险与 TrimDarkFrame 同款：整行/列 ≥80% 连续、单边 ≤ 短边 12%、
        // 裁完剩下的画面 ≥ 原来的 60%。返回 null = 没发现阴影带。
        //
        // ⚠️ 只对"跳过页"（彩色封面/纯印章）调用：黑白照片页整张都是低饱和灰，
        //   面积保护虽然能兜底，但没必要冒险 —— 普通页的边缘由 TrimBlank 管。
        public static Bitmap TrimShadowFrame(Bitmap src, out Rectangle cut)
        {
            cut = Rectangle.Empty;
            if (src == null || src.Width < 64 || src.Height < 64) return null;

            int w = src.Width, h = src.Height;
            PixelBuffer buf = null;
            try { buf = BitmapBridge.ToBuffer(src); }
            catch { return null; }
            byte[] d = buf.Data;
            int stride = buf.Stride;
            int maxSide = Math.Max(4, (int)(Math.Min(w, h) * 0.12));

            int top = 0, bottom = 0, left = 0, right = 0;

            for (int y = 0; y < maxSide; y++)
            {
                int off = y * stride, dark = 0;
                for (int x = 0; x < w; x++)
                {
                    int i = off + x * 3;
                    int b = d[i], g2 = d[i + 1], r = d[i + 2];
                    int mx = b; if (g2 > mx) mx = g2; if (r > mx) mx = r;
                    int mn = b; if (g2 < mn) mn = g2; if (r < mn) mn = r;
                    if (mx < 215 && mx - mn < 50) dark++;
                }
                if (dark < w * 0.80) break;
                top = y + 1;
            }
            for (int y = 0; y < maxSide; y++)
            {
                int off = (h - 1 - y) * stride, dark = 0;
                for (int x = 0; x < w; x++)
                {
                    int i = off + x * 3;
                    int b = d[i], g2 = d[i + 1], r = d[i + 2];
                    int mx = b; if (g2 > mx) mx = g2; if (r > mx) mx = r;
                    int mn = b; if (g2 < mn) mn = g2; if (r < mn) mn = r;
                    if (mx < 215 && mx - mn < 50) dark++;
                }
                if (dark < w * 0.80) break;
                bottom = y + 1;
            }
            for (int x = 0; x < maxSide; x++)
            {
                int dark = 0;
                for (int y = 0; y < h; y++)
                {
                    int i = y * stride + x * 3;
                    int b = d[i], g2 = d[i + 1], r = d[i + 2];
                    int mx = b; if (g2 > mx) mx = g2; if (r > mx) mx = r;
                    int mn = b; if (g2 < mn) mn = g2; if (r < mn) mn = r;
                    if (mx < 215 && mx - mn < 50) dark++;
                }
                if (dark < h * 0.80) break;
                left = x + 1;
            }
            for (int x = 0; x < maxSide; x++)
            {
                int dark = 0;
                int xx = w - 1 - x;
                for (int y = 0; y < h; y++)
                {
                    int i = y * stride + xx * 3;
                    int b = d[i], g2 = d[i + 1], r = d[i + 2];
                    int mx = b; if (g2 > mx) mx = g2; if (r > mx) mx = r;
                    int mn = b; if (g2 < mn) mn = g2; if (r < mn) mn = r;
                    if (mx < 215 && mx - mn < 50) dark++;
                }
                if (dark < h * 0.80) break;
                right = x + 1;
            }

            if (top == 0 && bottom == 0 && left == 0 && right == 0) return null;
            int cw = w - left - right, ch = h - top - bottom;
            if (cw < w * 0.6 || ch < h * 0.6) return null;      // 裁掉的太多 → 是灰色内容，不裁
            cut = new Rectangle(left, top, cw, ch);
            return Crop(src, cut);
        }

        // 标准 A4 的像素尺寸（横版时宽高对调）
        // 按 ISO 216 的 210×297mm 精确换算，得到业界通用的 300dpi=2480×3508、200dpi=1654×2339、150dpi=1240×1754
        public static Size A4Size(int dpi, bool landscape)
        {
            int w = (int)Math.Round(210.0 / 25.4 * dpi);     // 210mm
            int h = (int)Math.Round(297.0 / 25.4 * dpi);     // 297mm
            return landscape ? new Size(h, w) : new Size(w, h);
        }

        // 把图片放进标准 A4 画布：
        //   fit=true  完整放入（等比缩放到画布内，居中，四周留白）——归档最常用
        //   fill=true 铺满画布（等比放大到覆盖整页，多出的部分裁掉）
        public static Bitmap ToA4(Bitmap src, int dpi, bool landscape, bool fill)
        {
            Size a4 = A4Size(dpi, landscape);
            Bitmap page = new Bitmap(a4.Width, a4.Height, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(page))
            {
                g.Clear(Color.White);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                double sx = a4.Width / (double)src.Width;
                double sy = a4.Height / (double)src.Height;
                double s = fill ? Math.Max(sx, sy) : Math.Min(sx, sy);
                int dw = Math.Max(1, (int)Math.Round(src.Width * s));
                int dh = Math.Max(1, (int)Math.Round(src.Height * s));
                int dx = (a4.Width - dw) / 2, dy = (a4.Height - dh) / 2;
                g.DrawImage(src, new Rectangle(dx, dy, dw, dh));
            }
            return page;
        }

        public static Bitmap Ensure24bpp(Bitmap src)
        {
            if (src.PixelFormat == PixelFormat.Format24bppRgb) return src;
            Bitmap dst = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.Clear(Color.White);   // 透明像素按纸张白处理（默认叠加到黑底会变黑）
                g.DrawImage(src, new Rectangle(0, 0, dst.Width, dst.Height));
            }
            // 元数据（EXIF 方向等）必须跟过来，否则灰度 JPEG / 32bpp PNG 这类
            // 非 24bpp 源的 EXIF 转正永久失效
            foreach (PropertyItem pi in src.PropertyItems)
                try { dst.SetPropertyItem(pi); } catch { }
            src.Dispose();
            return dst;
        }

        public static void SaveImage(Bitmap src, string path, int jpgQuality)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            ImageFormat fmt = ImageFormat.Png;
            if (ext == ".jpg" || ext == ".jpeg") fmt = ImageFormat.Jpeg;
            else if (ext == ".bmp") fmt = ImageFormat.Bmp;
            else if (ext == ".tif" || ext == ".tiff") fmt = ImageFormat.Tiff;
            SaveImageAs(src, path, fmt, jpgQuality);
        }

        // 原子写：先写同目录临时文件，成功后整体替换目标。
        // 直接写原路径的话，写到一半断电/磁盘满/进程被杀 = 原件损坏 —— 对档案原件是灾难。
        public static void SaveImageAs(Bitmap src, string path, ImageFormat fmt, int jpgQuality)
        {
            string dir = Path.GetDirectoryName(path);
            if (dir == null || dir.Length == 0) dir = ".";
            string tmp = Path.Combine(dir, "~pictool-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
            bool ok = false;
            try
            {
                if (fmt == ImageFormat.Jpeg) SaveJpeg(src, tmp, jpgQuality);
                else src.Save(tmp, fmt);
                if (File.Exists(path))
                {
                    string bak = tmp + ".bak";
                    File.Replace(tmp, path, bak);       // 原子替换；备份文件随后删掉
                    try { File.Delete(bak); } catch { }
                }
                else File.Move(tmp, path);
                ok = true;
            }
            finally { if (!ok) try { File.Delete(tmp); } catch { } }
        }

        static void SaveJpeg(Bitmap src, string path, int quality)
        {
            if (quality < 10) quality = 10;
            if (quality > 100) quality = 100;
            ImageCodecInfo jpg = null;
            ImageCodecInfo[] encoders = ImageCodecInfo.GetImageEncoders();
            for (int i = 0; i < encoders.Length; i++)
                if (encoders[i].MimeType == "image/jpeg") { jpg = encoders[i]; break; }
            if (jpg == null) { src.Save(path, ImageFormat.Png); return; }   // 没有 JPEG 编码器时退化，避免 null 引用
            using (EncoderParameters eps = new EncoderParameters(1))
            using (EncoderParameter ep = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality))
            {
                eps.Param[0] = ep;
                src.Save(path, jpg, eps);
            }
        }

        // ---------------- 翻转 / 旋转 ----------------

        public static Bitmap Flip(Bitmap src, bool horizontal)
        {
            Bitmap dst = Ensure24bpp(new Bitmap(src));
            dst.RotateFlip(horizontal ? RotateFlipType.RotateNoneFlipX : RotateFlipType.RotateNoneFlipY);
            return dst;
        }

        public static Bitmap Rotate90(Bitmap src, bool clockwise)
        {
            Bitmap dst = Ensure24bpp(new Bitmap(src));
            dst.RotateFlip(clockwise ? RotateFlipType.Rotate90FlipNone : RotateFlipType.Rotate270FlipNone);
            return dst;
        }

        // 任意角度旋转（度）。autoCrop=true 时裁掉旋转产生的空白边。
        public static Bitmap RotateFine(Bitmap src, double degrees, bool autoCrop)
        {
            if (Math.Abs(degrees) < 0.01) return Ensure24bpp(new Bitmap(src));
            double a = degrees % 360.0;
            if (a > 180.0) a -= 360.0;
            if (a < -180.0) a += 360.0;

            double rad = a * Math.PI / 180.0;
            double cos = Math.Abs(Math.Cos(rad)), sin = Math.Abs(Math.Sin(rad));
            int W = src.Width, H = src.Height;
            int bw = (int)Math.Ceiling(W * cos + H * sin);
            int bh = (int)Math.Ceiling(W * sin + H * cos);

            Bitmap rotated = new Bitmap(bw, bh, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(rotated))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TranslateTransform(bw / 2.0f, bh / 2.0f);
                g.RotateTransform((float)a);
                g.DrawImage(src, new Rectangle(-W / 2, -H / 2, W, H));
            }

            if (!autoCrop)
                return To24(rotated);

            // 最大内接矩形（旋转矩形内无空白边的最大水平矩形）
            double t = Math.Abs(a) % 90.0;
            if (t > 45.0) t = 90.0 - t;
            double tr = t * Math.PI / 180.0;
            double cw = W * Math.Cos(tr) - H * Math.Sin(tr);
            double ch = H * Math.Cos(tr) - W * Math.Sin(tr);
            int cropW = (int)Math.Floor(Math.Min(cw, W));
            int cropH = (int)Math.Floor(Math.Min(ch, H));
            if (cropW < 16 || cropH < 16)
            {
                cropW = Math.Max(16, (int)(W * 0.6));
                cropH = Math.Max(16, (int)(H * 0.6));
            }
            int x0 = (bw - cropW) / 2, y0 = (bh - cropH) / 2;
            Rectangle rc = Rectangle.Intersect(new Rectangle(0, 0, bw, bh),
                                               new Rectangle(x0, y0, cropW, cropH));
            Bitmap cropped = rotated.Clone(rc, PixelFormat.Format32bppArgb);
            rotated.Dispose();
            return To24(cropped);
        }

        static Bitmap To24(Bitmap argb)
        {
            Bitmap dst = new Bitmap(argb.Width, argb.Height, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(argb, new Rectangle(0, 0, dst.Width, dst.Height));
            }
            argb.Dispose();
            return dst;
        }

        // 只纠正指定范围：把 roi 内的内容单独旋转后贴回原处，范围外一个像素都不动。
        // 旋转后多出来的边角补白（适合白纸扫描件）；autoCrop=true 时按最大内接矩形裁剪再居中贴回。
        public static Bitmap RotateRegion(Bitmap src, Rectangle roi, double degrees, bool autoCrop)
        {
            Bitmap result = Ensure24bpp(new Bitmap(src));
            roi = Rectangle.Intersect(roi, new Rectangle(0, 0, src.Width, src.Height));
            if (roi.Width < 8 || roi.Height < 8) return result;

            Bitmap piece = result.Clone(roi, PixelFormat.Format24bppRgb);
            Bitmap rot = null;
            try
            {
                rot = RotateFine(piece, degrees, autoCrop);
                // 先把范围内填白，再把转正后的内容居中贴回去
                using (Graphics g = Graphics.FromImage(result))
                using (SolidBrush b = new SolidBrush(Color.White))
                {
                    g.FillRectangle(b, roi);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    int px = roi.X + (roi.Width - rot.Width) / 2;
                    int py = roi.Y + (roi.Height - rot.Height) / 2;
                    Rectangle dst = Rectangle.Intersect(new Rectangle(px, py, rot.Width, rot.Height),
                                                        new Rectangle(0, 0, result.Width, result.Height));
                    if (dst.Width > 0 && dst.Height > 0)
                    {
                        Rectangle srcRect = new Rectangle(dst.X - px, dst.Y - py, dst.Width, dst.Height);
                        g.DrawImage(rot, dst, srcRect, GraphicsUnit.Pixel);
                    }
                }
                return result;
            }
            finally
            {
                piece.Dispose();
                if (rot != null) rot.Dispose();
            }
        }

        // ---------------- 调色 ----------------
        // 四个参数范围都是 -100..100：亮度 / 对比度 / 饱和度 / 色温
        // 就地修改（用于实时预览，避免频繁分配内存）

        public static Bitmap ColorAdjust(Bitmap src, double brightness, double contrast,
                                         double saturation, double temperature)
        {
            Bitmap dst = Ensure24bpp(new Bitmap(src));
            ColorAdjustInPlace(dst, brightness, contrast, saturation, temperature);
            return dst;
        }

        public static unsafe void ColorAdjustInPlace(Bitmap bmp, double brightness, double contrast,
                                                     double saturation, double temperature)
        {
            // 查表：亮度/对比度逐级映射，避免每像素重复计算
            byte[] lum = new byte[256];
            byte[] con = new byte[256];
            byte[] tmp = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                double v = i + brightness * 1.28;                    // 亮度：线性平移
                v = Math.Max(0, Math.Min(255, v));
                lum[i] = (byte)Math.Round(v);
            }
            double c = (100.0 + contrast) / 100.0; c *= c;           // 对比度：围绕 128 拉伸
            for (int i = 0; i < 256; i++)
            {
                double v = (i - 128) * c + 128;
                v = Math.Max(0, Math.Min(255, v));
                con[i] = (byte)Math.Round(v);
            }
            for (int i = 0; i < 256; i++) tmp[i] = con[lum[i]];

            // 色温：暖 = R 增 B 减；冷 = 反之
            int rGain = (int)Math.Round(temperature * 0.40);
            int bGain = -(int)Math.Round(temperature * 0.40);
            byte[] rMap = new byte[256], bMap = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                int vr = i + rGain, vb = i + bGain;
                rMap[i] = (byte)Math.Max(0, Math.Min(255, vr));
                bMap[i] = (byte)Math.Max(0, Math.Min(255, vb));
            }

            double sat = 1.0 + saturation / 100.0;

            PixelBuffer buf = BitmapBridge.ToBuffer(bmp);
            {
            int stride = buf.Stride, w = bmp.Width, h = bmp.Height;
            fixed (byte* basePtr = buf.Data)
            {
            for (int y = 0; y < h; y++)
            {
                byte* row = basePtr + y * stride;
                for (int x = 0; x < w; x++)
                {
                    byte* p = row + x * 3;
                    int b = tmp[p[0]], g = tmp[p[1]], r = tmp[p[2]];
                    r = rMap[r]; b = bMap[b];
                    // 饱和度：按亮度插值
                    if (sat != 1.0)
                    {
                        double l = 0.299 * r + 0.587 * g + 0.114 * b;
                        r = Clamp255(l + (r - l) * sat);
                        g = Clamp255(l + (g - l) * sat);
                        b = Clamp255(l + (b - l) * sat);
                    }
                    p[0] = (byte)b; p[1] = (byte)g; p[2] = (byte)r;
                }
            }
            }
            }
            BitmapBridge.WriteBack(bmp, buf);
        }

        static int Clamp255(double v)
        {
            if (v < 0) return 0;
            if (v > 255) return 255;
            return (int)v;
        }

        // 自动增强：逐通道 0.4%/99.6% 百分位拉伸（自动色阶）
        public static Bitmap AutoEnhance(Bitmap src)
        {
            Bitmap dst = Ensure24bpp(new Bitmap(src));
            AutoEnhanceInPlace(dst);
            return dst;
        }

        public static unsafe void AutoEnhanceInPlace(Bitmap bmp)
        {
            long[] histR = new long[256], histG = new long[256], histB = new long[256];
            PixelBuffer buf = BitmapBridge.ToBuffer(bmp);
            {
            int stride = buf.Stride, w = bmp.Width, h = bmp.Height;
            fixed (byte* basePtr = buf.Data)
            {
            for (int y = 0; y < h; y++)
            {
                byte* row = basePtr + y * stride;
                for (int x = 0; x < w; x++)
                {
                    byte* p = row + x * 3;
                    histB[p[0]]++; histG[p[1]]++; histR[p[2]]++;
                }
            }
            long total = (long)w * h;
            byte[] mapR = StretchMap(histR, total);
            byte[] mapG = StretchMap(histG, total);
            byte[] mapB = StretchMap(histB, total);
            for (int y = 0; y < h; y++)
            {
                byte* row = basePtr + y * stride;
                for (int x = 0; x < w; x++)
                {
                    byte* p = row + x * 3;
                    p[0] = mapB[p[0]]; p[1] = mapG[p[1]]; p[2] = mapR[p[2]];
                }
            }
            }
            }
            BitmapBridge.WriteBack(bmp, buf);
        }

        static byte[] StretchMap(long[] hist, long total)
        {
            byte[] map = new byte[256];
            long cut = (long)(total * 0.004);
            long acc = 0; int lo = 0, hi = 255;
            for (int i = 0; i < 256; i++) { acc += hist[i]; if (acc > cut) { lo = i; break; } }
            acc = 0;
            for (int i = 255; i >= 0; i--) { acc += hist[i]; if (acc > cut) { hi = i; break; } }
            if (hi - lo < 8) { for (int i = 0; i < 256; i++) map[i] = (byte)i; return map; }
            double scale = 255.0 / (hi - lo);
            for (int i = 0; i < 256; i++)
            {
                int v = (int)Math.Round((i - lo) * scale);
                map[i] = (byte)Math.Max(0, Math.Min(255, v));
            }
            return map;
        }

        // ---------------- 去除（Telea 快速行进法修复） ----------------
        // mask：8bpp 灰度图，尺寸同 src，>=128 的像素为待去除区域
        // 只处理涂抹区域的外接矩形（加边距），老机器上也是毫秒~秒级

        public static Bitmap Inpaint(Bitmap src, Bitmap mask, int radius, ProgressHandler progress)
        {
            return Inpaint(src, mask, radius, progress, null);
        }

        public static Bitmap Inpaint(Bitmap src, Bitmap mask, int radius, ProgressHandler progress, CancelCheck cancelled)
        {
            if (src.Width != mask.Width || src.Height != mask.Height)
                throw new Exception("修复遮罩尺寸与图片不一致。");
            if (radius < 2) radius = 2;
            if (radius > 24) radius = 24;

            Rectangle roi = MaskBounds(mask);
            if (roi.Width <= 0 || roi.Height <= 0)
                return Ensure24bpp(new Bitmap(src)); // 空遮罩：原样返回

            int pad = radius + 6;
            int x0 = Math.Max(0, roi.X - pad), y0 = Math.Max(0, roi.Y - pad);
            int x1 = Math.Min(src.Width, roi.X + roi.Width + pad);
            int y1 = Math.Min(src.Height, roi.Y + roi.Height + pad);
            Rectangle full = new Rectangle(0, 0, src.Width, src.Height);
            Rectangle rcRoi = Rectangle.Intersect(full, new Rectangle(x0, y0, x1 - x0, y1 - y0));

            int W = rcRoi.Width, H = rcRoi.Height;
            byte[] img = new byte[W * H * 3];
            byte[] msk = new byte[W * H];

            Rectangle rcSrc = new Rectangle(rcRoi.X, rcRoi.Y, W, H);
            PixelBuffer sbuf = BitmapBridge.ToBuffer(src);
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int q = (rcRoi.Y + y) * sbuf.Stride + (rcRoi.X + x) * 3;
                    int o = (y * W + x) * 3;
                    img[o] = sbuf.Data[q]; img[o + 1] = sbuf.Data[q + 1]; img[o + 2] = sbuf.Data[q + 2];
                }

            PixelBuffer mbuf = BitmapBridge.MaskToBuffer(mask);
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    msk[y * W + x] = (mbuf.Data[(rcRoi.Y + y) * mbuf.Stride + (rcRoi.X + x)] >= 128) ? (byte)1 : (byte)0;

            // 遮罩膨胀：盖住文字/水印边缘的压缩光晕，否则光晕会渗进修复结果
            DilateMask(msk, W, H, 2);

            TeleaFill(img, msk, W, H, radius, progress, cancelled);

            Bitmap result = Ensure24bpp(new Bitmap(src));
            PixelBuffer pbuf = new PixelBuffer(W, H, 3);
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int o = (y * W + x) * 3;
                    int q = y * pbuf.Stride + x * 3;
                    pbuf.Data[q] = img[o]; pbuf.Data[q + 1] = img[o + 1]; pbuf.Data[q + 2] = img[o + 2];
                }
            Bitmap patch = BitmapBridge.BufferToImage(pbuf);
            using (Graphics g = Graphics.FromImage(result))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(patch, rcRoi);
            }
            patch.Dispose();
            return result;
        }

        // 掩码的外接矩形（没有内容时返回空矩形）；"只纠正框选范围"用它取纠正范围
        static void CheckCancel(CancelCheck cancelled)
        {
            if (cancelled != null && cancelled()) throw new OpCancelledException();
        }

        public static Rectangle MaskBounds(Bitmap mask)
        {
            PixelBuffer buf = BitmapBridge.MaskToBuffer(mask);
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            for (int y = 0; y < mask.Height; y++)
            {
                int ro = y * buf.Stride;
                for (int x = 0; x < mask.Width; x++)
                {
                    if (buf.Data[ro + x] >= 128)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
            if (maxX < 0) return Rectangle.Empty;
            return new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
        }

        // 遮罩膨胀（3x3 方形结构元，迭代 iterations 次）
        static void DilateMask(byte[] msk, int W, int H, int iterations)
        {
            byte[] tmp = new byte[msk.Length];
            for (int it = 0; it < iterations; it++)
            {
                int i = 0;
                for (int y = 0; y < H; y++)
                {
                    int ym = (y > 0) ? y - 1 : 0, yp = (y < H - 1) ? y + 1 : H - 1;
                    for (int x = 0; x < W; x++, i++)
                    {
                        if (msk[i] != 0) { tmp[i] = 255; continue; }
                        int xm = (x > 0) ? x - 1 : 0, xp = (x < W - 1) ? x + 1 : W - 1;
                        byte v = msk[y * W + xm];
                        if (v == 0) v = msk[y * W + xp];
                        if (v == 0) v = msk[ym * W + x];
                        if (v == 0) v = msk[yp * W + x];
                        if (v == 0) v = msk[ym * W + xm];
                        if (v == 0) v = msk[ym * W + xp];
                        if (v == 0) v = msk[yp * W + xm];
                        if (v == 0) v = msk[yp * W + xp];
                        tmp[i] = v;
                    }
                }
                Array.Copy(tmp, msk, msk.Length);
            }
        }

        // ---- Telea 算法核心（参照 A. Telea 2004 / OpenCV INPAINT_TELEA 实现）----

        const byte ST_KNOWN = 0;   // 原图已知像素
        const byte ST_BAND = 1;    // 已进入窄带（堆中）待修复像素
        const byte ST_INSIDE = 2;  // 尚未入堆的待修复像素
        const byte ST_FINAL = 3;   // 已修复完成的像素
        const float INF = 1e6f;

        struct HeapNode { public float T; public int X, Y; }

        sealed class MinHeap
        {
            HeapNode[] a = new HeapNode[256];
            int n = 0;
            public int Count { get { return n; } }
            void Grow() { if (n == a.Length) { HeapNode[] b = new HeapNode[a.Length * 2]; Array.Copy(a, b, n); a = b; } }
            public void Push(float t, int x, int y)
            {
                Grow();
                int i = n++;
                a[i].T = t; a[i].X = x; a[i].Y = y;
                while (i > 0)
                {
                    int p = (i - 1) >> 1;
                    if (a[p].T <= a[i].T) break;
                    HeapNode tmp = a[p]; a[p] = a[i]; a[i] = tmp;
                    i = p;
                }
            }
            public HeapNode Pop()
            {
                HeapNode top = a[0];
                n--;
                if (n > 0)
                {
                    a[0] = a[n];
                    int i = 0;
                    while (true)
                    {
                        int l = i * 2 + 1, r = l + 1, m = i;
                        if (l < n && a[l].T < a[m].T) m = l;
                        if (r < n && a[r].T < a[m].T) m = r;
                        if (m == i) break;
                        HeapNode tmp = a[m]; a[m] = a[i]; a[i] = tmp;
                        i = m;
                    }
                }
                return top;
            }
        }

        unsafe static float Eikonal(float t1, float t2)
        {
            // 二维 Eikonal 方程 |∇T|=1 的局部解（t1/t2 为同轴两侧邻居的 T）
            if (t1 >= INF && t2 >= INF) return INF;
            if (t1 >= INF) t1 = t2;
            else if (t2 >= INF) t2 = t1;
            float d = t1 - t2;
            if (d < 0) d = -d;
            if (d >= 1.0f) return Math.Min(t1, t2) + 1.0f;
            float v = 0.5f * (t1 + t2 + (float)Math.Sqrt(2.0 - (double)d * d));
            return v;
        }

        // 用当前 T 值解 (x,y) 点的局部 Eikonal 距离
        unsafe static float SolveAt(float[] T, int W, int H, int x, int y)
        {
            int idx = y * W + x;
            float tL = (x > 0) ? T[idx - 1] : INF;
            float tR = (x < W - 1) ? T[idx + 1] : INF;
            float tU = (y > 0) ? T[idx - W] : INF;
            float tD = (y < H - 1) ? T[idx + W] : INF;
            float t = Eikonal(tL, tR);
            float tv = Eikonal(tU, tD);
            if (tv < t) t = tv;
            return t;
        }

        unsafe static void TeleaFill(byte[] img, byte[] msk, int W, int H, int radius, ProgressHandler progress, CancelCheck cancelled)
        {
            byte[] status = new byte[W * H];
            float[] T = new float[W * H];
            int i, x, y;

            for (i = 0; i < W * H; i++)
            {
                if (msk[i] != 0) { status[i] = ST_INSIDE; T[i] = INF; }
                else { status[i] = ST_KNOWN; T[i] = 0f; }
            }

            MinHeap heap = new MinHeap();
            // 初始窄带：待修复像素中与已知像素相邻的（入堆即认领，保证每像素至多一层副本）
            for (y = 0; y < H; y++)
                for (x = 0; x < W; x++)
                {
                    int idx = y * W + x;
                    if (status[idx] != ST_INSIDE) continue;
                    bool border = false;
                    if (x > 0 && status[idx - 1] == ST_KNOWN) border = true;
                    else if (x < W - 1 && status[idx + 1] == ST_KNOWN) border = true;
                    else if (y > 0 && status[idx - W] == ST_KNOWN) border = true;
                    else if (y < H - 1 && status[idx + W] == ST_KNOWN) border = true;
                    if (border) { status[idx] = ST_BAND; T[idx] = 0f; heap.Push(0f, x, y); }
                }

            int totalInside = 0, done = 0;
            for (i = 0; i < W * H; i++) if (status[i] != ST_KNOWN) totalInside++;
            int nextReport = 0;

            // FMM 主循环：按距离从小到大弹出，先定 T，再立即修复该点
            while (heap.Count > 0)
            {
                HeapNode node = heap.Pop();
                x = node.X; y = node.Y;
                int idx = y * W + x;
                if (status[idx] != ST_BAND) continue; // 过期副本

                float t = SolveAt(T, W, H, x, y);
                if (t >= INF) t = node.T;
                T[idx] = t;

                // 立即修复：用 ε 邻域内已知/已修复像素加权平均
                InpaintPixel(img, status, T, W, H, x, y, radius, t);
                status[idx] = ST_FINAL;

                // 继续向内推进：未入堆的邻居认领入堆；已入堆的仅在明显更优时更新
                for (int k = 0; k < 4; k++)
                {
                    int nx = x + (k == 0 ? -1 : k == 1 ? 1 : 0);
                    int ny = y + (k == 2 ? -1 : k == 3 ? 1 : 0);
                    if (nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
                    int nidx = ny * W + nx;
                    if (status[nidx] == ST_INSIDE)
                    {
                        float nt = SolveAt(T, W, H, nx, ny);
                        if (nt >= INF) nt = t + 1f;
                        status[nidx] = ST_BAND; T[nidx] = nt;
                        heap.Push(nt, nx, ny);
                    }
                    else if (status[nidx] == ST_BAND)
                    {
                        float nt = SolveAt(T, W, H, nx, ny);
                        if (nt < T[nidx] - 0.01f) { T[nidx] = nt; heap.Push(nt, nx, ny); }
                    }
                }

                done++;
                CheckCancel(cancelled);
                if (progress != null && done >= nextReport)
                {
                    progress(done * 100 / Math.Max(1, totalInside));
                    nextReport = done + Math.Max(1, totalInside / 25);
                }
            }
        }

        unsafe static void InpaintPixel(byte[] img, byte[] status, float[] T,
                                        int W, int H, int px, int py, int radius, float tp)
        {
            int pidx = py * W + px;
            int pOff = pidx * 3;
            double sumB = 0, sumG = 0, sumR = 0, sumW = 0;

            for (int dy = -radius; dy <= radius; dy++)
            {
                int qy = py + dy;
                if (qy < 0 || qy >= H) continue;
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int qx = px + dx;
                    if (qx < 0 || qx >= W) continue;
                    int qidx = qy * W + qx;
                    byte st = status[qidx];
                    // 只用已知像素（T=0）和已修复像素（T<=当前）作为来源
                    if (st != ST_KNOWN && st != ST_FINAL) continue;
                    if (st != ST_KNOWN && T[qidx] > tp) continue;

                    float tGradX = GradT(T, status, W, H, qx, qy, true);
                    float tGradY = GradT(T, status, W, H, qx, qy, false);
                    float glen = (float)Math.Sqrt(tGradX * tGradX + tGradY * tGradY);

                    float dist2 = dx * dx + dy * dy;
                    float dist = (float)Math.Sqrt(dist2);
                    float dst = 1.0f / (dist2 * dist);              // 距离权重
                    float lev = 1.0f / (1.0f + Math.Abs(tp - T[qidx])); // 层级权重
                    float dir = 0f;
                    if (glen > 1e-6f)
                    {
                        float inv = 1.0f / (dist * glen);
                        dir = (tGradX * (-dx) + tGradY * (-dy)) * inv; // 等照度线方向权重
                        if (dir < 0f) dir = 0f;
                    }
                    float w = dst * lev * (dir > 0f ? dir : 1e-4f);
                    int qOff = qidx * 3;
                    sumB += img[qOff] * w;
                    sumG += img[qOff + 1] * w;
                    sumR += img[qOff + 2] * w;
                    sumW += w;
                }
            }

            if (sumW <= 1e-12)
            {
                // 兜底：取最近已知像素
                for (int d = 1; d < 64; d++)
                {
                    bool found = false;
                    for (int dy = -d; dy <= d && !found; dy++)
                        for (int dx = -d; dx <= d && !found; dx++)
                        {
                            int qx = px + dx, qy = py + dy;
                            if (qx < 0 || qy < 0 || qx >= W || qy >= H) continue;
                            int qidx = qy * W + qx;
                            if (status[qidx] == ST_KNOWN || status[qidx] == ST_FINAL)
                            {
                                int qOff = qidx * 3;
                                img[pOff] = img[qOff]; img[pOff + 1] = img[qOff + 1]; img[pOff + 2] = img[qOff + 2];
                                found = true;
                            }
                        }
                    if (found) break;
                }
            }
            else
            {
                img[pOff] = (byte)Clamp255(sumB / sumW);
                img[pOff + 1] = (byte)Clamp255(sumG / sumW);
                img[pOff + 2] = (byte)Clamp255(sumR / sumW);
            }
        }

        static float GradT(float[] T, byte[] status, int W, int H, int x, int y, bool horizontal)
        {
            int idx = y * W + x;
            float c = (status[idx] == ST_KNOWN) ? 0f : T[idx];
            float prev, next;
            if (horizontal)
            {
                prev = (x > 0) ? ((status[idx - 1] == ST_KNOWN) ? 0f : T[idx - 1]) : c;
                next = (x < W - 1) ? ((status[idx + 1] == ST_KNOWN) ? 0f : T[idx + 1]) : c;
            }
            else
            {
                prev = (y > 0) ? ((status[idx - W] == ST_KNOWN) ? 0f : T[idx - W]) : c;
                next = (y < H - 1) ? ((status[idx + W] == ST_KNOWN) ? 0f : T[idx + W]) : c;
            }
            // 选梯度更大的方向（Telea 惯例：正向或反向差分取绝对值大者）
            float g1 = c - prev, g2 = next - c;
            if (Math.Abs(g1) > Math.Abs(g2)) return g1;
            return g2;
        }

        // ---------------- 修复遮罩 ----------------
        // 遮罩统一用 8bpp 灰度位图（0=保留，>=128=去除）。
        // 8bpp 是索引格式，GDI+ 不能直接画，所以用像素级光栅化（也是最快的画法）。

        public static Bitmap MakeMask(int w, int h)
        {
            // P1 迁移：空掩膜 = 全 0 的 8bpp 缓冲转位图（灰度调色板由桥统一设置）
            return BitmapBridge.BufferToMask(new PixelBuffer(w, h, 1));
        }

        // 在遮罩上画/擦一个圆（add=true 涂抹，false 橡皮擦）
        public static unsafe void MaskCircle(Bitmap mask, float cx, float cy, float r, bool add)
        {
            Rectangle rc = new Rectangle(0, 0, mask.Width, mask.Height);
            int x0 = Math.Max(0, (int)(cx - r) - 1), x1 = Math.Min(mask.Width - 1, (int)(cx + r) + 1);
            int y0 = Math.Max(0, (int)(cy - r) - 1), y1 = Math.Min(mask.Height - 1, (int)(cy + r) + 1);
            if (x1 < x0 || y1 < y0) return;
            BitmapData d = mask.LockBits(new Rectangle(x0, y0, x1 - x0 + 1, y1 - y0 + 1),  // resource-ok: 画笔实时路径
                                         ImageLockMode.ReadWrite, PixelFormat.Format8bppIndexed);
            byte* basePtr = (byte*)d.Scan0;
            float r2 = r * r;
            for (int y = y0; y <= y1; y++)
            {
                byte* row = basePtr + (y - y0) * d.Stride;
                float ddy = y - cy;
                for (int x = x0; x <= x1; x++)
                {
                    float ddx = x - cx;
                    if (ddx * ddx + ddy * ddy <= r2)
                        row[x - x0] = add ? (byte)255 : (byte)0;
                }
            }
            mask.UnlockBits(d);
        }

        // 24bpp 灰度图转 8bpp 遮罩（亮度>=128 记为待去除）。测试用。
        public static Bitmap ConvertToMask(Bitmap gray)
        {
            PixelBuffer gb = BitmapBridge.ToBuffer(gray);
            PixelBuffer mb = new PixelBuffer(gray.Width, gray.Height, 1);
            for (int y = 0; y < gray.Height; y++)
            {
                int rg = y * gb.Stride, rm = y * mb.Stride;
                for (int x = 0; x < gray.Width; x++)
                {
                    int p = rg + x * 3;
                    int lum = (gb.Data[p] + gb.Data[p + 1] + gb.Data[p + 2]) / 3;
                    mb.Data[rm + x] = (byte)(lum >= 128 ? 255 : 0);
                }
            }
            return BitmapBridge.BufferToMask(mb);
        }

        // 在可视化遮罩层（32bpp ARGB）上画/擦一个圆（与 MaskCircle 配套使用）
        public static unsafe void OverlayCircle(Bitmap overlay, float cx, float cy, float r, bool add)
        {
            Rectangle rc = new Rectangle(0, 0, overlay.Width, overlay.Height);
            int x0 = Math.Max(0, (int)(cx - r) - 1), x1 = Math.Min(overlay.Width - 1, (int)(cx + r) + 1);
            int y0 = Math.Max(0, (int)(cy - r) - 1), y1 = Math.Min(overlay.Height - 1, (int)(cy + r) + 1);
            if (x1 < x0 || y1 < y0) return;
            BitmapData d = overlay.LockBits(new Rectangle(x0, y0, x1 - x0 + 1, y1 - y0 + 1),  // resource-ok: 画笔实时路径
                                            ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            int paint = MaskPaint.ToArgb();  // 半透明红（ARGB 内存序）
            byte* basePtr = (byte*)d.Scan0;
            float r2 = r * r;
            for (int y = y0; y <= y1; y++)
            {
                int* row = (int*)(basePtr + (y - y0) * d.Stride);
                float ddy = y - cy;
                for (int x = x0; x <= x1; x++)
                {
                    float ddx = x - cx;
                    if (ddx * ddx + ddy * ddy <= r2)
                        row[x - x0] = add ? paint : 0;
                }
            }
            overlay.UnlockBits(d);
        }

        // 在遮罩上填充一个矩形（框选）
        public static unsafe void MaskRect(Bitmap mask, Rectangle rect, bool add)
        {
            Rectangle full = new Rectangle(0, 0, mask.Width, mask.Height);
            Rectangle rc = Rectangle.Intersect(full, rect);
            if (rc.Width <= 0 || rc.Height <= 0) return;
            BitmapData d = mask.LockBits(rc, ImageLockMode.ReadWrite, PixelFormat.Format8bppIndexed);  // resource-ok: 画笔实时路径
            byte v = add ? (byte)255 : (byte)0;
            for (int y = 0; y < rc.Height; y++)
            {
                byte* row = (byte*)d.Scan0 + y * d.Stride;
                for (int x = 0; x < rc.Width; x++) row[x] = v;
            }
            mask.UnlockBits(d);
        }

        // 在可视化遮罩层上填充一个矩形
        public static unsafe void OverlayRect(Bitmap overlay, Rectangle rect, bool add)
        {
            Rectangle full = new Rectangle(0, 0, overlay.Width, overlay.Height);
            Rectangle rc = Rectangle.Intersect(full, rect);
            if (rc.Width <= 0 || rc.Height <= 0) return;
            BitmapData d = overlay.LockBits(rc, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);  // resource-ok: 画笔实时路径
            int paint = add ? MaskPaint.ToArgb() : 0;
            for (int y = 0; y < rc.Height; y++)
            {
                int* row = (int*)((byte*)d.Scan0 + y * d.Stride);
                for (int x = 0; x < rc.Width; x++) row[x] = paint;
            }
            overlay.UnlockBits(d);
        }

        // 可以安全覆盖写回的原格式（其它格式覆盖会改变文件实际编码）
        public static bool CanOverwrite(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".bmp" || ext == ".tif" || ext == ".tiff";
        }

        // 位图是否仍然可用（已释放的位图访问会抛异常，绘制前先检查，避免崩溃）
        public static bool Alive(Bitmap b)
        {
            if (b == null) return false;
            try { int w = b.Width; return true; }
            catch { return false; }
        }

        // 遮罩面积（像素数）
        public static int MaskArea(Bitmap mask)
        {
            int n = 0;
            PixelBuffer buf = BitmapBridge.MaskToBuffer(mask);
            for (int y = 0; y < mask.Height; y++)
            {
                int ro = y * buf.Stride;
                for (int x = 0; x < mask.Width; x++) if (buf.Data[ro + x] >= 128) n++;
            }
            return n;
        }

        // 仿制图章：把 src 中以 (cx+dx, cy+dy) 为心的圆形区域，按羽化掩码叠加到 dst 的 (cx, cy)
        // mask：半径 r 的羽化权重表，尺寸 (2r+1)²，索引 (dy+r)*(2r+1)+(dx+r)
        public static unsafe void CloneCircle(Bitmap dst, Bitmap src, float cx, float cy, float dx, float dy,
                                              byte[] mask, int r)
        {
            int w = dst.Width, h = dst.Height;
            int x0 = Math.Max(0, (int)(cx - r)), x1 = Math.Min(w - 1, (int)(cx + r));
            int y0 = Math.Max(0, (int)(cy - r)), y1 = Math.Min(h - 1, (int)(cy + r));
            if (x1 < x0 || y1 < y0) return;

            Rectangle rc = new Rectangle(0, 0, w, h);
            BitmapData dd = dst.LockBits(rc, ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);  // resource-ok: 仿制图章实时路径（小 ROI 高频调用）
            BitmapData ds = null;
            try
            {
            ds = src.LockBits(rc, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            int side = 2 * r + 1;
            int icx = (int)Math.Round(cx), icy = (int)Math.Round(cy);
            int idx = (int)Math.Round(dx), idy = (int)Math.Round(dy);
            for (int y = y0; y <= y1; y++)
            {
                int sy = y + idy;
                if (sy < 0 || sy >= h) continue;
                byte* pd = (byte*)dd.Scan0 + y * dd.Stride;
                byte* ps = (byte*)ds.Scan0 + sy * ds.Stride;
                for (int x = x0; x <= x1; x++)
                {
                    int sx = x + idx;
                    if (sx < 0 || sx >= w) continue;
                    int mx = x - icx + r, my = y - icy + r;
                    if (mx < 0 || my < 0 || mx >= side || my >= side) continue;
                    int a = mask[my * side + mx];
                    if (a == 0) continue;
                    byte* d3 = pd + x * 3;
                    byte* s3 = ps + sx * 3;
                    if (a == 255)
                    {
                        d3[0] = s3[0]; d3[1] = s3[1]; d3[2] = s3[2];
                    }
                    else
                    {
                        int ia = 255 - a;
                        d3[0] = (byte)((s3[0] * a + d3[0] * ia) / 255);
                        d3[1] = (byte)((s3[1] * a + d3[1] * ia) / 255);
                        d3[2] = (byte)((s3[2] * a + d3[2] * ia) / 255);
                    }
                }
            }
            }
            finally
            {
                if (ds != null) src.UnlockBits(ds);
                dst.UnlockBits(dd);
            }
        }

        // 生成半径 r 的羽化掩码：内部 75% 实心，边缘线性过渡
        public static byte[] MakeBrushMask(int r)
        {
            int side = 2 * r + 1;
            byte[] m = new byte[side * side];
            double inner = r * 0.75;
            for (int y = -r; y <= r; y++)
                for (int x = -r; x <= r; x++)
                {
                    double d = Math.Sqrt((double)x * x + (double)y * y);
                    byte v;
                    if (d <= inner) v = 255;
                    else if (d >= r) v = 0;
                    else v = (byte)(255 * (r - d) / (r - inner));
                    m[(y + r) * side + (x + r)] = v;
                }
            return m;
        }

        // 由 8bpp 掩码生成可视化层（半透明着色），用于把识别结果显示给用户
        public static Bitmap OverlayFromMask(Bitmap mask, Color color, byte alpha)
        {
            int w = mask.Width, h = mask.Height;
            PixelBuffer mb = BitmapBridge.MaskToBuffer(mask);
            PixelBuffer ob = new PixelBuffer(w, h, 4);
            int paint = Color.FromArgb(alpha, color).ToArgb();
            for (int y = 0; y < h; y++)
            {
                int ro = y * mb.Stride, wo = y * ob.Stride;
                for (int x = 0; x < w; x++)
                {
                    int v = mb.Data[ro + x] >= 128 ? paint : 0;
                    ob.Data[wo + x * 4] = (byte)v;
                    ob.Data[wo + x * 4 + 1] = (byte)(v >> 8);
                    ob.Data[wo + x * 4 + 2] = (byte)(v >> 16);
                    ob.Data[wo + x * 4 + 3] = (byte)(v >> 24);
                }
            }
            return BitmapBridge.BufferToOverlay(ob);
        }

        // 复制一份 8bpp 掩码（后台线程用，避免与界面共享 GDI+ 对象）
        public static Bitmap CopyMask(Bitmap mask)
        {
            PixelBuffer sb = BitmapBridge.MaskToBuffer(mask);
            PixelBuffer db = new PixelBuffer(mask.Width, mask.Height, 1);
            for (int y = 0; y < mask.Height; y++)
                Buffer.BlockCopy(sb.Data, y * sb.Stride, db.Data, y * db.Stride, mask.Width);
            return BitmapBridge.BufferToMask(db);
        }

        // ---------------- 杂项 ----------------

        public static string FormatBytes(long n)
        {
            if (n < 1024) return n + " B";
            if (n < 1024 * 1024) return (n / 1024.0).ToString("F1") + " KB";
            if (n < 1024L * 1024 * 1024) return (n / 1024.0 / 1024.0).ToString("F1") + " MB";
            return (n / 1024.0 / 1024.0 / 1024.0).ToString("F2") + " GB";
        }
    }
}
