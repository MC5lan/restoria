// Bench.cs — 逐算子性能基准工具（CLI: --bench [goldenPath] [--regen] / --bench --regen）
//
// 为什么要有它：
//   验收标准是"提速后产物逐像素不变"。两件事都需要一把尺子：
//   而 CI 流水线只给出**整条流水线单页一个总毫秒数** —— 那个数被 OCR 推理
//   （秒级）盖住，对一个算子做向量化根本看不出来，也证明不了"没改样"。
//
// 它做什么：
//   把流水线里真正耗时的算子逐个拿出来，在**同一张确定性合成档案页**上跑
//   REPS+1 次（第一遍当 JIT 热身丢弃，其余取最小值 —— 对后台抖动最稳），报告：
//     · ms 与 Mpx/s          → "快了没有"
//     · 产物指纹（FNV-1a 64）→ "改样了没有"（整数 SIMD 最容易在舍入/饱和上偷偷改值）
//
// 指纹基准放在 tools/bench-golden.txt（随代码走），`--bench --regen` 重建。
// 指纹对不上时退出码 1，可脚本化卡住回归。
//
// 注意：本模式**只读 fixture、只写报告**，不碰任何用户文件、不做归档。
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;

namespace PicTool
{
    public static class Bench
    {
        // ---------- 基准配置 ----------
        const int PAGE_W = 1654;            // A4 @200dpi（= 归档成品的标准尺寸）
        const int PAGE_H = 2339;
        const int REPS = 5;                 // 有效次数（另加 1 次热身）。3 次时噪声 ±10%，
                                            // 判不了 1.1 倍的差别 —— 先取够样本再谈提速。
        const string DEFAULT_GOLDEN = @"tools\bench-golden.txt";

        class OpItem
        {
            public string Name;
            // 入参是"fixture 的一份新副本"，允许就地改；返回该算子自己的证据
            // （计数、或它自己产物的指纹）—— 与"改后位图指纹"一起构成双指纹。
            public Func<Bitmap, ulong> Run;
        }

