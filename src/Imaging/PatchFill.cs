// PatchFill.cs — 基于 PatchMatch 的内容感知填充（纯 C#，无任何外部依赖）
//
// 与扩散类算法（Telea）的区别：扩散只会把周边颜色平均过去，大面积必然发糊、跨结构边缘必断；
// 这里用的是“样本块合成”：在周边寻找与待填充块最相似的纹理块，搬过来拼合，再迭代收敛。
// 这是深度学习之前业界（含 Photoshop 内容识别填充）的主流做法，能保住纹理和大致结构。
//
// 工程约束：多尺度金字塔（粗层定结构、细层补纹理）+ 只处理洞的包围盒区域，保证老机器上可用。
using System;
using System.Drawing;
using System.Drawing.Imaging;

namespace PicTool
{
    public static class PatchFill
    {
        // ---------------- 层级数据 ----------------
        class Lv
        {
            public int w, h;
            public byte[] rgb;        // BGR
            public byte[] hole;       // 1 = 待填充
            public int[] nnx, nny;    // 每个像素的最近邻源坐标
            public int[] nnd;         // 最近邻距离
            public byte[] ok;         // 最近邻是否已找到有效匹配
            public int[] integ;       // 洞的积分图，用于 O(1) 判断“源块是否完全可用”
            // 投票累加缓冲：按层复用，避免每次迭代分配大数组（12MP 时曾达数百 MB）
            public float[] vAcc, vWgt, vWgt2;
        }

        // ---------------- 对外入口 ----------------
        public static int MaskDilate = 1;      // 遮罩膨胀半径（吃掉水印边缘的抗锯齿/光晕）

        // quality: true = 精细（迭代更多、块更小），false = 快速
        public static Bitmap Inpaint(Bitmap src, Bitmap mask, bool quality, ProgressHandler progress)
        {
            return Inpaint(src, mask, quality, progress, null);
        }

        public static Bitmap Inpaint(Bitmap src, Bitmap mask, bool quality, ProgressHandler progress, CancelCheck cancelled)
        {
            byte[] conf;
            return InpaintEx(src, mask, quality, progress, out conf, cancelled);
        }

        // conf：每个像素的“匹配置信度”（0=没找到合适样本块，255=找到了很相似的块）
        public static Bitmap InpaintEx(Bitmap src, Bitmap mask, bool quality, ProgressHandler progress, out byte[] conf)
        {
            return InpaintEx(src, mask, quality, progress, out conf, null);
        }

        public static Bitmap InpaintEx(Bitmap src, Bitmap mask, bool quality, ProgressHandler progress, out byte[] conf, CancelCheck cancelled)
        {
            if (src.Width != mask.Width || src.Height != mask.Height)
                throw new Exception("填充遮罩尺寸与图片不一致。");

            conf = new byte[src.Width * src.Height];
            int minX, minY, maxX, maxY;
            byte[] holeFull = ReadMaskDilated(mask, MaskDilate, out minX, out minY, out maxX, out maxY);
            if (maxX < 0) return Imaging.Ensure24bpp(new Bitmap(src));
            Rectangle roi = new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);

            // 安全阀：ROI 过大时直接拒绝（内容感知填充按像素占内存，老机器 2GB 会撑不住）
            long roiPixels = (long)(roi.Width + 320) * (roi.Height + 320);
            if (roiPixels > SysInfo.MaxRoiPixels)
                throw new Exception(string.Concat("圈出的区域太大了（约 ", (roiPixels / 10000).ToString(), " 万像素）。请分成几次、每次圈更小的区域处理。"));
            int margin = Math.Max(32, Math.Min(160, Math.Max(roi.Width, roi.Height) / 2));
            int x0 = Math.Max(0, roi.X - margin), y0 = Math.Max(0, roi.Y - margin);
            int x1 = Math.Min(src.Width, roi.X + roi.Width + margin), y1 = Math.Min(src.Height, roi.Y + roi.Height + margin);
            Rectangle rc = new Rectangle(x0, y0, x1 - x0, y1 - y0);

            Lv baseLv = BuildBase(src, holeFull, rc);
            int r = quality ? 3 : 4;               // 块半径：精细用 7x7，快速用 9x9

