// DocClean.Red.cs -- DocClean 分文件（六）：红色内容保护。
// 红框内部识别 RedEncloseMask（泛洪四处接入）+ 保红/红计数/墨量统计。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;

namespace PicTool
{
    public static partial class DocClean
    {
        // ==================== 三之四、红框内部识别 ====================
        //
        // 用户原话："把红框子里的黑色字样块也要保护，不能删。"
        //
        // 为什么去黑点专挑红框里的内容下手（两条判据叠加，登记表页实测）：
        //   ① 框线是**浅红/褪色红/细线**（如 (250,205,208)），按亮度算接近纸面 ——
        //      在"墨"的语义里根本不算内容，于是框里那块黑笔字在 gap 环带内**找不到别的墨**，
        //      被判成"孤立"；
        //   ② 表里手写的短字块（"097"、"2p4h" 这种）又紧实（fill ≥ 0.70）又近方，
        //      正好命中 R1"孤立实心斑"。两条一合 = 框里的填写内容被当灰尘整块抹掉。
        //   大半径护字（guardR ≈ 16px）救不了：格子一大，字块离框线就超过 16px。
        //
        // 判据（不看内容，只看拓扑）：**被红色闭环围住的区域 = 内容区**。
        //   ① 取偏红掩膜 → ② 外扩 bridge px，把褪色/断线的框线接成闭环 → ③ 从画幅四边泛洪，
        //   泛不到的地方就是"被框住"的内部。任何贴着画幅边缘的区域都直接有种子，
        //   所以"泛不到"必然是真正的内部（一条横贯全页的红线把图切成两半也不会误判 ——
        //   两半各自都够得着画幅边缘）。
        //
        // 护栏：内部像素 > 60% 画幅时判为"整页红边框"，直接放弃保护。
        //   那种页面的内容本来就贴着框线（guardR 覆盖得到），而把整页纸面都保护起来
        //   等于让去黑点整体失效（沉淀的黑点会全部回来）。
        //
        // 返回：w*h 掩膜（255 = 红框内部）；out inside = 内部像素数（0 = 没找到 / 已放弃）。
        public static unsafe byte[] RedEncloseMask(Bitmap bmp, int redMargin, int bridge, out int inside)
        {
            inside = 0;
            if (bmp == null) return null;
            int w = bmp.Width, h = bmp.Height;
            if (w < 32 || h < 32) return null;
            if (bridge <= 0)
            {
                bridge = Math.Min(w, h) / 400;        // 200dpi A4 ≈ 4px
                if (bridge < 2) bridge = 2;
                if (bridge > 10) bridge = 10;
            }

            byte[] line = DilateMask(RedProtectMask(bmp, redMargin), w, h, bridge);
            byte[] seen = new byte[w * h];
            int[] q = new int[w * h];
            int rd = 0, wr = 0;
            for (int x = 0; x < w; x++)               // 上下两条边上的种子
            {
                if (line[x] == 0 && seen[x] == 0) { seen[x] = 1; q[wr++] = x; }
                int e = (h - 1) * w + x;
                if (line[e] == 0 && seen[e] == 0) { seen[e] = 1; q[wr++] = e; }
            }
            for (int y = 0; y < h; y++)               // 左右两条边上的种子
            {
                int e = y * w;
                if (line[e] == 0 && seen[e] == 0) { seen[e] = 1; q[wr++] = e; }
                int f = e + w - 1;
                if (line[f] == 0 && seen[f] == 0) { seen[f] = 1; q[wr++] = f; }
            }
            while (rd < wr)                            // 背景连通性用 4 邻接才严密
            {
                int cur = q[rd++];
                int cx = cur % w, cy = cur / w;
                if (cx > 0) { int t = cur - 1; if (line[t] == 0 && seen[t] == 0) { seen[t] = 1; q[wr++] = t; } }
                if (cx < w - 1) { int t = cur + 1; if (line[t] == 0 && seen[t] == 0) { seen[t] = 1; q[wr++] = t; } }
                if (cy > 0) { int t = cur - w; if (line[t] == 0 && seen[t] == 0) { seen[t] = 1; q[wr++] = t; } }
                if (cy < h - 1) { int t = cur + w; if (line[t] == 0 && seen[t] == 0) { seen[t] = 1; q[wr++] = t; } }
            }

            byte[] m = new byte[w * h];
            int cnt = 0;
            for (int i = 0; i < w * h; i++)
                if (line[i] == 0 && seen[i] == 0) { m[i] = 255; cnt++; }
            if (cnt < 64) return null;                              // 太碎，不值得当内容区
            if (cnt > (int)((long)w * h * 6 / 10)) return null;     // 整页红边框：放弃
            inside = cnt;
            return m;
        }

