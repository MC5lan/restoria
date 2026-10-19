// SealTool.cs — 公章识别与上色（纯 C#，无外部依赖）
//
// 用途：扫描/复印后的文件里公章褪色、发灰甚至变黑，需要自动找到公章区域并重新上红。
// 思路（两条线索互补，都是经典图像处理，老机器上毫秒级）：
//   1) 颜色线索：公章本身是红的 → 用“红度”（R 明显高于 G/B）分割，最可靠；
//   2) 形状线索：褪色/黑白扫描时没有红色 → 用墨迹连通域 + 形状过滤（近似方形包围盒、
//      填充率适中、从质心射出的射线长度一致=圆环）来判定。
// 上色：保留原墨迹的浓淡（亮度即墨量），把墨色替换为标准印章红，因此章内的字、五角星、
//      边框都能保留，只是从灰/黑变成红。
using System;
using System.Drawing;
using System.Drawing.Imaging;

namespace PicTool
{
    public static class SealTool
    {
        // 标准印章红
        public static readonly Color SealRed = Color.FromArgb(200, 26, 32);
        public static int LastPaper = -1;   // 诊断用：上次上色时判定的纸张白度
        public static int LastTouched = -1; // 诊断用：上次上色实际改动的像素数

        // ---------------- 识别 ----------------
        // hintMask：用户标记的搜索区域（8bpp，>=128 表示“在这附近找”）
        // 返回：识别出的印章区域（8bpp，>=128 表示印章）
        // 只要章体覆盖范围（内外都算）：兼容原有调用
        public static Bitmap Detect(Bitmap src, Bitmap hintMask, out int count)
        {
            Bitmap frame;
            return Detect(src, hintMask, out count, out frame);
        }

        // 额外返回外框掩码：只含章体外框（圆环/方框）本身的笔画，
        // 不含章内文字与五角星——用于只描外框、不碰内文的上色方式。
        public static Bitmap Detect(Bitmap src, Bitmap hintMask, out int count, out Bitmap frameMask)
        {
            count = 0;
            int w = src.Width, h = src.Height;
            Rectangle full = new Rectangle(0, 0, w, h);

            // 读像素
            byte[] rgb = new byte[w * h * 3];
            PixelBuffer sbuf = BitmapBridge.ToBuffer(src);
            for (int y = 0; y < h; y++)
            {
                int ro = y * sbuf.Stride;
                for (int x = 0; x < w; x++)
                {
                    int p = ro + x * 3;
                    int o = (y * w + x) * 3;
                    rgb[o] = sbuf.Data[p]; rgb[o + 1] = sbuf.Data[p + 1]; rgb[o + 2] = sbuf.Data[p + 2];
                }
            }

            byte[] hint = new byte[w * h];
            PixelBuffer mbuf = BitmapBridge.MaskToBuffer(hintMask);
            for (int y = 0; y < h; y++)
            {
                int ro = y * mbuf.Stride;
                for (int x = 0; x < w; x++) hint[y * w + x] = (byte)(mbuf.Data[ro + x] >= 128 ? 1 : 0);
            }

            // 搜索范围：hint 的外接矩形（外扩 8 像素）
            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (hint[y * w + x] != 0)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
            Bitmap result = Imaging.MakeMask(w, h);
            Bitmap frameBmp = Imaging.MakeMask(w, h);
            frameMask = frameBmp;
            if (maxX < 0) return result;
            int x0 = Math.Max(0, minX - 8), y0 = Math.Max(0, minY - 8);
            int x1 = Math.Min(w - 1, maxX + 8), y1 = Math.Min(h - 1, maxY + 8);

            // 灰度 + Otsu 阈值（在搜索范围内统计）
            byte[] lum = new byte[w * h];
            long[] hist = new long[256];
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    int o = (y * w + x) * 3;
                    int L = (rgb[o] * 29 + rgb[o + 1] * 150 + rgb[o + 2] * 77) >> 8;
                    lum[y * w + x] = (byte)L;
                    hist[L]++;
                }
            int otsu = Otsu(hist, (y1 - y0 + 1) * (x1 - x0 + 1));

            // 纸张白度：搜索区域内亮度的最亮 4% 分位（扫描件偏灰时也能正确判断“墨量”）
            int paper = PaperLevel(hist, (y1 - y0 + 1) * (x1 - x0 + 1));
            if (paper < 120) paper = 120;

            // 两条线索
            byte[] ink = new byte[w * h];       // 墨迹（按纸张白度归一化）
            byte[] red = new byte[w * h];       // 红色（红度）
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    int i = y * w + x, o = i * 3;
                    int b = rgb[o], g = rgb[o + 1], r = rgb[o + 2];
                    int redness = r - (g > b ? g : b);
                    if (redness > 26 && r > 50) red[i] = 1;
                    int L = lum[i];
                    if (L < otsu && red[i] == 0) ink[i] = 1;
                    else if ((1.0 - L / (double)paper) > 0.12 && red[i] == 0) ink[i] = 1;   // 淡墨也算
                }

