// DocClean.cs -- 档案清洁算子（一）：连通域黑点过滤（核心）。
//
// 按职责拆分为 partial 分文件（纯移动、行为不变：自检 + 并行回归验证）：
//   DocClean.cs            黑点过滤 RemoveSpecks*/RemoveSpecksFinal* + 撤销保险判据 + 诊断静态字段
//   DocClean.Light.cs      光照 / 背景归一化（含缩略纸面场 ThumbGray/SmallPaperField/Morph*）
//   DocClean.Background.cs 去扫描背景（深色底漫水涂白 FlattenDarkBackground + Push/IsBg）
//   DocClean.Seal.cs       印章掩膜工具（掩膜 I/O/膨胀）+ 印章掩膜过滤 FilterSealMask
//   DocClean.Binarize.cs   固定阈值回二值化 ThresholdInPlace
//   DocClean.Red.cs        红框内部识别 RedEncloseMask + 保红统计 ThresholdKeepRed/RedCount/InkCount
//   DocClean.Stain.cs      脏块掩膜 DetectStainMask（供 PaperFill 纸面重建）
//   DocClean.Edge.cs       页边垃圾清除 RemoveEdgeJunk（装订孔/黑角/贴边黑条）
//
// 为什么拆：原单文件 2081 行、八种职责挤在一处，改一处要在一整屏里找边界。
//   拆的是**文件**不是**类** -- 仍是同一个 static partial class，成员互相可见、调用方零改动。
//   真正把职责切成多个类会动到全部调用点，留待后续。
//
// 共同点：只对像素做确定性运算，无 UI 依赖，无外部依赖，可直接进单文件版。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;

namespace PicTool
{
    public static partial class DocClean
    {
        // ==================== 二、连通域黑点过滤（v2：孤立 + 形状判据） ====================
        //
        // 解决什么：
        //   档案扫描件的"黑点"——霉斑点、纸屑、复印污点、扫描噪点。
        //
        // v1（纯"面积 + 孤立"判据）的三个洞：
        //   ① 自动阈值 483px@200dpi，半径 ≥13px 的点全部漏网；
        //   ② 面积 483~3869 的黑斑是空白档：黑点管不了、霉斑路径只认灰斑也不要；
        //   ③ 两个挨着的斑点互相保护，成簇的清不掉。
        //
        // v2 判据（对每个墨色连通域，按顺序尝试，命中即删）：
        //   R1 孤立实心斑：孤立（gap 环带内无任何其它墨）+ 实心（fill≥0.7）+
        //      近圆（长宽比≤6，排除下划线/签名线）+ 面积 ≤ 画幅/40。**大小不设限**
        //      —— 空纸上的孤立实心斑无论多大都是脏东西。
        //   R2 孤立小点（v1 原样）：面积 ≤ maxArea 且孤立。管不规则/碎屑状小尘点。
        //   R5 尘簇：活着的"尘粒"（fill≥0.55 且 最大边<半个字高 且 近圆），若环带内
        //      邻居**全部**是尘粒或已判杀 → 整簇清。防"互相保护的点簇"。
        //
        // ⚠️ 走过的弯路（勿重蹈）：
        //   R4"贴字大斑"（按尺寸门槛删贴着文字的大点）撤销了 —— 低清扫描的文字
        //   二值化后就是实心块，与尘点形状不可区分，实测把两份测试文档的正文整片吃掉。
        //   "密度"判据也撤销了 —— 低清文档里字连成片，文字高度估计失真（实测
        //   mh 从 44 飘到 108），密度推算全部失准。最终只信"孤立"这个硬判据。
        //
        // ⚠️ 残余风险（已知，勿当 bug）：
        //   空白页边距上一个孤立的、实心（fill≥0.7）的字符（如加粗的"●"）会被 R1 删。
        //   遇到这类档案用 --speck-max=1 关不掉 R1（它不看 maxArea），需改代码。
        //
        // 务必在二值化之后调用：纯黑白图上"黑"的定义才无歧义。
        //
        // 返回清掉的连通域个数（供自检与报告使用）。
        // 上一次调用因"紧贴文字"而**保留**的候选块数（诊断/报告用）：
        // 这个数字 > 0 就说明文字邻域保护真的拦下了东西。
        public static int LastTextGuarded;
        // 上一次调用**实际抹掉的像素数 / 抹掉的最大单块面积**（撤销保险用：
        // 一次删掉一个字那么大的块，或者删掉的总量占页面
        // 墨量比例过高，都说明这一步可能吃字了 —— 由调用方决定整步撤销）。
        //
        // ⚠️ 这三个字段**只服务单线程调用方**（SelfTest 自检）——
        //   并行归档**绝不能**再读它们：P2 并行下两页会互相覆盖对方这次调用的结果，
        //   撤销保险据此决策就会误判（漏撤销 = 吃字），实测确认的并发缺陷。
        //   并行调用方一律改用下面的 Ex 版本，从返回值里取数（见 SpeckStats）。
        public static int LastRemovedPixels;
        public static int LastRemovedMaxArea;