            // 金字塔（粗层定结构，细层补纹理）
            System.Collections.Generic.List<Lv> pyr = new System.Collections.Generic.List<Lv>();
            pyr.Add(baseLv);
            while (pyr[pyr.Count - 1].w > 48 && pyr[pyr.Count - 1].h > 48 && pyr.Count < 5)
                pyr.Add(Downsample(pyr[pyr.Count - 1]));

            // 从最粗层开始
            int total = pyr.Count;
            int totalSteps = 0;
            for (int k = 0; k < total; k++) totalSteps += quality ? Math.Min(3 + k * 2, 9) : Math.Min(2 + k, 4);
            int doneSteps = 0;
            for (int i = total - 1; i >= 0; i--)
            {
                Lv L = pyr[i];
                int levelIdx = total - 1 - i;          // 0 = 最粗
                int iters = quality ? Math.Min(3 + levelIdx * 2, 9) : Math.Min(2 + levelIdx, 4);
                // 并行时块间传播读不到本轮更新（Jacobi 式），收敛略慢，补两轮迭代换回画质
                if (HoleCount(L) >= 8000) iters += 2;
                EnsureNNF(L);
                if (i == total - 1)
                {
                    DiffuseInit(L);                // 最粗层：先扩散出一个初始猜测
                }
                else
                {
                    UpsampleHole(pyr[i + 1], L);   // 其余层：用粗层结果作为初始猜测
                    SeedNNF(pyr[i + 1], L);
                    pyr[i + 1].rgb = null; pyr[i + 1].hole = null; pyr[i + 1].integ = null;
                    FreeNNF(pyr[i + 1]);           // 粗层数据用完即释放（老机器内存紧张）
                }
                for (int it = 0; it < iters; it++)
                {
                    PatchMatchStep(L, r, it % 2 == 0);
                    Vote(L, r);
                    doneSteps++;
                    // 细化进度：大面积时要能看出程序在动（每层只报一次会显得卡死）
                    if (cancelled != null && cancelled()) throw new OpCancelledException();
                    if (progress != null) progress(doneSteps * 100 / Math.Max(1, totalSteps));
                }
                SmoothSeam(L);
                // 注意：本层的最近邻要留给下一（更细）层的 SeedNNF 用，
                // 由更细层在播种完成后调用 FreeNNF(pyr[i+1]) 释放，这里不能提前释放
            }

            // 写回
            Bitmap result = Imaging.Ensure24bpp(new Bitmap(src));
            WriteBack(baseLv, result, rc);

            // 置信度图：由最细层的匹配距离换算（供混合模式决定哪里信任内容感知结果）
            // 阈值自适应：以洞内匹配误差的实际分布为准——中位数水平完全信任，
            // 最差的 10% 才退回扩散填充保底（不同照片噪声水平差异很大，用固定阈值不可靠）
            double norm = (double)((2 * r + 1) * (2 * r + 1) * 3);
            System.Collections.Generic.List<double> errs = new System.Collections.Generic.List<double>();
            for (int y = 0; y < baseLv.h; y++)
                for (int x = 0; x < baseLv.w; x++)
                {
                    int i = y * baseLv.w + x;
                    if (baseLv.hole[i] == 0 || baseLv.ok[i] == 0) continue;
                    errs.Add(baseLv.nnd[i] / norm);
                }
            double med = 0, p90 = 0;
            if (errs.Count > 0)
            {
                errs.Sort();
                med = errs[errs.Count / 2];
                p90 = errs[Math.Min(errs.Count - 1, errs.Count * 9 / 10)];
            }
            if (p90 <= med + 1.0) p90 = med + 1.0;
            for (int y = 0; y < baseLv.h; y++)
                for (int x = 0; x < baseLv.w; x++)
                {
                    int i = y * baseLv.w + x;
                    if (baseLv.hole[i] == 0) continue;
                    int c;
                    if (baseLv.ok[i] == 0) c = 0;
                    else
                    {
                        double mse = baseLv.nnd[i] / norm;
                        double v = 255.0 * (p90 - mse) / (p90 - med);
                        c = v < 0 ? 0 : (v > 255 ? 255 : (int)v);
                    }
                    conf[(rc.Y + y) * src.Width + (rc.X + x)] = (byte)c;
                }

            if (progress != null) progress(100);
            return result;
        }

