// DocClean.Light.cs -- DocClean 分文件（二）：光照 / 背景归一化。
// 去灰底、去阴影、去光照不均；含缩略纸面场与形态学闭运算是本区的私有基建。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;

namespace PicTool
{
    public static partial class DocClean
    {
        // ==================== 一、光照 / 背景归一化 ====================
        //
        // 解决什么：
        //   老扫描件常见"同一张纸左边灰、右边白"、书脊阴影、灯管照度不均。
        //   全局色阶（AutoEnhance 的 0.4%/99.6% 百分位拉伸）对这类**空间上不均**的
        //   退化无能为力——它只能整体拉伸：拉白了右边就把左边拉爆，反之亦然。
        //   要"白底"，必须先消掉这个低频亮度场。
        //
        // 怎么做（业界标准的 shading correction）：
        //   ① 求出画面的"纸面亮度场" bg —— 把暗的墨迹抹掉，只留亮度的缓慢变化。
        //      用形态学闭运算（先膨胀取亮、再腐蚀）即可：核半径大于笔画宽度，
        //      笔画就被"抹平"成周围的纸面亮度，而纸面本身的起伏被保留。
        //   ② 逐像素归一化 out = in * 255 / bg —— 纸面(bg)被拉到 255，墨迹按比例保留。
        //
        // 三个工程要点（改之前先读）：
        //   · 背景场在**缩略图上**估计，再双线性放大。全分辨率做大核形态学太慢，
        //     而背景场本身就是低频的，256px 宽估得足够准，也快得多。
        //   · **三个通道用同一个增益**（增益由亮度场推导），所以颜色关系不变——
        //     红章归一化后还是红的。这一条是"印章保留红色"的前提，别改成逐通道。
        //   · 增益上限 maxGain 默认 2.5：防止大片黑边/阴影处被放大成噪声。

        public static Bitmap NormalizeIllumination(Bitmap src, double strength, double maxGain)
        {
            Bitmap dst = Imaging.Ensure24bpp(new Bitmap(src));
            NormalizeIlluminationInPlace(dst, strength, maxGain);
            return dst;
        }

        public static unsafe void NormalizeIlluminationInPlace(Bitmap bmp, double strength, double maxGain)
        {
            if (strength <= 0) return;
            if (strength > 1) strength = 1;
            if (maxGain < 1.05) maxGain = 2.5;

            int w = bmp.Width, h = bmp.Height;
            if (w < 16 || h < 16) return;

            // ---------- 纸面场：缩略图均值采样 → 形态学闭运算（提取为共用函数） ----------
            // 为什么共用：光照不均与大面积污渍在频域上是**同一类东西**（都是低频背景），
            // 差别只在幅度 —— 前者是渐变的小偏差，后者是"比局部纸面暗一截"的斑块。
            // 用同一个场，两者才分得开；各算各的则必然互相吃掉（详见 DetectStainMask）。
            int sw, sh;
            byte[] bg = SmallPaperField(bmp, w, h, out sw, out sh);

            // ---------- ③ 双线性放大背景场 + 逐像素按同一增益归一化 ----------
            // 半径 3 在缩略图尺度上 ≈ 原始像素的 3*(w/256) 倍——200dpi A4 约 19px，
            // 比任何笔画都宽，所以墨迹会被抹平成纸面亮度。
            // （P1 迁移：像素操作改为走 BitmapBridge 的缓冲，GDI 锁只存在于桥内。
            //   BGR 布局与行对齐都和 GDI+ 一致，逐字节逻辑不变。）
            PixelBuffer buf = BitmapBridge.ToBuffer(bmp);
            fixed (byte* bp = buf.Data)
            {
                double kx = w > 1 ? (double)(sw - 1) / (w - 1) : 0;
                double ky = h > 1 ? (double)(sh - 1) / (h - 1) : 0;
                for (int y = 0; y < h; y++)
                {
                    double fy = y * ky;
                    int y0 = (int)fy;
                    if (y0 > sh - 2) y0 = sh - 2;
                    if (y0 < 0) y0 = 0;
                    double wy = fy - y0;
                    int y1 = y0 + 1 < sh ? y0 + 1 : y0;
                    byte* row = bp + y * buf.Stride;
                    for (int x = 0; x < w; x++)
                    {
                        double fx = x * kx;
                        int x0 = (int)fx;
                        if (x0 > sw - 2) x0 = sw - 2;
                        if (x0 < 0) x0 = 0;
                        double wx = fx - x0;
                        int x1 = x0 + 1 < sw ? x0 + 1 : x0;

                        double bgv =
                            (bg[y0 * sw + x0] * (1 - wx) + bg[y0 * sw + x1] * wx) * (1 - wy) +
                            (bg[y1 * sw + x0] * (1 - wx) + bg[y1 * sw + x1] * wx) * wy;
                        if (bgv < 1) bgv = 1;

                        double gain = 255.0 / bgv;
                        if (gain > maxGain) gain = maxGain;
                        gain = 1.0 + (gain - 1.0) * strength;    // strength 让步进可调
                        if (gain < 1.0) gain = 1.0;

                        byte* p = row + x * 3;
                        int bb = (int)(p[0] * gain + 0.5); if (bb > 255) bb = 255;
                        int gg = (int)(p[1] * gain + 0.5); if (gg > 255) gg = 255;
                        int rr = (int)(p[2] * gain + 0.5); if (rr > 255) rr = 255;
                        p[0] = (byte)bb; p[1] = (byte)gg; p[2] = (byte)rr;
                    }
                }
            }
            BitmapBridge.WriteBack(bmp, buf);
        }