        // 一次 RemoveSpecks/RemoveSpecksFinal 的完整结果：
        // 从"隐式共享静态态"改为"显式返回值"，并行下每个调用者拿到的都是自己那次的数据。
        public struct SpeckStats
        {
            public int Removed;           // 清掉的连通域个数（= 旧版返回值）
            public int RemovedPixels;     // 实际抹掉的像素总数
            public int RemovedMaxArea;    // 抹掉的最大单块面积（px）
            public int TextGuarded;       // 因"紧贴文字"而保留、没删的候选块数
        }

        // 旧版 int 重载：保持对外行为（含写回 LastXxx 静态字段，供 SelfTest 单线程使用）。
        static int Legacy(SpeckStats st)
        {
            LastRemovedPixels = st.RemovedPixels;
            LastRemovedMaxArea = st.RemovedMaxArea;
            LastTextGuarded = st.TextGuarded;
            return st.Removed;
        }

        public static int RemoveSpecks(Bitmap bmp, int maxArea, int gap)
        {
            return RemoveSpecks(bmp, maxArea, gap, null, true);
        }

        // protect：可选保护掩膜（w*h，非 0 = 印章区）。
        //   印章豁免时**必须传**：印章本身就是一堆小连通域（五角星尖角、环内文字、
        //   褪色后断裂的笔画），不保护的话会被直接当黑点删掉。
        public static int RemoveSpecks(Bitmap bmp, int maxArea, int gap, byte[] protect)
        {
            return RemoveSpecks(bmp, maxArea, gap, protect, true);
        }

        public static int RemoveSpecks(Bitmap bmp, int maxArea, int gap, byte[] protect, bool textGuard)
        {
            return Legacy(RemoveSpecksImpl(bmp, maxArea, gap, protect, false, textGuard, false));
        }

        // safe = 「极致归档」的保守模式（宁可留着黑点，也不能吃掉字）。
        //   与常规模式的差别：面积上限减半、孤立半径翻倍、护字半径翻倍、
        //   **关掉 R1/R1b（字那么大的实心块一律不碰）与 R5（尘簇）**，只留 R2
        //   且额外要求"形状就是尘粒"（fill≥0.55 且 最大边 < 半个字高 且 近圆）。
        public static int RemoveSpecks(Bitmap bmp, int maxArea, int gap, byte[] protect, bool textGuard, bool safe)
        {
            return Legacy(RemoveSpecksImpl(bmp, maxArea, gap, protect, false, textGuard, safe));
        }

        // ★ 并行安全入口：**不碰任何静态字段**，结果全部从返回值取。
        //   归档流水线（可并行）一律用这个，绝不用上面那组 int 版。
        public static SpeckStats RemoveSpecksEx(Bitmap bmp, int maxArea, int gap, byte[] protect,
                                                bool textGuard, bool safe)
        {
            return RemoveSpecksImpl(bmp, maxArea, gap, protect, false, textGuard, safe);
        }

