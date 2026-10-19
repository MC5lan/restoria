// DocClean.Stain.cs -- DocClean 分文件（七）：脏块掩膜。
// 只负责找出"哪里是脏块"，生成 PaperFill 要的洞掩膜；不生成内容。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;

namespace PicTool
{
    public static partial class DocClean
    {
        public static unsafe Bitmap DetectStainMask(Bitmap src, int minStain, int inkLow, int stainDepth, out int found)
        {
            found = 0;
            int w = src.Width, h = src.Height;
            if (w < 64 || h < 64) return null;
            if (inkLow <= 0) inkLow = 90;
            if (stainDepth <= 0) stainDepth = 30;
            if (minStain <= 0)
            {
                minStain = w * h / 1000;              // 200dpi A4 ≈ 3.9k px，直径约 70px 的斑
                if (minStain < 400) minStain = 400;   // 小图用绝对下限，别让比例阈值失效
            }

            // ---------- 大核纸面场 + 灰度/强墨图 ----------
            // 为什么不用归一化那个小核场（SmallPaperField）：闭运算"填平比核小的暗细节、
            // 保住比核大的暗结构" —— 大污渍恰恰比小核（≈19px）大，会被场**原样保留**
            // （实测：灰斑中心暗差 = 0，全图候选只剩 303 像素，什么都检不出）。
            // 这里改用**大核**（半径 = 缩略图宽的 1/6 ≈ 原图 130px）：
            //   · 污渍（几十~两百 px）比核小 → 被填平 → 场≈纸面 → 暗差显现 ✓
            //   · 书脊阴影（半页宽的渐变）比核大 → 仍保留在场里 → 暗差≈0 不误检 ✓
            // 尺度分离本身就是"污渍 vs 光照渐变"的判据。
            int sw, sh;
            byte[] smallG = ThumbGray(src, w, h, out sw, out sh);
            int br = sw / 6; if (br < 16) br = 16;
            byte[] field = MorphClose(smallG, sw, sh, br);
            byte[] gray = new byte[w * h];
            byte[] ink = new byte[w * h];
            PixelBuffer gbuf = BitmapBridge.ToBuffer(src);
            fixed (byte* bp = gbuf.Data)
            {
                for (int y = 0; y < h; y++)
                {
                    byte* row = bp + y * gbuf.Stride;
                    int o = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        byte* p = row + x * 3;
                        int L = (p[2] * 77 + p[1] * 150 + p[0] * 29) >> 8;
                        gray[o + x] = (byte)L;
                        ink[o + x] = (byte)(L < inkLow ? 1 : 0);
                    }
                }
            }

            // 候选：比局部纸面暗够多、又不是强墨的像素（最近邻取场值，粒度 ~3px 足够）
            int inkTotal = 0;
            byte[] cand = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                int sy = (int)((long)y * sh / h);
                if (sy > sh - 1) sy = sh - 1;
                int fo = sy * sw;
                int o = y * w;
                for (int x = 0; x < w; x++)
                {
                    int sx = (int)((long)x * sw / w);
                    if (sx > sw - 1) sx = sw - 1;
                    if (ink[o + x] != 0) { inkTotal++; continue; }
                    if (field[fo + sx] - gray[o + x] >= stainDepth) cand[o + x] = 1;
                }
            }
            // 整页大半是深色（照片页/严重曝光不足）：不属于"纸上有斑"，不处理
            if (inkTotal > w * h / 2) return null;

            byte[] st = new byte[w * h];               // 0=未访问 1=已访问(候选) 2=已写入掩膜
            int[] stack = new int[w * h];              // 每像素至多入栈一次
            byte[] mdata = new byte[w * h];            // 紧凑 0/255，最后经 BytesToMask 转 8bpp 位图
            bool any = false;
            for (int start = 0; start < w * h; start++)
            {
                if (cand[start] == 0 || st[start] != 0) continue;

                // ---- 第一遍 flood：只量形状（面积/外接框），先判定再动笔 ----
                int top = 0;
                stack[top++] = start;
                st[start] = 1;
                int size = 0, minX = w, maxX = -1, minY = h, maxY = -1;
                while (top > 0)
                {
                    int idx = stack[--top];
                    size++;
                    int y = idx / w, x = idx - y * w;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                    if (x > 0 && cand[idx - 1] != 0 && st[idx - 1] == 0) { st[idx - 1] = 1; stack[top++] = idx - 1; }
                    if (x < w - 1 && cand[idx + 1] != 0 && st[idx + 1] == 0) { st[idx + 1] = 1; stack[top++] = idx + 1; }
                    if (y > 0 && cand[idx - w] != 0 && st[idx - w] == 0) { st[idx - w] = 1; stack[top++] = idx - w; }
                    if (y < h - 1 && cand[idx + w] != 0 && st[idx + w] == 0) { st[idx + w] = 1; stack[top++] = idx + w; }
                }
                if (size < minStain) continue;

                // 🔴 面积上限：超过页面积 10% 的"污渍"不是霉斑水渍，
                //    是卷边阴影/页边压痕/半页投影这类**结构性暗区**。它们送进 PaperFill
                //    会出大事 —— 掩膜太大，背景估计与接缝平差的边界条件被掩膜附近的
                //    深色内容（墨迹/装订孔/黑条）污染，填出来是大片黑垃圾
                //    （实测样本 0017：一条淡阴影被重建成 1/3 页的黑印子）。
                //    这类暗区光照归一化自己会处理，不需要重建。
                //    10% ≈ 直径 1/3 页宽的斑，真实的咖啡渍/水渍远小于此。
                if (size > w * h / 10) continue;

                // 判据③：外接框内强墨占比 —— "灰边围着字"的结构在这里现形
                int boxInk = 0;
                for (int yy = minY; yy <= maxY; yy++)
                {
                    int o = yy * w;
                    for (int xx = minX; xx <= maxX; xx++)
                        if (ink[o + xx] != 0) boxInk++;
                }
                int boxArea = (maxX - minX + 1) * (maxY - minY + 1);
                if (boxInk * 100 > boxArea * 8) continue;

                // ---- 判定通过：第二遍 flood 把这块写进掩膜 ----
                top = 0;
                stack[top++] = start;
                st[start] = 2;
                mdata[start] = 255;
                while (top > 0)
                {
                    int idx = stack[--top];
                    mdata[idx] = 255;
                    int y = idx / w, x = idx - y * w;
                    if (x > 0 && cand[idx - 1] != 0 && st[idx - 1] == 1) { st[idx - 1] = 2; stack[top++] = idx - 1; }
                    if (x < w - 1 && cand[idx + 1] != 0 && st[idx + 1] == 1) { st[idx + 1] = 2; stack[top++] = idx + 1; }
                    if (y > 0 && cand[idx - w] != 0 && st[idx - w] == 1) { st[idx - w] = 2; stack[top++] = idx - w; }
                    if (y < h - 1 && cand[idx + w] != 0 && st[idx + w] == 1) { st[idx + w] = 2; stack[top++] = idx + w; }
                }
                found++;
                any = true;
            }

            if (!any) return null;
            return BytesToMask(mdata, w, h);
        }

    }
}
