// ArchiveProcessor.cs — 「一键归档」流水线
//
// 目标：把古早档案扫描图变成 **标准 A4 · 白底黑字 · 无污垢** 的归档件。
// 方案与决策记录见 docs/一键归档流水线方案.md。
//
// ---------------------------------------------------------------
// 顺序（改之前先读 —— **顺序本身就是关键技术点**）：
//   ① EXIF 转正（Imaging.LoadFile 已做）
//   ② 倾斜检测 + 转正            ← 几何
//   ③ 裁黑边 + 裁白边
//   ④ 光照 / 背景归一化          ← 光度
//   ⑤ 自动色阶（百分位拉伸）
//   ⑥ 白底黑字二值化（Sauvola）
//   ⑦ 连通域黑点过滤
//   ⑧ A4 版面标准化（画布按原 dpi 算，等比居中留白）
//
// **为什么几何必须全部排在光度之前**（三条理由，别调换）：
//   · 几何变换会重采样像素 —— 先做光度统计（背景场、百分位），结果会随变换漂移，白算；
//   · 旋转的补白（白边或黑角）会污染背景估计；
//   · 裁边会改变内容框 —— 先裁再统计，样本才干净。
//
// **为什么黑点过滤放在二值化之后**（与预案里的初步设想相反，实现时改正的）：
//   二值化是局部自适应的，会把灰斑点也判成黑；在纯黑白图上"黑"的定义才无歧义，
//   连通域判据也才精确。放后面既更准、也少一次独立的阈值判断。
//
// 尚未接入的步骤（见方案文档，属后续增量）：
//   · ④.5 印章识别 → 掩膜被 ⑥ 与 ⑧ 共用（印章豁免，防止去污与二值化把红章吃掉）
//   · 大面积脏块 → PaperFill 区域重建
// ---------------------------------------------------------------
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;

namespace PicTool
{
    public class ArchiveOptions
    {
        public bool Deskew = true;            // ② 倾斜检测 + 转正
        public bool AutoCrop = true;          // ③ 裁黑边 + 裁白边
        public bool NormalizeLight = true;    // ④ 光照 / 背景归一化
        public double LightStrength = 1.0;    //    归一化强度 0~1
        public bool AutoLevels = true;        // ⑤ 自动色阶
        public bool Binarize = true;          // ⑥ 白底黑字
        public double SauvolaK = 0.2;         //    Sauvola 参数，与批量页默认一致
        public bool RemoveSpecks = true;      // ⑦ 连通域黑点过滤
        public int SpeckMaxArea = 0;          //    0 = 自动（按画幅面积推）
        public bool SealKeep = true;          // ★ 印章豁免：章区跳过黑白化，上色后保留红色
        public bool SpeckTextGuard = true;    // ★ 去黑点：紧贴文字的块不删
        public int SealStrength = 80;         //    上色浓度（与印章页默认值一致）
        public int SealDilate = 3;            //    掩膜外扩像素（防二值化啃掉章体外环）
        public bool ToA4Page = true;          // ⑧ A4 版面标准化
        public int ForceDpi = 0;              //    0 = 跟随原图推断；>0 强制
        public bool Landscape = false;        //    A4 横版
        public bool StainFill = true;         // ⑤.5 大面积脏块 → PaperFill 纸面重建
        public int StainMinArea = 0;          //    0 = 自动（按画幅面积推）
        public bool OrientAuto = true;        // ①.5 方向检测（横躺/倒置 → 无损 90° 转正）
        public bool MirrorCheck = true;       // ①.6 镜像检测（OCR 读不通就翻过来读，读通了才翻）
        public bool SkipSealOnly = true;      // ⓪ 纯印章页跳过（整页只有章没有正文 → 原样保存）
        public bool SkipColorCover = true;    // ⓪.5 彩色封面页跳过（整页 25%+ 饱和彩色 → 原样保存）
        public bool EdgeJunk = true;          // ①.7 页边垃圾清除（装订孔/贴边黑条/黑角）
        // ★ 去扫描黑框：修掉扫描仪漏光留在四周边框的垃圾。
        //   排在**所有判断之前**、对**所有页**生效 —— 包括会被"原样跳过"的彩色封面页
        //   和纯印章页：黑框是扫描仪漏光留下的垃圾，不是内容，跳过页也该修掉。
        //   （普通页的黑框 TrimBlank 本来就会裁，这里只是保证"没有漏网的"。）
        public bool TrimDarkFrame = true;
        // ★ 红框内容保护：被红色闭环围住的区域 = 登记框/表格的内容区，
        //   里面的黑笔填写内容（"097"、"2p4h" 这类短字块）不许当黑点删。
        //   框里那些灰底也不送纸面重建（宁可留灰，不丢内容）。CLI --no-redbox 可关。
        public bool RedBoxProtect = true;
        // ⑪ A4 之后的"回二值化"（固定阈值收灰边）。仅诊断用：CLI --no-final-bin 可关，
        //   关掉后能单独看出这一步有没有把细笔画（淡墨/手写）削掉。
        public bool FinalBinarize = true;
        // ★ 极致归档（"宁愿放弃消除小黑点，也要把字保留全了"）：打开后三件事一起生效：
        //     ① 去黑点走保守模式（只删最确定的尘点，见 DocClean.RemoveSpecks 的 safe）
        //     ② 去黑点撤销保险：删掉一个字那么大的块、或删掉的总量占页面墨量比例
        //        过高 → **整步撤销**，宁可留着黑点
        //     ③ 无装订孔证据时用 OCR 四向定向（0/90/180/270 各读一遍取最优）→ 判了就必须转
        //   代价：慢（③ 最坏多跑 3 次 OCR）、页面上会留下一些没清掉的尘点。
        public bool Thorough = false;
        // ★ A4 版面自动匹配内容的横竖：转正后内容还是横的（宽>高）时
        //   自动走横版 A4 —— 否则宽表被塞进竖版，字小到得放大才看得清。
        public bool AutoLandscape = true;
        // ★ P2 管线并行：**批量层**同时处理几页（CLI --parallel=N；UI 归档固定 2）。
        //   单页 Run() 不读它 —— 只影响 RunForTest 的文件夹循环与 UI 批量循环。
        //   8GB 内存默认 2；上限 4（并行度越高内存越涨，超过 4 收益递减）。
        public int Parallel = 2;
        // ★ 摩尔纹去除：印刷网点 × 扫描栅格的竖直周期纹（实测样本
        //   24~25 行），二值化后变黑线。竖直 FFT 陷波（详见 MoireFilter），检测不
        //   显著的页自动跳过零开销。CLI --no-moire 可关。
        public bool MoireRemove = true;

        // ★（P1-4）**可中断**：取消检查回调（null = 不检查）。
        //   批量归档时由 UI 传入（读 archiveCancel）；单页 Run / SelfTest 不传。
        //   Run 在每个大步骤边界问一次：命中立刻收工 —— 返回 **null**，
        //   调用方**绝不能**保存（半成品是坏档案）。
        //   效果：点"取消"后最多再等一个步骤，而不是整页跑完（原生 OCR 推理没有中止
        //   接口，那几秒仍要等，但不必再等后面的二值化/去黑点/A4/终筛）。
        public System.Func<bool> CancelCheck;
        // 仅诊断用（CLI --cancel-after=<ms>）：归档开始 N 毫秒后自动取消，
        // 给"中断路径"一个能脚本化复验的入口（UI 那次靠手点，没法回归）。
        public int CancelAfterMs = 0;
    }