        // 缩略图灰度：按格子取均值（避免点采样漏掉笔画使背景场抖动）。
        // 返回 g[sy*sw+sx]，sw/sh 为缩略图尺寸。
        static unsafe byte[] ThumbGray(Bitmap bmp, int w, int h, out int sw, out int sh)
        {
            sw = w < 256 ? w : 256;
            sh = (int)Math.Round((double)h * sw / w);
            if (sh < 16) sh = 16;

            byte[] small = new byte[sw * sh];
            PixelBuffer buf = BitmapBridge.ToBuffer(bmp);
            fixed (byte* bp = buf.Data)
            {
                for (int sy = 0; sy < sh; sy++)
                {
                    int y0 = (int)((long)sy * h / sh), y1 = (int)((long)(sy + 1) * h / sh);
                    if (y1 <= y0) y1 = y0 + 1;
                    if (y1 > h) y1 = h;
                    for (int sx = 0; sx < sw; sx++)
                    {
                        int x0 = (int)((long)sx * w / sw), x1 = (int)((long)(sx + 1) * w / sw);
                        if (x1 <= x0) x1 = x0 + 1;
                        if (x1 > w) x1 = w;
                        long sum = 0; int n = 0;
                        for (int y = y0; y < y1; y++)
                        {
                            byte* row = bp + y * buf.Stride;
                            for (int x = x0; x < x1; x++)
                            {
                                byte* p = row + x * 3;
                                sum += (p[2] * 77 + p[1] * 150 + p[0] * 29) >> 8;
                                n++;
                            }
                        }
                        small[sy * sw + sx] = (byte)(n > 0 ? sum / n : 255);
                    }
                }
            }
            return small;
        }

        // 小核纸面场（归一化用）：核半径 3 ≈ 原图 19px，比任何笔画都宽，
        // 所以墨迹被填平成纸面亮度；**比核大的暗斑（大污渍、书脊阴影）原样保留**。
        static unsafe byte[] SmallPaperField(Bitmap bmp, int w, int h, out int sw, out int sh)
        {
            byte[] small = ThumbGray(bmp, w, h, out sw, out sh);
            int r = sw < 64 ? 2 : 3;
            return MorphClose(small, sw, sh, r);
        }

        // 形态学闭运算（膨胀→腐蚀），行列分离。缩略图规模下朴素窗口足够快。
        static byte[] MorphClose(byte[] src, int w, int h, int r)
        {
            byte[] dil = Morph(src, w, h, r, true);
            byte[] ero = Morph(dil, w, h, r, false);
            return ero;
        }

        static byte[] Morph(byte[] src, int w, int h, int r, bool takeMax)
        {
            byte[] tmp = new byte[w * h];
            byte[] dst = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                int o = y * w;
                for (int x = 0; x < w; x++)
                {
                    int a = x - r < 0 ? 0 : x - r;
                    int b = x + r >= w ? w - 1 : x + r;
                    byte v = src[o + a];
                    for (int xx = a + 1; xx <= b; xx++)
                    {
                        byte t = src[o + xx];
                        if (takeMax ? t > v : t < v) v = t;
                    }
                    tmp[o + x] = v;
                }
            }
            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++)
                {
                    int a = y - r < 0 ? 0 : y - r;
                    int b = y + r >= h ? h - 1 : y + r;
                    byte v = tmp[a * w + x];
                    for (int yy = a + 1; yy <= b; yy++)
                    {
                        byte t = tmp[yy * w + x];
                        if (takeMax ? t > v : t < v) v = t;
                    }
                    dst[y * w + x] = v;
                }
            }
            return dst;
        }

    }
}
