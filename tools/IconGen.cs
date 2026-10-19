// IconGen.cs — 按 assets\icon.svg 的同一份几何生成多尺寸 .ico
//
// 为什么不用 SVG 渲染器：目标机上和开发机上都没有 Inkscape/ImageMagick/rsvg，
// 而程序需要的是 WinForms 能用的 .ico。这里用 GDI+ 把同一份几何重画一遍：
//   * 大尺寸（>=48）：折角文档 + 三行文本 + 扫描光带 + 像素点
//   * 小尺寸（<=32）：简化成"折角文档 + 两行文本"，避免糊成一团
// 输出：assets\app.ico（16/24/32/48/64 用 BMP 条目，128/256 用 PNG 条目，兼容 Win7）
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace IconGen
{
    static class Program
    {
        static void Main(string[] args)
        {
            string outPath = args.Length > 0 ? args[0] : @"assets\app.ico";
            int[] sizes = new int[] { 16, 24, 32, 48, 64, 128, 256 };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)));

            WriteIco(outPath, sizes);
            Console.WriteLine("生成: " + Path.GetFullPath(outPath) + "  (" + new FileInfo(outPath).Length + " 字节, " + sizes.Length + " 个尺寸)");

            // 另出一份"小尺寸版"：自解压安装程序的 -iicon 不接受 128/256 的大条目
            string small = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath)), "app-small.ico");
            WriteIco(small, new int[] { 16, 24, 32, 48, 64 });
            Console.WriteLine("生成: " + small + "  (" + new FileInfo(small).Length + " 字节, 5 个尺寸，供安装包使用)");

            // 第二个参数可选：同时导出一张 256×256 PNG（用于文档/预览）
            if (args.Length > 1)
            {
                using (Bitmap bmp = Render(256))
                    bmp.Save(args[1], ImageFormat.Png);
                Console.WriteLine("预览图: " + Path.GetFullPath(args[1]));
            }
        }

        static void WriteIco(string outPath, int[] sizes)
        {
            using (FileStream fs = new FileStream(outPath, FileMode.Create, FileAccess.Write))
            using (BinaryWriter w = new BinaryWriter(fs))
            {
                int n = sizes.Length;
                // ICONDIR
                w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)n);
                long dataStart = 6 + 16 * n;
                long offset = dataStart;
                byte[][] blobs = new byte[n][];
                for (int i = 0; i < n; i++)
                {
                    using (Bitmap bmp = Render(sizes[i]))
                        blobs[i] = BmpBytes(bmp);   // 全部用 BMP 条目：PNG 条目 System.Drawing.Icon 读不了，Win7 外壳也不稳
                }
                for (int i = 0; i < n; i++)
                {
                    int s = sizes[i];
                    w.Write((byte)(s >= 256 ? 0 : s));      // 宽（256 记 0）
                    w.Write((byte)(s >= 256 ? 0 : s));      // 高
                    w.Write((byte)0);                        // 调色板
                    w.Write((byte)0);                        // 保留
                    w.Write((ushort)1);                      // 平面数
                    w.Write((ushort)32);                     // 位深
                    w.Write((uint)blobs[i].Length);
                    w.Write((uint)offset);
                    offset += blobs[i].Length;
                }
                for (int i = 0; i < n; i++) w.Write(blobs[i]);
            }
        }

        static Bitmap Render(int S)
        {
            Bitmap bmp = new Bitmap(S, S, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);

                float k = S / 256f;                       // 设计稿是 256×256
                bool small = S <= 32;

                // ---- 03 档案抽屉：蓝灰渐变底 + 三层抽屉 + 状态点 ----
                // 几何与 assets\icon-proposals\gen_icons.py 的 v03_archive 一致（1024 空间比例 /4 = 256 空间）
                // 圆角方底（纵向渐变 蓝灰 #475569 → 深藏青 #1E293B）
                RectangleF bg = new RectangleF(8 * k, 8 * k, 240 * k, 240 * k);
                using (GraphicsPath p = RoundRect(bg, 54 * k))
                using (LinearGradientBrush br = new LinearGradientBrush(
                    new PointF(0, bg.Top), new PointF(0, bg.Bottom),
                    Color.FromArgb(0x47, 0x55, 0x69), Color.FromArgb(0x1E, 0x29, 0x3B)))
                    g.FillPath(br, p);

                float bx = 61.4f * k, by = 51.2f * k;
                float bw = 133.1f * k, bh = 153.6f * k;
                float gap = 7.2f * k;
                float hh = (bh - gap * 2) / 3f;
                float[] tabs = { 0.18f, 0.42f, 0.30f };

                for (int i = 0; i < 3; i++)
                {
                    float ty = by + i * (hh + gap);
                    // 抽屉屉体
                    RectangleF row = new RectangleF(bx, ty, bw, hh);
                    using (GraphicsPath p = RoundRect(row, 7.7f * k))
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(0xF1, 0xF5, 0xF9)))
                        g.FillPath(b, p);
                    if (!small)
                    {
                        // 抽屉拉手（标签块）
                        RectangleF tab = new RectangleF(
                            bx + bw * tabs[i], ty + hh * 0.30f, bw * 0.22f, hh * 0.32f);
                        using (GraphicsPath p = RoundRect(tab, 3.6f * k))
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(0x64, 0x74, 0x8B)))
                            g.FillPath(b, p);
                    }
                    // 右侧状态点（青色：数字化就绪）
                    float dot = hh * 0.28f;
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(0x38, 0xBD, 0xF8)))
                        g.FillEllipse(b, bx + bw * 0.86f, ty + hh * 0.36f, dot, dot);
                }
            }
            return bmp;
        }

        static GraphicsPath RoundRect(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // 文档外形：左上右下直角，右上有个折角切掉
        static GraphicsPath DocPath(RectangleF r, float fold)
        {
            GraphicsPath p = new GraphicsPath();
            p.AddLine(r.Left, r.Top, r.Right - fold, r.Top);
            p.AddLine(r.Right - fold, r.Top, r.Right, r.Top + fold);
            p.AddLine(r.Right, r.Top + fold, r.Right, r.Bottom);
            p.AddLine(r.Right, r.Bottom, r.Left, r.Bottom);
            p.CloseFigure();
            return p;
        }

        static byte[] PngBytes(Bitmap bmp)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }

        // 32bpp DIB 条目（BITMAPINFOHEADER：高度翻倍 + XOR 位图 + AND 掩码）
        static byte[] BmpBytes(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                bw.Write(40);                  // biSize
                bw.Write(w);
                bw.Write(h * 2);               // 含掩码，高度翻倍
                bw.Write((ushort)1);
                bw.Write((ushort)32);
                bw.Write(0);                   // BI_RGB
                bw.Write(w * h * 4);
                bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
                // XOR：自下而上，BGRA
                for (int y = h - 1; y >= 0; y--)
                    for (int x = 0; x < w; x++)
                    {
                        Color c = bmp.GetPixel(x, y);
                        bw.Write(c.B); bw.Write(c.G); bw.Write(c.R); bw.Write(c.A);
                    }
                // AND 掩码：全 0（Alpha 通道已经表达透明）
                int stride = ((w + 31) / 32) * 4;
                byte[] zero = new byte[stride];
                for (int y = 0; y < h; y++) bw.Write(zero);
                return ms.ToArray();
            }
        }
    }
}