    public class ArchiveResult
    {
        public int DpiUsed;                   // 实际采用的 dpi（推断或强制）
        public int SpecksRemoved;             // 清掉的黑点连通域个数
        public int SealsFound;                // 识别到的印章个数（0 = 无章 / 未开启）
        public int RedPixels;                 // 输出里偏红像素数（>0 说明红章保住了）
        public int InkPixels;                 // 输出里的非白像素数（判断有没有把内容弄丢）
        public int StainsFound;               // 检出并重建的大面积脏块数
        public int OrientTurns;               // 方向纠正转了几个 90°（0=没转）
        public bool Mirrored;                 // 检测到镜像并已翻转
        public double SkewDeg;                // ★ 细倾斜转正的角度（0=没转）
        public string SkewHow = "";           // ★ 倾斜角来源："几何投影"/"OCR框角"
        public bool MoireRemoved;             // ★ 摩尔纹陷波是否触发（检测不过=false=未动）
        public double MoirePeriod;            // ★ 陷波的主峰周期（行）
        public string Timings = "";           // ★ 逐段耗时剖析（P3 SIMD 找热点用，"段=ms; "拼接）
        public bool SkippedSealOnly;          // 纯印章页被跳过
        public bool SkippedColorCover;        // 彩色封面页被跳过
        public int Width, Height;             // 输出尺寸
        public bool SpecksUndone;             // ★ 去黑点被"撤销保险"整步撤销（极致档）
        public bool Cancelled;                // ★ 被取消（P1-4）：Run 返回 null，不得保存半成品
        public string OrientHow = "";         // ★ 方向是怎么判出来的（"装订孔"/"OCR四向"）
        public string Steps = "";             // 实际执行了哪些步骤（供报告与自检）
    }

    public static class ArchiveProcessor
    {
        // （P1-4）：取消信号。用异常把控制权从流水线深处（Mark 边界）拉回 Run，
        //   省掉在每个步骤后面手抄一遍 return —— 且天然覆盖以后新增的步骤（只要照例 Mark）。
        //   只在**步骤边界**抛，不会打断任何单步内部的原子操作。
        sealed class ArchiveCancelled : Exception { }

        // ---------------- 原图 dpi 推断 ----------------
        //
        // ⚠️ **扫描仪写的 dpi 元数据经常是错的、或者干脆没有。** GDI+ 读不到时
        //    会返回一个默认值，这时把它当真就会算出荒谬的 A4 画布尺寸。
        //    判据（保守）：
        //      · <= 0                    → 未知
        //      · < 100（含 72 / 96 默认值）→ 未知
        //      · > 1200                  → 当作异常值，夹回 1200
        //    判不出来统一 **200dpi 兜底** —— 阅读与 OCR 都够，也避免无意义的向上插值。
        public static int GuessDpi(Bitmap src)
        {
            double v = 0;
            try
            {
                double hx = src.HorizontalResolution, hy = src.VerticalResolution;
                v = hx > 0 ? hx : hy;
                if (v <= 0) v = hy;
            }
            catch { v = 0; }
            if (v <= 0) return 200;
            if (v < 100) return 200;                 // 72 / 96 这类典型默认值不算数
            int d = (int)Math.Round(v);
            if (d > 1200) d = 1200;
            return d;
        }

