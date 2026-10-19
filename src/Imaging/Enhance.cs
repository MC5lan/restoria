// Enhance.cs — 工程级修图算法库（档案数字化增强套件）
//   色阶（黑场/白场/Gamma）、Unsharp Mask 锐化、自动纠偏（投影法检测）、
//   自适应二值化（Sauvola，转黑白存档）、四点透视校正（单应矩阵 + 双线性重采样）、
//   亮度直方图（检查器可视化）。
// 像素操作走 BitmapBridge 的 PixelBuffer（P1 迁移完成），无外部依赖，.NET Framework 4.8。
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace PicTool
{
    // 一次"调色 + 色阶 + 锐化"的全部参数。滑条语义与 MainForm 现有约定一致：
    // 数值永远表示相对基准图的量，提交时用基准图重算（幂等）。
    public class AdjustParams
    {
        public double Bright, Contr, Sat, Temp;   // 与 Imaging.ColorAdjust 同义
        public double Black = 0;                  // 黑场 0..254
        public double White = 255;                // 白场 1..255
        public double Gamma = 1.0;                // 0.1..3.0
        public double Sharpen = 0;                // 0..1.5（Unsharp Mask 强度）

        public bool IsNeutral()
        {
            return Bright == 0 && Contr == 0 && Sat == 0 && Temp == 0 &&
                   Black == 0 && White == 255 && Gamma == 1.0 && Sharpen == 0;
        }
    }

    public static class Enhance
    {
        // ---------------- 综合调参入口 ----------------

        public static Bitmap Apply(Bitmap src, AdjustParams p)
        {
            Bitmap dst = Imaging.Ensure24bpp(new Bitmap(src));
            ApplyInPlace(dst, p);
            return dst;
        }

        // 顺序固定：亮度/对比度/饱和度/色温 → 色阶 → 锐化（与 Photoshop 惯例一致）
        public static void ApplyInPlace(Bitmap bmp, AdjustParams p)
        {
            if (p == null || p.IsNeutral()) return;
            if (p.Bright != 0 || p.Contr != 0 || p.Sat != 0 || p.Temp != 0)
                Imaging.ColorAdjustInPlace(bmp, p.Bright, p.Contr, p.Sat, p.Temp);
            if (p.Black != 0 || p.White != 255 || p.Gamma != 1.0)
                LevelsInPlace(bmp, p.Black, p.White, p.Gamma);
            if (p.Sharpen > 0)
                UnsharpInPlace(bmp, p.Sharpen);
        }

        // ---------------- 色阶 ----------------

        // 白底黑字校准的核心：把"该白的地方"压到 255、"该黑的地方"压到 0，
        // 扫描件常见的灰蒙蒙/发黄底色一次拉干净。
        public static unsafe void LevelsInPlace(Bitmap bmp, double black, double white, double gamma)
        {
            if (white <= black + 1) white = black + 1;      // 防手滑：白场必须大于黑场
            double range = white - black;
            double invGamma = 1.0 / Math.Max(0.05, gamma);
            byte[] lut = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                double t = (i - black) / range;
                if (t < 0) t = 0;
                if (t > 1) t = 1;
                double v = 255.0 * Math.Pow(t, invGamma);
                lut[i] = (byte)Math.Round(v < 0 ? 0 : (v > 255 ? 255 : v));
            }
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
                    p[0] = lut[p[0]]; p[1] = lut[p[1]]; p[2] = lut[p[2]];
                }
            }
            }
            }
            BitmapBridge.WriteBack(bmp, buf);
        }

        // ---------------- Unsharp Mask 锐化 ----------------

        // 高斯模糊（分离式两次盒滤波近似）→ out = in + amount * (in - blur)
        // 半径固定 2px：档案修图要的是"字口更紧"，不是磨皮；
        // 大半径会让笔画周围出白边，宁可强度调低也不加大半径。
        public static unsafe void UnsharpInPlace(Bitmap bmp, double amount)
        {
            if (amount <= 0) return;
            if (amount > 2) amount = 2;
            int w = bmp.Width, h = bmp.Height;
            if (w < 5 || h < 5) return;
            Rectangle rc = new Rectangle(0, 0, w, h);

            // 灰度/逐通道太贵也不必要：对亮度做钝化掩模，彩色分量保持原样
            float[] lum = new float[w * h];
            PixelBuffer buf = BitmapBridge.ToBuffer(bmp);
            int stride = buf.Stride;
            fixed (byte* basePtr = buf.Data)
            {
            for (int y = 0; y < h; y++)
            {
                byte* row = basePtr + y * stride;
                for (int x = 0; x < w; x++)
                {
                    byte* p = row + x * 3;
                    lum[y * w + x] = (float)(0.299 * p[2] + 0.587 * p[1] + 0.114 * p[0]);
                }
            }
            }

            float[] blur = BoxBlur(BoxBlur(lum, w, h), w, h);   // 两次盒滤波 ≈ 高斯

            // 增量只作用于亮度：按比例缩放 RGB 与目标亮度的差
            fixed (byte* basePtr2 = buf.Data)
            {
            for (int y = 0; y < h; y++)
            {
                byte* row = basePtr2 + y * stride;
                for (int x = 0; x < w; x++)
                {
                    byte* p = row + x * 3;
                    float l = (float)(0.299 * p[2] + 0.587 * p[1] + 0.114 * p[0]);
                    float diff = (lum[y * w + x] - blur[y * w + x]) * (float)amount;
                    float nl = l + diff;
                    float f = nl - l;                          // 亮度增量摊到 RGB
                    float b = p[0] + f, gg = p[1] + f, r = p[2] + f;
                    p[0] = (byte)(b < 0 ? 0 : (b > 255 ? 255 : b));
                    p[1] = (byte)(gg < 0 ? 0 : (gg > 255 ? 255 : gg));
                    p[2] = (byte)(r < 0 ? 0 : (r > 255 ? 255 : r));
                }
            }
            }
            BitmapBridge.WriteBack(bmp, buf);
        }

        // 一次半径 2 的盒滤波（水平 + 垂直分离）
        static float[] BoxBlur(float[] src, int w, int h)
        {
            float[] tmp = new float[w * h];
            float[] dst = new float[w * h];
            const int R = 2;
            for (int y = 0; y < h; y++)
            {
                int ro = y * w;
                float acc = 0;
                for (int x = -R; x <= R; x++) acc += src[ro + ClampI(x, 0, w - 1)];
                float inv = 1f / (R * 2 + 1);
                for (int x = 0; x < w; x++)
                {
                    tmp[ro + x] = acc * inv;
                    acc -= src[ro + ClampI(x - R, 0, w - 1)];
                    acc += src[ro + ClampI(x + R + 1, 0, w - 1)];
                }
            }
            for (int x = 0; x < w; x++)
            {
                float acc = 0;
                for (int y = -R; y <= R; y++) acc += tmp[ClampI(y, 0, h - 1) * w + x];
                float inv = 1f / (R * 2 + 1);
                for (int y = 0; y < h; y++)
                {
                    dst[y * w + x] = acc * inv;
                    acc -= tmp[ClampI(y - R, 0, h - 1) * w + x];
                    acc += tmp[ClampI(y + R + 1, 0, h - 1) * w + x];
                }
            }
            return dst;
        }

        static int ClampI(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }

        // ---------------- 自动纠偏（Deskew） ----------------

        // 投影轮廓法：文字行摆正时"每行墨迹像素数的方差"最大。
        // 先缩到 ~800px 宽再算，几十毫秒级；先粗扫（0.5°步进）再细扫（0.05°）。
        // 返回需要"逆时针旋转多少度才能转正"的角度（±MaxAngle），基本正时返回 0。
        // 逃生口 / 诊断用：设 PICTOOL_SKEW_NOGUARD=1 可关掉黑边保险（默认开）。
        // 用途：把 --archive 跑两遍（一开一关）比 MD5 —— 逐字节相同就证明"保险在归档路径上
        // 一次都没触发"，即这次改动对归档输出零影响（见 tools\verify_guard.py）。
        static int noGuardEnv = -1;
        public static bool NoBorderGuard
        {
            get
            {
                if (noGuardEnv < 0)
                    noGuardEnv = (Environment.GetEnvironmentVariable("PICTOOL_SKEW_NOGUARD") == "1") ? 1 : 0;
                return noGuardEnv != 0;
            }
        }
        public static double DetectSkewAngle(Bitmap src) { return DetectSkewAngle(src, !NoBorderGuard); }

        // guardBorder：是否启用"忽略与画面边框连通的暗块"（见 BorderDarkRing 说明）。
        // 正常调用一律 true；false 只给 --skew-test / 自检做对照，用来复现"没有这道保险"的老行为。
        public static unsafe double DetectSkewAngle(Bitmap src, bool guardBorder)
        {
            Bitmap small = Imaging.ScaleToFit(src, 800);
            if (small == null) return 0;
            try
            {
                int w = small.Width, h = small.Height;
                byte[] gray = new byte[w * h];
                PixelBuffer sbuf = BitmapBridge.ToBuffer(small);
                fixed (byte* basePtr = sbuf.Data)
                {
                    for (int y = 0; y < h; y++)
                    {
                        byte* row = basePtr + y * sbuf.Stride;
                        for (int x = 0; x < w; x++)
                        {
                            byte* p = row + x * 3;
                            gray[y * w + x] = (byte)((p[2] * 77 + p[1] * 150 + p[0] * 29) >> 8);
                        }
                    }
                }

                // Otsu 自动阈值取墨迹
                long[] hist = new long[256];
                for (int i = 0; i < gray.Length; i++) hist[gray[i]]++;
                int threshold = Otsu(hist, gray.Length);

                // 只保留墨迹像素坐标。
                // 修 bug：这里原来写的是 y += 2（隔行采样）—— 隔行步长会和"按角度错切"
                // 发生混叠，把 0° 硬顶成最高分：实测 -3° 的图在隔行采样下 Σ²(0°)=1.55e5 >
                // Σ²(+3°)=9.9e4（结论错），改成整行采样后 Σ²(0°)=3.27e5 < Σ²(+3°)=4.17e5（结论对）。
                // 于是小角度（1~5°，用户场景绝大多数）永远判成 0°。现在整行、整列都取，
                // 计算量对现代机器毫无压力（约 n×70 次加法），数组容量按最多一半像素给。
                int cap = Math.Max(1024, w * h / 2);
                short[] px = new short[cap];
                short[] py = new short[cap];
                int n = 0;

                // 黑边保险：带黑边的图（旋转没自动裁边留下的黑角、扫描仪黑背景露边）
                // 会被 Otsu 当成墨迹，投影法直接被带偏 —— 自检里就躺着一条实测：同一张 -3° 的图，
                // autoCrop=false（留黑角）时检测结果直接是 0°。
                // 分两步，且**只在"确实有黑边"时才动手**，正常页（边框是纸白）零行为变化：
                //   ① 先量外圈 2px 里"接近纯黑"的比例，达到 BorderDarkRing 才认为有黑边；
                //   ② 有黑边时把"与边框 4 连通的暗块"整块排除 —— 黑角/黑框无论多宽都一次去干净。
                byte[] dropBorder = null;
                if (guardBorder)
                {
                    int inkRing = 0, totRing = 0;
                    for (int y = 0; y < h; y++)
                    {
                        bool edgeRow = (y < 2 || y >= h - 2);
                        for (int x = 0; x < w; x++)
                        {
                            if (!edgeRow && x >= 2 && x < w - 2) continue;
                            totRing++;
                            if (gray[y * w + x] < BorderDarkLevel) inkRing++;
                        }
                    }
                    if (totRing > 0 && (double)inkRing / totRing >= BorderDarkRing)
                    {
                        dropBorder = new byte[w * h];
                        int[] stack = new int[w * h];
                        int sp = 0;
                        for (int x = 0; x < w; x++)
                        {
                            PushInk(stack, ref sp, dropBorder, gray, threshold, w, h, x, 0);
                            PushInk(stack, ref sp, dropBorder, gray, threshold, w, h, x, h - 1);
                        }
                        for (int y = 0; y < h; y++)
                        {
                            PushInk(stack, ref sp, dropBorder, gray, threshold, w, h, 0, y);
                            PushInk(stack, ref sp, dropBorder, gray, threshold, w, h, w - 1, y);
                        }
                        while (sp > 0)
                        {
                            int q = stack[--sp];
                            int qx = q % w, qy = q / w;
                            PushInk(stack, ref sp, dropBorder, gray, threshold, w, h, qx - 1, qy);
                            PushInk(stack, ref sp, dropBorder, gray, threshold, w, h, qx + 1, qy);
                            PushInk(stack, ref sp, dropBorder, gray, threshold, w, h, qx, qy - 1);
                            PushInk(stack, ref sp, dropBorder, gray, threshold, w, h, qx, qy + 1);
                        }
                    }
                }

                for (int y = 0; y < h && n < cap; y++)
                    for (int x = 0; x < w && n < cap; x++)
                    {
                        if (gray[y * w + x] >= threshold) continue;
                        if (dropBorder != null && dropBorder[y * w + x] != 0) continue;
                        px[n] = (short)x; py[n] = (short)y; n++;
                    }
                if (n < 200) return 0;      // 图太空（照片/空白页），纠偏没有意义

                double best = 0; double bestScore = -1;
                for (double ang = -MaxAngle; ang <= MaxAngle; ang += 0.5)
                {
                    double s = ProjectionScore(px, py, n, ang, h);
                    if (s > bestScore) { bestScore = s; best = ang; }
                }
                for (double ang = best - 0.5; ang <= best + 0.5; ang += 0.05)
                {
                    double s = ProjectionScore(px, py, n, ang, h);
                    if (s > bestScore) { bestScore = s; best = ang; }
                }
                if (Math.Abs(best) < 0.08) return 0;
                return Math.Round(best, 2);
            }
            finally { small.Dispose(); }
        }

        // 墨迹像素按角度错切后按行聚合：score = Σ(每行计数²)，行越"聚拢"分数越高
        //
        // 修 bug：旧实现的桶数写成 h + 2*|tan|*400 —— **桶数随角度变大而变多**，
        // 同样的墨迹被摊到更多桶里，分数天然吃亏，于是小角度永远赢：实测 -1.5°/-5° 都判成 0°，
        // 只有 -8° 这种大角度才测得出（用户场景绝大多数是 1~5° 轻微歪斜 ⇒ 等于没作用）。
        // 现在：桶数固定（按最大扫描角算一次）、错切围绕画面中心、并做双线性累加去掉量化台阶。
        // 检测范围 ±20°。注意 ProjectionScore 的投影桶只有 1 像素宽、
        // 且偏移量是整数，所以 maxShift（= 桶数组长度，由 MaxAngle 推出）只影响内存、**不影响分数**
        // —— 放宽范围不会改变 |角| ≤ 12° 的既有结果，只是原来测不到的 13~20° 现在能测到。
        const double MaxAngle = 20.0;

        // 黑边保险的触发门槛：画面外圈 2px 里"接近纯黑"的像素占比 ≥ 此值，才认为这张图有黑边。
        // 注意用**绝对灰度**（BorderDarkLevel）而不是 Otsu 阈值 —— 文字多的页 Otsu 阈值会偏高，
        // 把偏灰的纸边也算成"墨"，就会在正常页上误触发（实测样本第 8 页就中过这一枪，
        // 结果被白白挪动 0.1°）。黑角是旋转时补的纯黑（接近 0），扫描黑背景也在 0~70，
        // 而纸边/阴影通常在 120 以上 —— 用绝对线一刀切开，正常页的触发率就归零。
        const int BorderDarkLevel = 70;
        const double BorderDarkRing = 0.15;

        // 把 (x,y) 处的**墨迹**像素压进泛洪栈；已标记过或非墨迹则跳过。
        // mark 同时当 visited 用（入栈前就置 1），所以不会重复入栈。
        static void PushInk(int[] stack, ref int sp, byte[] mark, byte[] gray, int thr, int w, int h, int x, int y)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return;
            int i = y * w + x;
            if (mark[i] != 0) return;
            if (gray[i] >= thr) return;      // 非墨迹（纸面）
            mark[i] = 1;
            stack[sp++] = i;
        }

        static double ProjectionScore(short[] px, short[] py, int n, double deg, int h)
        {
            double tan = Math.Tan(deg * Math.PI / 180.0);
            int maxShift = (int)(Math.Tan(MaxAngle * Math.PI / 180.0) * 400) + 2;   // 与 deg 无关
            int rows = h + maxShift * 2;
            double[] counts = new double[rows];
            for (int i = 0; i < n; i++)
            {
                double rr = py[i] + tan * (px[i] - 400) + maxShift;
                int r0 = (int)Math.Floor(rr);
                double f = rr - r0;
                if (r0 >= 0 && r0 < rows) counts[r0] += 1.0 - f;
                if (r0 + 1 >= 0 && r0 + 1 < rows) counts[r0 + 1] += f;
            }
            double score = 0;
            for (int i = 0; i < rows; i++) score += counts[i] * counts[i];
            return score;
        }

        static int Otsu(long[] hist, long total)
        {
            double sum = 0;
            for (int i = 0; i < 256; i++) sum += i * hist[i];
            double sumB = 0; long wB = 0; double best = -1; int bestT = 128;
            for (int t = 0; t < 256; t++)
            {
                wB += hist[t];
                if (wB == 0) continue;
                long wF = total - wB;
                if (wF == 0) break;
                sumB += t * hist[t];
                double mB = sumB / wB;
                double mF = (sum - sumB) / wF;
                double between = (double)wB * wF * (mB - mF) * (mB - mF);
                if (between > best) { best = between; bestT = t; }
            }
            return bestT;
        }

        // ---------------- 自适应二值化（转黑白存档） ----------------

        // Sauvola 局部阈值：t = mean * (1 + k * (std/R - 1))，R=128。
        // 比全局 Otsu 强在光照不均的老扫描件：阴影里的字也能保住。
        // 输出纯黑白 24bpp，并做一轮 3x3 孤点去噪（档案归档的标准做法）。
        public static Bitmap Binarize(Bitmap src, double k)
        {
            Bitmap dst = Imaging.Ensure24bpp(new Bitmap(src));
            BinarizeInPlace(dst, k);
            return dst;
        }

        public static unsafe void BinarizeInPlace(Bitmap bmp, double k)
        {
            BinarizeInPlace(bmp, k, null);
        }

        // protect：可选的 w*h 保护掩膜，非 0 处**跳过二值化、保留原色**。
        // 用途：**印章豁免** —— 黑白化只看亮度，红章会被当成"墨"变成黑块，
        // 而归档要的是"白底黑字 + 红章保留"，所以印章区域必须开个后门。
        // 掩膜由 SealTool.Detect 产出，用前先外扩几像素（见 DocClean.DilateMask），
        // 否则二值化的边界会啃进章体边缘。
        public static unsafe void BinarizeInPlace(Bitmap bmp, double k, byte[] protect)
        {
            if (k <= 0) k = 0.2;
            int w = bmp.Width, h = bmp.Height;
            byte[] gray = new byte[w * h];
            PixelBuffer buf = BitmapBridge.ToBuffer(bmp);
            fixed (byte* basePtr = buf.Data)
            {
            int stride = buf.Stride;
            for (int y = 0; y < h; y++)
            {
                byte* row = basePtr + y * stride;
                for (int x = 0; x < w; x++)
                {
                    byte* p = row + x * 3;
                    gray[y * w + x] = (byte)((p[2] * 77 + p[1] * 150 + p[0] * 29) >> 8);
                }
            }
            }

            // 滑动窗口列和：原实现用两张 (w+1)*(h+1) 的 long 积分图，
            // 600dpi A4 要 ~1.1GB，32 位进程直接 OOM（且 (w+1)*(h+1) 是 int 乘法，宽 >46340 溢出）。
            // 改为逐行维护列和 + 行内前缀和：内存从 O(w*h) 降到 O(w)；
            // 全程整数精确累加，窗口和与积分图逐位一致，阈值结果不变。
            // 窗口随分辨率走：300dpi A4 约 61px，小图自动缩，上限 121
            int win = Math.Max(15, Math.Min(121, (Math.Min(w, h) / 12) | 1));
            if (win % 2 == 0) win++;
            int r = win / 2;

            long[] colSum = new long[w], colSum2 = new long[w];
            long[] pref = new long[w + 1], pref2 = new long[w + 1];

            // 初始：colSum 覆盖行 [0, min(h-1, r)]（即 y=0 的窗口行范围）
            for (int yy = 0; yy <= Math.Min(h - 1, r); yy++)
            {
                int o = yy * w;
                for (int x = 0; x < w; x++) { int g = gray[o + x]; colSum[x] += g; colSum2[x] += (long)g * g; }
            }

            byte[] bin = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                if (y > 0)
                {
                    int add = y + r;                       // 新进入窗口的行
                    if (add <= h - 1)
                    {
                        int o = add * w;
                        for (int x = 0; x < w; x++) { int g = gray[o + x]; colSum[x] += g; colSum2[x] += (long)g * g; }
                    }
                    int rem = y - 1 - r;                   // 移出窗口的行
                    if (rem >= 0)
                    {
                        int o = rem * w;
                        for (int x = 0; x < w; x++) { int g = gray[o + x]; colSum[x] -= g; colSum2[x] -= (long)g * g; }
                    }
                }
                // 行内前缀和：任意列区间的窗口和 O(1)
                pref[0] = 0; pref2[0] = 0;
                for (int x = 0; x < w; x++) { pref[x + 1] = pref[x] + colSum[x]; pref2[x + 1] = pref2[x] + colSum2[x]; }

                int y0 = Math.Max(0, y - r), y1 = Math.Min(h - 1, y + r);
                long rowArea = y1 - y0 + 1;
                for (int x = 0; x < w; x++)
                {
                    int x0 = Math.Max(0, x - r), x1 = Math.Min(w - 1, x + r);
                    long area = rowArea * (x1 - x0 + 1);
                    long s = pref[x1 + 1] - pref[x0];
                    long s2 = pref2[x1 + 1] - pref2[x0];
                    double mean = s / (double)area;
                    double varr = s2 / (double)area - mean * mean;
                    if (varr < 0) varr = 0;
                    double std = Math.Sqrt(varr);
                    double t = mean * (1.0 + k * (std / 128.0 - 1.0));
                    bin[y * w + x] = gray[y * w + x] <= t ? (byte)0 : (byte)255;
                }
            }

            Despeckle(bin, w, h);

            // 写回：纯黑白（protect 非 0 处跳过 —— 保留原色，即印章豁免）
            fixed (byte* basePtr2 = buf.Data)
            {
            for (int y = 0; y < h; y++)
            {
                byte* row = basePtr2 + y * buf.Stride;
                int o = y * w;
                for (int x = 0; x < w; x++)
                {
                    if (protect != null && protect[o + x] != 0) continue;   // 印章区：保持原色
                    byte v = bin[o + x];
                    byte* p = row + x * 3;
                    p[0] = v; p[1] = v; p[2] = v;
                }
            }
            }
            BitmapBridge.WriteBack(bmp, buf);
        }

        // 3x3 多数票去孤点：黑点周围黑邻居 < 2 → 变白；白点周围白邻居 < 2 → 变黑
        static void Despeckle(byte[] bin, int w, int h)
        {
            byte[] src = (byte[])bin.Clone();
            for (int y = 1; y < h - 1; y++)
            {
                for (int x = 1; x < w - 1; x++)
                {
                    int o = y * w + x;
                    int dark = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                            if ((dx != 0 || dy != 0) && src[o + dy * w + dx] == 0) dark++;
                    if (src[o] == 0 && dark < 2) bin[o] = 255;
                    else if (src[o] != 0 && dark >= 7) bin[o] = 0;   // 白点被黑包围（纸面破洞）
                }
            }
        }

        // ---------------- 四点透视校正 ----------------

        // pts 顺序：左上 → 右上 → 右下 → 左下（图像坐标）。
        // 输出尺寸取对边平均长度；单应矩阵把输出矩形映回源四边形，逐像素双线性重采样。
        // 出界像素填白（拍歪的文件通常四角外是桌面/背景，填白最接近归档预期）。
        public static unsafe Bitmap PerspectiveWarp(Bitmap src, PointF[] pts)
        {
            if (pts == null || pts.Length != 4) throw new Exception("透视校正需要四个角点。");

            // ---- 入口守卫（为什么必须在入口拦，而不是只靠像素循环里兜底）----
            // 若上游因缩放宽/高算成 0 而产出 NaN 角点，它会一路渗到 SolveHomography：
            // 主元比较 `Math.Abs(NaN) > ...` 恒为 false ⇒ 主元选择失效 ⇒ h8 全变 NaN ⇒
            // denom 也是 NaN。而 `NaN > -1e-9` 是 false，循环里那道"denom 趋零"的守卫
            // **拦不住 NaN**，于是 u=NaN 穿过全部边界比较，最后 (int)NaN = 0x80000000 野指针写。
            // 与其在百万像素里逐点兜底，不如在这里拒绝并给出人能看懂的原因。
            for (int k = 0; k < 4; k++)
            {
                if (float.IsNaN(pts[k].X) || float.IsNaN(pts[k].Y) ||
                    float.IsInfinity(pts[k].X) || float.IsInfinity(pts[k].Y))
                    throw new Exception("角点坐标无效（含 NaN 或无穷大），请重新取点。");
            }
            if (Dist(pts[0], pts[1]) < 1.0 || Dist(pts[1], pts[2]) < 1.0 ||
                Dist(pts[2], pts[3]) < 1.0 || Dist(pts[3], pts[0]) < 1.0)
                throw new Exception("角点重合或过于接近，无法校正。请把四个点拉开一些。");

            // 鞋带公式算四边形面积（返回两倍面积）：四点近共线时单应矩阵病态，输出全是噪声
            double area2 = 0;
            for (int k = 0; k < 4; k++)
            {
                int nx = (k + 1) & 3;
                area2 += (double)pts[k].X * pts[nx].Y - (double)pts[nx].X * pts[k].Y;
            }
            if (Math.Abs(area2) < 8.0)
                throw new Exception("四个点几乎在一条直线上，无法校正。请重新取点。");

            double wTop = Dist(pts[0], pts[1]), wBot = Dist(pts[3], pts[2]);
            double hL = Dist(pts[0], pts[3]), hR = Dist(pts[1], pts[2]);
            int ow = (int)Math.Round((wTop + wBot) / 2);
            int oh = (int)Math.Round((hL + hR) / 2);
            if (ow < 8) ow = 8;
            if (oh < 8) oh = 8;
            if (ow > 12000 || oh > 12000) throw new Exception("校正区域过大，请重新取点。");

            // 求 H：dest (x,y,1) -> src (u,v)。8 未知量线性方程组（高斯消元）。
            double[] h8 = SolveHomography(0, 0, pts[0].X, pts[0].Y,
                                          ow - 1, 0, pts[1].X, pts[1].Y,
                                          ow - 1, oh - 1, pts[2].X, pts[2].Y,
                                          0, oh - 1, pts[3].X, pts[3].Y);

            PixelBuffer sbuf = BitmapBridge.ToBuffer(src);
            PixelBuffer dbuf = new PixelBuffer(ow, oh, 3);
            fixed (byte* sp = sbuf.Data)
            fixed (byte* dp = dbuf.Data)
            {
            int sStride = sbuf.Stride, dStride = dbuf.Stride;
            int sw = src.Width, sh = src.Height;

            for (int y = 0; y < oh; y++)
            {
                byte* drow = dp + y * dStride;
                for (int x = 0; x < ow; x++)
                {
                    byte* q = drow + x * 3;
                    double denom = h8[6] * x + h8[7] * y + 1.0;
                    // 退化四边形（四点近共线）会让 denom 过 0：u/v 变 NaN 后所有边界比较都是
                    // false，会掉进重采样分支做野指针读写（(int)NaN = 0x80000000）。直接填白。
                    // ⚠️ IsNaN 必须**显式**判：NaN 与任何值比较都返回 false，
                    //    所以 `denom > -1e-9 && denom < 1e-9` 这种区间写法对 NaN 恒为 false，
                    //    会把 NaN 放行 —— 这正是本守卫曾经的漏洞。
                    if (double.IsNaN(denom) || (denom > -1e-9 && denom < 1e-9))
                    {
                        q[0] = 255; q[1] = 255; q[2] = 255;
                        continue;
                    }
                    double u = (h8[0] * x + h8[1] * y + h8[2]) / denom;
                    double v = (h8[3] * x + h8[4] * y + h8[5]) / denom;
                    // 同理：u/v 若是 NaN，下面两个边界比较全为 false 会直接穿过去。
                    // （+Inf/-Inf 反而安全：Inf > sw-1 或 Inf < 0 至少有一个为真。）
                    if (double.IsNaN(u) || double.IsNaN(v) || u < 0 || v < 0 || u > sw - 1 || v > sh - 1)
                    {
                        q[0] = 255; q[1] = 255; q[2] = 255;   // 出界填白
                        continue;
                    }
                    int x0 = (int)u, y0 = (int)v;
                    int x1 = x0 + 1 < sw ? x0 + 1 : x0;
                    int y1 = y0 + 1 < sh ? y0 + 1 : y0;
                    double fx = u - x0, fy = v - y0;
                    byte* p00 = sp + y0 * sStride + x0 * 3;
                    byte* p10 = sp + y0 * sStride + x1 * 3;
                    byte* p01 = sp + y1 * sStride + x0 * 3;
                    byte* p11 = sp + y1 * sStride + x1 * 3;
                    for (int c = 0; c < 3; c++)
                    {
                        double top = p00[c] + (p10[c] - p00[c]) * fx;
                        double bot = p01[c] + (p11[c] - p01[c]) * fx;
                        double val = top + (bot - top) * fy;
                        q[c] = (byte)(val < 0 ? 0 : (val > 255 ? 255 : val + 0.5));
                    }
                }
            }
            }
            Bitmap dst = BitmapBridge.BufferToImage(dbuf);
            return dst;
        }

        static double Dist(PointF a, PointF b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        // 4 点对应求单应矩阵（8 个自由度），高斯主元消元
        static double[] SolveHomography(double x1, double y1, double u1, double v1,
                                        double x2, double y2, double u2, double v2,
                                        double x3, double y3, double u3, double v3,
                                        double x4, double y4, double u4, double v4)
        {
            double[,] A = new double[8, 9];
            double[,] pt = { { x1, y1, u1, v1 }, { x2, y2, u2, v2 }, { x3, y3, u3, v3 }, { x4, y4, u4, v4 } };
            for (int i = 0; i < 4; i++)
            {
                double x = pt[i, 0], y = pt[i, 1], u = pt[i, 2], v = pt[i, 3];
                int r = i * 2;
                A[r, 0] = x; A[r, 1] = y; A[r, 2] = 1; A[r, 3] = 0; A[r, 4] = 0; A[r, 5] = 0;
                A[r, 6] = -u * x; A[r, 7] = -u * y; A[r, 8] = u;
                r++;
                A[r, 0] = 0; A[r, 1] = 0; A[r, 2] = 0; A[r, 3] = x; A[r, 4] = y; A[r, 5] = 1;
                A[r, 6] = -v * x; A[r, 7] = -v * y; A[r, 8] = v;
            }
            for (int col = 0; col < 8; col++)
            {
                int piv = col;
                for (int row = col + 1; row < 8; row++)
                    if (Math.Abs(A[row, col]) > Math.Abs(A[piv, col])) piv = row;
                if (piv != col) { for (int c = col; c < 9; c++) { double t = A[col, c]; A[col, c] = A[piv, c]; A[piv, c] = t; } }
                if (Math.Abs(A[col, col]) < 1e-12) A[col, col] = 1e-12;   // 退化四边形兜底，避免除零
                for (int row = 0; row < 8; row++)
                {
                    if (row == col) continue;
                    double f = A[row, col] / A[col, col];
                    if (f == 0) continue;
                    for (int c = col; c < 9; c++) A[row, c] -= f * A[col, c];
                }
            }
            double[] hh = new double[8];
            for (int i = 0; i < 8; i++) hh[i] = A[i, 8] / A[i, i];
            return hh;
        }

        // ---------------- 亮度直方图 ----------------

        // 传缩小图进来（几百像素宽），几毫秒算完；检查器 300ms 刷新毫无压力
        // 注意：Ensure24bpp 转换时会释放源图并返回新对象，本方法对"转换出的新对象"自行释放
        public static unsafe long[] LuminanceHistogram(Bitmap src)
        {
            long[] hist = new long[256];
            if (src == null || !Imaging.Alive(src)) return hist;
            Bitmap bmp;
            bool ownsBmp = false;
            try
            {
                if (src.PixelFormat == PixelFormat.Format24bppRgb) bmp = src;
                else { bmp = Imaging.Ensure24bpp(src); ownsBmp = true; }
            }
            catch { return hist; }
            try
            {
                PixelBuffer buf = BitmapBridge.ToBuffer(bmp);
                fixed (byte* basePtr = buf.Data)
                {
                    for (int y = 0; y < bmp.Height; y++)
                    {
                        byte* row = basePtr + y * buf.Stride;
                        for (int x = 0; x < bmp.Width; x++)
                        {
                            byte* p = row + x * 3;
                            hist[(p[2] * 77 + p[1] * 150 + p[0] * 29) >> 8]++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // 静默吞掉会让直方图空白且无从排查：记一条 Warn
                try { Diag.Warn("直方图", "LockBits 失败：" + ex.Message); } catch { }
            }
            if (ownsBmp) bmp.Dispose();
            return hist;    // 传入的 src 仍归调用方管理
        }
    }

    // 直方图小控件：调色页顶部一眼看出"黑场压没压下去、白场顶没顶上去"
    public class HistogramView : Control
    {
        long[] data;

        public HistogramView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Panel;
            TabStop = false;
        }

        public long[] Data
        {
            get { return data; }
            set { data = value; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Theme.FillRound(g, r, Theme.Radius, Theme.WindowBg);
            Theme.StrokeRound(g, r, Theme.Radius, Theme.Border, 1f);
            if (data == null || data.Length != 256)
            {
                Theme.DrawText(g, "直方图（打开图片后显示）", Theme.F11,
                    new Rectangle(8, 0, Width - 16, Height), Theme.TextMuted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                return;
            }
            // 裁掉两端极值再取峰值，避免一两个白点/黑点把坐标系压扁
            long max = 1;
            for (int i = 1; i < 255; i++) if (data[i] > max) max = data[i];
            int pad = 4;
            int bw = (Width - pad * 2) / 256;
            if (bw < 1) bw = 1;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(150, 130, 150, 170)))
            {
                for (int i = 0; i < 256; i++)
                {
                    int bh = (int)((double)data[i] / max * (Height - pad * 2 - 4));
                    if (bh < 0) bh = 0;
                    if (bh > 0)
                        g.FillRectangle(b, pad + i * bw, Height - pad - bh, bw, bh);
                }
            }
            // 0/255 参考线：色阶有没有压到位看这两条
            using (Pen p = new Pen(Color.FromArgb(70, 0, 0, 0), 1f))
            {
                g.DrawLine(p, pad, Height - pad, Width - pad, Height - pad);
            }
        }
    }

    // ================= 摩尔纹去除 =================
    //
    // 【横纹的真相】实测样本定案：用户看到的"横纹"不是扫描行噪声、不是折痕，
    //   是**印刷半调网点与扫描栅格的摩尔纹** —— 竖直方向 24~25 行周期的波浪状细纹，
    //   铺满整页（含空白纸面），灰度图上只有几级灰度、二值化后被保留成黑线。
    //   印刷面（奇数页）重、背面（偶数页）轻 —— 扫描打分呈奇偶分化即此因。
    //
    // 【方案】竖直方向 FFT 陷波：每列一个竖直序列，检测显著周期 p（12~80 行），
    //   把 p±4 行的窄带增益压到 0.15（全删会振铃），逆变换回像素域；亮度的修正量 Δ
    //   逐像素加回 RGB 三通道 —— 与"光照归一化增益三通道相同"同一哲学：只调亮度不动色相。
    //   文字笔画/表格线是宽带的孤立结构，窄带衰减几乎无伤（Python 原型已验证：
    //   tools/_ridge_notch.py，二值化对比效果图 _ridge-proto\notch_*.jpg）。
    //
    // 【经济性】先在 ≤64 个采样列上做周期发现，不显著**直接跳过**（干净页零开销，
    //   只花一次检测的十几毫秒）；只有确认有纹的页才全分辨率逐列陷波（列分块并行）。
    //
    // 【并发】列与列完全独立（各自读自己的竖直条带、写回各自字节），Parallel.For
    //   按列分块并行 —— 无共享可变状态，页内并行与 P2 的页间并行互不干扰。
    public static class MoireFilter
    {
        const double BandLoDef = 12.0, BandHiDef = 80.0;   // 检测搜索的周期范围（行）
        const double KeepGain = 0.15;                       // 带内保留增益（全删会振铃）
        const double NotchHalf = 4.0;                       // 陷波半宽（周期 ±4 行）

        // 检测 + 处理。返回 true = 检测到摩尔纹并已陷波（periodOut = 主峰周期，行）；
        // false = 无显著竖直周期纹（图像原样不动，调用方零成本跳过）。
        public static bool RemoveInPlace(Bitmap page, out double periodOut)
        {
            return RemoveInPlace(page, out periodOut, true);
        }

        // ★（P2-6）：allowInnerParallel —— 页内逐列陷波是否再开页内并行。
        //   批量归档走 P2 页间并行时传 false（页与页已经并行，页内再并行 = 线程数相乘，
        //   在 4 核机上互相抢占）。列与列本来完全独立，串行只是慢一点、CPU 总量不变。
        public static bool RemoveInPlace(Bitmap page, out double periodOut, bool allowInnerParallel)
        {
            periodOut = 0;
            PixelBuffer buf = BitmapBridge.ToBuffer(page);
            int w = buf.Width, h = buf.Height, stride = buf.Stride;
            byte[] data = buf.Data;
            if (h < 128 || w < 64) return false;            // 太小的图没有"周期"可言

            double p;
            if (!Detect(data, stride, w, h, out p)) return false;
            periodOut = p;

            int N = 1; while (N < h) N <<= 1;
            int[] rev = BitRev(N);
            double lo = Math.Max(6.0, p - NotchHalf), hi = p + NotchHalf;

            int chunk = 64;                                  // 每块 64 列：缓冲复用、GC 友好
            int nChunks = (w + chunk - 1) / chunk;
            System.Action<int> notchChunk = delegate(int ci)
            {
                float[] re = new float[N], im = new float[N];
                int x0 = ci * chunk, x1 = Math.Min(w, x0 + chunk);
                for (int x = x0; x < x1; x++)
                {
                    // 载入一列（Y = 三通道均值；摩尔纹在亮度上，RGB 同加修正量即可）
                    for (int y = 0; y < h; y++)
                    {
                        int o = y * stride + x * 3;
                        re[y] = (data[o] + data[o + 1] + data[o + 2]) * (1f / 3f);
                        im[y] = 0;
                    }
                    for (int y = h; y < N; y++) { re[y] = 0; im[y] = 0; }

                    Fft(re, im, rev, false);
                    for (int f = 1; f <= N / 2; f++)
                    {
                        double period = (double)N / f;
                        if (period < lo || period > hi) continue;
                        re[f] *= (float)KeepGain; im[f] *= (float)KeepGain;
                        re[N - f] *= (float)KeepGain; im[N - f] *= (float)KeepGain;
                    }
                    Fft(re, im, rev, true);

                    // Δ = 陷波后 Y − 原 Y，三通道同加（clip 防过冲）
                    for (int y = 0; y < h; y++)
                    {
                        int o = y * stride + x * 3;
                        float d = re[y] - (data[o] + data[o + 1] + data[o + 2]) * (1f / 3f);
                        for (int cc = 0; cc < 3; cc++)
                        {
                            float v = data[o + cc] + d;
                            data[o + cc] = (byte)(v < 0 ? 0 : (v > 255 ? 255 : v));
                        }
                    }
                }
            };
            // 页间已在并行时页内退化串行（见方法头注释）；否则列分块并行。
            if (allowInnerParallel)
                System.Threading.Tasks.Parallel.For(0, nChunks, notchChunk);
            else
                for (int ci = 0; ci < nChunks; ci++) notchChunk(ci);

            BitmapBridge.WriteBack(page, buf);
            return true;
        }

        // 周期发现：≤64 个采样列各做一次补零 FFT，对平均谱在 12~80 行范围找主峰。
        // 显著性 = 主峰 ±1 bin 的能量 / 带内平均 bin 能量 —— 摩尔纹能量集中在极窄的
        // 周期上（比值大），文字/表格内容在带内平铺（比值小）。阈值 4.0 由
        // 00019/00021/00031（真纹）vs 00054/00002（干净）实测标定。
        static bool Detect(byte[] data, int stride, int w, int h, out double periodOut)
        {
            periodOut = 0;
            int N = 1; while (N < h) N <<= 1;
            int cols = Math.Min(64, w);
            int[] rev = BitRev(N);
            float[] re = new float[N], im = new float[N];
            double[] spec = new double[N / 2 + 1];
            for (int c = 0; c < cols; c++)
            {
                int x = (int)((c + 0.5) * w / cols); if (x >= w) x = w - 1;
                for (int y = 0; y < h; y++)
                {
                    int o = y * stride + x * 3;
                    re[y] = (data[o] + data[o + 1] + data[o + 2]) * (1f / 3f);
                    im[y] = 0;
                }
                for (int y = h; y < N; y++) { re[y] = 0; im[y] = 0; }
                Fft(re, im, rev, false);
                for (int f = 1; f <= N / 2; f++) spec[f] += Math.Sqrt(re[f] * (double)re[f] + im[f] * (double)im[f]);
            }
            double best = 0; int bestF = -1; double bandSum = 0; int bandBins = 0;
            for (int f = 1; f <= N / 2; f++)
            {
                double p = (double)N / f;
                if (p < BandLoDef || p > BandHiDef) continue;
                bandSum += spec[f]; bandBins++;
                if (spec[f] > best) { best = spec[f]; bestF = f; }
            }
            if (bestF < 0 || bandBins == 0) return false;
            double peakE = spec[bestF];
            if (bestF > 1) peakE += spec[bestF - 1];
            if (bestF < N / 2) peakE += spec[bestF + 1];
            double ratio = peakE / (bandSum / bandBins * 3.0 + 1e-9);
            if (ratio < 4.0) return false;
            periodOut = (double)N / bestF;
            Diag.Info("摩尔纹", string.Format("检测: 周期 {0:0.0} 行, 显著度 {1:0.0}", periodOut, ratio));
            return true;
        }

        // 迭代 radix-2 FFT（in-place）。inverse=true 时含 1/N 归一化。
        // twiddle 用逐级递推（float 精度对 8bit 图像足够；自检用例盯住精度）。
        static void Fft(float[] re, float[] im, int[] rev, bool inverse)
        {
            int n = rev.Length;
            for (int i = 0; i < n; i++)
            {
                int j = rev[i];
                if (j > i)
                {
                    float t = re[i]; re[i] = re[j]; re[j] = t;
                    t = im[i]; im[i] = im[j]; im[j] = t;
                }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = 2.0 * Math.PI / len * (inverse ? 1.0 : -1.0);
                float wr = (float)Math.Cos(ang), wi = (float)Math.Sin(ang);
                int half = len >> 1;
                for (int i = 0; i < n; i += len)
                {
                    float cr = 1f, ci = 0f;
                    for (int k = 0; k < half; k++)
                    {
                        int a = i + k, b = a + half;
                        float tr = re[b] * cr - im[b] * ci;
                        float ti = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - tr; im[b] = im[a] - ti;
                        re[a] += tr; im[a] += ti;
                        float ncr = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr;
                        cr = ncr;
                    }
                }
            }
            if (inverse)
            {
                float s = 1f / n;
                for (int i = 0; i < n; i++) { re[i] *= s; im[i] *= s; }
            }
        }

        static int[] BitRev(int n)
        {
            int bits = 0; while ((1 << bits) < n) bits++;
            int[] rev = new int[n];
            for (int i = 0; i < n; i++)
            {
                int x = i, r = 0;
                for (int b = 0; b < bits; b++) { r = (r << 1) | (x & 1); x >>= 1; }
                rev[i] = r;
            }
            return rev;
        }
    }
}