        // ---------------- 混合模式 ----------------
        // 内容感知填充能延续边线与纹理，但它失败的地方（周围找不到相似样本块）会留下补丁痕迹；
        // 扩散填充没有这个风险，代价是抹断边线。
        // 混合方案：用内容感知填充自己的“匹配置信度”逐像素取舍——有把握的地方用它，
        // 没把握的地方退回升散填充保底。目标是“既续上结构，又不出现怪东西”。
        public static Bitmap InpaintHybrid(Bitmap src, Bitmap mask, bool quality, ProgressHandler progress)
        {
            return InpaintHybrid(src, mask, quality, progress, null);
        }

        public static Bitmap InpaintHybrid(Bitmap src, Bitmap mask, bool quality, ProgressHandler progress, CancelCheck cancelled)
        {
            byte[] conf;
            Bitmap patch = InpaintEx(src, mask, quality, null, out conf, cancelled);
            Bitmap telea = Imaging.Inpaint(src, mask, 5, progress, cancelled);
            try
            {
                Bitmap result = Imaging.Ensure24bpp(new Bitmap(src));
                BlendHole(result, telea, patch, mask, conf);
                return result;
            }
            finally
            {
                telea.Dispose();
                patch.Dispose();
            }
        }

        static void BlendHole(Bitmap dst, Bitmap a, Bitmap b, Bitmap mask, byte[] weight)
        {
            int w = dst.Width, h = dst.Height;
            PixelBuffer bd = BitmapBridge.ToBuffer(dst);
            PixelBuffer ba = BitmapBridge.ToBuffer(a);
            PixelBuffer bbv = BitmapBridge.ToBuffer(b);
            PixelBuffer bm = BitmapBridge.MaskToBuffer(mask);
            unsafe
            {
                fixed (byte* pd = bd.Data)
                fixed (byte* pa = ba.Data)
                fixed (byte* pb = bbv.Data)
                fixed (byte* pm = bm.Data)
                {
                    for (int y = 0; y < h; y++)
                    {
                        byte* rd = pd + y * bd.Stride;
                        byte* ra = pa + y * ba.Stride;
                        byte* rb = pb + y * bbv.Stride;
                        byte* rm = pm + y * bm.Stride;
                        for (int x = 0; x < w; x++)
                        {
                            if (rm[x] < 128) continue;              // 只改洞内像素
                            int k = weight[y * w + x];
                            if (k == 0)
                            {
                                rd[x * 3] = ra[x * 3]; rd[x * 3 + 1] = ra[x * 3 + 1]; rd[x * 3 + 2] = ra[x * 3 + 2];
                            }
                            else if (k == 255)
                            {
                                rd[x * 3] = rb[x * 3]; rd[x * 3 + 1] = rb[x * 3 + 1]; rd[x * 3 + 2] = rb[x * 3 + 2];
                            }
                            else
                            {
                                for (int c = 0; c < 3; c++)
                                    rd[x * 3 + c] = (byte)((ra[x * 3 + c] * (255 - k) + rb[x * 3 + c] * k) / 255);
                            }
                        }
                    }
                }
            }
            BitmapBridge.WriteBack(dst, bd);
        }

        // ---------------- 基础操作 ----------------

        // 读掩码并膨胀（膨胀能吃掉水印边缘的抗锯齿/光晕残留），返回全图大小的洞标记与外接矩形
        static byte[] ReadMaskDilated(Bitmap mask, int iterations, out int minX, out int minY, out int maxX, out int maxY)
        {
            int w = mask.Width, h = mask.Height;
            byte[] hole = new byte[w * h];
            PixelBuffer bm = BitmapBridge.MaskToBuffer(mask);
            for (int y = 0; y < h; y++)
            {
                int ro = y * bm.Stride;
                for (int x = 0; x < w; x++)
                    if (bm.Data[ro + x] >= 128) hole[y * w + x] = 1;
            }

            byte[] tmp = new byte[hole.Length];
            for (int it = 0; it < iterations; it++)
            {
                for (int y = 0; y < h; y++)
                {
                    int ym = y > 0 ? y - 1 : 0, yp = y < h - 1 ? y + 1 : h - 1;
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        if (hole[i] != 0) { tmp[i] = 1; continue; }
                        int xm = x > 0 ? x - 1 : 0, xp = x < w - 1 ? x + 1 : w - 1;
                        byte v = hole[y * w + xm];
                        if (v == 0) v = hole[y * w + xp];
                        if (v == 0) v = hole[ym * w + x];
                        if (v == 0) v = hole[yp * w + x];
                        if (v == 0) v = hole[ym * w + xm];
                        if (v == 0) v = hole[ym * w + xp];
                        if (v == 0) v = hole[yp * w + xm];
                        if (v == 0) v = hole[yp * w + xp];
                        tmp[i] = v;
                    }
                }
                Array.Copy(tmp, hole, hole.Length);
            }