            byte[] outMask = new byte[w * h];
            byte[] frameOut = new byte[w * h];   // 只含外框笔画（不含内文），用于"只描外框"上色

            try
            {
            // 1) 红色线索：大面积红色连通域
            int nRed = MarkComponents(red, outMask, frameOut, w, h, x0, y0, x1, y1, true, hint, ink, paper, lum, rgb);
            // 2) 形状线索：近似圆形/方形的墨迹连通域
            int nShape = MarkComponents(ink, outMask, frameOut, w, h, x0, y0, x1, y1, false, hint, ink, paper, lum, rgb);
            count = nRed + nShape;

            // 膨胀 2 像素，把边缘抗锯齿和淡墨一并覆盖（外框掩码只补 1 像素，免得糊到内文）
            Dilate(outMask, w, h, 2);
            Dilate(frameOut, w, h, 1);
            }
            catch { result.Dispose(); frameBmp.Dispose(); throw; }   // 失败时释放已分配的结果掩码，避免泄漏整图大小的位图

            // 写入结果掩码
            PixelBuffer rres = new PixelBuffer(w, h, 1);
            PixelBuffer rfrm = new PixelBuffer(w, h, 1);
            for (int y = 0; y < h; y++)
            {
                int ro = y * rres.Stride, fo = y * rfrm.Stride;
                for (int x = 0; x < w; x++)
                {
                    rres.Data[ro + x] = (byte)(outMask[y * w + x] != 0 ? 255 : 0);
                    rfrm.Data[fo + x] = (byte)(frameOut[y * w + x] != 0 ? 255 : 0);
                }
            }
            Bitmap result2 = BitmapBridge.BufferToMask(rres);
            Bitmap frameBmp2 = BitmapBridge.BufferToMask(rfrm);
            result.Dispose(); frameBmp.Dispose();
            result = result2; frameMask = frameBmp2;
            return result;
        }

        static int Otsu(long[] hist, long total)
        {
            double sum = 0;
            for (int i = 0; i < 256; i++) sum += (double)i * hist[i];
            double sumB = 0; long wB = 0; double best = -1; int threshold = 128;
            for (int t = 0; t < 256; t++)
            {
                wB += hist[t];
                if (wB == 0) continue;
                long wF = total - wB;
                if (wF == 0) break;
                sumB += (double)t * hist[t];
                double mB = sumB / wB, mF = (sum - sumB) / wF;
                double between = (double)wB * wF * (mB - mF) * (mB - mF);
                if (between > best) { best = between; threshold = t; }
            }
            return threshold;
        }

        // 纸张白度：亮度直方图的 88 分位（避开笔画，取“纸”的亮度）
        static int PaperLevel(long[] hist, long total)
        {
            long acc = 0, target = (long)(total * 0.04) + 1;
            for (int i = 255; i >= 0; i--)
            {
                acc += hist[i];
                if (acc >= target) return i;
            }
            return 255;
        }

        // 连通域扫描 + 形状过滤，命中的写入 outMask，返回命中个数
        static int MarkComponents(byte[] map, byte[] outMask, byte[] frameOut, int w, int h,
                                  int x0, int y0, int x1, int y1, bool isRed, byte[] hint,
                                  byte[] ink, int paper, byte[] lum, byte[] rgb)
        {
            int bw = x1 - x0 + 1, bh = y1 - y0 + 1;
            byte[] seen = new byte[bw * bh];
            int[] stack = new int[bw * bh];
            int[] queue = new int[bw * bh];
            int hits = 0;

            for (int sy = y0; sy <= y1; sy++)
                for (int sx = x0; sx <= x1; sx++)
                {
                    if (map[sy * w + sx] == 0) continue;
                    int li = (sy - y0) * bw + (sx - x0);
                    if (seen[li] != 0) continue;

                    // 洪水填充收集连通域
                    int top = 0, cnt = 0;
                    stack[top++] = li; seen[li] = 1;
                    int cminX = sx, cmaxX = sx, cminY = sy, cmaxY = sy;
                    long sumX = 0, sumY = 0;
                    while (top > 0)
                    {
                        int v = stack[--top];
                        int vy = v / bw + y0, vx = v % bw + x0;
                        queue[cnt++] = v;
                        sumX += vx; sumY += vy;
                        if (vx < cminX) cminX = vx;
                        if (vx > cmaxX) cmaxX = vx;
                        if (vy < cminY) cminY = vy;
                        if (vy > cmaxY) cmaxY = vy;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dy == 0) continue;
                                int nx = vx + dx, ny = vy + dy;
                                if (nx < x0 || ny < y0 || nx > x1 || ny > y1) continue;
                                int ni = (ny - y0) * bw + (nx - x0);
                                if (seen[ni] != 0 || map[ny * w + nx] == 0) continue;
                                seen[ni] = 1;
                                stack[top++] = ni;
                            }
                    }

                    int area = cnt;
                    int cw = cmaxX - cminX + 1, ch = cmaxY - cminY + 1;
                    if (area < 120) continue;
                    if (cw < 24 || ch < 24) continue;                       // 太小：噪点/小字
                    if (cw > (x1 - x0 + 1) * 9 / 10 && ch > (y1 - y0 + 1) * 9 / 10) continue;  // 整个搜索区：不是章
                    double aspect = cw / (double)ch;
                    if (aspect < 0.45 || aspect > 2.2) continue;            // 细长：多半是文字行
                    double fill = area / (double)(cw * ch);
                    if (fill < 0.08 || fill > 0.92) continue;               // 太空/太实都不像章

                    bool ok;
                    if (isRed)
                    {
                        // 红色线索本身就很有说服力：只要够大、近似方形即可
                        ok = true;
                    }
                    else
                    {
                        // 形状线索要求更严：圆环/圆盘（射线长度一致性）
                        double cx = sumX / (double)area, cy = sumY / (double)area;
                        ok = RadialConsistency(map, w, h, cx, cy, cminX, cminY, cmaxX, cmaxY);
                    }
                    if (!ok) continue;

                    // 至少有一部分落在用户标记的范围内（外扩 12 像素容忍）
                    bool near = false;
                    for (int k = 0; k < cnt && !near; k += 7)
                    {
                        int vy = queue[k] / bw + y0, vx = queue[k] % bw + x0;
                        for (int dy = -12; dy <= 12 && !near; dy += 4)
                            for (int dx = -12; dx <= 12 && !near; dx += 4)
                            {
                                int nx = vx + dx, ny = vy + dy;
                                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                                if (hint[ny * w + nx] != 0) near = true;
                            }
                    }
                    if (!near) continue;

                    for (int k = 0; k < cnt; k++)
                    {
                        int vy = queue[k] / bw + y0, vx = queue[k] % bw + x0;
                        outMask[vy * w + vx] = 1;
                        frameOut[vy * w + vx] = 1;   // 连通域本身就是外框笔画
                    }

                    // 章体内部（五角星、环内文字）一并纳入：
                    // 在圆盘范围内，把所有“有墨”的像素补进来，纸张仍是白的不受影响
                    double mcx = sumX / (double)area, mcy = sumY / (double)area;
                    double rr = Math.Max(cmaxX - cminX, cmaxY - cminY) / 2.0 + 3;
                    int bx0 = Math.Max(x0, cminX - 2), bx1 = Math.Min(x1, cmaxX + 2);
                    int by0 = Math.Max(y0, cminY - 2), by1 = Math.Min(y1, cmaxY + 2);
                    for (int vy = by0; vy <= by1; vy++)
                        for (int vx = bx0; vx <= bx1; vx++)
                        {
                            double ddx = vx - mcx, ddy = vy - mcy;
                            if (ddx * ddx + ddy * ddy > rr * rr) continue;
                            int vi = vy * w + vx;
                            if (outMask[vi] != 0) continue;
                            int o = vi * 3;
                            int b = rgb[o], g = rgb[o + 1], r = rgb[o + 2];
                            int redness = r - (g > b ? g : b);
                            double inkn = 1.0 - lum[vi] / (double)paper;
                            if (redness > 26 && r > 50) outMask[vi] = 1;          // 红色像素
                            else if (inkn > 0.12) outMask[vi] = 1;                // 有墨的像素
                        }
                    hits++;
                }
            return hits;
        }

        // 从质心向外射线，检查“最远墨迹点距离”的一致性：圆环/圆盘的一致性高，文字块低
        static bool RadialConsistency(byte[] map, int w, int h, double cx, double cy,
                                      int minX, int minY, int maxX, int maxY)
        {
            int rays = 36, hit = 0;
            double sum = 0, sum2 = 0;
            double maxR = Math.Max(maxX - minX, maxY - minY) / 2.0 + 2;
            for (int k = 0; k < rays; k++)
            {
                double ang = k * 2 * Math.PI / rays;
                double dx = Math.Cos(ang), dy = Math.Sin(ang);
                double last = -1;
                for (double t = 2; t <= maxR; t += 1.0)
                {
                    int px = (int)Math.Round(cx + dx * t), py = (int)Math.Round(cy + dy * t);
                    if (px < 0 || py < 0 || px >= w || py >= h) break;   // y 方向必须判：图像下边缘附近的印章会越界
                    if (map[py * w + px] != 0) last = t;
                }
                if (last > 0)
                {
                    hit++;
                    sum += last; sum2 += last * last;
                }
            }
            if (hit < rays * 2 / 3) return false;
            double mean = sum / hit;
            double var = sum2 / hit - mean * mean;
            double std = var > 0 ? Math.Sqrt(var) : 0;
            if (mean <= 0) return false;
            return std / mean <= 0.26;      // 半径变化小 → 环形/圆形
        }

        static void Dilate(byte[] m, int w, int h, int iterations)
        {
            byte[] tmp = new byte[m.Length];
            for (int it = 0; it < iterations; it++)
            {
                for (int y = 0; y < h; y++)
                {
                    int ym = y > 0 ? y - 1 : 0, yp = y < h - 1 ? y + 1 : h - 1;
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        if (m[i] != 0) { tmp[i] = 1; continue; }
                        int xm = x > 0 ? x - 1 : 0, xp = x < w - 1 ? x + 1 : w - 1;
                        byte v = m[y * w + xm];
                        if (v == 0) v = m[y * w + xp];
                        if (v == 0) v = m[ym * w + x];
                        if (v == 0) v = m[yp * w + x];
                        if (v == 0) v = m[ym * w + xm];
                        if (v == 0) v = m[ym * w + xp];
                        if (v == 0) v = m[yp * w + xm];
                        if (v == 0) v = m[yp * w + xp];
                        tmp[i] = v;
                    }
                }
                Array.Copy(tmp, m, m.Length);
            }
        }

        // ---------------- 上色 ----------------
        // 把 sealMask 范围内的像素按“墨量”染成印章红：淡墨也能被提亮，浓墨保持饱和
        // 墨量以纸张白度为基准计算，扫描偏灰的纸不会被染粉
        public static void Colorize(Bitmap src, Bitmap sealMask, int strength)
        {
            Colorize(src, sealMask, SealRed, strength);
        }

        // 任意目标色版本（调色页「区域上色」与印章上色共用同一条算法）：
        // 掩码内墨迹按墨量向 target 过渡 —— 黑=描黑、红=染红、白=涂白（淡墨提亮），
        // 纸张（墨量≈0）一个字节都不动，浓淡（抗锯齿边缘）原样保留。
        public static void Colorize(Bitmap src, Bitmap maskBmp, Color target, int strength)
        {
            if (strength < 5) strength = 5;
            if (strength > 100) strength = 100;
            int w = src.Width, h = src.Height;
            Rectangle full = new Rectangle(0, 0, w, h);

            byte[] mask = new byte[w * h];
            PixelBuffer mbuf = BitmapBridge.MaskToBuffer(maskBmp);
            for (int y = 0; y < h; y++)
            {
                int ro = y * mbuf.Stride;
                for (int x = 0; x < w; x++) mask[y * w + x] = (byte)(mbuf.Data[ro + x] >= 128 ? 1 : 0);
            }

            // 纸张白度：整图亮度的 88 分位（用同一块缓冲：读一遍统计，上色在缓冲上改，最后一次写回）
            long[] hist = new long[256];
            PixelBuffer buf = BitmapBridge.ToBuffer(src);
            for (int y = 0; y < h; y++)
            {
                int ro = y * buf.Stride;
                for (int x = 0; x < w; x++)
                {
                    int p = ro + x * 3;
                    hist[(buf.Data[p] * 29 + buf.Data[p + 1] * 150 + buf.Data[p + 2] * 77) >> 8]++;
                }
            }
            int paper = PaperLevel(hist, (long)w * h);
            if (paper < 120) paper = 120;
            LastPaper = paper;
            LastTouched = 0;

            for (int y = 0; y < h; y++)
            {
                int ro = y * buf.Stride;
                for (int x = 0; x < w; x++)
                {
                    if (mask[y * w + x] == 0) continue;
                    int p = ro + x * 3;
                    int b = buf.Data[p], g = buf.Data[p + 1], r = buf.Data[p + 2];
                    int lum = (b * 29 + g * 150 + r * 77) >> 8;
                    double ink = 1.0 - lum / (double)paper;      // 墨量（相对纸张）
                    if (ink <= 0.05) continue;
                    if (ink > 1.0) ink = 1.0;
                    double a = Math.Pow(ink, 0.72) * (strength / 100.0);   // 淡墨提亮
                    if (a > 1.0) a = 1.0;
                    buf.Data[p] = (byte)(b + (target.B - b) * a);
                    buf.Data[p + 1] = (byte)(g + (target.G - g) * a);
                    buf.Data[p + 2] = (byte)(r + (target.R - r) * a);
                    LastTouched++;
                }
            }
            BitmapBridge.WriteBack(src, buf);
        }
    }
}