        // 终筛版：A4 回二值化之后用。固定阈值会把缩放灰边重新变成小黑点，
        // 这一批只有在这里才第一次有机会被清。redIsContent：偏红像素视为印章内容，
        // 所在连通域绝不删（终筛阶段没有掩膜可用，靠颜色自认，见 ArchiveProcessor ⑪ 的说明）。
        public static int RemoveSpecksFinal(Bitmap bmp, int maxArea)
        {
            return RemoveSpecksFinal(bmp, maxArea, true);
        }

        public static int RemoveSpecksFinal(Bitmap bmp, int maxArea, bool textGuard)
        {
            return Legacy(RemoveSpecksImpl(bmp, maxArea, 0, null, true, textGuard, false));
        }

        // protect：可选保护掩膜（红框内部，见 RedEncloseMask）。终筛也用它：
        //   框里的填写内容在 A4 缩放后同样"孤立 + 紧实"，不保护照样会被终筛咬掉。
        public static int RemoveSpecksFinal(Bitmap bmp, int maxArea, bool textGuard, byte[] protect)
        {
            return Legacy(RemoveSpecksImpl(bmp, maxArea, 0, protect, true, textGuard, false));
        }

        public static int RemoveSpecksFinal(Bitmap bmp, int maxArea, bool textGuard, byte[] protect, bool safe)
        {
            return Legacy(RemoveSpecksImpl(bmp, maxArea, 0, protect, true, textGuard, safe));
        }

        // ★ 并行安全入口（终筛版）
        public static SpeckStats RemoveSpecksFinalEx(Bitmap bmp, int maxArea, bool textGuard,
                                                     byte[] protect, bool safe)
        {
            return RemoveSpecksImpl(bmp, maxArea, 0, protect, true, textGuard, safe);
        }