            minX = int.MaxValue; minY = int.MaxValue; maxX = -1; maxY = -1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (hole[y * w + x] != 0)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
            return hole;
        }

        static Lv BuildBase(Bitmap src, byte[] holeFull, Rectangle rc)
        {
            Lv L = new Lv();
            L.w = rc.Width; L.h = rc.Height;
            L.rgb = new byte[L.w * L.h * 3];
            L.hole = new byte[L.w * L.h];
            L.nnx = new int[L.w * L.h];
            L.nny = new int[L.w * L.h];
            L.nnd = new int[L.w * L.h];
            L.ok = new byte[L.w * L.h];

            // ROI 只是全图的一个子块：桥按全图取缓冲，再从缓冲里取 ROI 行
            PixelBuffer sbuf = BitmapBridge.ToBuffer(src);
            for (int y = 0; y < L.h; y++)
            {
                int srow = (rc.Y + y) * sbuf.Stride + rc.X * 3;
                for (int x = 0; x < L.w; x++)
                {
                    int q = srow + x * 3;
                    int o = (y * L.w + x) * 3;
                    L.rgb[o] = sbuf.Data[q]; L.rgb[o + 1] = sbuf.Data[q + 1]; L.rgb[o + 2] = sbuf.Data[q + 2];
                }
            }

            for (int y = 0; y < L.h; y++)
                for (int x = 0; x < L.w; x++)
                    L.hole[y * L.w + x] = holeFull[(rc.Y + y) * src.Width + (rc.X + x)];
            BuildIntegral(L);
            return L;
        }

        static void BuildIntegral(Lv L)
        {
            int w = L.w, h = L.h;
            L.integ = new int[(w + 1) * (h + 1)];
            for (int y = 0; y < h; y++)
            {
                int rowSum = 0;
                for (int x = 0; x < w; x++)
                {
                    rowSum += L.hole[y * w + x];
                    L.integ[(y + 1) * (w + 1) + (x + 1)] = L.integ[y * (w + 1) + (x + 1)] + rowSum;
                }
            }
        }

        // 源块是否完全落在可用区域（不含洞）
        static bool PatchValid(Lv L, int cx, int cy, int r)
        {
            int x0 = cx - r, y0 = cy - r, x1 = cx + r + 1, y1 = cy + r + 1;
            if (x0 < 0 || y0 < 0 || x1 > L.w || y1 > L.h) return false;
            int w1 = L.w + 1;
            int s = L.integ[y1 * w1 + x1] - L.integ[y0 * w1 + x1] - L.integ[y1 * w1 + x0] + L.integ[y0 * w1 + x0];
            return s == 0;
        }

        // 最近邻数组按需分配：金字塔各层不必一开局就各占 13 字节/像素
        static void EnsureNNF(Lv L)
        {
            if (L.nnx != null) return;
            int n = L.w * L.h;
            L.nnx = new int[n]; L.nny = new int[n]; L.nnd = new int[n]; L.ok = new byte[n];
        }

        static void FreeNNF(Lv L)
        {
            L.nnx = null; L.nny = null; L.nnd = null; L.ok = null;
            L.vAcc = null; L.vWgt = null; L.vWgt2 = null;
        }

        static Lv Downsample(Lv f)
        {
            Lv c = new Lv();
            c.w = Math.Max(1, f.w / 2);
            c.h = Math.Max(1, f.h / 2);
            c.rgb = new byte[c.w * c.h * 3];
            c.hole = new byte[c.w * c.h];
            c.nnx = new int[c.w * c.h];
            c.nny = new int[c.w * c.h];
            c.nnd = new int[c.w * c.h];
            c.ok = new byte[c.w * c.h];

            for (int y = 0; y < c.h; y++)
                for (int x = 0; x < c.w; x++)
                {
                    int sx = x * 2, sy = y * 2;
                    int cntB = 0, cntG = 0, cntR = 0, n = 0, hn = 0;
                    for (int dy = 0; dy < 2; dy++)
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int px = sx + dx, py = sy + dy;
                            if (px >= f.w || py >= f.h) continue;
                            if (f.hole[py * f.w + px] != 0) { hn++; continue; }
                            int o = (py * f.w + px) * 3;
                            cntB += f.rgb[o]; cntG += f.rgb[o + 1]; cntR += f.rgb[o + 2];
                            n++;
                        }
                    int ci = y * c.w + x;
                    if (n > 0)
                    {
                        c.rgb[ci * 3] = (byte)(cntB / n);
                        c.rgb[ci * 3 + 1] = (byte)(cntG / n);
                        c.rgb[ci * 3 + 2] = (byte)(cntR / n);
                    }
                    // 四个子像素全是洞才算洞（实测比“任一子像素是洞”更好：粗层洞越小，引导越可靠）
                    c.hole[ci] = (byte)((hn == 4 || n == 0) ? 1 : 0);
                }
            BuildIntegral(c);
            return c;
        }

        // 粗层 -> 细层：洞内像素取粗层值
        static void UpsampleHole(Lv c, Lv f)
        {
            for (int y = 0; y < f.h; y++)
                for (int x = 0; x < f.w; x++)
                {
                    int fi = y * f.w + x;
                    if (f.hole[fi] == 0) continue;
                    int cx = Math.Min(c.w - 1, x / 2), cy = Math.Min(c.h - 1, y / 2);
                    int co = (cy * c.w + cx) * 3;
                    f.rgb[fi * 3] = c.rgb[co];
                    f.rgb[fi * 3 + 1] = c.rgb[co + 1];
                    f.rgb[fi * 3 + 2] = c.rgb[co + 2];
                }
        }

        // 用粗层的最近邻初始化细层的最近邻（×2 并加抖动），这是质量和收敛速度的关键
        static void SeedNNF(Lv c, Lv f)
        {
            Random rnd = new Random(7);
            for (int y = 0; y < f.h; y++)
                for (int x = 0; x < f.w; x++)
                {
                    int fi = y * f.w + x;
                    if (f.hole[fi] == 0) continue;
                    int cx = Math.Min(c.w - 1, x / 2), cy = Math.Min(c.h - 1, y / 2);
                    int ci = cy * c.w + cx;
                    int nx = c.nnx[ci] >= 0 ? c.nnx[ci] * 2 + rnd.Next(-2, 3) : x;
                    int ny = c.nny[ci] >= 0 ? c.nny[ci] * 2 + rnd.Next(-2, 3) : y;
                    if (nx < 0) nx = 0; if (ny < 0) ny = 0;
                    if (nx >= f.w) nx = f.w - 1; if (ny >= f.h) ny = f.h - 1;
                    f.nnx[fi] = nx; f.nny[fi] = ny;
                    f.nnd[fi] = int.MaxValue;
                }
        }

        // 最粗层的初始猜测：从边界向内逐圈填充（每圈填完即视为“已知”，下一圈才能继续向内），
        // 再做几轮平滑。只改 rgb、不动 hole 标记（hole 后面还要用于投票与写回）。
        static void DiffuseInit(Lv L)
        {
            int w = L.w, h = L.h;
            byte[] known = new byte[L.hole.Length];
            for (int i = 0; i < known.Length; i++) known[i] = (byte)(L.hole[i] == 0 ? 1 : 0);

            // 逐圈向内填充
            for (int pass = 0; pass < w + h; pass++)
            {
                bool filled = false;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        if (known[i] != 0) continue;
                        int sB = 0, sG = 0, sR = 0, n = 0;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dy == 0) continue;
                                int px = x + dx, py = y + dy;
                                if (px < 0 || py < 0 || px >= w || py >= h) continue;
                                int pi = py * w + px;
                                if (known[pi] == 0) continue;
                                sB += L.rgb[pi * 3]; sG += L.rgb[pi * 3 + 1]; sR += L.rgb[pi * 3 + 2]; n++;
                            }
                        if (n > 0)
                        {
                            L.rgb[i * 3] = (byte)(sB / n);
                            L.rgb[i * 3 + 1] = (byte)(sG / n);
                            L.rgb[i * 3 + 2] = (byte)(sR / n);
                            known[i] = 2;          // 本圈填好，下一圈可作为来源
                            filled = true;
                        }
                    }
                if (!filled) break;
                for (int i = 0; i < known.Length; i++) if (known[i] == 2) known[i] = 1;
            }

            // 平滑若干轮，让初始猜测更连续（为 PatchMatch 提供好的起点）
            int rounds = Math.Min(220, Math.Max(30, (w + h) / 4));   // 最粗层洞很小，多平滑几乎不花时间
            for (int it = 0; it < rounds; it++)
            {
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        if (L.hole[i] == 0) continue;
                        int sB = 0, sG = 0, sR = 0, n = 0;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int px = x + dx, py = y + dy;
                                if (px < 0 || py < 0 || px >= w || py >= h) continue;
                                int pi = py * w + px;
                                sB += L.rgb[pi * 3]; sG += L.rgb[pi * 3 + 1]; sR += L.rgb[pi * 3 + 2]; n++;
                            }
                        L.rgb[i * 3] = (byte)(sB / n);
                        L.rgb[i * 3 + 1] = (byte)(sG / n);
                        L.rgb[i * 3 + 2] = (byte)(sR / n);
                    }
            }
        }
        // ---------------- PatchMatch 核心 ----------------

        static int PatchDist(Lv L, int tx, int ty, int sx, int sy, int r, int cutoff)
        {
            // 目标块与源块共用同一组偏移，两者都必须落在图像内
            int dyLo = Math.Max(-r, -ty);
            int dyHi = Math.Min(r, L.h - 1 - ty);
            int dxLo = Math.Max(-r, -tx);
            int dxHi = Math.Min(r, L.w - 1 - tx);
            int sum = 0;
            for (int dy = dyLo; dy <= dyHi; dy++)
            {
                int to = ((ty + dy) * L.w + (tx + dxLo)) * 3;
                int so = ((sy + dy) * L.w + (sx + dxLo)) * 3;
                for (int dx = dxLo; dx <= dxHi; dx++)
                {
                    int d0 = L.rgb[to] - L.rgb[so];
                    int d1 = L.rgb[to + 1] - L.rgb[so + 1];
                    int d2 = L.rgb[to + 2] - L.rgb[so + 2];
                    sum += d0 * d0 + d1 * d1 + d2 * d2;
                    if (sum >= cutoff) return sum;      // 提前退出
                    to += 3; so += 3;
                }
            }
            return sum;
        }

        static void TryCandidate(Lv L, int x, int y, int sx, int sy, int r)
        {
            if (sx < r || sy < r || sx >= L.w - r || sy >= L.h - r) return;
            if (!PatchValid(L, sx, sy, r)) return;
            int i = y * L.w + x;
            int best = L.ok[i] != 0 ? L.nnd[i] : int.MaxValue;
            int d = PatchDist(L, x, y, sx, sy, r, best);
            if (d < best)
            {
                L.nnd[i] = d; L.nnx[i] = sx; L.nny[i] = sy; L.ok[i] = 1;
            }
        }

        // 统计洞内像素数（用于判断值不值得开多线程）
        static int HoleCount(Lv L)
        {
            int n = 0;
            byte[] hole = L.hole;
            for (int i = 0; i < hole.Length; i++) if (hole[i] != 0) n++;
            return n;
        }

        static void PatchMatchStep(Lv L, int r, bool forward)
        {
            int w = L.w, h = L.h;
            int yStart = forward ? 0 : h - 1, yEnd = forward ? h : -1, yStep = forward ? 1 : -1;
            int xStart = forward ? 0 : w - 1, xEnd = forward ? w : -1, xStep = forward ? 1 : -1;
            bool parallel = HoleCount(L) >= 8000;

            if (!parallel)
            {
                PatchMatchRows(L, r, forward, yStart, yEnd, yStep, xStart, xEnd, xStep);
                return;
            }
            // 按行分块并行。每个像素只写自己的槽位，读邻居的槽位即使读到"刚更新过"的候选也是合法候选，
            // 只会让收敛路径略有不同，不会产生错误结果（GDI+/数组层面也无需加锁）。
            ParallelHelper.Run(h, 0, delegate(int from, int to)
            {
                int ys = forward ? from : (h - 1 - from);
                int ye = forward ? to : (h - 1 - to);
                int ystep = forward ? 1 : -1;
                PatchMatchRows(L, r, forward, ys, ye, ystep, xStart, xEnd, xStep);
            });
        }

        static void PatchMatchRows(Lv L, int r, bool forward, int yStart, int yEnd, int yStep,
                                   int xStart, int xEnd, int xStep)
        {
            Random rnd = new Random(1234 + (forward ? 1 : 2) + yStart);
            int w = L.w, h = L.h;
            for (int y = yStart; y != yEnd; y += yStep)
            {
                for (int x = xStart; x != xEnd; x += xStep)
                {
                    int i = y * w + x;
                    if (L.hole[i] == 0) continue;
                    if (L.ok[i] == 0)
                    {
                        // 多试几个随机候选，直到找到合法匹配（源块必须完全不含洞）
                        for (int t = 0; t < 8 && L.ok[i] == 0; t++)
                        {
                            int rx = r + rnd.Next(Math.Max(1, w - 2 * r));
                            int ry = r + rnd.Next(Math.Max(1, h - 2 * r));
                            TryCandidate(L, x, y, rx, ry, r);
                        }
                        if (L.ok[i] == 0) continue;   // 仍无匹配：保留上一层的结果
                    }

                    // 传播：邻域的最近邻偏移 +1
                    int pi = (x - xStep) >= 0 && (x - xStep) < w ? y * w + (x - xStep) : -1;
                    if (pi >= 0 && L.ok[pi] != 0)
                        TryCandidate(L, x, y, L.nnx[pi] - xStep, L.nny[pi], r);
                    int pj = (y - yStep) >= 0 && (y - yStep) < h ? (y - yStep) * w + x : -1;
                    if (pj >= 0 && L.ok[pj] != 0)
                        TryCandidate(L, x, y, L.nnx[pj], L.nny[pj] - yStep, r);

                    // 随机搜索：半径指数递减
                    int radius = Math.Max(w, h);
                    int cx = L.nnx[i], cy = L.nny[i];
                    while (radius > 0)
                    {
                        // 每个半径试两个候选（标准改进）：匹配更准、结果更稳定
                        for (int t2 = 0; t2 < 2; t2++)
                        {
                            int rx = cx + rnd.Next(-radius, radius + 1);
                            int ry = cy + rnd.Next(-radius, radius + 1);
                            TryCandidate(L, x, y, rx, ry, r);
                        }
                        radius /= 2;
                    }
                }
            }
        }

        // 把所有洞像素的最近邻块投票平均（重叠区按距离加权）
        // 实现为"聚集式"：每个目标像素收集所有覆盖它的样本块。好处是天然可并行——
        // 每个线程只写自己负责的像素，读的都是只读的最近邻和已知像素。
        static void Vote(Lv L, int r)
        {
            int w = L.w, h = L.h;
            int n = w * h;
            // 累加缓冲按层复用：每次迭代都新分配会在百万像素级产生数百 MB 的瞬时内存/GC 压力
            if (L.vAcc == null || L.vAcc.Length != n * 3)
            {
                L.vAcc = new float[n * 3];
                L.vWgt = new float[n];
            }
            else
            {
                Array.Clear(L.vAcc, 0, n * 3);
                Array.Clear(L.vWgt, 0, n);
            }
            float[] accB = L.vAcc;
            float[] accW = L.vWgt;

            // 每个样本块的投票权重先算好（指数只在每个像素上算一次，不进内层循环）
            float[] wgt = L.vWgt2;
            if (wgt == null || wgt.Length != n) { wgt = new float[n]; L.vWgt2 = wgt; }
            double norm = (double)((2 * r + 1) * (2 * r + 1) * 3);
            float inv2s2 = (float)(1.0 / (2.0 * 2.5 * 2.5));
            for (int i = 0; i < n; i++)
            {
                if (L.hole[i] == 0 || L.ok[i] == 0) { wgt[i] = 0f; continue; }
                wgt[i] = (float)Math.Exp(-(float)(L.nnd[i] / norm) * inv2s2);   // 只让最相似的块说话，避免边缘被“平均”糊掉
            }

            bool parallel = HoleCount(L) >= 8000;
            RangeDelegate body = delegate(int from, int to)
            {
                for (int y = from; y < to; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int q = y * w + x;
                        if (L.hole[q] == 0) continue;
                        double sB = 0, sG = 0, sR = 0, sW = 0;
                        int dyLo = Math.Max(-r, -y), dyHi = Math.Min(r, h - 1 - y);
                        int dxLo = Math.Max(-r, -x), dxHi = Math.Min(r, w - 1 - x);
                        for (int dy = dyLo; dy <= dyHi; dy++)
                        {
                            int py = y + dy;
                            for (int dx = dxLo; dx <= dxHi; dx++)
                            {
                                int p = py * w + (x + dx);
                                float wt = wgt[p];
                                if (wt <= 0f) continue;                 // 该邻居没有有效匹配
                                int sx = L.nnx[p] + (x - (x + dx));     // 样本块中对应 q 的那个像素
                                int sy = L.nny[p] + (y - py);
                                if (sx < 0 || sy < 0 || sx >= w || sy >= h) continue;
                                int o = (sy * w + sx) * 3;
                                sB += L.rgb[o] * wt; sG += L.rgb[o + 1] * wt; sR += L.rgb[o + 2] * wt;
                                sW += wt;
                            }
                        }
                        if (sW > 1e-6)
                        {
                            int o3 = q * 3;
                            accB[o3] = (float)sB; accB[o3 + 1] = (float)sG; accB[o3 + 2] = (float)sR;
                            accW[q] = (float)sW;
                        }
                    }
            };
            if (parallel) ParallelHelper.Run(h, 0, body);
            else body(0, h);

            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (L.hole[i] == 0 || accW[i] <= 0) continue;
                    int o = i * 3;
                    L.rgb[o] = (byte)Clamp(accB[o] / accW[i]);
                    L.rgb[o + 1] = (byte)Clamp(accB[o + 1] / accW[i]);
                    L.rgb[o + 2] = (byte)Clamp(accB[o + 2] / accW[i]);
                }
        }

        static double Clamp(double v) { return v < 0 ? 0 : (v > 255 ? 255 : v); }

        // 接缝平滑：只处理洞口一圈像素，消除拼块硬边
        static void SmoothSeam(Lv L)
        {
            int w = L.w, h = L.h;
            byte[] copy = new byte[L.rgb.Length];
            Array.Copy(L.rgb, copy, L.rgb.Length);
            for (int y = 1; y < h - 1; y++)
                for (int x = 1; x < w - 1; x++)
                {
                    int i = y * w + x;
                    if (L.hole[i] == 0) continue;
                    bool boundary = L.hole[i - 1] == 0 || L.hole[i + 1] == 0 ||
                                    L.hole[i - w] == 0 || L.hole[i + w] == 0;
                    if (!boundary) continue;
                    int sB = 0, sG = 0, sR = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int o = ((y + dy) * w + (x + dx)) * 3;
                            sB += copy[o]; sG += copy[o + 1]; sR += copy[o + 2];
                        }
                    int io = i * 3;
                    // 与自身值混合，避免过度模糊
                    L.rgb[io] = (byte)((sB / 9 + copy[io] * 2) / 3);
                    L.rgb[io + 1] = (byte)((sG / 9 + copy[io + 1] * 2) / 3);
                    L.rgb[io + 2] = (byte)((sR / 9 + copy[io + 2] * 2) / 3);
                }
        }

        static void WriteBack(Lv L, Bitmap dst, Rectangle rc)
        {
            PixelBuffer buf = BitmapBridge.ToBuffer(dst);
            for (int y = 0; y < L.h; y++)
            {
                int row = (rc.Y + y) * buf.Stride + rc.X * 3;
                for (int x = 0; x < L.w; x++)
                {
                    int i = y * L.w + x;
                    if (L.hole[i] == 0) continue;          // 非洞像素保持原样
                    int q = row + x * 3;
                    buf.Data[q] = L.rgb[i * 3];
                    buf.Data[q + 1] = L.rgb[i * 3 + 1];
                    buf.Data[q + 2] = L.rgb[i * 3 + 2];
                }
            }
            BitmapBridge.WriteBack(dst, buf);
        }
    }
}