        // 两张 w*h 掩膜取并集（任一非 0 → 255）。把"印章区"与"红框内部"合成一张保护掩膜用。
        // 长度不一致时按短的那张来（防御：正常路径下两者都是 w*h）。
        public static byte[] OrMask(byte[] a, byte[] b)
        {
            if (a == null) return b;
            if (b == null) return a;
            int n = a.Length < b.Length ? a.Length : b.Length;
            byte[] o = new byte[n];
            for (int i = 0; i < n; i++) o[i] = (a[i] != 0 || b[i] != 0) ? (byte)255 : (byte)0;
            return o;
        }

        public static unsafe int ThresholdKeepRed(Bitmap bmp, int thr, int redMargin)
        {
            if (thr <= 0) thr = 128;
            if (redMargin <= 0) redMargin = 40;
            int w = bmp.Width, h = bmp.Height;
            int dark = 0;
            PixelBuffer buf = BitmapBridge.ToBuffer(bmp);
            fixed (byte* bp = buf.Data)
            {
                for (int y = 0; y < h; y++)
                {
                    byte* row = bp + y * buf.Stride;
                    for (int x = 0; x < w; x++)
                    {
                        byte* p = row + x * 3;              // BGR
                        int b = p[0], g = p[1], r = p[2];
                        int mx = g > b ? g : b;
                        if (r - mx >= redMargin) { dark++; continue; }   // 偏红：原色保留
                        int lum = (r * 77 + g * 150 + b * 29) >> 8;
                        if (lum < thr) { p[0] = 0; p[1] = 0; p[2] = 0; dark++; }
                        else { p[0] = 255; p[1] = 255; p[2] = 255; }
                    }
                }
            }
            BitmapBridge.WriteBack(bmp, buf);
            return dark;
        }

        // 统计"偏红"像素数（自检与报告用：印章有没有被保住）
        public static unsafe int RedCount(Bitmap bmp, int redMargin)
        {
            if (redMargin <= 0) redMargin = 40;
            int w = bmp.Width, h = bmp.Height;
            int n = 0;
            PixelBuffer buf = BitmapBridge.ToBuffer(bmp);
            fixed (byte* bp = buf.Data)
            {
                for (int y = 0; y < h; y++)
                {
                    byte* row = bp + y * buf.Stride;
                    for (int x = 0; x < w; x++)
                    {
                        byte* p = row + x * 3;
                        int b = p[0], g = p[1], r = p[2];
                        int mx = g > b ? g : b;
                        if (r - mx >= redMargin) n++;
                    }
                }
            }
            return n;
        }

        // 统计"非白像素"数量 —— 用来判断某一步有没有把内容弄丢。
        public static unsafe int InkCount(Bitmap bmp, int thr)
        {
            if (thr <= 0) thr = 128;
            int w = bmp.Width, h = bmp.Height;
            int n = 0;
            PixelBuffer buf = BitmapBridge.ToBuffer(bmp);
            fixed (byte* bp = buf.Data)
            {
                for (int y = 0; y < h; y++)
                {
                    byte* row = bp + y * buf.Stride;
                    for (int x = 0; x < w; x++)
                    {
                        byte* p = row + x * 3;
                        if (((p[2] * 77 + p[1] * 150 + p[0] * 29) >> 8) < thr) n++;
                    }
                }
            }
            return n;
        }

    }
}
