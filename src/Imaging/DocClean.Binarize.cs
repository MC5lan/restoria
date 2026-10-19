// DocClean.Binarize.cs -- DocClean 分文件（五）：固定阈值回二值化。
// A4 缩放后双三次插值磨出的灰过渡边，用固定阈值收干净。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;

namespace PicTool
{
    public static partial class DocClean
    {
        // ==================== 四、固定阈值回二值化 ====================
        //
        // 用途：A4 版面缩放用的是双三次插值，会把纯黑白图的边缘"磨"出一圈灰过渡。
        // 归档件要求"纯白底、纯黑字"，所以缩放之后要用一个固定阈值把它收回去。
        // （这一步不需要 Sauvola：图已经是二值的，128 足够；也快得多。）
        // 返回非白像素数（供判断"有没有把内容弄丢"）。
        public static unsafe int ThresholdInPlace(Bitmap bmp, int thr)
        {
            if (thr <= 0) thr = 128;
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
                        byte* p = row + x * 3;
                        int lum = (p[2] * 77 + p[1] * 150 + p[0] * 29) >> 8;
                        if (lum < thr) { p[0] = 0; p[1] = 0; p[2] = 0; dark++; }
                        else { p[0] = 255; p[1] = 255; p[2] = 255; }
                    }
                }
            }
            BitmapBridge.WriteBack(bmp, buf);
            return dark;
        }

        // 固定阈值收尾，但**保留"偏红"的像素原色**（印章豁免的最后一步）。
        //
        // 为什么要单独一个函数、而不是把印章掩膜也一起做几何变换：
        //   A4 版面是等比缩放，掩膜得跟着缩放 + 平移才能对上，而那个变换参数藏在 ToA4 里，
        //   复刻一份就等于埋一个"改了 ToA4 就静默错位"的雷。
        //   改用**颜色自己认自己**：偏红的像素保留，其余按阈值收成纯黑白。不用掩膜，也就不会错位。
        //
        // 偏红判据 R - max(G,B) >= redMargin。参照值：
        //   标准印章红 (200,26,32) → 168 ｜ 褪色红章 (176,92,84) → 84 ｜ 暖白纸 (252,246,230) → 6
        // 默认 40，只放"明确偏红"的过去，暖色纸与黄渍不会被误留。
        public static unsafe byte[] RedProtectMask(Bitmap bmp, int redMargin)
        {
            if (redMargin <= 0) redMargin = 40;
            int w = bmp.Width, h = bmp.Height;
            byte[] m = new byte[w * h];
            PixelBuffer buf = BitmapBridge.ToBuffer(bmp);
            fixed (byte* bp = buf.Data)
            {
                for (int y = 0; y < h; y++)
                {
                    byte* row = bp + y * buf.Stride;
                    for (int x = 0; x < w; x++)
                    {
                        byte* p = row + x * 3;                  // BGR
                        int b = p[0], g = p[1], r = p[2];
                        int mx = g > b ? g : b;
                        if (r - mx >= redMargin) m[y * w + x] = 255;
                    }
                }
            }
            return m;
        }

        // 掩膜里"非 0"的像素个数（诊断/报告用）
        public static int MaskCount(byte[] m)
        {
            if (m == null) return 0;
            int n = 0;
            for (int i = 0; i < m.Length; i++) if (m[i] != 0) n++;
            return n;
        }

        // 把 backup 里"偏红"的像素按 mask 的位置写回 dst（两张图必须同尺寸）。
        // 用途（实战）：脏块重建 PaperFill 的接缝平差会改**掩膜外一圈**，
        // 于是挨着污渍的红框/红字被顺手啃掉 —— 实测一张红色登记框页：重建前红像素
        // 7135，重建后只剩 1267（先剔除掩膜里的红像素也救不了，因为它动的是掩膜外面）。
        // 红色是内容，这里按位还原：宁留污渍，不丢内容。
        // 通用版：按 mask 把 backup 的像素写回 dst（"红色内容还原"与"红框内部还原"共用同一套实现）。
        // 名字里的 "Red" 是历史遗留 —— 它只认掩膜，不认颜色。
        public static int RestoreRedPixels(Bitmap dst, Bitmap backup, byte[] mask)
        {
            return RestorePixelsMasked(dst, backup, mask);
        }

        public static unsafe int RestorePixelsMasked(Bitmap dst, Bitmap backup, byte[] mask)
        {
            if (dst == null || backup == null || mask == null) return 0;
            int w = dst.Width, h = dst.Height;
            if (backup.Width != w || backup.Height != h) return 0;
            int n = 0;
            PixelBuffer d = BitmapBridge.ToBuffer(dst);
            PixelBuffer s = BitmapBridge.ToBuffer(backup);
            fixed (byte* dp = d.Data)
            fixed (byte* sp = s.Data)
            {
                for (int y = 0; y < h; y++)
                {
                    byte* dr = dp + y * d.Stride;
                    byte* sr = sp + y * s.Stride;
                    int mo = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        if (mask[mo + x] == 0) continue;
                        dr[x * 3] = sr[x * 3];
                        dr[x * 3 + 1] = sr[x * 3 + 1];
                        dr[x * 3 + 2] = sr[x * 3 + 2];
                        n++;
                    }
                }
            }
            BitmapBridge.WriteBack(dst, d);
            return n;
        }

    }
}