        // ---------------- 主流水线 ----------------
        // 输入位图不被修改（内部持有自己的副本）；返回的新位图由调用方负责释放。
        public static Bitmap Run(Bitmap src, ArchiveOptions opt, out ArchiveResult info)
        {
            info = new ArchiveResult();
            if (src == null) return null;
            if (opt == null) opt = new ArchiveOptions();

            int dpi = opt.ForceDpi > 0 ? opt.ForceDpi : GuessDpi(src);
            info.DpiUsed = dpi;
            // ★ 去黑点"连坐"标志：⑦ 判定"这一页删得不可信"并撤销后，
            //   ⑫ 的终筛也必须跟着停 —— 否则终筛会把同样的东西再删一遍，保险就白做了。
            bool speckVeto = false;

            Bitmap copy = new Bitmap(src);
            Bitmap cur = Imaging.Ensure24bpp(copy);
            if (cur != copy) copy.Dispose();
            // 在 try 之外声明：catch 块与 try 块是**兄弟作用域**，try 里声明的变量 catch 看不到。
            byte[] sealProtect = null;
            Bitmap sealMask = null;

            // ★ 逐段计时（性能剖析基建）：每个大步骤结束处 Mark 一次，
            //   结果落在 info.Timings（"段=ms; "拼接）—— SIMD 优化找热点全靠它，
            //   也随 Diag 落日志。纯旁路记录，不参与任何处理逻辑。
            var _swAll = System.Diagnostics.Stopwatch.StartNew();
            long _tLast = 0;
            var _tm = new System.Text.StringBuilder();
            System.Action<string> Mark = delegate(string seg)
            {
                long now = _swAll.ElapsedMilliseconds;
                _tm.Append(seg).Append('=').Append(now - _tLast).Append("ms; ");
                _tLast = now;
                // ★（P1-4）：步骤边界查一次取消 —— 命中即抛，交给下方 catch 收尾。
                //   先记计时再查：被取消的页也留下完整逐段耗时（排查用）。
                if (opt.CancelCheck != null && opt.CancelCheck()) throw new ArchiveCancelled();
            };
            try
            {
                // ★ ⓪-前：先裁掉扫描仪留下的四周黑框。
                //   放在所有判断（含"跳过"判断）**之前**：黑框是扫描垃圾不是内容，
                //   连彩色封面页、纯印章页也该修 —— 否则 68 张里就那两张还挂着一圈黑。
                if (opt.TrimDarkFrame)
                {
                    Rectangle cutR;
                    Bitmap trimmed = Imaging.TrimDarkFrame(cur, out cutR);
                    if (trimmed != null)
                    {
                        int ow = cur.Width, oh = cur.Height;
                        cur.Dispose();
                        cur = trimmed;
                        info.Steps = "去扫描黑框 " + ow + "x" + oh + " → " +
                                     cur.Width + "x" + cur.Height + "; ";
                    }
                }

                Mark("黑框");

                // ⓪ 纯印章页跳过：整页只有章、几乎没有正文 → 任何"白底黑字"
                //     处理对它都是破坏（章会被二值化吃掉、纸面纹理会被拉白）。
                //     判据见 Orient.IsSealOnlyPage。
                //     ★ 跳过的是**内容处理**，版式照做 —— 排进标准 A4，
                //     免得这几张的尺寸和其余成品对不上（黑框在上面已统一裁掉）。
                if (opt.SkipSealOnly)
                {
                    double darkRatio, redRatio;
                    if (Orient.IsSealOnlyPage(cur, out darkRatio, out redRatio))
                    {
                        info.SkippedSealOnly = true;
                        info.Steps += "纯印章页跳过(墨" + (darkRatio * 100).ToString("0.00") + "% 章" +
                                     (redRatio * 100).ToString("0.00") + "%); ";
                        // 跳过页的边缘常挂着扫描阴影带（灰色的，不够黑，TrimDarkFrame 抓不到）
                        // —— 这里再裁一次；白纸和印章的红色都不满足"灰"判据，不会误伤
                        Rectangle shS;
                        Bitmap shImg = Imaging.TrimShadowFrame(cur, out shS);
                        if (shImg != null)
                        {
                            int sw0 = cur.Width, sh0 = cur.Height;
                            cur.Dispose(); cur = shImg;
                            info.Steps += "去边缘阴影 " + sw0 + "x" + sh0 + " → " +
                                          cur.Width + "x" + cur.Height + "; ";
                        }
                        if (opt.ToA4Page)
                        {
                            bool landS = opt.Landscape || (opt.AutoLandscape && cur.Width > cur.Height);
                            Bitmap ns = Imaging.ToA4(cur, dpi, landS, false);
                            cur.Dispose(); cur = ns;
                            info.Steps += "A4 " + ns.Width + "x" + ns.Height + "@" + dpi + "dpi" +
                                          (landS ? "(横)" : "") + "; ";
                        }
                        info.RedPixels = DocClean.RedCount(cur, 40);   // 跳过页不走二值化，这里补统计
                        info.Width = cur.Width;
                        info.Height = cur.Height;
                        return cur;
                    }
                }

                // ⓪.5 彩色封面页跳过（实战教训，实测样本 红封面被毁）：
                //     整页 25%+ 是饱和彩色（红封面/彩印图版）的页，任何"白底黑字"处理
                //     都是破坏 —— 光照归一化/自动色阶会把大片彩色当"背景"拉白、
                //     二值化再吃掉剩下的（实测红底 250 万像素被洗成白纸，只剩党徽）。
                //     判据与 Orient 的方向守卫同一阈值。
                //     ★ 跳过的只是**内容处理**（二值化/色阶这些会毁彩色的），
                //     版式仍然照做 —— 排进标准 A4，尺寸与其余成品一致；黑框已在前面统一裁掉。
                if (opt.SkipColorCover)
                {
                    double coverRatio = Orient.ColoredRatio(cur);
                    if (coverRatio > 0.25)
                    {
                        info.SkippedColorCover = true;
                        info.Steps += "彩色封面页跳过(彩色占比 " + (coverRatio * 100).ToString("0") + "%); ";
                        // 同上：封面图像边缘的扫描阴影带在这里裁掉（判"灰"不判"黑"，
                        // 红封面主体是高饱和彩色，不会被当成阴影）
                        Rectangle shC;
                        Bitmap shCov = Imaging.TrimShadowFrame(cur, out shC);
                        if (shCov != null)
                        {
                            int cw0 = cur.Width, ch0 = cur.Height;
                            cur.Dispose(); cur = shCov;
                            info.Steps += "去边缘阴影 " + cw0 + "x" + ch0 + " → " +
                                          cur.Width + "x" + cur.Height + "; ";
                        }
                        if (opt.ToA4Page)
                        {
                            bool landC = opt.Landscape || (opt.AutoLandscape && cur.Width > cur.Height);
                            Bitmap nc = Imaging.ToA4(cur, dpi, landC, false);
                            cur.Dispose(); cur = nc;
                            info.Steps += "A4 " + nc.Width + "x" + nc.Height + "@" + dpi + "dpi" +
                                          (landC ? "(横)" : "") + "; ";
                        }
                        info.RedPixels = DocClean.RedCount(cur, 40);   // 跳过页不走二值化，这里补统计
                        info.Width = cur.Width;
                        info.Height = cur.Height;
                        return cur;
                    }
                }

                // ③-1 先把**深色扫描背景涂白**（黑盖板/深色垫板/压在深色台面上）
                //     —— 必须排在倾斜检测之前。否则黑角会被倾斜检测当成墨迹，
                //     角度直接判 0°；而 Imaging.TrimBlank 在这类图上也会失效
                //     （它取"最亮的角"当纸色，四角全黑时判错）。见 docs/架构.md 的遗留条目。
                Mark("跳过判断");

                //     为什么用"漫水填充"而不是"求外接框再裁"：倾斜的纸外接框≈整幅画面，
                //     裁不出东西 —— 详见 DocClean.FlattenDarkBackground 的注释。
                if (opt.AutoCrop)
                {
                    int painted = DocClean.FlattenDarkBackground(cur, 24);
                    if (painted > 0) info.Steps += "涂白深色背景 " + painted + "px; ";
                }

                Mark("涂白");

                // ①.5 方向检测：横躺/倒置的无损 90° 转正。
                //     必须排在细倾斜检测之前——页面横躺时"文字行"是竖的，倾斜检测没有意义。
                //     90°/180° 是无损重排不是重采样，放在光度步骤之后不违反"几何先于光度"红线
                //     （那条红线管的是会重采样的变换，见文件头说明）。
                if (opt.OrientAuto)
                {
                    Orient.Result or = Orient.Detect(cur);
                    int turns = or.TurnsCw;
                    info.OrientHow = or.HoleConfirmed ? "装订孔" : "";

                    // ★ 极致档：装订孔没给结论（无孔页）→ 让 OCR 把四个朝向各读一遍
                    //   （"检测到需要翻转的一定要翻转"）。
                    //   为什么放在这里而不是改 Orient.Detect 的定案：那条"无孔不转向"
                    //   是用 15 页空白表格页的误判换来的，不能推翻 —— 只有在有
                    //   **真正读懂内容**的证据（OCR）时才允许转向，几何统计一律不算。
                    if (turns == 0 && !or.HoleConfirmed && opt.Thorough)
                    {
                        int t = -1;
                        try { t = Ocr.OrientVerdict(cur); }
                        catch (Exception ex) { Diag.Log(ex, "OCR 定向异常（已忽略）"); }
                        if (t > 0) { turns = t; info.OrientHow = "OCR四向"; }
                        else info.OrientHow = "OCR未下结论";
                    }

                    info.OrientTurns = turns;
                    if (turns > 0)
                    {
                        Bitmap n = Orient.Apply(cur, turns);
                        if (n != cur) { cur.Dispose(); cur = n; }
                        info.Steps += "方向 " + turns * 90 + "°(" + info.OrientHow + "); ";
                    }
                    info.Steps += "方向检测(" + or.Note +
                                  (info.OrientHow.Length > 0 ? " → " + info.OrientHow : "") + "); ";
                }

                Mark("方向");

                // ①.6 镜像检测：OCR 读不顺才翻过来读，翻着读明显更好才翻。
                //     判据与安全模式见 Ocr.cs。OCR 不可用/证据不足/任何异常 → 原样继续。
                // ★ 顺便把正读 det 框统计出的页面倾斜角（ocrSkew）带给下面的
                //     细倾斜步骤当兜底 —— 那次 det 反正要跑，角度是白捡的，零额外 OCR 成本。
                double ocrSkew = double.NaN;
                if (opt.MirrorCheck)
                {
                    bool? mirrored = null;
                    try { mirrored = Ocr.MirrorVerdict(cur, out ocrSkew); }
                    catch (Exception ex) { Diag.Log(ex, "镜像检测异常（已忽略）"); ocrSkew = double.NaN; }
                    if (mirrored == true)
                    {
                        Bitmap n = Imaging.Flip(cur, true);
                        if (n != cur) { cur.Dispose(); cur = n; }
                        info.Mirrored = true;
                        // 水平翻转把文字行的倾斜反号（行方向 (dx,dy)→(−dx,dy)），角度跟着取反
                        if (!double.IsNaN(ocrSkew)) ocrSkew = -ocrSkew;
                        info.Steps += "镜像翻转; ";
                    }
                    else
                    {
                        info.Steps += mirrored == false ? "镜像检测(正常); " : "镜像检测(证据不足,未动); ";
                    }
                }

                Mark("镜像OCR");

                // ①.7 页边垃圾清除：装订孔/贴边黑条/黑角。
                //     必须排在方向检测之后 —— 装订孔是判方向的物理证据，先清了孔
                //     方向就没判据了。判据与安全边界见 DocClean.RemoveEdgeJunk。
                if (opt.EdgeJunk)
                {
                    int junk = DocClean.RemoveEdgeJunk(cur);
                    if (junk > 0) info.Steps += "页边垃圾 " + junk + " 处; ";
                }

                Mark("页边");

                // ③-2 倾斜检测 + 转正
                // ★ OCR 框角兜底：投影法对小角度有结构性盲区（自检挂着"-3° 失效"
                //   的 Known 项，1~5° 是用户场景的大多数）。只在**几何没测到**（|ang|<0.08，
                //   即本来就不会动）时，才用镜像判定白捡的 OCR 文字框主轴角兜底转正；
                //   几何测到时一律不掺和（零回归：能测到的场景行为一字不变）。
                //   符号：OCR 主轴角是图像坐标系 atan2(y 向下)，与 RotateFine（GDI+ 顺时针
                //   为正）反号 —— 喂 −θ；可靠窗口 |角|∈[0.5°, 8°]（细倾斜的职责范围）。
                if (opt.Deskew)
                {
                    double ang = Enhance.DetectSkewAngle(cur);
                    if (Math.Abs(ang) >= 0.08)
                    {
                        Bitmap n = Imaging.RotateFine(cur, ang, true);
                        if (n != cur) { cur.Dispose(); cur = n; }
                        info.SkewDeg = ang; info.SkewHow = "几何投影";
                        info.Steps += "转正 " + ang.ToString("0.00") + "°; ";
                    }
                    else if (!double.IsNaN(ocrSkew) && Math.Abs(ocrSkew) >= 0.5 && Math.Abs(ocrSkew) <= 8.0)
                    {
                        double a = Math.Round(-ocrSkew, 2);
                        if (Math.Abs(a) >= 0.08)
                        {
                            Bitmap n = Imaging.RotateFine(cur, a, true);
                            if (n != cur) { cur.Dispose(); cur = n; }
                            info.SkewDeg = a; info.SkewHow = "OCR框角";
                            info.Steps += "转正 " + a.ToString("0.00") + "°(OCR框角); ";
                        }
                        else info.Steps += "转正 无需（" + ang.ToString("0.00") + "°）";
                    }
                    else info.Steps += "转正 无需（" + ang.ToString("0.00") + "°）; ";
                }

                Mark("倾斜");

                // ③-3 裁白边（纸自己的空白边 → 内容框）
                if (opt.AutoCrop)
                {
                    Rectangle content;
                    Bitmap n = Imaging.TrimBlank(cur, 24, out content);
                    if (n != cur) { cur.Dispose(); cur = n; }
                    info.Steps += "裁白边 → " + n.Width + "x" + n.Height + "; ";
                }

                Mark("裁边");

                // ③-3.5 摩尔纹去除：必须在二值化之前 —— 网点拍频纹在
                //   灰度图上只有几级灰度，二值化后才变成肉眼可见的黑横线，到那边就晚了。
                //   位置在**所有几何之后**（遵守"几何先于光度"红线）；检测不显著的页
                //   原样跳过（零开销）。陷波只调亮度不动色相（三通道同加修正量）。
                if (opt.MoireRemove)
                {
                    try
                    {
                        double mPeriod;
                        // ★（P2-6）：页间并行时（Parallel>1）页内退化串行 —— 免得
                        //   线程数相乘在 4 核机上互相抢占。单页运行（=1）时页内照常并行。
                        if (MoireFilter.RemoveInPlace(cur, out mPeriod, opt.Parallel <= 1))
                        {
                            info.MoireRemoved = true;
                            info.MoirePeriod = mPeriod;
                            info.Steps += "摩尔纹陷波(" + mPeriod.ToString("0") + "行); ";
                        }
                    }
                    catch (Exception ex) { Diag.Log(ex, "摩尔纹陷波失败（已忽略，不影响归档）"); }
                }

                Mark("陷波");

                // ③-4 大面积脏块 → PaperFill 纸面重建
                //     为什么必须在光照归一化**之前**：归一化用缩略图闭运算估计"纸面应该多亮"，
                //     而大块污渍比那个核大得多 —— 会被当成背景一起拉白，之后就没得检了
                //     （第一版排在归一化后面，实测 0 检出，就是这个原因）。
                //     为什么用重建而不是清除：脏块面积与汉字重叠（实测 400~600 vs 400~1800），
                //     按面积清必然连字删；PaperFill 不生成内容、只还纸面，是这个场景的正解。
                if (opt.StainFill)
                {
                    int stains;
                    Bitmap smask = DocClean.DetectStainMask(cur, opt.StainMinArea, 0, 0, out stains);

                    // ★ 重建前留一份底：PaperFill 的接缝平差会改**掩膜外一圈**，
                    //   所以光是"把红像素/框内从掩膜里扣掉"还不够 ——
                    //   实测一张红色登记框页：重建前红像素 7135，重建后只剩 1267（挨着污渍的
                    //   红框边被顺手啃掉）。这里先克隆一份，重建完按位还原。红内容与红框内部共用这张底。
                    byte[] redPre = null, boxPre = null;
                    Bitmap preSrc = null;
                    if (opt.SealKeep || opt.RedBoxProtect)
                    {
                        try
                        {
                            if (opt.SealKeep)
                            {
                                byte[] rp = DocClean.RedProtectMask(cur, 40);
                                if (DocClean.MaskCount(rp) > 0) redPre = rp;
                            }
                            if (opt.RedBoxProtect)
                            {
                                // 外扩 4px：PaperFill 的接缝平差啃的是**掩膜外一圈**，
                                // 还原范围得比"框内"再宽一点（实测：登记框里
                                // 手写 "09" 的一笔就是被这圈平差改掉、再被二值化判成白的）
                                int inBox;
                                byte[] bp = DocClean.RedEncloseMask(cur, 40, 0, out inBox);
                                if (bp != null && inBox > 0)
                                    boxPre = DocClean.DilateMask(bp, cur.Width, cur.Height, 4);
                            }
                            if (redPre != null || boxPre != null) preSrc = (Bitmap)cur.Clone();
                        }
                        catch { redPre = null; boxPre = null; preSrc = null; }
                    }

                    if (smask != null)
                    {
                        // ★ 红色内容不是污渍：红笔手写 / 红色表格线 / 红头红字在 DetectStainMask
                        //   眼里就是"大面积暗块"，送进 PaperFill 会被**整块涂成纸面**
                        //   （实测：加 --no-stain 关掉脏块重建，红色立刻从 0.001% 回到 0.184%）。
                        //   先把"偏红像素"从重建掩膜里扣掉，缩小重建范围。
                        try
                        {
                            byte[] mb = DocClean.MaskToBytes(smask, cur.Width, cur.Height);
                            byte[] red = DocClean.RedProtectMask(cur, 40);
                            int cut = 0;
                            for (int i = 0; i < mb.Length && i < red.Length; i++)
                                if (mb[i] != 0 && red[i] != 0) { mb[i] = 0; cut++; }
                            if (cut > 0)
                            {
                                Bitmap nm = DocClean.BytesToMask(mb, cur.Width, cur.Height);
                                smask.Dispose();
                                smask = nm;
                                info.Steps += "脏块剔除红色 " + cut + "px; ";
                            }
                        }
                        catch { }   // 剔除失败不该让整条流水线挂掉

                        // ★ 红框内部不做纸面重建：框里的"灰底"多半是框线阴影／
                        //   褪色红的晕，在 DetectStainMask 眼里就是一块大面积暗块；送进 PaperFill
                        //   会把框里黑笔填写的字样块**连内容一起涂成纸面**。宁可留灰，不丢内容。
                        if (opt.RedBoxProtect)
                        {
                            try
                            {
                                int inBox;
                                byte[] inner = DocClean.RedEncloseMask(cur, 40, 0, out inBox);
                                if (inner != null && inBox > 0)
                                {
                                    byte[] mb2 = DocClean.MaskToBytes(smask, cur.Width, cur.Height);
                                    int cut2 = 0;
                                    for (int i = 0; i < mb2.Length && i < inner.Length; i++)
                                        if (mb2[i] != 0 && inner[i] != 0) { mb2[i] = 0; cut2++; }
                                    if (cut2 > 0)
                                    {
                                        Bitmap nm2 = DocClean.BytesToMask(mb2, cur.Width, cur.Height);
                                        smask.Dispose();
                                        smask = nm2;
                                        info.Steps += "脏块剔除红框内 " + cut2 + "px; ";
                                    }
                                }
                            }
                            catch { }   // 同上：保护失败不该让整条流水线挂掉
                        }

                        // 扣完红色后如果已经没有实质污渍了，就不做重建（空掩膜没有意义）
                        bool empty;
                        try { empty = DocClean.MaskCount(DocClean.MaskToBytes(smask, cur.Width, cur.Height)) < 64; }
                        catch { empty = true; }
                        if (empty)
                        {
                            smask.Dispose();
                        }
                        else
                        {
                            try
                            {
                                Bitmap rebuilt = PaperFill.Run(cur, smask, null);
                                if (rebuilt != null && rebuilt != cur)
                                {
                                    cur.Dispose();
                                    cur = rebuilt;
                                    info.StainsFound += stains;
                                    info.Steps += "脏块重建 " + stains + " 块; ";
                                }
                            }
                            finally { smask.Dispose(); }
                        }
                    }

                    // 重建后按位还原：① 红色内容（红框、红字回到重建前的样子）
                    //                 ② 红框内部（框里的填写内容一像素都不许被重建动到）
                    if (preSrc != null)
                    {
                        try
                        {
                            if (redPre != null)
                            {
                                int nRed = DocClean.RestoreRedPixels(cur, preSrc, redPre);
                                if (nRed > 0) info.Steps += "红色还原 " + nRed + "px; ";
                            }
                            if (boxPre != null)
                            {
                                int nBox = DocClean.RestorePixelsMasked(cur, preSrc, boxPre);
                                if (nBox > 0) info.Steps += "红框内还原 " + nBox + "px; ";
                            }
                        }
                        catch { }
                        finally { preSrc.Dispose(); }
                    }
                }

                Mark("重建");

                // ④ ★ 印章识别（印章豁免的第 1 步）
                //    位置很讲究：放在**光度处理之前** —— 此时颜色最接近原扫描件，
                //    红度分割判得准；后面的自动色阶是**逐通道**百分位拉伸，会改动通道间关系。
                //    掩膜在 ⑦⑧ 两处共用（这正是方案文档里说的"唯一非线性处"）。
                if (opt.SealKeep && opt.Binarize)
                {
                    Bitmap hint = FullWhiteHint(cur.Width, cur.Height);
                    int cnt = 0;
                    Bitmap frame;
                    try
                    {
                        sealMask = SealTool.Detect(cur, hint, out cnt, out frame);
                        info.SealsFound = cnt;
                    }
                    catch
                    {
                        sealMask = null;   // 识别失败不该让整条流水线挂掉
                        cnt = 0;
                    }
                    finally { hint.Dispose(); }
                    if (sealMask != null)
                    {
                        // ★ 关键一步：SealTool 的形状线索在**整页**自动找章时会把汉字/表格格子判成章
                        //   （实测：一份没有印章的灰文档被判出 24 枚，大片文字被涂红且跳过黑白化）。
                        //   所以再过一道"必须有成片红色证据 + 够大"的过滤。
                        //   详见 DocClean.FilterSealMask 的注释。
                        int kept = 0;
                        byte[] raw = DocClean.MaskToBytes(sealMask, cur.Width, cur.Height);
                        byte[] filtered = DocClean.FilterSealMask(cur, raw, cur.Width, cur.Height, 40, 0, 0, out kept);
                        sealMask.Dispose();
                        sealMask = null;
                        if (kept > 0)
                        {
                            sealMask = DocClean.BytesToMask(filtered, cur.Width, cur.Height);
                            info.SealsFound = kept;
                            sealProtect = DocClean.DilateMask(filtered, cur.Width, cur.Height, opt.SealDilate);
                        }
                        info.Steps += "印章 " + kept + " 枚(原始检出 " + cnt + "); ";
                    }
                }

                // ④.5 ★ 红色内容保护：红笔手写、红色表格线、红头文件的
                //      红字/红线 —— 在档案件里**全是内容**，绝不能丢颜色。
                //      为什么必须在这一步做：⑦ 二值化**只看亮度**，红（灰度约 130）会被压成黑；
                //      颜色一旦在第 ⑦ 步丢了，后面 ⑪ 的"保红"（ThresholdKeepRed）也救不回来
                //      —— 实测一张红色登记框页：处理前该区域红像素 2.27%，处理后 0.000%
                //      （红框整条变黑）。而 ⑦ 原来只保护"印章掩膜"，红色表格线不是章，
                //      复筛直接拒掉（报告里"印章 56 枚(原始检出) → 0 枚"就是这么来的）。
                //      做法：把"偏红像素"并进 ⑦⑧ 共用的保护掩膜（与非印章红内容同一判据）。
                if (opt.SealKeep && opt.Binarize)
                {
                    try
                    {
                        byte[] red = DocClean.RedProtectMask(cur, 40);
                        int nRed = DocClean.MaskCount(red);
                        if (nRed > 0)
                        {
                            // 外扩 2px：二值化的边界会啃进红笔画边缘，扩一圈才不会留毛刺
                            red = DocClean.DilateMask(red, cur.Width, cur.Height, 2);
                            if (sealProtect == null) sealProtect = red;
                            else
                                for (int i = 0; i < sealProtect.Length && i < red.Length; i++)
                                    if (red[i] != 0) sealProtect[i] = 255;
                            info.Steps += "红色内容保护 " + nRed + "px; ";
                        }
                    }
                    catch { }   // 保护失败不该让整条流水线挂掉
                }

                Mark("印章");

                // ⑤ 光照 / 背景归一化（去灰底、去阴影、去光照不均）
                if (opt.NormalizeLight)
                {
                    DocClean.NormalizeIlluminationInPlace(cur, opt.LightStrength, 2.5);
                    info.Steps += "光照归一化; ";
                }

                Mark("光照");

                // ⑥ 自动色阶
                if (opt.AutoLevels)
                {
                    Imaging.AutoEnhanceInPlace(cur);
                    info.Steps += "自动色阶; ";
                }

                Mark("色阶");

                // ⑦ 白底黑字二值化 —— 印章区跳过（否则红章会变成黑块）
                if (opt.Binarize)
                {
                    Enhance.BinarizeInPlace(cur, opt.SauvolaK, sealProtect);
                    info.Steps += "二值化; ";
                }

                Mark("二值化");

                // ⑧ 连通域黑点过滤（必须在二值化之后，见文件头说明）
                //    印章区内的连通域一律视为内容 —— 章里的五角星尖角、环内文字、
                //    褪色断裂的笔画看着就像"孤立小黑块"，不保护会被当黑点直接删掉。
                if (opt.RemoveSpecks && opt.Binarize)
                {
                    // ★ 红框内容保护（"红框子里的黑色字样块也要保护，不能删"）：
                    //   框线是浅红细线，在"墨"的语义里不算内容 → 框里的黑笔填写块在 gap 环带内
                    //   找不到别的墨（判成孤立），又紧实近方（正好是 R1 的靶心）→ 整块被抹。
                    //   这里把"被红色闭环围住的内部"并进保护掩膜（见 RedEncloseMask）。
                    byte[] speckProt = DocClean.OrMask(sealProtect, RedBoxMask(cur, opt, info, "红框内容保护 "));

                    // ★ 撤销保险（极致档）：去黑点**之前先留底**，去完比对，
                    //   删得太狠就把这一步整个撤销 —— 留着黑点只是不好看，吃掉字是事故。
                    //   两条判据命中任一即撤销：
                    //     A 删掉了"一个字那么大"的块（≥ 字面积门槛）—— 吃字的典型特征；
                    //     B 删掉的总像素占页面墨量比例过高（2%）
                    //       —— 墨太少（<2000px，多是空白背面页）时不看比例，免得噪声误触发。
                    Bitmap preSpeck = null;
                    int inkBefore = 0;
                    if (opt.Thorough)
                    {
                        try { preSpeck = (Bitmap)cur.Clone(); } catch { preSpeck = null; }
                        inkBefore = DocClean.InkCount(cur, 128);
                    }

                    // 极致档传 safe=true：只删最确定的尘点（见 DocClean 的 safe 模式说明）
                    // ★ ：改用 SpeckStats 返回值（不再读 DocClean 的静态字段）——
                    //   并行归档下静态字段会被邻页覆盖，撤销保险据此决策会误判。
                    DocClean.SpeckStats sp1 = DocClean.RemoveSpecksEx(cur, opt.SpeckMaxArea, 0, speckProt,
                                                              opt.SpeckTextGuard, opt.Thorough);
                    info.SpecksRemoved = sp1.Removed;
                    string why = "";
                    bool undo = false;
                    if (opt.Thorough && preSpeck != null && info.SpecksRemoved > 0)
                        undo = SpeckTooRisky(sp1.RemovedPixels, sp1.RemovedMaxArea,
                                             inkBefore, cur.Width, cur.Height, out why);
                    if (undo) speckVeto = true;   // 这一页的去黑点不可信 → 终筛也别再删了

                    if (undo)
                    {
                        cur.Dispose();
                        cur = preSpeck;
                        preSpeck = null;
                        info.SpecksRemoved = 0;
                        info.SpecksUndone = true;
                        info.Steps += "去黑点已撤销(" + why + "); ";
                    }
                    else
                    {
                        int keptPx = sp1.RemovedPixels;
                        if (preSpeck != null) { preSpeck.Dispose(); preSpeck = null; }
                        info.Steps += "去黑点 " + info.SpecksRemoved + " 处";
                        if (sp1.TextGuarded > 0)
                            info.Steps += "(护字 " + sp1.TextGuarded + " 处)";
                        if (opt.Thorough && keptPx > 0) info.Steps += "[保守 " + keptPx + "px]";
                        info.Steps += "; ";
                    }
                }

                Mark("去黑点");

                // ⑨ 印章上色（此时坐标与掩膜仍然对齐：唯一会改几何的 A4 排版还没做）
                if (sealMask != null && info.SealsFound > 0)
                {
                    try
                    {
                        SealTool.Colorize(cur, sealMask, opt.SealStrength);
                        info.Steps += "印章上色; ";
                    }
                    catch { }
                }

                Mark("上色");

                // ⑩ A4 版面标准化（fill=false：等比缩放居中留白，**绝不拉变形**）
                if (opt.ToA4Page)
                {
                    // ★ 版面自动匹配内容的横竖：转正后内容还是横的
                    //   （宽 > 高）就走横版 A4 —— 硬塞进竖版会把宽表缩到看不清。
                    //   opt.Landscape 为真时保持强制横版，不被覆盖。
                    bool land = opt.Landscape || (opt.AutoLandscape && cur.Width > cur.Height);
                    Bitmap n = Imaging.ToA4(cur, dpi, land, false);
                    cur.Dispose(); cur = n;
                    info.Steps += "A4 " + n.Width + "x" + n.Height + "@" + dpi + "dpi" +
                                  (land ? "(横)" : "") + "; ";

                    // ⑪ 缩放用的是双三次插值，会在边缘磨出一圈灰过渡；归档件要求纯白底纯黑字，
                    //     所以最后再用固定阈值收一次。（实现时才发现的必要一步）
                    //
                    //     **印章区不能用掩膜来保护** —— A4 是等比缩放，掩膜得跟着缩放平移才能对上，
                    //     而变换参数藏在 ToA4 里，复刻一份就是埋"改了 ToA4 就静默错位"的雷。
                    //     改成**用颜色自己认自己**：偏红的像素原色保留。不用掩膜，也就不会错位。
                    if (opt.Binarize && opt.FinalBinarize)
                    {
                        if (opt.SealKeep)
                        {
                            info.InkPixels = DocClean.ThresholdKeepRed(cur, 128, 40);
                            info.Steps += "回二值化(保红); ";
                        }
                        else
                        {
                            info.InkPixels = DocClean.ThresholdInPlace(cur, 128);
                            info.Steps += "回二值化; ";
                        }
                        info.RedPixels = DocClean.RedCount(cur, 40);
                    }

                    // ⑫ 终筛去杂点：回二值化的固定阈值会把双三次缩放的
                    //     灰边重新变成一批小黑点，v2.5.0 之前这一批从来没有人清过。
                    //     RemoveSpecksFinal 把偏红像素当内容（印章豁免靠颜色自认，见 ⑪）。
                    //     ★ 极致档：⑦ 已经判过"这一页删得不可信"时（speckVeto），这里直接跳过
                    //       —— 否则终筛会把 ⑦ 撤销掉的那一批再删一遍，保险形同虚设（实测踩到）。
                    if (opt.RemoveSpecks && !speckVeto)
                    {
                        // 红框内部同样护住（A4 缩放后重新现算：颜色自认，不会错位）
                        byte[] finProt = RedBoxMask(cur, opt, info, "红框内容保护(终) ");

                        // 极致档：终筛同样留底 + 撤销保险（A4 缩放后墨量重新统计）
                        Bitmap preFin = null;
                        int inkBeforeFin = 0;
                        if (opt.Thorough)
                        {
                            try { preFin = (Bitmap)cur.Clone(); } catch { preFin = null; }
                            inkBeforeFin = DocClean.InkCount(cur, 128);
                        }

                        DocClean.SpeckStats sp2 = DocClean.RemoveSpecksFinalEx(cur, opt.SpeckMaxArea,
                                                            opt.SpeckTextGuard, finProt, opt.Thorough);
                        int n2 = sp2.Removed;
                        string why2;
                        if (opt.Thorough && preFin != null && n2 > 0 &&
                            SpeckTooRisky(sp2.RemovedPixels, sp2.RemovedMaxArea,
                                          inkBeforeFin, cur.Width, cur.Height, out why2))
                        {
                            cur.Dispose();
                            cur = preFin;
                            preFin = null;
                            info.SpecksUndone = true;
                            info.Steps += "终筛去杂点已撤销(" + why2 + "); ";
                        }
                        else
                        {
                            if (preFin != null) { preFin.Dispose(); preFin = null; }
                            info.SpecksRemoved += n2;
                            info.Steps += "去杂点(终) " + n2 + " 处";
                            if (sp2.TextGuarded > 0)
                                info.Steps += "(护字 " + sp2.TextGuarded + " 处)";
                            info.Steps += "; ";
                        }
                    }
                    else if (speckVeto)
                    {
                        info.Steps += "终筛去杂点已跳过(承接撤销); ";
                    }

                    // ⑬ 页边垃圾终扫：二值化会把孔的灰晕、碎裂的孔缘
                    //     重新析出成小黑斑 —— 终图上再跑一遍孔簇/角块清理
                    //     （finalPass：只清孔和角块，不碰贴边细长条，防误伤表格线）。
                    if (opt.EdgeJunk)
                    {
                        int j2 = DocClean.RemoveEdgeJunk(cur, true);
                        if (j2 > 0) info.Steps += "页边垃圾(终) " + j2 + " 处; ";
                    }
                }

                Mark("A4终筛");

                info.Timings = _tm.ToString();
                Diag.Info("归档剖析", info.Timings);

                if (sealMask != null) { try { sealMask.Dispose(); } catch { } }
                // ：这里原本又赋一次 info.Timings（与上方重复，`_tm` 期间未变），删掉。
                info.Width = cur.Width;
                info.Height = cur.Height;
                return cur;
            }
            catch (ArchiveCancelled)
            {
                // ★（P1-4）：用户取消。**返回 null** —— 半成品交给调用方保存就是坏档案；
                //   调用方见到 info.Cancelled / null 必须丢弃，不写盘。
                info.Cancelled = true;
                info.Timings = _tm + "（已取消）";
                info.Steps += "归档已取消（未输出）; ";
                try { if (sealMask != null) sealMask.Dispose(); } catch { }
                try { cur.Dispose(); } catch { }
                Diag.Info("归档", "已取消，未输出半成品");
                return null;
            }
            catch
            {
                info.Timings = _tm + "（异常中断）";
                try { if (sealMask != null) sealMask.Dispose(); } catch { }
                try { cur.Dispose(); } catch { }
                throw;
            }
        }

