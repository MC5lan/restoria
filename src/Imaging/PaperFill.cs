// PaperFill.cs — 纸面重建：把涂抹区换成"同一张纸的干净纸面"
//
// 为什么需要它（实测对照）：
//   档案扫描件的去除场景（霉斑/水渍/折痕/印迹）要的不是"编一个看起来合理的内容"，
//   而是"还这一片纸它本来的样子"。生成式模型在这个场景下各有一处硬伤：
//     · AOT-GAN（Qualcomm/CelebA 权重）：在洞里幻觉出人脸 —— 完全不可用（坏点 34%）
//     · LaMa：色调偏灰绿，大面积洞上一眼可见（低通误差 6.26）；且 ORT 1.14 跑不动它
//     · MI-GAN：指标与纸面重建相当，但失效方式不同 —— 它能把表格线这类长直线延伸出去
//       （这是它的长处），也会在洞里留灰色的块状糊斑（这是它的短处）
//   纸面重建的取舍很清楚：**不含任何生成，因此永远不会编出内容**；在纯纸面上比
//   生成模型干净（坏点 7.97% vs 14.38%），代价是被抹掉的长直线不会自己回来。
//   两者的关系是互补，不是替代 —— 界面上并列给用户选。
//
// 算法四步（每一步都有实测依据）：
//   1. 低频背景 B：分块估计纸面水平，**两遍** —— 第一遍"丢最暗 45% 再平均"，
//      第二遍只采信与初值接近的像素（真纸面）。污渍的淡晕落在掩膜之外，只用一遍的话
//      洞口那圈分块会被它拖暗，洞里就跟着暗一截（实测差 7 级，肉眼是一圈可见的盘）。
//   2. 纸纹 T：从洞外的平坦纸面量高频残差，**按经验分布重采样**填进洞里 ——
//      补出来的颗粒直方图与真纸一致，而不是另造一种噪声（否则洞口会显出"质地不一样"）。
//   3. 接缝平差 D：边界条件取"环上纸面像素的真实残差 f-B"，解调和方程摊进洞里。
//      背景若有系统性偏差，这一步会把它拉平，保证洞口不出现亮度台阶。
//      注意：边界数据必须是真实残差；早期版本用随机纸纹当边界，那等于零均值噪声，
//      解出来什么都修不了 —— 别改回去。
//   4. 掩膜先外扩再羽化合成：污渍的淡晕在掩膜外，不外扩就会在合成时按比例被放回来。
//      外扩量按污渍尺寸成比例（基础 6px + 6%，上限 20px）—— 实测低通误差 6.89 → 5.16。
using System;
using System.Drawing;
using System.Text;
using System.Drawing.Imaging;

namespace PicTool
{
    public static class PaperFill
    {
        // ---- 可调参数（默认值都是实测选出来的）----
        public const int BlockSize = 64;        // 背景分块边长
        public const double TrimRatio = 0.45;   // 第一遍每块丢掉最暗的比例
        public const int FlatTol = 8;           // "这块像素算纸面"的容差（与背景估计的差）
        public const int BaseGrow = 6;          // 掩膜外扩的基础量
        public const double GrowRatio = 0.06;   // 再按污渍短边成比例外扩
        public const int GrowCap = 20;          // 比例外扩的上限
        public const int FeatherPx = 5;         // 合成羽化
        public const int CoarseStep = 8;        // 接缝平差的粗网格倍数
        public const int RefineIters = 30;      // 原尺度细化次数
        public const int MinSamples = 200;      // 纸纹池最少样本数，少于它就不加纹理

        // ---- 直线续接（第 4 步）的参数 ----
        public static bool ContinueLines = true;   // 关掉就退回"只还纸面"的行为
        public const float InkDrop = 45f;          // 比局部纸面暗这么多才算"墨"
        public const double LineCover = 0.55;      // 该行/列已知部分至少这么密才算线
        public const int LineMaxGaps = 6;          // 最多允许几个"断口"：表格线只有洞这一个断口，文字行会有一堆
        public const int LineMaxThick = 8;         // 只认细线；更粗的当图形，不碰
        public static int LastLinesContinued;      // 诊断：上一次续接了几条线
        public static string LastLinesInfo;        // 诊断：每一条的名字/颜色/粗细

        public static int GrowFor(Rectangle maskBounds)
        {
            int side = Math.Min(maskBounds.Width, maskBounds.Height);
            return BaseGrow + Math.Min(GrowCap, (int)(side * GrowRatio));
        }