        // ---------- 入口 ----------
        public static string RunAll(string[] args)
        {
            bool regen = false;
            string goldenPath = DEFAULT_GOLDEN;
            if (args != null)
            {
                for (int i = 1; i < args.Length; i++)
                {
                    if (args[i] == "--regen") regen = true;
                    else if (args[i].Length > 0 && args[i][0] != '-') goldenPath = args[i];
                }
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine(AppInfo.Name + " 逐算子基准（--bench）   版本 " + AppInfo.Version);
            sb.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("测试页: " + PAGE_W + "x" + PAGE_H + " 合成档案页（确定性，无外部依赖）");
            sb.AppendLine("计时: 每项跑 " + (REPS + 1) + " 次（第 1 次热身丢弃），取最小");
            sb.AppendLine("");

            Bitmap fix = null;
            try
            {
                fix = Fixture(PAGE_W, PAGE_H);
                byte[] sealProt = BuildSealProtect(fix);

                OpItem[] ops = BuildOps(sealProt);

                // ---- 基准（指纹 + 参考耗时）----
                goldenPath = ResolveGolden(goldenPath);
                ulong[] goldRes = new ulong[ops.Length];
                ulong[] goldPix = new ulong[ops.Length];
                long[] goldMs = new long[ops.Length];
                bool hasGold = false;
                if (!regen && File.Exists(goldenPath)) hasGold = LoadGolden(goldenPath, goldRes, goldPix, goldMs, ops.Length);

                sb.AppendLine("基线文件: " + goldenPath + (File.Exists(goldenPath) ? "（存在）" : "（不存在）"));
                sb.AppendLine(hasGold
                    ? "基线: 已载入（指纹必须逐位一致；耗时仅供倍数参考）"
                    : "基线: 未载入（本次" + (regen ? "重建" : "仅报告，不会判失败") + "）");
                sb.AppendLine("测试页指纹(基准): 0x" + Hash(fix).ToString("x16"));
                sb.AppendLine("");
                sb.AppendLine(" idx | 算子                                    |    ms |  Mpx/s |   基线ms |  倍数 | 指纹判定");

                ulong[] newRes = new ulong[ops.Length];
                ulong[] newPix = new ulong[ops.Length];
                long[] newMs = new long[ops.Length];
                string[] err = new string[ops.Length];
                int diff = 0;
                double px = (double)PAGE_W * PAGE_H / 1e6;

                for (int i = 0; i < ops.Length; i++)
                {
                    ulong res = 0;
                    double best = double.MaxValue;
                    ulong pix = 0;

                    for (int r = 0; r <= REPS; r++)
                    {
                        Bitmap b = (Bitmap)fix.Clone();       // 每轮都在干净副本上跑
                        try
                        {
                            if (r == 0)
                            {
                                err[i] = null;
                                ops[i].Run(b);                // 热身轮（丢弃结果，只为让 JIT/缓存到位）
                                continue;
                            }
                            // 计时前清一次堆：这些算子每轮都会 new 出 MB 级数组，
                            // 不控住 GC 的话，命中一次 Gen2 就能把 10% 的差异糊掉。
                            GC.Collect();
                            GC.WaitForPendingFinalizers();
                            GC.Collect();
                            Stopwatch sw = Stopwatch.StartNew();
                            res = ops[i].Run(b);
                            sw.Stop();
                            double ms = sw.Elapsed.TotalMilliseconds;
                            if (ms < best) best = ms;
                            pix = Hash(b);                    // 指纹在计时之外算，不污染耗时
                        }
                        catch (Exception ex)
                        {
                            // 单个算子出错不该拖垮整轮基准：记下来、跳过，其余照跑。
                            err[i] = ex.GetType().Name + ": " + ex.Message;
                            break;
                        }
                        finally { b.Dispose(); }
                    }

                    if (err[i] != null)
                    {
                        sb.AppendLine(string.Format("{0,4} | {1,-39} |     - |      - |        - |     - | ERR {2}",
                            i, ops[i].Name, err[i]));
                        continue;
                    }

                    newRes[i] = res;
                    newPix[i] = pix;
                    newMs[i] = (long)Math.Round(best);

                    string state;
                    if (!hasGold)
                    {
                        state = "-";
                    }
                    else if (goldRes[i] != res || goldPix[i] != pix)
                    {
                        state = "DIFF!!";
                        diff++;
                    }
                    else
                    {
                        state = "OK";
                    }

                    string speed = "-";
                    if (hasGold && goldMs[i] > 0 && newMs[i] > 0)
                        speed = "x" + ((double)goldMs[i] / newMs[i]).ToString("0.00");

                    sb.AppendLine(string.Format("{0,4} | {1,-39} | {2,5} | {3,6} | {4,8} | {5,5} | {6}",
                        i, ops[i].Name, newMs[i],
                        (px * 1000.0 / (newMs[i] <= 0 ? 1 : newMs[i])).ToString("0.0"),
                        hasGold ? goldMs[i].ToString() : "-", speed, state));
                }

                sb.AppendLine("");
                if (hasGold)
                {
                    if (diff == 0)
                        sb.AppendLine("结论: 全部算子产物指纹一致（" + ops.Length + "/" + ops.Length + "），可以放心比对提速倍数。");
                    else
                        sb.AppendLine("结论: **有 " + diff + " 个算子产物指纹变化** —— 逐像素不变的红线被破了，必须查。");
                }
                else if (regen)
                {
                    SaveGolden(goldenPath, newRes, newPix, newMs, ops, err);
                    sb.AppendLine("结论: 已重建基线 → " + goldenPath + "（下次 --bench 用它比对）");
                }
                else
                {
                    sb.AppendLine("结论: 仅报告（无基线可比）。要建立/更新基线请跑：--bench --regen");
                }

                Environment.ExitCode = (hasGold && diff > 0) ? 1 : 0;
            }
            catch (Exception ex)
            {
                sb.AppendLine("基准运行异常: " + ex.Message);
                Environment.ExitCode = 2;
            }
            finally
            {
                if (fix != null) fix.Dispose();
            }
            return sb.ToString();
        }

        // 相对路径解析：先按当前工作目录（脚本驱动时的 D:\lama），
        // 再退到"exe 所在目录的上一级"（dist\ → 仓库根，直接双击 exe 调试时用）。
        static string ResolveGolden(string p)
        {
            if (Path.IsPathRooted(p)) return p;
            if (File.Exists(p)) return Path.GetFullPath(p);
            try
            {
                string alt = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", p));
                if (File.Exists(alt)) return alt;
            }
            catch { }
            return Path.GetFullPath(p);      // 都不存在 → 用 cwd 下的路径（首次 --regen 就在这里建）
        }

        // ---------- 算子清单（参数与 ArchiveProcessor 实际调用一致）----------
        static OpItem[] BuildOps(byte[] sealProt)
        {
            int w = PAGE_W, h = PAGE_H;
            return new OpItem[]
            {
                new OpItem(){ Name = "① 去扫描黑框 FlattenDarkBackground", Run = delegate(Bitmap b) { return (ulong)DocClean.FlattenDarkBackground(b, 24); } },
                new OpItem(){ Name = "② 页边垃圾 RemoveEdgeJunk",         Run = delegate(Bitmap b) { return (ulong)DocClean.RemoveEdgeJunk(b); } },
                new OpItem(){ Name = "③ 倾斜检测 DetectSkewAngle",         Run = delegate(Bitmap b) { return (ulong)BitConverter.DoubleToInt64Bits(Enhance.DetectSkewAngle(b)); } },
                new OpItem(){ Name = "④ 裁白边 TrimBlank",                 Run = delegate(Bitmap b) { Rectangle r; Bitmap o = Imaging.TrimBlank(b, 24, out r); ulong x = Hash(o); o.Dispose(); return x; } },
                new OpItem(){ Name = "⑤ 污渍掩膜 DetectStainMask",         Run = delegate(Bitmap b) { int n; Bitmap m = DocClean.DetectStainMask(b, 0, 0, 0, out n); if (m != null) m.Dispose(); return (ulong)n; } },
                new OpItem(){ Name = "⑥ 光照归一 NormalizeIllumination",   Run = delegate(Bitmap b) { DocClean.NormalizeIlluminationInPlace(b, 1.0, 2.5); return 0; } },
                new OpItem(){ Name = "⑦ 自动色阶 AutoEnhance",             Run = delegate(Bitmap b) { Imaging.AutoEnhanceInPlace(b); return 0; } },
                new OpItem(){ Name = "⑧ 二值化 Sauvola",                   Run = delegate(Bitmap b) { Enhance.BinarizeInPlace(b, 0.2, sealProt); return 0; } },
                new OpItem(){ Name = "⑨ 去黑点 RemoveSpecksEx",            Run = delegate(Bitmap b) { DocClean.SpeckStats st = DocClean.RemoveSpecksEx(b, 0, 0, sealProt, true, false); return (ulong)st.Removed; } },
                new OpItem(){ Name = "⑩ 回二值化 ThresholdInPlace",        Run = delegate(Bitmap b) { return (ulong)DocClean.ThresholdInPlace(b, 128); } },
                new OpItem(){ Name = "⑪ 回二值化(保红) ThresholdKeepRed",  Run = delegate(Bitmap b) { return (ulong)DocClean.ThresholdKeepRed(b, 128, 40); } },
                new OpItem(){ Name = "⑫ 终筛 RemoveSpecksFinalEx",         Run = delegate(Bitmap b) { DocClean.SpeckStats st = DocClean.RemoveSpecksFinalEx(b, 0, true, sealProt, false); return (ulong)st.Removed; } },
                new OpItem(){ Name = "⑬ 终筛页边 RemoveEdgeJunk(final)",   Run = delegate(Bitmap b) { return (ulong)DocClean.RemoveEdgeJunk(b, true); } },
                new OpItem(){ Name = "⑭ 缩放 ScaleTo(A4→150dpi)",          Run = delegate(Bitmap b) { Bitmap o = Imaging.ScaleTo(b, 1240, 1754); ulong x = Hash(o); o.Dispose(); return x; } },
                new OpItem(){ Name = "⑮ 锐化 Unsharp",                     Run = delegate(Bitmap b) { Enhance.UnsharpInPlace(b, 0.6); return 0; } },
                new OpItem(){ Name = "⑯ 掩膜膨胀 DilateMask(r=3)",         Run = delegate(Bitmap b) { return HashBytes(DocClean.DilateMask(sealProt, w, h, 3)); } },
                new OpItem(){ Name = "⑰ 掩膜计数 MaskCount",               Run = delegate(Bitmap b) { return (ulong)DocClean.MaskCount(sealProt); } },
            };
        }

        // 印章保护掩膜：与归档流水线同一口径（偏红 → 外扩 SealDilate=3）。
        // 在计时之外算一次（它是输入准备，不是被测算子）。
        static byte[] BuildSealProtect(Bitmap fix)
        {
            byte[] red = DocClean.RedProtectMask(fix, 40);
            return DocClean.DilateMask(red, fix.Width, fix.Height, 3);
        }

        // ---------- 指纹（FNV-1a 64：无依赖、确定性、够敏感）----------
        public static ulong Hash(Bitmap b)
        {
            PixelBuffer buf = BitmapBridge.ToBuffer(b);
            int rowBytes = buf.Width * buf.Bpp;
            ulong h = 14695981039346656037UL;
            for (int y = 0; y < buf.Height; y++)
            {
                int o = y * buf.Stride;
                for (int x = 0; x < rowBytes; x++)
                {
                    h ^= buf.Data[o + x];
                    h *= 1099511628211UL;
                }
            }
            return h;
        }

        public static ulong HashBytes(byte[] m)
        {
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < m.Length; i++)
            {
                h ^= m[i];
                h *= 1099511628211UL;
            }
            return h;
        }