        // 去黑点的"撤销判据"（极致档专用，⑦ 与 ⑫ 共用）。
        // 命中任一即判定"这一步删得不可信"：
        //   A 删掉了"一个字那么大"的块（≥ 字面积门槛）—— 吃字的典型特征；
        //   B 删掉的总像素占页面墨量比例过高（2%）—— 删得太狠。
        // 页面墨太少（<2000px，多是空白背面页）时不看比例，免得噪声误触发。
        static bool SpeckTooRisky(int removedPx, int removedMaxArea, int inkBefore, int w, int h, out string why)
        {
            why = "";
            // 字高沿用 RemoveSpecksImpl 的估计（短边/45），一个汉字的实心部分约占字高的 35%
            // （"口""日"这类满格字实测 40%+）
            int mhEst = Math.Min(w, h) / 45;
            if (mhEst < 16) mhEst = 16;
            int charArea = (int)(mhEst * (double)mhEst * 0.35);
            if (removedMaxArea >= charArea)
            {
                why = "单块" + removedMaxArea + "px ≥ 字面积" + charArea + "px";
                return true;
            }
            if (inkBefore >= 2000 && removedPx > inkBefore * 0.02)
            {
                why = "删了墨量的 " + (100.0 * removedPx / inkBefore).ToString("0.0") + "%";
                return true;
            }
            return false;
        }

