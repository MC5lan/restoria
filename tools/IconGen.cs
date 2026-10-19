// IconGen.cs — 生成 assets\app.ico（多尺寸）+ 变体对比图
//
// 设计规范依据（阿里开放平台《图形设计风格》+ 行业"减法 prompt"共识）：
//   1. 纯色背景，禁止渐变 / 阴影 / 纹理 / 3D
//   2. 图形归约为 1~2 个基础几何形，白色主体，对比色仅小面积点缀
//   3. 为小尺寸设计：16/24/32px 下不能糊成一团
//   4. 对比色尽量小面积
//
// R 的构造：严格几何网格 + 等宽笔画（30 设计单位，全字统一）
//   左竖  x 62..92   y 50..206
//   碗板  x 62..194  y 50..138（右侧半圆，圆心 (150,94) 半径 44）
//   内孔  x 92..164  y 80..108（高 28）
//   斜腿  四边形 (120,138)(152,138)(178,206)(146,206)
// 碗板用 FillMode.Alternate 一次挖孔：顶横 / 碗环 / 中横一次成型，无接缝。
//
// 变体：0=A 圆孔 R · 1=B 方孔 R · 2=C 朱砂斜腿 · 3=D 方孔+折角
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
            // 默认变体 2 = C（朱砂斜腿）：品牌记忆点最强，32px 下仍可辨识
            int variant = args.Length > 2 ? int.Parse(args[2]) : 2;
            int[] sizes = new int[] { 16, 24, 32, 48, 64, 128, 256 };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)));

            WriteIco(outPath, sizes, variant);
            Console.WriteLine("生成: " + Path.GetFullPath(outPath) + "  (" + new FileInfo(outPath).Length + " 字节, " + sizes.Length + " 个尺寸, 变体 " + variant + ")");

            // 另出一份"小尺寸版"：自解压安装程序的 -iicon 不接受 128/256 的大条目
            string small = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath)), "app-small.ico");
            WriteIco(small, new int[] { 16, 24, 32, 48, 64 }, variant);
            Console.WriteLine("生成: " + small + "  (" + new FileInfo(small).Length + " 字节, 5 个尺寸，供安装包使用)");

            // 第二个参数可选：同时导出一张 256×256 PNG（用于文档/预览）
            if (args.Length > 1)
            {
                using (Bitmap bmp = Render(256, variant))
                    bmp.Save(args[1], ImageFormat.Png);
                Console.WriteLine("预览图: " + Path.GetFullPath(args[1]));
            }

            // 变体对比图（4 变体 × 256/64/32 三档尺寸）
            string sheet = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath)), "icon-variants.png");
            DrawSheet(sheet);
            Console.WriteLine("对比图: " + Path.GetFullPath(sheet));
        }

        static void WriteIco(string outPath, int[] sizes, int variant)
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
                    using (Bitmap bmp = Render(sizes[i], variant))
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

        // ---------------- 变体对比图 ----------------
        static void DrawSheet(string path)
        {
            const int cell = 256, gap = 20, margin = 28, headH = 40;
            int cols = 4;
            int W = margin * 2 + cols * (cell + gap) - gap;
            int smallY = margin + headH + cell + 22;
            int H = smallY + 64 + margin;

            using (Bitmap bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.FromArgb(0xF2, 0xF4, 0xF7));

                string[] tag = { "A", "B", "C", "D" };
                string[] cap = { "round counter", "square counter", "cinnabar leg", "folded corner" };
                using (Font fb = new Font("Segoe UI", 20, FontStyle.Bold, GraphicsUnit.Pixel))
                using (Font fc = new Font("Segoe UI", 14, FontStyle.Regular, GraphicsUnit.Pixel))
                using (SolidBrush ink = new SolidBrush(Color.FromArgb(0x1B, 0x2A, 0x3B)))
                using (SolidBrush grey = new SolidBrush(Color.FromArgb(0x64, 0x74, 0x8B)))
                {
                    for (int i = 0; i < 4; i++)
                    {
                        int x0 = margin + i * (cell + gap);
                        g.DrawString(tag[i], fb, ink, x0 + 2, margin - 4);
                        g.DrawString(cap[i], fc, grey, x0 + 30, margin + 4);

                        using (Bitmap big = Render(256, i))
                            g.DrawImage(big, x0, margin + headH);
                        using (Bitmap m64 = Render(64, i))
                            g.DrawImage(m64, x0, smallY);
                        using (Bitmap m32 = Render(32, i))
                            g.DrawImage(m32, x0 + 80, smallY + 16);
                    }
                    g.DrawString("256 / 64 / 32 px", fc, grey, margin, smallY + 70);
                }
                bmp.Save(path, ImageFormat.Png);
            }
        }

        // ---------------- 核心绘制 ----------------
        // variant: 0=A 圆孔 · 1=B 方孔 · 2=C 朱砂斜腿 · 3=D 方孔+右上折角
        static Bitmap Render(int S, int variant)
        {
            Bitmap bmp = new Bitmap(S, S, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);

                float k = S / 256f;                        // 设计空间 256×256
                bool fine = S >= 48;                       // 小尺寸下去掉折角（会糊）
                Color ink = Color.FromArgb(0x16, 0x23, 0x3A);        // 深墨蓝（纯色，禁用渐变）
                Color paper = Color.FromArgb(0xF7, 0xF4, 0xED);     // 纸白
                Color cinnabar = Color.FromArgb(0xC0, 0x39, 0x2B);  // 朱砂（唯一对比色）

                // ---- 纯色圆角方底（超椭圆感，半径 56/256 ≈ 22%）----
                using (GraphicsPath p = RoundRect(new RectangleF(8 * k, 8 * k, 240 * k, 240 * k), 56 * k))
                using (SolidBrush b = new SolidBrush(ink))
                    g.FillPath(b, p);

                // ---- 碗板（顶横 + 碗环 + 中横，Alternate 一次挖孔）----
                using (GraphicsPath plate = BuildPlate(k, variant, fine))
                using (SolidBrush b = new SolidBrush(paper))
                    g.FillPath(b, plate);

                // ---- 左竖 ----
                using (GraphicsPath stem = new GraphicsPath())
                {
                    stem.AddRectangle(new RectangleF(62 * k, 50 * k, 30 * k, 156 * k));
                    using (SolidBrush b = new SolidBrush(paper))
                        g.FillPath(b, stem);
                }

                // ---- 斜腿（变体 C 用朱砂，构成唯一对比色）----
                using (GraphicsPath leg = BuildLeg(k))
                using (SolidBrush b = new SolidBrush(variant == 2 ? cinnabar : paper))
                    g.FillPath(b, leg);
            }
            return bmp;
        }

        // 碗板：外轮廓 = 矩形右侧接半圆；内孔用 Alternate 挖掉
        static GraphicsPath BuildPlate(float k, int variant, bool fine)
        {
            GraphicsPath p = new GraphicsPath();
            p.FillMode = FillMode.Alternate;

            // 外轮廓：(62,50) →(150,50) →半圆→ (150,138) →(62,138)
            p.StartFigure();
            p.AddLine(62 * k, 50 * k, 150 * k, 50 * k);
            p.AddArc(106 * k, 50 * k, 88 * k, 88 * k, -90, 180);   // 圆心(150,94) r=44
            p.AddLine(150 * k, 138 * k, 62 * k, 138 * k);
            p.CloseFigure();

            // 内孔：y 80..108，x 92..（164）
            p.StartFigure();
            if (variant == 0)
            {
                // A 圆孔：右侧内弧（圆心(150,94) r=14）
                p.AddLine(92 * k, 80 * k, 150 * k, 80 * k);
                p.AddArc(136 * k, 80 * k, 28 * k, 28 * k, -90, 180);
                p.AddLine(150 * k, 108 * k, 92 * k, 108 * k);
            }
            else if (variant == 3 && fine)
            {
                // D 方孔 + 右上 45° 折角（纸折角意象，小尺寸自动退化为方孔）
                p.AddLine(92 * k, 80 * k, 152 * k, 80 * k);
                p.AddLine(152 * k, 80 * k, 164 * k, 92 * k);
                p.AddLine(164 * k, 92 * k, 164 * k, 108 * k);
                p.AddLine(164 * k, 108 * k, 92 * k, 108 * k);
            }
            else
            {
                // B / D(小尺寸) 方孔
                p.AddLine(92 * k, 80 * k, 164 * k, 80 * k);
                p.AddLine(164 * k, 80 * k, 164 * k, 108 * k);
                p.AddLine(164 * k, 108 * k, 92 * k, 108 * k);
            }
            p.CloseFigure();
            return p;
        }

        // 斜腿：上宽 32（垂直厚度 30，因斜率 68/26）
        static GraphicsPath BuildLeg(float k)
        {
            GraphicsPath p = new GraphicsPath();
            p.StartFigure();
            p.AddLine(120 * k, 138 * k, 152 * k, 138 * k);
            p.AddLine(152 * k, 138 * k, 178 * k, 206 * k);
            p.AddLine(178 * k, 206 * k, 146 * k, 206 * k);
            p.AddLine(146 * k, 206 * k, 120 * k, 138 * k);
            p.CloseFigure();
            return p;
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

        // ---------------- .ico 写入 ----------------
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