        // ---------- 基准读写（纯 ASCII，按序号对齐，与算子名无关）----------
        static bool LoadGolden(string path, ulong[] res, ulong[] pix, long[] ms, int n)
        {
            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch { return false; }
            bool any = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string s = lines[i].Trim();
                if (s.Length == 0 || s[0] == '#') continue;
                int cut = s.IndexOf('#');
                if (cut >= 0) s = s.Substring(0, cut).Trim();   // 去掉行尾的算子名注释
                string[] f = s.Split('|');
                if (f.Length < 4) continue;
                int idx;
                long r, g, m;
                if (!int.TryParse(f[0].Trim(), out idx)) continue;
                if (idx < 0 || idx >= n) continue;
                if (!long.TryParse(f[1].Trim(), out r)) continue;
                if (!long.TryParse(f[2].Trim(), out g)) continue;
                if (!long.TryParse(f[3].Trim(), out m)) continue;
                res[idx] = (ulong)r; pix[idx] = (ulong)g; ms[idx] = m;
                any = true;
            }
            return any;
        }

        static void SaveGolden(string path, ulong[] res, ulong[] pix, long[] ms, OpItem[] ops, string[] err)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# PicTool --bench golden: 逐算子产物指纹（idx|resHash|pixHash|ms）");
            sb.AppendLine("# P3 提速（SIMD/LUT）改完必须逐位一致；改算法**有意**变产物时重建：--bench --regen");
            for (int i = 0; i < ops.Length; i++)
            {
                if (err != null && err[i] != null) continue;      // 跑不起来的算子不入基线
                sb.AppendLine(i + "|" + (long)res[i] + "|" + (long)pix[i] + "|" + ms[i] + "   # " + ops[i].Name);
            }
            string dir = Path.GetDirectoryName(path);
            if (dir != null && dir.Length > 0 && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        // ---------- 合成测试页（确定性；不用 Random/DateTime，保证跨机器同一张）----------
        //
        // 造这张图的目的是"让每个算子都有活干"，不是好看：
        //   · 横向亮度渐变 + 纵向衰减 + 纸面噪点 → 光照归一 / 自动色阶 / 二值化 有活干
        //   · 48 行黑条 + 8 条长表格线               → 倾斜检测 / 裁白边 / 去黑点 有活干
        //   · 一枚红章（红是内容）                    → 印章豁免 / 保红 / 保护掩膜 有活干
        //   · 2500 粒尘点                             → 去黑点 / 终筛 有活干
        //   · 3 块大灰渍                              → 污渍掩膜 / 去扫描背景 有活干
        static Bitmap Fixture(int w, int h)
        {
            uint seed = 20261009u;
            PixelBuffer buf = new PixelBuffer(w, h, 3);
            byte[] d = buf.Data;

            // 1) 纸面
            for (int y = 0; y < h; y++)
            {
                int o = y * buf.Stride;
                int vg = y * 22 / h;
                for (int x = 0; x < w; x++)
                {
                    seed = seed * 1103515245u + 12345u;
                    int v = 250 - (x * 45 / w) - vg - (int)((seed >> 18) % 5);
                    int p = o + x * 3;
                    d[p] = (byte)v; d[p + 1] = (byte)v; d[p + 2] = (byte)v;
                }
            }

            // 2) 正文黑条（模拟文字行）
            int[] widths = new int[] { 34, 52, 78, 96, 44, 66 };
            int wi = 0;
            for (int i = 0; i < 48; i++)
            {
                int y0 = 120 + i * 44;
                if (y0 + 22 >= h - 120) break;
                int x = 130;
                while (x < w - 170)
                {
                    seed = seed * 1103515245u + 12345u;
                    int bw = widths[wi % widths.Length]; wi++;
                    int gap = 8 + (int)((seed >> 20) % 14);
                    int v = 38 + (int)((seed >> 24) % 26);
                    for (int y = y0; y < y0 + 20; y++)
                    {
                        int o = y * buf.Stride + x * 3;
                        for (int k = 0; k < bw; k++) { d[o] = (byte)v; d[o + 1] = (byte)v; d[o + 2] = (byte)v; o += 3; }
                    }
                    x += bw + gap;
                }
            }

            // 3) 表格长线
            for (int i = 0; i < 8; i++)
            {
                int y0 = 200 + i * 290;
                if (y0 + 6 >= h) break;
                for (int y = y0; y < y0 + 5; y++)
                {
                    int o = y * buf.Stride + 130 * 3;
                    for (int x = 130; x < w - 130; x++) { d[o] = 60; d[o + 1] = 60; d[o + 2] = 60; o += 3; }
                }
            }

            // 4) 红色印章（环 + 内圈短笔画）
            int cx = w * 3 / 4, cy = h * 4 / 5, ro = 92, rt = 9;
            for (int y = cy - ro; y <= cy + ro; y++)
            {
                if (y < 0 || y >= h) continue;
                int o0 = y * buf.Stride;
                for (int x = cx - ro; x <= cx + ro; x++)
                {
                    if (x < 0 || x >= w) continue;
                    int dx = x - cx, dy = y - cy;
                    int dd = dx * dx + dy * dy;
                    if (dd > ro * ro) continue;
                    bool ring = dd >= (ro - rt) * (ro - rt);
                    bool spoke = (Math.Abs(dy) < 4) || (Math.Abs(dx) < 4);
                    if (!ring && !spoke) continue;
                    int p = o0 + x * 3;
                    d[p] = 32; d[p + 1] = 26; d[p + 2] = 200;      // BGR: 印章红 (200,26,32)
                }
            }

            // 5) 尘点
            for (int i = 0; i < 2500; i++)
            {
                seed = seed * 1103515245u + 12345u;
                int x = (int)((seed >> 16) % (uint)(w - 8)) + 4;
                seed = seed * 1103515245u + 12345u;
                int y = (int)((seed >> 16) % (uint)(h - 8)) + 4;
                seed = seed * 1103515245u + 12345u;
                int sz = 1 + (int)((seed >> 16) % 3u);
                int v = 25 + (int)((seed >> 22) % 66);
                for (int yy = y; yy < y + sz && yy < h; yy++)
                {
                    int o = yy * buf.Stride + x * 3;
                    for (int xx = 0; xx < sz && x + xx < w; xx++)
                    {
                        d[o] = (byte)v; d[o + 1] = (byte)v; d[o + 2] = (byte)v;
                        o += 3;
                    }
                }
            }

            // 6) 大块灰渍（比纸面暗一截，但还远没到墨）
            for (int s = 0; s < 3; s++)
            {
                seed = seed * 1103515245u + 12345u;
                int sx = 200 + (int)((seed >> 16) % (uint)(w - 500));
                seed = seed * 1103515245u + 12345u;
                int sy = 300 + (int)((seed >> 16) % (uint)(h - 700));
                seed = seed * 1103515245u + 12345u;
                int rr = 100 + (int)((seed >> 16) % 60u);
                int v = 196 + (int)((seed >> 22) % 14);
                for (int y = sy - rr; y <= sy + rr; y++)
                {
                    if (y < 0 || y >= h) continue;
                    int o0 = y * buf.Stride;
                    for (int x = sx - rr; x <= sx + rr; x++)
                    {
                        if (x < 0 || x >= w) continue;
                        int dx = x - sx, dy = y - sy;
                        if (dx * dx + dy * dy > rr * rr) continue;
                        int p = o0 + x * 3;
                        if (d[p] < 190) continue;                 // 只压纸面，不压墨迹/印章
                        d[p] = (byte)v; d[p + 1] = (byte)v; d[p + 2] = (byte)v;
                    }
                }
            }

            return BitmapBridge.BufferToImage(buf);
        }
    }
}