        // 红框内部保护掩膜。**每次在"当前这张图"上现算**，绝不跨步骤缓存 ——
        // A4 缩放会改几何，缓存下来必然错位（与 ⑪ "保红靠颜色自己认自己"同一个理由）。
        // 失败一律返回 null：保护失败不该让整条流水线挂掉（保护是加强项，不是必需品）。
        static byte[] RedBoxMask(Bitmap cur, ArchiveOptions opt, ArchiveResult info, string tag)
        {
            if (!opt.RedBoxProtect) return null;
            try
            {
                int inside;
                byte[] m = DocClean.RedEncloseMask(cur, 40, 0, out inside);
                if (m != null && inside > 0)
                {
                    if (info != null) info.Steps += tag + inside + "px; ";
                    return m;
                }
            }
            catch { }
            return null;
        }

        // 全白 8bpp 提示掩膜：让 SealTool 在全图范围内搜印章
        static Bitmap FullWhiteHint(int w, int h)
        {
            PixelBuffer buf = new PixelBuffer(w, h, 1);
            for (int i = 0; i < buf.Data.Length; i++) buf.Data[i] = 255;
            return BitmapBridge.BufferToMask(buf);
        }

        // ---------------- 命令行入口 ----------------
        // --archive <输入文件或文件夹> <输出文件或文件夹> [强制dpi] [选项]
        //   选项：--no-deskew --no-crop --no-light --no-levels --no-bin --no-specks --no-a4 --landscape
        // 输出目录不存在会自动建。报告写入 %TEMP%\PicTool-archive.txt。
        static readonly string[] Exts = { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff" };

        public static string RunForTest(string[] args)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(AppInfo.Name + " 「一键归档」流水线报告   版本 " + AppInfo.Version);
            sb.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine();
            if (args.Length < 3)
            {
                sb.AppendLine("用法：--archive <输入文件或文件夹> <输出文件或文件夹> [强制dpi] [选项]");
                return sb.ToString();
            }

            ArchiveOptions opt = ParseOptions(args, 3);
            string inPath = args[1], outPath = args[2];

            // ★（P1-4）：--cancel-after=<ms> 时挂上取消回调（UI 里那根是 archiveCancel）。
            if (opt.CancelAfterMs > 0)
            {
                int t0 = Environment.TickCount;
                opt.CancelCheck = delegate() { return (Environment.TickCount - t0) >= opt.CancelAfterMs; };
            }

            try
            {
                if (Directory.Exists(inPath))
                {
                    if (!Directory.Exists(outPath)) Directory.CreateDirectory(outPath);
                    string[] files = Directory.GetFiles(inPath);
                    int done = 0, fail = 0, skipped = 0, cancelled = 0;
                    sb.AppendLine("输入文件夹：" + inPath);
                    sb.AppendLine("输出文件夹：" + outPath);
                    sb.AppendLine("选项：" + Describe(opt));
                    sb.AppendLine();
                    // ★ P2 管线并行：页间并行、报告保序 —— 先按文件序号各自算进
                    //   lines[i]，算完再按顺序写报告（对比脚本/逐字节校验都依赖报告顺序）。
                    //   OCR 推理在 Ocr.OcrGate 上整段串行，其余步骤照常并行。
                    string[] lines = new string[files.Length];
                    int workers = Math.Max(1, Math.Min(4, opt.Parallel));
                    System.Threading.Tasks.Parallel.For(0, files.Length,
                        new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = workers },
                        delegate(int i)
                    {
                        if (!IsImage(files[i])) return;
                        string outp = Path.Combine(outPath, Path.GetFileName(files[i]));
                        lines[i] = One(files[i], outp, opt);
                    });
                    for (int i = 0; i < files.Length; i++)
                    {
                        if (lines[i] == null) continue;
                        sb.AppendLine(lines[i]);
                        if (lines[i].StartsWith("OK")) done++;
                        else if (lines[i].StartsWith("SKIP")) skipped++;
                        else if (lines[i].StartsWith("CANCEL")) cancelled++;
                        else fail++;
                    }
                    sb.AppendLine();
                    sb.AppendLine("合计：成功 " + done + "，纯印章页跳过 " + skipped +
                                  "，已取消 " + cancelled + "，失败 " + fail);
                }
                else if (File.Exists(inPath))
                {
                    sb.AppendLine("输入：" + inPath);
                    sb.AppendLine("选项：" + Describe(opt));
                    sb.AppendLine();
                    sb.AppendLine(One(inPath, outPath, opt));
                }
                else
                {
                    sb.AppendLine("输入路径不存在：" + inPath);
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("异常：" + ex.Message);
                sb.AppendLine(ex.StackTrace);
            }
            return sb.ToString();
        }