        // 诊断入口（命令行 --paper-fill <输入图> <输出图> [x y w h]）：
        // 造一块矩形掩膜跑真实管线并落盘，用于离线比对画质与耗时
        public static string FillForTest(string inPath, string outPath, string[] argv)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            try
            {
                Bitmap src = Imaging.Ensure24bpp(new Bitmap(inPath));
                int w = src.Width, h = src.Height;
                int x = w / 2 - Math.Max(8, w / 8), y = h / 2 - Math.Max(8, h / 8);
                int bw = Math.Max(16, w / 4), bh = Math.Max(16, h / 4);
                if (argv != null && argv.Length >= 7)
                {
                    x = int.Parse(argv[3]); y = int.Parse(argv[4]);
                    bw = int.Parse(argv[5]); bh = int.Parse(argv[6]);
                }
                sb.AppendLine("输入 : " + inPath + "  " + w + "x" + h);
                sb.AppendLine("涂抹区 : " + x + "," + y + " " + bw + "x" + bh);

                Bitmap mask = Imaging.MakeMask(w, h);
                {
                    // 8bpp 索引格式不能建 Graphics：缓冲里写 255 再转位图
                    PixelBuffer mbuf = new PixelBuffer(w, h, 1);
                    for (int yy = y; yy < y + bh && yy < h; yy++)
                    {
                        if (yy < 0) continue;
                        int ro = yy * mbuf.Stride;
                        for (int xx = x; xx < x + bw && xx < w; xx++)
                            if (xx >= 0) mbuf.Data[ro + xx] = 255;
                    }
                    Bitmap nm = BitmapBridge.BufferToMask(mbuf);
                    mask.Dispose();
                    mask = nm;
                }
                sb.AppendLine("外扩量 : " + GrowFor(Imaging.MaskBounds(mask)) + " px");

                DateTime t0 = DateTime.Now;
                Bitmap res = Run(src, mask, null);
                double ms = (DateTime.Now - t0).TotalMilliseconds;
                res.Save(outPath, ImageFormat.Png);
                sb.AppendLine("直线续接 : " + (ContinueLines ? "开" : "关")
                    + "  →  认出 " + LastLinesContinued + " 条"
                    + (LastLinesInfo == null ? "（未发现横穿涂抹区的长直线）" : "，" + LastLinesInfo));
                sb.AppendLine("输出 : " + outPath);
                sb.AppendLine("耗时 : " + (int)ms + " ms");
                if (res != null) res.Dispose();
                src.Dispose();
                mask.Dispose();
            }
            catch (Exception ex)
            {
                sb.AppendLine("失败 : " + ex.Message);
            }
            return sb.ToString();
        }

        public static Bitmap Run(Bitmap src, Bitmap mask, ProgressHandler progress)
        {
            if (src == null) return null;
            Bitmap result = Imaging.Ensure24bpp(new Bitmap(src));
            if (mask == null) return result;

            Rectangle mb = Imaging.MaskBounds(mask);
            if (mb.Width <= 0 || mb.Height <= 0) return result;

            int grow = GrowFor(mb);
            // ROI 要装下三样东西：外扩后的洞、背景估计用的环、纸纹取样用的环
            int margin = Math.Max(BlockSize * 3, 96) + grow;
            Rectangle roi = Inflate(mb, margin);
            roi.Intersect(new Rectangle(0, 0, src.Width, src.Height));
            if (roi.Width < 8 || roi.Height < 8) return result;

            int w = roi.Width, h = roi.Height;
            float[] f = new float[w * h * 3];
            ReadRgb(result, roi, w, h, f);
            bool[] hole = ReadHole(mask, roi, w, h);
            if (progress != null) progress(10);

            Dilate(hole, w, h, grow);
            if (progress != null) progress(20);

            float[] B = CoarseBg(f, hole, w, h, null);
            B = CoarseBg(f, hole, w, h, B);
            if (progress != null) progress(45);

            // 纸纹池：洞外、且接近背景（真纸面）的残差
            float[] pool;
            int poolN;
            bool[] flat;
            BuildPool(f, B, hole, w, h, out pool, out poolN, out flat);
            if (progress != null) progress(60);

            // 纸纹 + 接缝平差
            float[] baseFil = new float[w * h * 3];
            float[] texMean = new float[3];
            if (poolN >= MinSamples)
            {
                for (int c = 0; c < 3; c++)
                {
                    double s = 0;
                    for (int i = 0; i < poolN; i++) s += pool[i * 3 + c];
                    texMean[c] = (float)(s / poolN);
                }
                uint seed = 0x2545F491;
                for (int i = 0; i < w * h; i++)
                {
                    if (!hole[i]) continue;
                    int pick = (int)(NextRand(ref seed) % (uint)poolN);
                    for (int c = 0; c < 3; c++) baseFil[i * 3 + c] = pool[pick * 3 + c] - texMean[c];
                }
            }
            if (progress != null) progress(70);

            float[] D = SeamFix(flat, f, B, w, h);
            if (progress != null) progress(90);

            // 填充结果单独放一个缓冲：f 必须保持"原图"不动 ——
            // 直线续接要先从原图里认出穿过洞的表格线，把 f 覆盖掉就等于把要认的东西擦了（踩过这个坑）。
            float[] fillBuf = new float[w * h * 3];
            for (int i = 0; i < w * h * 3; i++) fillBuf[i] = Clamp(B[i] + baseFil[i] + D[i]);

            // ④ 直线续接：把穿过涂抹区的长直线（表格线/边框/装订线）补回去。
            //    纸面重建是"还一片干净纸"，它不会自己把被抹掉的表格线接回来 —— 这是它相对
            //    MI-GAN 唯一的短板（S1/S2 的肉眼瑕疵主要就是断掉的表格线）。做法完全确定性：
            //    只在"同一行/列在洞的两侧都有连续暗像素"时才认定是直线，颜色取已知部分的中位色，
            //    绝不凭想象编结构。识别不出直线就什么都不画（宁可留白，也不画错）。
            bool[] lineMask = new bool[w * h];
            if (ContinueLines)
            {
                float[] lineColor = new float[w * h * 3];
                LastLinesContinued = DetectLines(f, B, hole, w, h, lineMask, lineColor, out LastLinesInfo);
                for (int i = 0; i < w * h; i++)
                    if (lineMask[i])
                        for (int c = 0; c < 3; c++) fillBuf[i * 3 + c] = lineColor[i * 3 + c];
            }
            if (progress != null) progress(95);

            // 🔴 填充结果自检（实战教训，实测样本 0017/0035/0051）：
            //    纸面重建的产物必须和周围纸面一样亮 —— 它的职责就是"还纸面"。
            //    若洞内填充均值比洞外纸面**显著更暗**，说明背景估计/接缝平差的
            //    边界条件被洞边的深色内容（黑条/墨迹/装订孔）拖黑了，这次重建
            //    已经失败 —— 硬填进去就是大片黑印子（实测：一条淡阴影+黑条被
            //    填成 1/3 页黑块）。此时宁可不填、保留原样（淡阴影交给光照
            //    归一化处理，实测那条路是干净的），也绝不输出黑块。
            //    直线续接画的黑线不算"变暗"（那是内容），统计时剔除。
            {
                double ringSum = 0; long ringN = 0;
                for (int i = 0; i < w * h; i++)
                {
                    if (!flat[i]) continue;
                    ringSum += (f[i * 3] + f[i * 3 + 1] + f[i * 3 + 2]) / 3f;
                    ringN++;
                }
                if (ringN >= 100)
                {
                    double holeSum = 0; long holeN = 0;
                    for (int i = 0; i < w * h; i++)
                    {
                        if (!hole[i] || lineMask[i]) continue;
                        holeSum += (fillBuf[i * 3] + fillBuf[i * 3 + 1] + fillBuf[i * 3 + 2]) / 3f;
                        holeN++;
                    }
                    if (holeN > 0)
                    {
                        double ringMean = ringSum / ringN, holeMean = holeSum / holeN;
                        if (holeMean < ringMean - 20)
                        {
                            Diag.Info("PaperFill", string.Format(
                                "重建自检失败：洞内均值 {0:0} 比纸面 {1:0} 暗 {2:0} 级 → 放弃填充，保留原样",
                                holeMean, ringMean, ringMean - holeMean));
                            return result;
                        }
                    }
                }
            }

            // 羽化合成：只换掉洞里的像素，洞外一个字节都不动（就地修改并返回 result）
            Blend(result, fillBuf, hole, roi, w, h);
            if (progress != null) progress(100);
            return result;
        }