        // 核心实现：返回 SpeckStats，**不写静态字段**（并行安全的关键）。
        static unsafe SpeckStats RemoveSpecksImpl(Bitmap bmp, int maxArea, int gap, byte[] protect,
                                           bool redIsContent, bool textGuard, bool safe)
        {
            SpeckStats stats = new SpeckStats();
            int w = bmp.Width, h = bmp.Height;
            if (w < 5 || h < 5) return stats;

            // R2 的面积上限：按画幅面积给 —— 这个数字是拿真实档案图量出来的，别随手改。
            if (maxArea <= 0)
            {
                maxArea = w * h / 8000;
                if (maxArea < 16) maxArea = 16;
                if (maxArea > 2000) maxArea = 2000;
            }
            // 保守模式：上限再砍一半（字的一半都够不上才算尘点）
            if (safe) { maxArea = maxArea / 2; if (maxArea < 8) maxArea = 8; }
            if (gap <= 0)
            {
                gap = Math.Min(w, h) / 250;
                if (gap < 3) gap = 3;
                if (gap > 24) gap = 24;
            }
            // 保守模式：孤立判定半径翻倍（离别人更远才敢判它是杂点）
            if (safe) { gap = gap * 2; if (gap > 48) gap = 48; }
            int loneCap = w * h / 20;

            PixelBuffer buf = BitmapBridge.ToBuffer(bmp);
            int removed = 0;
            int[] lab = new int[w * h];       // 0=纸，1..N=连通域编号
            List<int> cA = new List<int>();   // 面积
            List<int> cX0 = new List<int>(), cX1 = new List<int>(), cY0 = new List<int>(), cY1 = new List<int>();
            List<byte> cSolid = new List<byte>();   // 1=印章/保护内容（绝不删）

            fixed (byte* bp = buf.Data)
            {
                // ---- 读灰度/偏红：红（印章）直接当内容，不参与删除 ----
                bool[] ink = new bool[w * h];
                for (int y = 0; y < h; y++)
                {
                    byte* row = bp + y * buf.Stride;
                    int o = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        byte* p = row + x * 3;
                        int mx = p[1] > p[0] ? p[1] : p[0];
                        bool red = redIsContent && p[2] - mx >= 40;
                        ink[o + x] = red || ((p[2] * 77 + p[1] * 150 + p[0] * 29) >> 8) < 128;
                    }
                }

                // ---- 连通域标记（8 邻接 BFS）----
                int[] queue = new int[w * h];
                for (int start = 0; start < w * h; start++)
                {
                    if (!ink[start] || lab[start] != 0) continue;
                    int id = cA.Count + 1;
                    int rd = 0, wr = 0;
                    lab[start] = id;
                    queue[wr++] = start;
                    int minX = start % w, maxX = minX, minY = start / w, maxY = minY, area = 0;
                    bool solid = false;
                    while (rd < wr)
                    {
                        int cur = queue[rd++];
                        int cx = cur % w, cy = cur / w;
                        area++;
                        if (cx < minX) minX = cx; else if (cx > maxX) maxX = cx;
                        if (cy < minY) minY = cy; else if (cy > maxY) maxY = cy;
                        if (!solid && protect != null && protect[cur] != 0) solid = true;
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            int ny = cy + dy;
                            if (ny < 0 || ny >= h) continue;
                            int baseIdx = ny * w;
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dy == 0) continue;
                                int nx = cx + dx;
                                if (nx < 0 || nx >= w) continue;
                                int ni = baseIdx + nx;
                                if (ink[ni] && lab[ni] == 0) { lab[ni] = id; queue[wr++] = ni; }
                            }
                        }
                    }
                    cA.Add(area);
                    cX0.Add(minX); cX1.Add(maxX); cY0.Add(minY); cY1.Add(maxY);
                    cSolid.Add((byte)(solid ? 1 : 0));
                }
                int nComp = cA.Count;
                if (nComp == 0) { BitmapBridge.WriteBack(bmp, buf); return stats; }

                // ---- 文字高度 MH：取"笔画型"组件（fill<0.55、20≤高≤200）的高的中位数 ----
                // ⚠️ 下限必须 20：小尘埃点（h 10~15）fill 也可能 <0.55，混进来会把
                //    中位数从 ~44 拉到 ~28，后面所有"半个字高"判据全部失准（实测）。
                List<int> hs = new List<int>();
                for (int i = 0; i < nComp; i++)
                {
                    int ch = cY1[i] - cY0[i] + 1;
                    double fill = cA[i] / (double)((cX1[i] - cX0[i] + 1) * (long)ch);
                    if (fill < 0.55 && ch >= 20 && ch <= 200) hs.Add(ch);
                }
                hs.Sort();
                // 用 75 分位而不是中位数：中英混排时拉丁小写（x 高度只有汉字的 ~0.7）
                // 会把中位数拉低 1/3，导致"半个字高"门槛失准（实测 mh 44 → 30，
                // 17px 的尘簇全部绕过判据）。75 分位落在汉字高度附近。
                int mh = hs.Count >= 5 ? hs[(int)(hs.Count * 0.75)] : 0;
                // 画幅下限兜底：正常版面字高不会小于短边/45（200dpi A4 ≈ 46px）
                if (mh < Math.Min(w, h) / 45) mh = Math.Min(w, h) / 45;
                if (mh < 16) mh = 16;
                if (mh > 120) mh = 120;
                // 文档类型守卫：页面里必须有足够多的笔画型组件（真实文字）才允许 R1。
                // 否则（5/7 号那类块状文档：全部内容都是实心块、没有任何笔画型组件），
                // R1 会把正文当灰尘整片吃掉 —— 实测两次翻车的最终防线。
                bool hasRealText = hs.Count >= 30;

                // ---- 尘粒标记（R5 用）：fill>=0.55 且 最大边 < 半个字高 且 近圆 ----
                // 汉字/数字的尺寸 ≈ 一个字高，够不上"尘粒"；低清文档里字会连成片，
                // mh 会失真 —— 所以 R1/R2 只信"孤立"这个硬判据，不信密度推算。
                bool[] dust = new bool[nComp + 1];
                for (int i = 0; i < nComp; i++)
                {
                    int bw = cX1[i] - cX0[i] + 1, bh = cY1[i] - cY0[i] + 1;
                    double fill = cA[i] / (double)((long)bw * bh);
                    int mx = bw > bh ? bw : bh, mn = bw < bh ? bw : bh;
                    dust[i + 1] = fill >= 0.55 && mx * 2 < mh && mx / (double)mn <= 2.5;
                }

                // ---- 护字：大半径孤立复核（最大限度保护文字）----
                // 原理：文字被二值化打断后，碎片与**同字的其他笔画**只隔几像素 ——
                //       用"更大半径看它还孤不孤立"就能把碎片和纸面杂点分开：
                //         · gap(3px) 内无墨 → 看着孤立，会进 R1/R2 的候选
                //         · guardR(短边/100 ≈ 16px) 内有墨 → 它紧贴内容 → **不删**
                //         · 两级都孤立 → 真杂点 → 照删
                // 为什么不用"文字主体 + 膨胀保护圈"那套（第一版，已废弃）：
                //   ① 小字（表格里手写的数字，面积 200~500px）够不上"主体"门槛 →
                //      没人给它生成保护圈 → 它自己反被当黑点整块删掉（实测残留）；
                //   ② 门槛一降到够低，"够大的污点"就会自我保护，去黑点整体失效
                //      （自检当场失败 3 项）。
                //   "环带里还有没有别的墨"天然排除了自身，这两个坑都绕开。
                // 半径用画幅比例而不是"字高 mh"：mh 在低清文档上会飘（实测 44→108）。
                int guardR = 0;
                if (textGuard)
                {
                    // 保守模式：护字半径翻倍（200dpi A4 约 32px ≈ 字高的 2/3），
                    // 上下限也跟着放宽——宁可多留点，别把断笔当杂点。
                    guardR = safe ? Math.Min(w, h) / 50 : Math.Min(w, h) / 100;
                    if (guardR < (safe ? 14 : 7)) guardR = safe ? 14 : 7;
                    if (guardR > (safe ? 60 : 30)) guardR = safe ? 60 : 30;
                }

                // ---- 逐域判定（v2 最终版：全部围绕"孤立"）----
                bool[] kill = new bool[nComp + 1];
                int cR1 = 0, cR2 = 0, cR5 = 0, cGuarded = 0;
                for (int i = 0; i < nComp; i++)
                {
                    if (cSolid[i] != 0) continue;
                    int area = cA[i];
                    int bw = cX1[i] - cX0[i] + 1, bh = cY1[i] - cY0[i] + 1;
                    double fill = area / (double)((long)bw * bh);
                    int maxDim = bw > bh ? bw : bh;
                    int minDim = bw < bh ? bw : bh;
                    double aspect = maxDim / (double)minDim;

                    // 孤立判定：外接框外扩 gap 的环带里有没有别的连通域
                    bool isolated = true;
                    int rx0 = cX0[i] - gap < 0 ? 0 : cX0[i] - gap;
                    int rx1 = cX1[i] + gap >= w ? w - 1 : cX1[i] + gap;
                    int ry0 = cY0[i] - gap < 0 ? 0 : cY0[i] - gap;
                    int ry1 = cY1[i] + gap >= h ? h - 1 : cY1[i] + gap;
                    for (int yy = ry0; yy <= ry1 && isolated; yy++)
                    {
                        int ro = yy * w;
                        bool edgeRow = (yy < cY0[i] || yy > cY1[i]);
                        for (int xx = rx0; xx <= rx1; xx++)
                        {
                            if (!edgeRow && xx >= cX0[i] && xx <= cX1[i]) continue;
                            int l = lab[ro + xx];
                            if (l != 0 && l != i + 1) { isolated = false; break; }
                        }
                    }
                    if (!isolated) continue;              // 环带里有别的墨：一律不动（保守）

                    // 文字邻域保护：紧贴文字的块很可能是字的断裂部件（二值化把细笔画打断了），
                    // 一律不删 —— 这是"最大限度保护文字"的落点。真相不明的点留在纸上，
                    // 比把字啃掉一块安全得多。
                    // 大半径孤立复核（护字）：guardR 内还有别的墨 → 它紧贴内容，
                    // 很可能是被二值化打断的笔画碎片 → 不删。真相不明的点留在纸上，
                    // 比把字啃掉一块安全得多。
                    if (guardR > 0)
                    {
                        bool nearInk = false;
                        int gx0 = cX0[i] - guardR < 0 ? 0 : cX0[i] - guardR;
                        int gx1 = cX1[i] + guardR >= w ? w - 1 : cX1[i] + guardR;
                        int gy0 = cY0[i] - guardR < 0 ? 0 : cY0[i] - guardR;
                        int gy1 = cY1[i] + guardR >= h ? h - 1 : cY1[i] + guardR;
                        for (int yy = gy0; yy <= gy1 && !nearInk; yy++)
                        {
                            int ro = yy * w;
                            bool edgeRow = (yy < cY0[i] || yy > cY1[i]);
                            for (int xx = gx0; xx <= gx1; xx++)
                            {
                                if (!edgeRow && xx >= cX0[i] && xx <= cX1[i]) continue;
                                int l2 = lab[ro + xx];
                                if (l2 != 0 && l2 != i + 1) { nearInk = true; break; }
                            }
                        }
                        if (nearInk) { cGuarded++; continue; }
                    }

                    // R1 孤立实心斑（任意大小）：实心 + 近圆（长宽比<=6，排除下划线/签名线）
                    // 上限 = maxArea*4（随画幅自动缩放，200dpi A4 ≈ 1900px，直径 50px）：
                    // 再大就有吃掉"低清扫描的块状正文"的风险（实测 5/7 号文档两次翻车）
                    // ⚠️ 保守模式整体关掉 R1/R1b：它删的是"字那么大"的实心块，
                    //    正是"●"这类实心字符与低清块状正文的体量 —— 极致档宁可留着。
                    if (!safe && hasRealText && fill >= 0.70 && aspect <= 6.0 && area <= maxArea * 4) { kill[i + 1] = true; removed++; cR1++; }
                    // R1b 孤立超大斑：比最大的字还大（maxDim ≥ 字高）的实心斑，
                    // 只在"页面有真实文字"时启用 —— 比字还大的孤立实心块不可能是文字。
                    // 上限放宽到画幅/40（再大是装订孔/印章类的领地）。
                    if (!safe && hasRealText && fill >= 0.70 && aspect <= 3.0 && maxDim >= mh && area <= w * h / 40)
                    { kill[i + 1] = true; removed++; cR1++; }
                    // R2 孤立小点（v1 原样，管不规则/碎屑状的小尘点）
                    //    保守模式额外要求"形状就是尘粒"（dust 判据），不规则碎屑一律放过
                    else if (area <= maxArea && (!safe || dust[i + 1])) { kill[i + 1] = true; removed++; cR2++; }
                }

                // R5 尘簇规则：活着的尘粒，若 gap 环带内的邻居**全部**是尘粒或已被判杀
                // → 整簇一起清。防"互相保护的小点簇"（实测 3 颗 17px 的点簇全部漏网）。
                // ⚠️ 保守模式关掉：整簇一起删的量级接近一个字，极致档不冒这个险。
                if (!safe)
                for (int i = 0; i < nComp; i++)
                {
                    if (kill[i + 1] || cSolid[i] != 0 || !dust[i + 1]) continue;
                    bool allDust = true;
                    bool hasNeighbor = false;
                    int rx0 = cX0[i] - gap < 0 ? 0 : cX0[i] - gap;
                    int rx1 = cX1[i] + gap >= w ? w - 1 : cX1[i] + gap;
                    int ry0 = cY0[i] - gap < 0 ? 0 : cY0[i] - gap;
                    int ry1 = cY1[i] + gap >= h ? h - 1 : cY1[i] + gap;
                    for (int yy = ry0; yy <= ry1 && allDust; yy++)
                    {
                        int ro = yy * w;
                        bool edgeRow = (yy < cY0[i] || yy > cY1[i]);
                        for (int xx = rx0; xx <= rx1; xx++)
                        {
                            if (!edgeRow && xx >= cX0[i] && xx <= cX1[i]) continue;
                            int l = lab[ro + xx];
                            if (l == 0 || l == i + 1) continue;
                            hasNeighbor = true;
                            if (!kill[l] && !dust[l]) { allDust = false; break; }
                        }
                    }
                    if (allDust && hasNeighbor) { kill[i + 1] = true; removed++; cR5++; }
                }

                stats.Removed = removed;
                stats.TextGuarded = cGuarded;
                if (removed > 0)
                {
                    int removedPx = 0, maxKilled = 0;
                    for (int y = 0; y < h; y++)
                    {
                        byte* row = bp + y * buf.Stride;
                        int o = y * w;
                        for (int x = 0; x < w; x++)
                        {
                            int l = lab[o + x];
                            if (l != 0 && kill[l])
                            {
                                byte* p = row + x * 3;
                                p[0] = 255; p[1] = 255; p[2] = 255;
                                removedPx++;
                            }
                        }
                    }
                    for (int i = 0; i < nComp; i++)
                        if (kill[i + 1] && cA[i] > maxKilled) maxKilled = cA[i];
                    stats.RemovedPixels = removedPx;
                    stats.RemovedMaxArea = maxKilled;
                }
            }
            BitmapBridge.WriteBack(bmp, buf);
            return stats;
        }

        // 脏块掩膜 DetectStainMask（霉斑 / 水渍 / 大面积灰斑 —— 供 PaperFill 重建）。
        // 为什么不归 RemoveSpecks：黑点是**小面积、高对比**的孤立斑点（那边管）；脏块是**大面积、
        // 低对比**的灰斑 —— 亮度介于纸(≈255)与墨之间，面积大到按面积清必然连字一起删
        // （实测霉斑 400~600 与汉字 400~1800 完全重叠）。这类场景的解法修复页已验证：
        // PaperFill 纸面重建（不含任何生成，永远不编内容）。
        // 本函数只做一件事：**找出"哪里是脏块"**，生成 PaperFill 要的洞掩膜。
        //
        // 三道保守判据（原则仍是"宁可漏检，不可误伤"—— 误伤=把文字当污渍抹掉，是损伤）：
        //   ① 候选像素：比**局部纸面场**暗 ≥ stainDepth 级、且不是强墨（亮度 ≥ inkLow）。
        //      为什么必须相对：本函数跑在光照归一化**之前**（归一化会把大块低频污渍
        //      连"背景"一起拉白，之后就没得检了 —— 第一次实现就栽在这上面），此时纸面
        //      本身可能是灰的（米白 248、阴影 180），绝对亮度会把正常灰纸也当污渍。
        //      "比局部纸面暗一截"才能区分：污渍有边界、暗差大；光照渐变跟着场走、暗差小。
        //   ② 连通域面积 ≥ minStain（黑点级别的斑归 RemoveSpecks，不归这里）
        //   ③ 域外接框内**强墨占比 ≤ 8%** —— 文字周围的反锯齿灰边会成片进入候选，
        //      但它们紧挨大量强墨；真污渍长在空纸上。这条就是"污渍 vs 文字灰边"的分界。
        // 污渍的深色核心（亮度 < inkLow）不会进候选 —— 掩膜外扩（本函数不扩，PaperFill
        // 内部外扩 6px）加上浅晕环带足以把它包住。
        // 返回：8bpp 掩膜（255=脏块，符合 PaperFill 的洞约定）；一处都没找到时返回 null。
    }
}