        static string One(string inPath, string outPath, ArchiveOptions opt)
        {
            Bitmap src = null, dst = null;
            try
            {
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                src = Imaging.LoadFile(inPath);
                ArchiveResult info;
                dst = Run(src, opt, out info);
                if (info.Cancelled || dst == null)
                {
                    // ★（P1-4）：取消 → Run 返回 null。**不写盘**（半成品是坏档案）。
                    sw.Stop();
                    return "CANCEL " + Path.GetFileName(inPath) +
                           "   已取消（未输出半成品）   " + sw.ElapsedMilliseconds + "ms";
                }
                Imaging.SaveImage(dst, outPath, 92);
                sw.Stop();
                if (info.SkippedSealOnly)
                    return "SKIP " + Path.GetFileName(inPath) +
                           "   纯印章页已原样保存（" + info.Steps + "）   " + sw.ElapsedMilliseconds + "ms";
                if (info.SkippedColorCover)
                    return "SKIP " + Path.GetFileName(inPath) +
                           "   彩色封面页已原样保存（" + info.Steps + "）   " + sw.ElapsedMilliseconds + "ms";
                return "OK   " + Path.GetFileName(inPath) +
                       "   原 " + src.Width + "x" + src.Height + " → A4 " + info.Width + "x" + info.Height +
                       " @" + info.DpiUsed + "dpi" +
                       (info.OrientTurns > 0 ? "   方向 " + info.OrientTurns * 90 + "°(" + info.OrientHow + ")" : "") +
                       (info.Mirrored ? "   镜像已翻" : "") +
                       (info.SpecksUndone ? "   去黑点已撤销(保字)" : "   去黑点 " + info.SpecksRemoved) +
                       "   印章 " + info.SealsFound + " 枚/红像素 " + info.RedPixels +
                       "   脏块重建 " + info.StainsFound + " 块" +
                       "   " + sw.ElapsedMilliseconds + "ms   [" + info.Steps + "]";
            }
            catch (Exception ex)
            {
                return "FAIL " + Path.GetFileName(inPath) + "   " + ex.Message;
            }
            finally
            {
                if (src != null) { try { src.Dispose(); } catch { } }
                if (dst != null) { try { dst.Dispose(); } catch { } }
            }
        }