        // ---------------- 基础工具 ----------------

        static Rectangle Inflate(Rectangle r, int m)
        {
            return new Rectangle(r.X - m, r.Y - m, r.Width + 2 * m, r.Height + 2 * m);
        }

        static float Clamp(float v)
        {
            return v < 0f ? 0f : (v > 255f ? 255f : v);
        }

        static int ClampI(int v, int lo, int hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        // 确定性伪随机（不能用 System.Random：它在不同 .NET 版本上序列不同，会让结果不可复现）
        static uint NextRand(ref uint s)
        {
            s ^= s << 13; s ^= s >> 17; s ^= s << 5;
            return s;
        }

        static void ReadRgb(Bitmap bmp, Rectangle roi, int w, int h, float[] dst)
        {
            PixelBuffer buf = BitmapBridge.ToBuffer(bmp);
            for (int y = 0; y < h; y++)
            {
                int ro = (roi.Y + y) * buf.Stride + roi.X * 3;
                int o = y * w * 3;
                for (int x = 0; x < w; x++)
                {
                    int q = ro + x * 3;
                    dst[o] = buf.Data[q];          // B
                    dst[o + 1] = buf.Data[q + 1];  // G
                    dst[o + 2] = buf.Data[q + 2];  // R
                    o += 3;
                }
            }
        }

        static bool[] ReadHole(Bitmap mask, Rectangle roi, int w, int h)
        {
            bool[] hole = new bool[w * h];
            PixelBuffer buf = BitmapBridge.MaskToBuffer(mask);
            for (int y = 0; y < h; y++)
            {
                int ro = (roi.Y + y) * buf.Stride;
                int o = y * w;
                for (int x = 0; x < w; x++)
                    hole[o + x] = buf.Data[ro + roi.X + x] >= 128;
            }
            return hole;
        }

        // 可分离的方形膨胀（先横后纵），复杂度 O(n*r)，大半径时比二维卷积快一个量级
        static void Dilate(bool[] m, int w, int h, int r)
        {
            if (r <= 0) return;
            bool[] src = (bool[])m.Clone();
            for (int y = 0; y < h; y++)
            {
                int o = y * w;
                for (int x = 0; x < w; x++)
                {
                    if (src[o + x]) { m[o + x] = true; continue; }
                    int x0 = Math.Max(0, x - r), x1 = Math.Min(w - 1, x + r);
                    bool hit = false;
                    for (int k = x0; k <= x1; k++)
                        if (src[o + k]) { hit = true; break; }
                    m[o + x] = hit;
                }
            }
            Array.Copy(m, src, m.Length);
            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++)
                {
                    if (src[y * w + x]) continue;
                    int y0 = Math.Max(0, y - r), y1 = Math.Min(h - 1, y + r);
                    bool hit = false;
                    for (int k = y0; k <= y1; k++)
                        if (src[k * w + x]) { hit = true; break; }
                    m[y * w + x] = hit;
                }
            }
        }

        // ---------------- 1) 低频背景 ----------------

        // guide == null：第一遍，每块丢最暗 TrimRatio 再平均
        // guide != null：第二遍，只采信 |f-guide| 的通道均值 < FlatTol 的像素（真纸面）
        static float[] CoarseBg(float[] f, bool[] hole, int w, int h, float[] guide)
        {
            int bw = (w + BlockSize - 1) / BlockSize;
            int bh = (h + BlockSize - 1) / BlockSize;
            float[] coarse = new float[bw * bh * 3];
            bool[] valid = new bool[bw * bh];
            int[] hist = new int[256];

            for (int by = 0; by < bh; by++)
            {
                int y0 = by * BlockSize, y1 = Math.Min(h, y0 + BlockSize);
                for (int bx = 0; bx < bw; bx++)
                {
                    int x0 = bx * BlockSize, x1 = Math.Min(w, x0 + BlockSize);
                    int total = (y1 - y0) * (x1 - x0);
                    int known = 0;
                    for (int y = y0; y < y1; y++)
                        for (int x = x0; x < x1; x++)
                            if (!hole[y * w + x]) known++;
                    if (known < Math.Max(6, (int)(0.12 * total))) continue;

                    int need = (guide == null) ? 4 : 12;
                    float sr = 0, sg = 0, sb = 0;
                    int cnt = 0;

                    if (guide == null)
                    {
                        // 亮度直方图 → 找出"丢最暗 TrimRatio"的阈值
                        for (int i = 0; i < 256; i++) hist[i] = 0;
                        for (int y = y0; y < y1; y++)
                            for (int x = x0; x < x1; x++)
                            {
                                int i = y * w + x;
                                if (hole[i]) continue;
                                int lum = (int)((f[i * 3] * 29 + f[i * 3 + 1] * 150 + f[i * 3 + 2] * 77) / 256f);
                                hist[ClampI(lum, 0, 255)]++;
                            }
                        int target = (int)(known * TrimRatio);
                        int acc = 0, cut = 0;
                        for (int i = 0; i < 256; i++)
                        {
                            acc += hist[i];
                            if (acc >= target) { cut = i; break; }
                        }
                        for (int y = y0; y < y1; y++)
                            for (int x = x0; x < x1; x++)
                            {
                                int i = y * w + x;
                                if (hole[i]) continue;
                                int lum = (int)((f[i * 3] * 29 + f[i * 3 + 1] * 150 + f[i * 3 + 2] * 77) / 256f);
                                if (ClampI(lum, 0, 255) < cut) continue;
                                sb += f[i * 3]; sg += f[i * 3 + 1]; sr += f[i * 3 + 2]; cnt++;
                            }
                    }
                    else
                    {
                        for (int y = y0; y < y1; y++)
                            for (int x = x0; x < x1; x++)
                            {
                                int i = y * w + x;
                                if (hole[i]) continue;
                                float d = (Math.Abs(f[i * 3] - guide[i * 3])
                                         + Math.Abs(f[i * 3 + 1] - guide[i * 3 + 1])
                                         + Math.Abs(f[i * 3 + 2] - guide[i * 3 + 2])) / 3f;
                                if (d >= FlatTol) continue;
                                sb += f[i * 3]; sg += f[i * 3 + 1]; sr += f[i * 3 + 2]; cnt++;
                            }
                    }

                    if (cnt < need) continue;
                    int ci = (by * bw + bx) * 3;
                    coarse[ci] = sb / cnt;
                    coarse[ci + 1] = sg / cnt;
                    coarse[ci + 2] = sr / cnt;
                    valid[by * bw + bx] = true;
                }
            }

            if (AllInvalid(valid))
                return Flood(f, hole, w, h);

            // 空洞块：用邻域已知块迭代互补（6 轮足够覆盖常见的洞）
            for (int pass = 0; pass < 6; pass++)
            {
                for (int by = 0; by < bh; by++)
                    for (int bx = 0; bx < bw; bx++)
                    {
                        if (valid[by * bw + bx]) continue;
                        float sb = 0, sg = 0, sr = 0; int n = 0;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int ny = by + dy, nx = bx + dx;
                                if (ny < 0 || nx < 0 || ny >= bh || nx >= bw) continue;
                                if (!valid[ny * bw + nx]) continue;
                                int ci = (ny * bw + nx) * 3;
                                sb += coarse[ci]; sg += coarse[ci + 1]; sr += coarse[ci + 2]; n++;
                            }
                        if (n == 0) continue;
                        int ti = (by * bw + bx) * 3;
                        coarse[ti] = sb / n; coarse[ti + 1] = sg / n; coarse[ti + 2] = sr / n;
                        valid[by * bw + bx] = true;
                    }
            }

            // 粗网格双三次放大到原尺度
            return Upsample(coarse, bw, bh, w, h);
        }

        static bool AllInvalid(bool[] v)
        {
            for (int i = 0; i < v.Length; i++) if (v[i]) return false;
            return true;
        }

        // 全部块都无效（洞大到没有纸面可用）→ 退化：整幅用洞外的纸面均值
        static float[] Flood(float[] f, bool[] hole, int w, int h)
        {
            double sb = 0, sg = 0, sr = 0; long n = 0;
            for (int i = 0; i < w * h; i++)
            {
                if (hole[i]) continue;
                sb += f[i * 3]; sg += f[i * 3 + 1]; sr += f[i * 3 + 2]; n++;
            }
            if (n == 0) { sb = sg = sr = 255; n = 1; }
            float[] B = new float[w * h * 3];
            for (int i = 0; i < w * h; i++)
            {
                B[i * 3] = (float)(sb / n);
                B[i * 3 + 1] = (float)(sg / n);
                B[i * 3 + 2] = (float)(sr / n);
            }
            return B;
        }

        // 粗网格 → 原尺度（双线性；粗网格本身就是低频，够用且快）
        static float[] Upsample(float[] coarse, int bw, int bh, int w, int h)
        {
            float[] B = new float[w * h * 3];
            for (int y = 0; y < h; y++)
            {
                float fy = ((y + 0.5f) * bh / h) - 0.5f;
                int y0 = (int)Math.Floor(fy);
                float ty = fy - y0;
                int y1 = ClampI(y0 + 1, 0, bh - 1);
                y0 = ClampI(y0, 0, bh - 1);
                for (int x = 0; x < w; x++)
                {
                    float fx = ((x + 0.5f) * bw / w) - 0.5f;
                    int x0 = (int)Math.Floor(fx);
                    float tx = fx - x0;
                    int x1 = ClampI(x0 + 1, 0, bw - 1);
                    x0 = ClampI(x0, 0, bw - 1);
                    int i00 = (y0 * bw + x0) * 3, i01 = (y0 * bw + x1) * 3;
                    int i10 = (y1 * bw + x0) * 3, i11 = (y1 * bw + x1) * 3;
                    int o = (y * w + x) * 3;
                    for (int c = 0; c < 3; c++)
                    {
                        float a = coarse[i00 + c] + (coarse[i01 + c] - coarse[i00 + c]) * tx;
                        float b = coarse[i10 + c] + (coarse[i11 + c] - coarse[i10 + c]) * tx;
                        B[o + c] = a + (b - a) * ty;
                    }
                }
            }
            return B;
        }

        // ---------------- 2) 纸纹池 ----------------

        static void BuildPool(float[] f, float[] B, bool[] hole, int w, int h,
                              out float[] pool, out int poolN, out bool[] flat)
        {
            flat = new bool[w * h];
            int cap = Math.Min(w * h, 400000);
            pool = new float[cap * 3];
            poolN = 0;
            for (int i = 0; i < w * h; i++)
            {
                if (hole[i]) continue;
                float d = (Math.Abs(f[i * 3] - B[i * 3])
                         + Math.Abs(f[i * 3 + 1] - B[i * 3 + 1])
                         + Math.Abs(f[i * 3 + 2] - B[i * 3 + 2])) / 3f;
                if (d >= FlatTol) continue;
                flat[i] = true;
                if (poolN < cap)
                {
                    pool[poolN * 3] = f[i * 3] - B[i * 3];
                    pool[poolN * 3 + 1] = f[i * 3 + 1] - B[i * 3 + 1];
                    pool[poolN * 3 + 2] = f[i * 3 + 2] - B[i * 3 + 2];
                    poolN++;
                }
            }
            if (poolN < MinSamples && poolN > 0)      // 纸面太少：退回全部洞外像素的残差
            {
                poolN = 0;
                for (int i = 0; i < w * h; i++)
                {
                    if (hole[i] || poolN >= cap) continue;
                    pool[poolN * 3] = f[i * 3] - B[i * 3];
                    pool[poolN * 3 + 1] = f[i * 3 + 1] - B[i * 3 + 1];
                    pool[poolN * 3 + 2] = f[i * 3 + 2] - B[i * 3 + 2];
                    poolN++;
                }
            }
        }

        // ---------------- 3) 接缝平差 ----------------

        // 以 flat（环上纸面像素）的真实残差 f-B 为边界，洞内解调和方程。
        // 由粗到细：先在 1/CoarseStep 网格上解出大局，放大后再原尺度细化。
        static float[] SeamFix(bool[] flat, float[] f, float[] B, int w, int h)
        {
            float[] D = new float[w * h * 3];
            int fixedCount = 0;
            for (int i = 0; i < w * h; i++) if (flat[i]) fixedCount++;
            if (fixedCount < 50) return D;               // 没有可用的边界，不做平差

            int cw = Math.Max(2, w / CoarseStep), ch = Math.Max(2, h / CoarseStep);
            bool[] cfix = new bool[cw * ch];
            float[] ctar = new float[cw * ch * 3];
            int[] cnum = new int[cw * ch];
            float[] csum = new float[cw * ch * 3];
            for (int y = 0; y < h; y++)
            {
                int cy = Math.Min(ch - 1, y * ch / h);
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (!flat[i]) continue;
                    int cx = Math.Min(cw - 1, x * cw / w);
                    int ci = cy * cw + cx;
                    cnum[ci]++;
                    csum[ci * 3] += f[i * 3] - B[i * 3];
                    csum[ci * 3 + 1] += f[i * 3 + 1] - B[i * 3 + 1];
                    csum[ci * 3 + 2] += f[i * 3 + 2] - B[i * 3 + 2];
                }
            }
            bool anyFixed = false, anyFree = false;
            for (int ci = 0; ci < cw * ch; ci++)
            {
                if (cnum[ci] > 0)
                {
                    cfix[ci] = true; anyFixed = true;
                    ctar[ci * 3] = csum[ci * 3] / cnum[ci];
                    ctar[ci * 3 + 1] = csum[ci * 3 + 1] / cnum[ci];
                    ctar[ci * 3 + 2] = csum[ci * 3 + 2] / cnum[ci];
                }
                else anyFree = true;
            }
            if (!anyFixed || !anyFree) return D;

            Solve(cfix, ctar, cw, ch, Math.Max(cw, ch) * 3 / 2 + 30);

            // 放大回原尺度，再以原尺度边界细化
            float[] seed = new float[w * h * 3];
            for (int y = 0; y < h; y++)
            {
                int y0 = Math.Min(ch - 1, y * ch / h);
                for (int x = 0; x < w; x++)
                {
                    int x0 = Math.Min(cw - 1, x * cw / w);
                    int s = (y0 * cw + x0) * 3, o = (y * w + x) * 3;
                    seed[o] = ctar[s]; seed[o + 1] = ctar[s + 1]; seed[o + 2] = ctar[s + 2];
                }
            }
            float[] tgt = new float[w * h * 3];
            for (int i = 0; i < w * h; i++)
            {
                tgt[i * 3] = f[i * 3] - B[i * 3];
                tgt[i * 3 + 1] = f[i * 3 + 1] - B[i * 3 + 1];
                tgt[i * 3 + 2] = f[i * 3 + 2] - B[i * 3 + 2];
            }
            float[] outD = new float[w * h * 3];
            Array.Copy(seed, outD, seed.Length);
            SolveFrom(flat, tgt, outD, w, h, RefineIters);
            return outD;
        }

        // 洞内解调和方程（Jacobi 邻域平均）；fix 处固定为 target
        static void Solve(bool[] fix, float[] target, int w, int h, int iters)
        {
            float[] cur = new float[w * h * 3];
            SolveFrom(fix, target, cur, w, h, iters);
            Array.Copy(cur, target, cur.Length);          // 粗网格结果写回 target 供放大使用
        }

        static void SolveFrom(bool[] fix, float[] target, float[] cur, int w, int h, int iters)
        {
            float[] next = new float[w * h * 3];
            for (int it = 0; it < iters; it++)
            {
                for (int y = 0; y < h; y++)
                {
                    int rowUp = (y > 0 ? y - 1 : y) * w, rowDn = (y < h - 1 ? y + 1 : y) * w, row = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int i = row + x;
                        if (fix[i])
                        {
                            for (int c = 0; c < 3; c++) next[i * 3 + c] = target[i * 3 + c];
                            continue;
                        }
                        int xl = x > 0 ? x - 1 : x, xr = x < w - 1 ? x + 1 : x;
                        int a = (rowUp + x) * 3, b = (rowDn + x) * 3, l = (row + xl) * 3, r = (row + xr) * 3;
                        int n = (y > 0 ? 1 : 0) + (y < h - 1 ? 1 : 0) + (x > 0 ? 1 : 0) + (x < w - 1 ? 1 : 0);
                        float inv = 1f / Math.Max(1, n);
                        for (int c = 0; c < 3; c++)
                            next[i * 3 + c] = (cur[a + c] + cur[b + c] + cur[l + c] + cur[r + c]) * inv;
                    }
                }
                Array.Copy(next, cur, cur.Length);
            }
        }

        // ---------------- 4) 直线续接 ----------------

        // 在洞的左右（或上下）两侧找到"同一条长直线"的痕迹，把洞里那一段按同样的颜色补上。
        //
        // 判定一条水平线的三个条件（缺一不可，宁可漏判也不误画）：
        //   ① 该行在已知（洞外）部分里"墨"的占比 ≥ LineCover
        //   ② 该行在洞的左右两侧都有墨 —— 说明它确实是横穿过去的，不是恰好在洞口结束
        //   ③ 该行的墨最多只有 LineMaxGaps 个断口 —— 表格线只有洞这一个断口，
        //      而一行文字会有几十个断口。这条是把"文字行"和"表格线"分开的关键。
        // 竖直方向同理。颜色取该行/列已知墨像素的中位色（中位，不是均值，避免被抗锯齿边缘带偏）。
        static int DetectLines(float[] f, float[] B, bool[] hole, int w, int h,
                               bool[] lineMask, float[] lineColor, out string info)
        {
            StringBuilder sb = new StringBuilder();
            int count = 0;

            // 洞的包围盒
            int hx0 = w, hx1 = -1, hy0 = h, hy1 = -1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (hole[y * w + x])
                    {
                        if (x < hx0) hx0 = x;
                        if (x > hx1) hx1 = x;
                        if (y < hy0) hy0 = y;
                        if (y > hy1) hy1 = y;
                    }
            if (hx1 < 0) { info = null; return 0; }

            bool[] ink = new bool[w * h];
            int[] rowInk = new int[h], rowKnown = new int[h];
            int[] colInk = new int[w], colKnown = new int[w];
            for (int y = 0; y < h; y++)
            {
                int ro = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = ro + x;
                    if (hole[i]) continue;
                    float paper = (B[i * 3] + B[i * 3 + 1] + B[i * 3 + 2]) / 3f;
                    float lum = (f[i * 3] + f[i * 3 + 1] + f[i * 3 + 2]) / 3f;
                    if (lum < paper - InkDrop) { ink[i] = true; rowInk[y]++; colInk[x]++; }
                    rowKnown[y]++; colKnown[x]++;
                }
            }

            // ---- 水平线 ----
            bool[] rowHit = new bool[h];
            int bestY = -1;
            double bestCov = -1;
            int bestGaps = -1, bestInk = 0;
            bool bestBoth = false;
            for (int y = 0; y < h; y++)
            {
                if (rowKnown[y] < 30) continue;
                double cov = (double)rowInk[y] / rowKnown[y];
                if (cov > bestCov) { bestCov = cov; bestY = y; bestGaps = Gaps(ink, w, h, y, true); bestInk = rowInk[y]; bestBoth = BothSides(ink, w, h, y, hx0, hx1, true); }
                if (cov < LineCover) continue;
                if (!BothSides(ink, w, h, y, hx0, hx1, true)) continue;
                if (Gaps(ink, w, h, y, true) > LineMaxGaps) continue;
                rowHit[y] = true;
            }
            if (bestY >= 0)
                sb.Append("最密行 y=").Append(bestY).Append(" 覆盖率 ").Append(bestCov.ToString("0.00"))
                  .Append(" 墨数 ").Append(bestInk).Append(" 断口 ").Append(bestGaps)
                  .Append(" 两侧都有墨 ").Append(bestBoth ? "是" : "否").Append("；");
            // 只保留"细"的连续行块（厚度 ≤ LineMaxThick），块内行一起续接
            for (int y = 0; y < h; y++)
            {
                if (!rowHit[y]) continue;
                int y2 = y;
                while (y2 + 1 < h && rowHit[y2 + 1]) y2++;
                int thick = y2 - y + 1;
                if (thick <= LineMaxThick)
                {
                    float[] col = MedianColor(f, ink, w, h, y, y2, true);
                    for (int yy = y; yy <= y2; yy++)
                        for (int x = hx0; x <= hx1; x++)
                        {
                            int i = yy * w + x;
                            if (!hole[i]) continue;
                            lineMask[i] = true;
                            lineColor[i * 3] = col[0]; lineColor[i * 3 + 1] = col[1]; lineColor[i * 3 + 2] = col[2];
                        }
                    count++;
                    if (sb.Length < 400) sb.Append("[横 y=").Append(y).Append('-').Append(y2).Append(" x")
                        .Append(hx0).Append('-').Append(hx1).Append("] ");
                }
                y = y2;
            }

            // ---- 竖直线 ----
            bool[] colHit = new bool[w];
            for (int x = 0; x < w; x++)
            {
                if (colKnown[x] < 30 || colInk[x] < LineCover * colKnown[x]) continue;
                if (!BothSides(ink, w, h, x, hy0, hy1, false)) continue;
                if (Gaps(ink, w, h, x, false) > LineMaxGaps) continue;
                colHit[x] = true;
            }
            for (int x = 0; x < w; x++)
            {
                if (!colHit[x]) continue;
                int x2 = x;
                while (x2 + 1 < w && colHit[x2 + 1]) x2++;
                int thick = x2 - x + 1;
                if (thick <= LineMaxThick)
                {
                    float[] col = MedianColor(f, ink, w, h, x, x2, false);
                    for (int xx = x; xx <= x2; xx++)
                        for (int y = hy0; y <= hy1; y++)
                        {
                            int i = y * w + xx;
                            if (!hole[i]) continue;
                            lineMask[i] = true;
                            lineColor[i * 3] = col[0]; lineColor[i * 3 + 1] = col[1]; lineColor[i * 3 + 2] = col[2];
                        }
                    count++;
                    if (sb.Length < 400) sb.Append("[竖 x=").Append(x).Append('-').Append(x2).Append(" y")
                        .Append(hy0).Append('-').Append(hy1).Append("] ");
                }
                x = x2;
            }

            info = count == 0 ? ("未命中（" + sb.ToString().Trim().TrimEnd((char)0xFF1B) + "）") : (count + " 条：" + sb.ToString().Trim());
            return count;
        }

        // 洞的两侧在同一个 y（或 x）上都必须有墨：只有"进得来又出得去"才算横穿
        static bool BothSides(bool[] ink, int w, int h, int line, int h0, int h1, bool horizontal)
        {
            bool before = false, after = false;
            if (horizontal)
            {
                int ro = line * w;
                for (int x = 0; x < h0; x++) if (ink[ro + x]) { before = true; break; }
                for (int x = h1 + 1; x < w; x++) if (ink[ro + x]) { after = true; break; }
            }
            else
            {
                for (int y = 0; y < h0; y++) if (ink[y * w + line]) { before = true; break; }
                for (int y = h1 + 1; y < h; y++) if (ink[y * w + line]) { after = true; break; }
            }
            return before && after;
        }

        // 该行（或列）已知部分里"墨→非墨"的断口数
        static int Gaps(bool[] ink, int w, int h, int line, bool horizontal)
        {
            int gaps = 0;
            bool prev = false;
            if (horizontal)
            {
                int ro = line * w;
                for (int x = 0; x < w; x++)
                {
                    bool cur = ink[ro + x];
                    if (prev && !cur) gaps++;
                    prev = cur;
                }
            }
            else
            {
                for (int y = 0; y < h; y++)
                {
                    bool cur = ink[y * w + line];
                    if (prev && !cur) gaps++;
                    prev = cur;
                }
            }
            return gaps;
        }

        // 该行/列块内已知墨像素的中位色（B/G/R 三个通道分别取中位）
        static float[] MedianColor(float[] f, bool[] ink, int w, int h, int a, int b, bool horizontal)
        {
            int n = 0;
            if (horizontal)
            {
                for (int y = a; y <= b; y++)
                    for (int x = 0; x < w; x++)
                        if (ink[y * w + x]) n++;
            }
            else
            {
                for (int x = a; x <= b; x++)
                    for (int y = 0; y < h; y++)
                        if (ink[y * w + x]) n++;
            }
            float[] out3 = new float[3];
            if (n == 0) return out3;
            byte[][] buf = new byte[3][];
            for (int c = 0; c < 3; c++) buf[c] = new byte[n];
            int k = 0;
            if (horizontal)
            {
                for (int y = a; y <= b; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        if (!ink[i]) continue;
                        for (int c = 0; c < 3; c++) buf[c][k] = (byte)Clamp(f[i * 3 + c]);
                        k++;
                    }
            }
            else
            {
                for (int x = a; x <= b; x++)
                    for (int y = 0; y < h; y++)
                    {
                        int i = y * w + x;
                        if (!ink[i]) continue;
                        for (int c = 0; c < 3; c++) buf[c][k] = (byte)Clamp(f[i * 3 + c]);
                        k++;
                    }
            }
            for (int c = 0; c < 3; c++)
            {
                Array.Sort(buf[c]);
                out3[c] = buf[c][n / 2];
            }
            return out3;
        }

        // ---------------- 5) 羽化合成 ----------------

        static Bitmap Blend(Bitmap dst, float[] fill, bool[] hole, Rectangle roi, int w, int h)
        {
            // alpha：先按洞的"距离"羽化。用两次盒式模糊近似高斯，避免硬边。
            byte[] a = new byte[w * h];
            for (int i = 0; i < w * h; i++) a[i] = hole[i] ? (byte)255 : (byte)0;
            BoxBlur(a, w, h, FeatherPx);
            BoxBlur(a, w, h, FeatherPx);

            PixelBuffer buf = BitmapBridge.ToBuffer(dst);
            for (int y = 0; y < h; y++)
            {
                int ro = (roi.Y + y) * buf.Stride + roi.X * 3;
                int o = y * w * 3;
                for (int x = 0; x < w; x++)
                {
                    int al = a[y * w + x];
                    if (al == 0) { o += 3; continue; }
                    int q = ro + x * 3;
                    if (al == 255)
                    {
                        buf.Data[q] = (byte)fill[o];
                        buf.Data[q + 1] = (byte)fill[o + 1];
                        buf.Data[q + 2] = (byte)fill[o + 2];
                    }
                    else
                    {
                        int ia = 255 - al;
                        buf.Data[q] = (byte)((buf.Data[q] * ia + fill[o] * al) / 255f);
                        buf.Data[q + 1] = (byte)((buf.Data[q + 1] * ia + fill[o + 1] * al) / 255f);
                        buf.Data[q + 2] = (byte)((buf.Data[q + 2] * ia + fill[o + 2] * al) / 255f);
                    }
                    o += 3;
                }
            }
            BitmapBridge.WriteBack(dst, buf);
            return dst;
        }

        // 可分离盒式模糊（就地）
        static void BoxBlur(byte[] a, int w, int h, int r)
        {
            if (r <= 0) return;
            byte[] tmp = new byte[a.Length];
            for (int y = 0; y < h; y++)
            {
                int o = y * w, sum = 0, cnt = 0;
                for (int x = 0; x <= r && x < w; x++) { sum += a[o + x]; cnt++; }
                for (int x = 0; x < w; x++)
                {
                    tmp[o + x] = (byte)(sum / cnt);
                    int add = x + r + 1, rem = x - r;
                    if (add < w) { sum += a[o + add]; cnt++; }
                    if (rem >= 0) { sum -= a[o + rem]; cnt--; }
                }
            }
            for (int x = 0; x < w; x++)
            {
                int sum = 0, cnt = 0;
                for (int y = 0; y <= r && y < h; y++) { sum += tmp[y * w + x]; cnt++; }
                for (int y = 0; y < h; y++)
                {
                    a[y * w + x] = (byte)(sum / cnt);
                    int add = y + r + 1, rem = y - r;
                    if (add < h) { sum += tmp[add * w + x]; cnt++; }
                    if (rem >= 0) { sum -= tmp[rem * w + x]; cnt--; }
                }
            }
        }
    }
}