        static ArchiveOptions ParseOptions(string[] args, int from)
        {
            ArchiveOptions o = new ArchiveOptions();
            for (int i = from; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--no-deskew") o.Deskew = false;
                else if (a == "--no-crop") o.AutoCrop = false;
                else if (a == "--no-light") o.NormalizeLight = false;
                else if (a == "--no-levels") o.AutoLevels = false;
                else if (a == "--no-bin") o.Binarize = false;
                else if (a == "--no-specks") o.RemoveSpecks = false;
                else if (a == "--no-a4") o.ToA4Page = false;
                else if (a == "--landscape") o.Landscape = true;
                else if (a == "--no-seal") o.SealKeep = false;
                // 关掉"文字邻域保护"（排查用：关了以后去黑点会连紧贴文字的小块一起删）
                else if (a == "--no-speck-guard") o.SpeckTextGuard = false;
                // 关掉"红框内容保护"（排查用：红框里的填写内容不再豁免，可对照看误删来自哪一步）
                else if (a == "--no-redbox") o.RedBoxProtect = false;
                else if (a.StartsWith("--seal-strength="))
                {
                    int v; if (int.TryParse(a.Substring(16), out v)) o.SealStrength = v;
                }
                else if (a.StartsWith("--speck-max="))
                {
                    int v; if (int.TryParse(a.Substring(12), out v)) o.SpeckMaxArea = v;
                }
                else if (a == "--no-stain") o.StainFill = false;
                // 关掉"页边垃圾清除"（排查用：装订孔/贴边黑条/黑角/卷边折痕都不清了）
                else if (a == "--no-edge") o.EdgeJunk = false;
                // 关掉 A4 后的"回二值化"（排查用：单独看这一步有没有削掉细笔画/淡手写）
                else if (a == "--no-final-bin") o.FinalBinarize = false;
                else if (a == "--no-orient") o.OrientAuto = false;
                else if (a == "--no-mirror") o.MirrorCheck = false;
                // ★ 极致归档：保守去黑点 + 撤销保险 + OCR 四向定向（慢，准确性优先）
                else if (a == "--thorough") o.Thorough = true;
                // 关掉"内容横竖自动匹配版面"（A4 一律竖版）
                else if (a == "--no-autoland") o.AutoLandscape = false;
                else if (a == "--no-skip-seal") o.SkipSealOnly = false;
                else if (a == "--no-skip-cover") o.SkipColorCover = false;
                else if (a.StartsWith("--stain-min="))
                {
                    int v; if (int.TryParse(a.Substring(12), out v)) o.StainMinArea = v;
                }
                // ★ P2 管线并行：批量时同时处理几页（1~4，默认 2；1 = 退回单线程）
                else if (a.StartsWith("--parallel="))
                {
                    int v; if (int.TryParse(a.Substring(11), out v)) o.Parallel = Math.Max(1, Math.Min(4, v));
                }
                // 仅诊断用：归档开始 N 毫秒后自动取消 —— 给 P1-4 的中断路径一个可脚本化复验入口
                else if (a.StartsWith("--cancel-after="))
                {
                    int v; if (int.TryParse(a.Substring(15), out v)) o.CancelAfterMs = Math.Max(0, v);
                }
                // 关掉摩尔纹陷波（排查用：单独看横纹是不是陷波消掉的）
                else if (a == "--no-moire") o.MoireRemove = false;
                else
                {
                    int d;
                    if (int.TryParse(a, out d) && d > 0) o.ForceDpi = d;
                }
            }
            return o;
        }

        static string Describe(ArchiveOptions o)
        {
            return "dpi=" + (o.ForceDpi > 0 ? o.ForceDpi.ToString() + "(强制)" : "跟随原图") +
                   " 方向=" + (o.OrientAuto ? "自动" : "关") +
                   " 镜像=" + (o.MirrorCheck ? "OCR" : "关") +
                   " 印章页跳过=" + (o.SkipSealOnly ? "开" : "关") +
                   " 彩色封面跳过=" + (o.SkipColorCover ? "开" : "关") +
                   " 光照=" + (o.NormalizeLight ? "开" : "关") +
                   " 色阶=" + (o.AutoLevels ? "开" : "关") +
                   " 二值化=" + (o.Binarize ? "开" : "关") +
                   " 去黑点=" + (o.RemoveSpecks ? "开" : "关") + (o.SpeckMaxArea > 0 ? "(阈值" + o.SpeckMaxArea + ")" : "(阈值自动)") +
                   " 脏块重建=" + (o.StainFill ? "开" : "关") + (o.StainMinArea > 0 ? "(阈值" + o.StainMinArea + ")" : "") +
                   " 印章=" + (o.SealKeep ? "保留红色(浓度" + o.SealStrength + ")" : "关") +
                   " 护字=" + (o.SpeckTextGuard ? "开" : "关") +
                   " 红框保护=" + (o.RedBoxProtect ? "开" : "关") +
                   " 页边垃圾=" + (o.EdgeJunk ? "开" : "关") +
                   " 档位=" + (o.Thorough ? "极致(保守去黑点+撤销保险+OCR定向)" : "普通") +
                   " A4=" + (o.ToA4Page ? "开" : "关") + (o.Landscape ? "(横版)" : "(竖版)") +
                   (o.AutoLandscape ? "(内容横竖自适应)" : "") +
                   " 并行=" + Math.Max(1, Math.Min(4, o.Parallel)) + "页" +
                   " 摩尔纹=" + (o.MoireRemove ? "陷波" : "关");
        }

        static bool IsImage(string p)
        {
            string e = Path.GetExtension(p);
            if (e == null) return false;
            e = e.ToLowerInvariant();
            for (int i = 0; i < Exts.Length; i++) if (Exts[i] == e) return true;
            return false;
        }
    }
}
