// Ocr.cs — OCR 方向裁判（PP-OCRv3 det + rec，跑在本工具自带的 onnxruntime 上）
//
// 只回答一个问题：**这一页是不是镜像的。**
//
// 为什么镜像不用几何判据：真实公文带落款、缩进、参差行，纯几何统计会误报
//   （实测某 upright 页曾被误判成镜像）。OCR 是直接证据——读得顺就是正的。
//
// 为什么可行（用 RapidOCR 在真实档案页上量出来的规律）：
//   · det（DBNet）只找"哪里有字"，对镜像不敏感——镜像页照样出文本框；
//   · rec 读镜像文字时置信度崩塌：正常 363 字 @0.90 → 镜像 35 字 @0.58。
//   ⇒ 判据：det 出框后，正着读的得分（Σ字数×置信度）够高 → 不是镜像（省一半时间）；
//     正着读不动而翻着读明显更好（×1.3）→ 是镜像；两边都读不动 → 不下结论。
//
// 安全模式与 AiFill 完全一致：
//   · 素材在 ai\ocr\ 下（det/rec 模型 + keys.txt 字典），缺任何一样 → OCR 不可用，归档照常；
//   · **启动路径永不加载 onnxruntime.dll**，先用 AiFill.ProbeRuntime() 的子进程隔离探测；
//   · 任何异常都只让"镜像检测"这一步放弃，绝不拖垮归档流水线。
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Text;

namespace PicTool
{
    public static class Ocr
    {
        public static string OcrDir { get { return Path.Combine(AiFill.AiDir, "ocr"); } }
        public static string DetPath { get { return Path.Combine(OcrDir, "ch_PP-OCRv3_det_infer.onnx"); } }
        public static string RecPath { get { return Path.Combine(OcrDir, "ch_PP-OCRv3_rec_infer.onnx"); } }
        public static string KeysPath { get { return Path.Combine(OcrDir, "keys.txt"); } }

        public static bool AssetsPresent
        {
            get
            {
                return File.Exists(DetPath) && File.Exists(RecPath) &&
                       File.Exists(KeysPath) && AiFill.RuntimePresent;
            }
        }
        public static bool InteropReady { get { return AiFill.InteropReady; } }
        public static bool Available { get { return AssetsPresent && InteropReady; } }

        public static string StatusText
        {
            get
            {
                if (!InteropReady) return "OCR 未构建：缺少 ONNX 互操作层";
                if (!Directory.Exists(OcrDir)) return "OCR 未安装：缺 " + OcrDir;
                if (!File.Exists(DetPath) || !File.Exists(RecPath)) return "OCR 未安装：缺检测/识别模型";
                if (!File.Exists(KeysPath)) return "OCR 未安装：缺字典 keys.txt";
                if (!AiFill.RuntimePresent) return "OCR 未安装：缺 ai\\onnxruntime.dll";
                return "OCR 素材就绪（PP-OCRv3 det+rec）";
            }
        }

#if ORT_INTEROP

        // ---------------- 最小 ORT 绑定（函数指针集比 AiFill 小一圈） ----------------

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr GetApiBaseDelegate();
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr GetApiDelegate(uint version);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr GetVersionStringDelegate();
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr CreateEnvDelegate(int level, string logid, out IntPtr env);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr CreateSessionOptionsDelegate(out IntPtr so);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr SetIntraOpNumThreadsDelegate(IntPtr so, int n);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr SetSessionGraphOptimizationLevelDelegate(IntPtr so, int level);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr CreateSessionFromArrayDelegate(IntPtr env, IntPtr modelData, IntPtr modelLen, IntPtr so, out IntPtr session);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr CreateCpuMemoryInfoDelegate(int allocType, int memType, out IntPtr memInfo);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr CreateTensorWithDataAsOrtValueDelegate(IntPtr memInfo, IntPtr data, IntPtr dataLen, long[] shape, IntPtr shapeLen, int elemType, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr RunDelegate(IntPtr session, IntPtr runOptions, IntPtr[] inputNames, IntPtr[] inputs, IntPtr inputLen, IntPtr[] outputNames, IntPtr outputLen, IntPtr[] outputs);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr GetTensorMutableDataDelegate(IntPtr value, out IntPtr data);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr GetTensorTypeAndShapeDelegate(IntPtr value, out IntPtr info);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr SessionGetInputCountDelegate(IntPtr session, out IntPtr count);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr SessionGetOutputCountDelegate(IntPtr session, out IntPtr count);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr SessionGetInputNameDelegate(IntPtr session, IntPtr index, IntPtr allocator, out IntPtr name);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr SessionGetOutputNameDelegate(IntPtr session, IntPtr index, IntPtr allocator, out IntPtr name);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr GetDimensionsCountDelegate(IntPtr tensorInfo, out IntPtr count);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr GetDimensionsDelegate(IntPtr tensorInfo, long[] dims, IntPtr count);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr GetTensorShapeElementCountDelegate(IntPtr tensorInfo, out IntPtr count);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr GetAllocatorWithDefaultOptionsDelegate(out IntPtr allocator);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void ReleaseDelegate(IntPtr obj);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr GetErrorMessageDelegate(IntPtr status);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr LoadLibraryW(string path);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetDllDirectoryW(string path);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool FreeLibrary(IntPtr module);

        const int LOGGING_ERROR = 3;
        const int ORT_ARENA_ALLOCATOR = 0;
        const int ORT_MEM_TYPE_DEFAULT = 0;
        const int TYPE_FLOAT = 1;

        // 会话只做 float NCHW 输入 / float 输出——PP-OCR 的 det/rec 全是这个格式
        sealed class Session
        {
            public IntPtr lib, env, session, memInfo, allocator;
            public string InName, OutName;
            public CreateTensorWithDataAsOrtValueDelegate CreateTensor;
            public RunDelegate Run;
            public GetTensorMutableDataDelegate GetTensorData;
            public GetTensorTypeAndShapeDelegate GetTensorShape;
            public GetDimensionsCountDelegate DimCount;
            public GetDimensionsDelegate Dims;
            public GetTensorShapeElementCountDelegate ElemCount;
            public ReleaseDelegate ReleaseSession, ReleaseValue, ReleaseMemInfo, ReleaseEnv, ReleaseOptions, ReleaseStatus;
            public ReleaseDelegate ReleaseTypeInfo;
            public GetErrorMessageDelegate ErrorMessage;
        }

        static Session sDet, sRec;
        static string[] sKeys;

        // ★ 批量归档会多页同时跑，而 det/rec 会话是本类**静态共享**
        //   的（sDet/sRec 惰性加载），互操作封装也未按并发设计 —— 所以所有推理统一串行：
        //   一个时刻只有一页在跑 OCR，其余纯像素步骤照常并行。锁粒度是"整次判决"
        //   （镜像一至两次读 / 方向一至三次读），锁内没有再入调用，无死锁面。
        //   OCR 本来就是页耗时的大头，把它串行后 2 页并行的预期收益约 1.5 倍（实测见报告）。
        static readonly object OcrGate = new object();

        // 跟踪写文件（程序是 WinExe 没控制台；与 AiFill.Tr 同一套路）
        // ★ 原来**无条件**写盘，而 Tr 落在 RunTensor 热路径上（每个文字框都调一次
        //   rec）—— 一页上千次同步文件追加、且不与 Diag 一样滚动，%TEMP% 会无上限增长，
        //   还拖慢 OcrGate 锁内的并行归档。改为与 AiFill.Trace 一致的开关，默认关。
        //   排查时设 PICTOOL_OCR_TRACE=1 打开。
        public static bool Trace = false;
        static bool traceInit = false;

        static void Tr(string s)
        {
            if (!traceInit)
            {
                traceInit = true;
                try
                {
                    string v = Environment.GetEnvironmentVariable("PICTOOL_OCR_TRACE");
                    Trace = v != null && v.Length > 0 && v != "0";
                }
                catch { }
            }
            if (!Trace) return;
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "PicTool-ocr-trace.txt"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + Environment.NewLine,
                    System.Text.Encoding.UTF8);
            }
            catch { }
        }

        static T Fn<T>(IntPtr p, string name) where T : class
        {
            if (p == IntPtr.Zero)
                throw new Exception("ONNX Runtime 缺少接口 " + name);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
        }

        static string ErrOf(Session s, IntPtr status)
        {
            if (status == IntPtr.Zero || s == null || s.ErrorMessage == null) return "未知错误";
            try
            {
                IntPtr m = s.ErrorMessage(status);
                string t = Marshal.PtrToStringAnsi(m);
                return string.IsNullOrEmpty(t) ? "未知错误" : t;
            }
            catch { return "未知错误"; }
        }

        static void Check(Session s, IntPtr status, string what)
        {
            if (status == IntPtr.Zero) return;
            string m = ErrOf(s, status);
            try { if (s.ReleaseStatus != null) s.ReleaseStatus(status); } catch { }
            throw new Exception(what + " 失败：" + m);
        }

        static Session EnsureSession(string modelPath, ref Session slot, string tag)
        {
            if (slot != null) return slot;
            if (!AiFill.ProbeRuntime())
                throw new Exception("OCR 运行时探测未通过：" + (AiFill.RuntimeProbeError ?? "未知原因"));

            Session s = new Session();
            try
            {
                // 与 AiFill 共用同一条"两套运行时依次试"的加载路径（原版 → ai\win7 兼容版）
                string used; int err;
                s.lib = AiFill.LoadAnyRuntimePublic(out used, out err);
                if (s.lib == IntPtr.Zero)
                    throw new Exception("加载 onnxruntime.dll 失败（错误码 " + err + "）");
                IntPtr pBase = GetProcAddress(s.lib, "OrtGetApiBase");
                if (pBase == IntPtr.Zero) throw new Exception("找不到 OrtGetApiBase");
                IntPtr pApiBase = Fn<GetApiBaseDelegate>(pBase, "OrtGetApiBase")();
                if (pApiBase == IntPtr.Zero) throw new Exception("OrtGetApiBase() 返回空");
                GetApiDelegate getApi = Fn<GetApiDelegate>(Marshal.ReadIntPtr(pApiBase, 0), "OrtApiBase.GetApi");
                IntPtr apiPtr = IntPtr.Zero;
                for (int v = OrtApiInfo.ApiVersion; v >= 1 && apiPtr == IntPtr.Zero; v--)
                    apiPtr = getApi((uint)v);
                if (apiPtr == IntPtr.Zero) throw new Exception("ONNX Runtime API 版本不兼容");
                OrtApiRaw api = (OrtApiRaw)Marshal.PtrToStructure(apiPtr, typeof(OrtApiRaw));

                CreateEnvDelegate createEnv = Fn<CreateEnvDelegate>(api.CreateEnv, "CreateEnv");
                CreateSessionOptionsDelegate createSO = Fn<CreateSessionOptionsDelegate>(api.CreateSessionOptions, "CreateSessionOptions");
                SetIntraOpNumThreadsDelegate setThreads = Fn<SetIntraOpNumThreadsDelegate>(api.SetIntraOpNumThreads, "SetIntraOpNumThreads");
                SetSessionGraphOptimizationLevelDelegate setOpt = Fn<SetSessionGraphOptimizationLevelDelegate>(api.SetSessionGraphOptimizationLevel, "SetSessionGraphOptimizationLevel");
                CreateSessionFromArrayDelegate createSess = Fn<CreateSessionFromArrayDelegate>(api.CreateSessionFromArray, "CreateSessionFromArray");
                CreateCpuMemoryInfoDelegate createMem = Fn<CreateCpuMemoryInfoDelegate>(api.CreateCpuMemoryInfo, "CreateCpuMemoryInfo");
                s.CreateTensor = Fn<CreateTensorWithDataAsOrtValueDelegate>(api.CreateTensorWithDataAsOrtValue, "CreateTensor");
                s.Run = Fn<RunDelegate>(api.Run, "Run");
                s.GetTensorData = Fn<GetTensorMutableDataDelegate>(api.GetTensorMutableData, "GetTensorMutableData");
                s.GetTensorShape = Fn<GetTensorTypeAndShapeDelegate>(api.GetTensorTypeAndShape, "GetTensorTypeAndShape");
                s.DimCount = Fn<GetDimensionsCountDelegate>(api.GetDimensionsCount, "GetDimensionsCount");
                s.Dims = Fn<GetDimensionsDelegate>(api.GetDimensions, "GetDimensions");
                s.ElemCount = Fn<GetTensorShapeElementCountDelegate>(api.GetTensorShapeElementCount, "GetTensorShapeElementCount");
                s.ErrorMessage = Fn<GetErrorMessageDelegate>(api.GetErrorMessage, "GetErrorMessage");
                s.ReleaseSession = Fn<ReleaseDelegate>(api.ReleaseSession, "ReleaseSession");
                s.ReleaseValue = Fn<ReleaseDelegate>(api.ReleaseValue, "ReleaseValue");
                s.ReleaseMemInfo = Fn<ReleaseDelegate>(api.ReleaseMemoryInfo, "ReleaseMemoryInfo");
                s.ReleaseEnv = Fn<ReleaseDelegate>(api.ReleaseEnv, "ReleaseEnv");
                s.ReleaseOptions = Fn<ReleaseDelegate>(api.ReleaseSessionOptions, "ReleaseSessionOptions");
                s.ReleaseStatus = Fn<ReleaseDelegate>(api.ReleaseStatus, "ReleaseStatus");
                // 注意：GetTensorTypeAndShape 返回的是 OrtTensorTypeAndShapeInfo*，
                // 必须用 ReleaseTensorTypeAndShapeInfo（槽位 99）释放；
                // ReleaseTypeInfo（槽位 98）是给 GetTypeInfo 那一族用的，绑错 = 原生层 AV。
                s.ReleaseTypeInfo = Fn<ReleaseDelegate>(api.ReleaseTensorTypeAndShapeInfo, "ReleaseTensorTypeAndShapeInfo");

                Check(s, createEnv(LOGGING_ERROR, "PicTool-OCR", out s.env), "创建 OCR 推理环境");
                IntPtr so;
                Check(s, createSO(out so), "创建会话选项");
                try { Check(s, setThreads(so, 1), "设置线程数"); } catch { }
                try { Check(s, setOpt(so, 3), "设置图优化"); } catch { }
                // 模型读进内存再建会话：绕开路径编码问题（与 AiFill 同一套）
                byte[] mb = File.ReadAllBytes(modelPath);
                IntPtr pmb = Marshal.AllocHGlobal(mb.Length);
                bool loaded = false;
                try
                {
                    Marshal.Copy(mb, 0, pmb, mb.Length);
                    IntPtr st = createSess(s.env, pmb, (IntPtr)mb.Length, so, out s.session);
                    if (st == IntPtr.Zero) loaded = true;
                    else { string m1 = ErrOf(s, st); try { s.ReleaseStatus(st); } catch { } throw new Exception("建会话失败：" + m1); }
                }
                finally { Marshal.FreeHGlobal(pmb); }
                s.ReleaseOptions(so);
                Check(s, createMem(ORT_ARENA_ALLOCATOR, ORT_MEM_TYPE_DEFAULT, out s.memInfo), "创建内存描述");
                GetAllocatorWithDefaultOptionsDelegate getAlloc = Fn<GetAllocatorWithDefaultOptionsDelegate>(api.GetAllocatorWithDefaultOptions, "GetAllocator");
                Check(s, getAlloc(out s.allocator), "获取分配器");

                // 输入/输出名（各一个；名字由 ORT 分配器持有，进程内少量占用可接受）
                SessionGetInputCountDelegate inCnt = Fn<SessionGetInputCountDelegate>(api.SessionGetInputCount, "SessionGetInputCount");
                SessionGetOutputCountDelegate outCnt = Fn<SessionGetOutputCountDelegate>(api.SessionGetOutputCount, "SessionGetOutputCount");
                SessionGetInputNameDelegate inName = Fn<SessionGetInputNameDelegate>(api.SessionGetInputName, "SessionGetInputName");
                SessionGetOutputNameDelegate outName = Fn<SessionGetOutputNameDelegate>(api.SessionGetOutputName, "SessionGetOutputName");
                IntPtr c;
                Check(s, inCnt(s.session, out c), "读输入个数");
                if ((int)c != 1) throw new Exception("模型输入个数 " + (int)c + "（期望 1）");
                Check(s, outCnt(s.session, out c), "读输出个数");
                if ((int)c != 1) throw new Exception("模型输出个数 " + (int)c + "（期望 1）");
                IntPtr pn;
                Check(s, inName(s.session, IntPtr.Zero, s.allocator, out pn), "读输入名");
                s.InName = Marshal.PtrToStringAnsi(pn);
                Check(s, outName(s.session, IntPtr.Zero, s.allocator, out pn), "读输出名");
                s.OutName = Marshal.PtrToStringAnsi(pn);

                slot = s;
                Diag.Info("OCR", tag + " 会话就绪（" + Path.GetFileName(modelPath) + "）");
                return s;
            }
            catch (Exception ex)
            {
                ReleaseSession(s);
                throw new Exception("OCR 初始化失败（" + tag + "）：" + ex.Message, ex);
            }
        }

        static void ReleaseSession(Session s)
        {
            if (s == null) return;
            try { if (s.session != IntPtr.Zero && s.ReleaseSession != null) s.ReleaseSession(s.session); } catch { }
            try { if (s.memInfo != IntPtr.Zero && s.ReleaseMemInfo != null) s.ReleaseMemInfo(s.memInfo); } catch { }
            try { if (s.env != IntPtr.Zero && s.ReleaseEnv != null) s.ReleaseEnv(s.env); } catch { }
            try { if (s.lib != IntPtr.Zero) FreeLibrary(s.lib); } catch { }
        }

        // ★ 释放必须与推理互斥 —— 否则关窗时 UI 线程在这里 ReleaseSession +
        //   FreeLibrary，而 worker 线程正卡在 s.Run() 里用同一个原生 session/env
        //   → use-after-free / 访问已卸载模块 = 原生崩溃（托管 catch 抓不住）。
        //   锁粒度与推理入口一致（OcrGate）：拿到锁时保证没有在跑的推理。
        public static void Shutdown()
        {
            lock (OcrGate)
            {
                ReleaseSession(sDet); sDet = null;
                ReleaseSession(sRec); sRec = null;
            }
        }

        // 跑一次推理：输入 float NCHW，输出 float（形状从输出张量读回）
        static float[] RunTensor(Session s, float[] data, long[] shape, out long[] outDims)
        {
            Tr("RunTensor 进 shape=[" + Join(shape) + "] 数据 " + data.Length);
            long[] dims = (long[])shape.Clone();
            IntPtr val = IntPtr.Zero;
            GCHandle h = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                Tr("RunTensor 建张量");
                Check(s, s.CreateTensor(s.memInfo, h.AddrOfPinnedObject(), (IntPtr)(data.Length * 4),
                        dims, (IntPtr)dims.Length, TYPE_FLOAT, out val), "建张量");
                IntPtr nIn = Marshal.StringToHGlobalAnsi(s.InName);
                IntPtr nOut = Marshal.StringToHGlobalAnsi(s.OutName);
                IntPtr[] outs = new IntPtr[1];
                try
                {
                    Tr("RunTensor 推理前 in=" + s.InName + " out=" + s.OutName);
                    Check(s, s.Run(s.session, IntPtr.Zero,
                            new IntPtr[] { nIn }, new IntPtr[] { val }, (IntPtr)1,
                            new IntPtr[] { nOut }, (IntPtr)1, outs), "OCR 推理");
                    Tr("RunTensor 推理完成");
                }
                finally
                {
                    Marshal.FreeHGlobal(nIn);
                    Marshal.FreeHGlobal(nOut);
                }
                IntPtr outVal = outs[0];
                try
                {
                    IntPtr si;
                    long elems = 0; outDims = new long[0];
                    Tr("RunTensor A: GetTensorShape");
                    IntPtr stShape = s.GetTensorShape(outVal, out si);
                    Tr("RunTensor B: st=" + stShape.ToString());
                    if (stShape == IntPtr.Zero && si != IntPtr.Zero)
                    {
                        IntPtr cnt;
                        Tr("RunTensor C: ElemCount");
                        if (s.ElemCount(si, out cnt) == IntPtr.Zero) elems = (long)cnt;
                        Tr("RunTensor D: elems=" + elems);
                        IntPtr ndc;
                        Tr("RunTensor E: DimCount");
                        if (s.DimCount(si, out ndc) == IntPtr.Zero)
                        {
                            Tr("RunTensor F: nd=" + (int)ndc);
                            int n = (int)ndc;
                            outDims = new long[n];
                            if (n > 0 && s.Dims(si, outDims, ndc) != IntPtr.Zero) outDims = new long[0];
                            Tr("RunTensor G: dims=[" + Join(outDims) + "]");
                        }
                        Tr("RunTensor H: release shape info");
                        if (s.ReleaseTypeInfo != null) { try { s.ReleaseTypeInfo(si); } catch { } }
                    }
                    Tr("RunTensor I: 输出形状=[" + Join(outDims) + "] 元素 " + elems);
                    if (elems <= 0 || elems > 1L << 30)
                    {
                        long prod = 1;
                        for (int i = 0; i < outDims.Length; i++) prod *= outDims[i];
                        elems = prod;
                        Tr("RunTensor 回退用形状积 " + elems);
                    }
                    IntPtr pOut;
                    Tr("RunTensor 取数据指针");
                    Check(s, s.GetTensorData(outVal, out pOut), "读输出数据");
                    float[] res = new float[elems];
                    Tr("RunTensor 拷回 " + elems);
                    Marshal.Copy(pOut, res, 0, (int)elems);
                    Tr("RunTensor 拷回完成");
                    return res;
                }
                finally { try { s.ReleaseValue(outVal); } catch { } }
            }
            finally { h.Free(); }
        }

        static string Join(long[] a)
        {
            string s = "";
            for (int i = 0; i < a.Length; i++) { if (i > 0) s += ","; s += a[i]; }
            return s;
        }

        // ---------------- det：DBNet 文本框 ----------------

        // 缩放位图（双三次）
        static Bitmap ResizeBmp(Bitmap src, int w, int h)
        {
            Bitmap b = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, w, h));
            }
            return b;
        }

        // 24bpp → float NCHW（BGR 通道序）。det 用 ImageNet 均值方差，rec 用 (0.5/0.5)。
        static float[] ToNchw(Bitmap bmp, float[] mean, float[] std, out int w, out int h)
        {
            w = bmp.Width; h = bmp.Height;
            PixelBuffer buf = BitmapBridge.ToBuffer(bmp);
            int plane = w * h;
            float[] outp = new float[plane * 3];
            for (int y = 0; y < h; y++)
            {
                int ro = y * buf.Stride;
                for (int x = 0; x < w; x++)
                {
                    int p = ro + x * 3;
                    int i = y * w + x;
                    outp[i] = (buf.Data[p] / 255f - mean[0]) / std[0];
                    outp[plane + i] = (buf.Data[p + 1] / 255f - mean[1]) / std[1];
                    outp[plane * 2 + i] = (buf.Data[p + 2] / 255f - mean[2]) / std[2];
                }
            }
            return outp;
        }

        // det 输入：**短边不足 736 才放大，否则原尺寸**（limit_type=min 的官方规则，
        //   无脑缩到 736 会把字缩小到笔画模糊，实测只出碎片框）；对齐 32。
        // 归一化用 ImageNet 均值方差（PaddleOCR 官方 det 配置，与 rec 的 (0.5/0.5) 不同
        //   ——搞错就只检出碎片，实测：用 rec 归一化喂 det，35px 的字只出 16px 高的碎框）。
        const int DetLimitSide = 736;
        static readonly float[] DetMean = { 0.485f, 0.456f, 0.406f };   // 按 BGR 通道序
        static readonly float[] DetStd = { 0.229f, 0.224f, 0.225f };
        const float DetThresh = 0.3f;
        const int MinBoxArea = 60;      // det 尺度下的最小框面积
        const int MinBoxH = 6;

        // ★ 倾斜兜底：连通域遍历时顺手累加二阶矩，域结束时对"一行字"形状
        //   的框（长宽比 ≥2）算主轴角 = 文字行方向（PCA：θ = atan2(2Σxy, Σxx−Σyy)/2）。
        //   整页把各框角度做 2θ 圆统计取主峰 —— 文字行平行时主峰极尖，这就是页面倾斜角。
        //   为什么放在 det 里：镜像判定反正要跑一次 det，角度是白捡的，零额外 OCR 成本。
        static List<Rectangle> DetectBoxes(Bitmap page, out double scaleBack, out double skewDeg)
        {
            skewDeg = double.NaN;
            int w = page.Width, h = page.Height;
            double k = Math.Min(w, h) < DetLimitSide ? (double)DetLimitSide / Math.Min(w, h) : 1.0;
            int rw = Math.Max(32, (int)((long)(w * k) / 32) * 32);
            int rh = Math.Max(32, (int)((long)(h * k) / 32) * 32);
            scaleBack = (double)w / rw;

            Session det = EnsureSession(DetPath, ref sDet, "det");
            Bitmap scaled = ResizeBmp(page, rw, rh);
            float[] input;
            int iw, ih;
            try { input = ToNchw(scaled, DetMean, DetStd, out iw, out ih); }
            finally { scaled.Dispose(); }
            long[] outDims;
            float[] prob = RunTensor(det, input, new long[] { 1, 3, ih, iw }, out outDims);
            if (prob == null || prob.Length < iw * ih)
                throw new Exception("det 输出形状异常");

            // 概率图 → 二值 → 连通域 → 框（页面已基本转正，轴对齐框足够）
            List<Rectangle> boxes = new List<Rectangle>();
            List<double> angles = new List<double>();     // 各"一行字"框的主轴角（度，图像坐标系）
            bool[] bin = new bool[iw * ih];
            for (int i = 0; i < iw * ih; i++) bin[i] = prob[i] > DetThresh;
            bool[] visited = new bool[iw * ih];
            int[] stack = new int[iw * ih];
            for (int seed = 0; seed < bin.Length; seed++)
            {
                if (!bin[seed] || visited[seed]) continue;
                int sp = 0;
                stack[sp++] = seed;
                visited[seed] = true;
                int x0 = iw, x1 = -1, y0 = ih, y1 = -1, area = 0;
                // 二阶矩（long 不溢出：det 尺度 ≤1472² 像素，坐标 ≤2047，Σxx ≤ 1.3e12）
                long sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
                while (sp > 0)
                {
                    int cur = stack[--sp];
                    int cx = cur % iw, cy = cur / iw;
                    area++;
                    if (cx < x0) x0 = cx;
                    if (cx > x1) x1 = cx;
                    if (cy < y0) y0 = cy;
                    if (cy > y1) y1 = cy;
                    sx += cx; sy += cy;
                    sxx += (long)cx * cx; syy += (long)cy * cy; sxy += (long)cx * cy;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = cx + dx, ny = cy + dy;
                            if (nx < 0 || ny < 0 || nx >= iw || ny >= ih) continue;
                            int ni = ny * iw + nx;
                            if (bin[ni] && !visited[ni]) { visited[ni] = true; stack[sp++] = ni; }
                        }
                }
                int bw = x1 - x0 + 1, bh = y1 - y0 + 1;
                if (area < MinBoxArea || bh < MinBoxH) continue;
                // 主轴角只认"一行字"形状：长宽比 ≥2 且足够长 —— 单字/方块/圆形章的主轴不可信
                int bLong = Math.Max(bw, bh), bShort = Math.Min(bw, bh);
                if (bLong >= 2 * bShort && bLong >= 24)
                {
                    double n = area;
                    double mx = (double)sx / n, my = (double)sy / n;
                    double cxx = (double)sxx / n - mx * mx;
                    double cyy = (double)syy / n - my * my;
                    double cxy = (double)sxy / n - mx * my;
                    angles.Add(Math.Atan2(2.0 * cxy, cxx - cyy) / 2.0 * 180.0 / Math.PI);
                }
                // 框还原到原图坐标，外扩余量：左右 4px，上下 1/4 框高
                // （det 框通常只圈到字符中段，rec 需要完整字面）
                int padX = 4, padY = Math.Max(4, (int)((y1 - y0) * 0.5) + 2);
                int px0 = (int)(x0 / scaleBack) - padX;
                int py0 = (int)(y0 / scaleBack) - padY;
                int px1 = (int)((x1 + 1) / scaleBack) + padX;
                int py1 = (int)((y1 + 1) / scaleBack) + padY;
                px0 = Math.Max(0, px0); py0 = Math.Max(0, py0);
                px1 = Math.Min(w, px1); py1 = Math.Min(h, py1);
                if (px1 - px0 < 12 || py1 - py0 < 10) continue;
                boxes.Add(Rectangle.FromLTRB(px0, py0, px1, py1));
            }
            // 大框优先，最多 12 行（够统计了，控制耗时）
            boxes.Sort(delegate(Rectangle a, Rectangle b)
            { return (b.Width * b.Height).CompareTo(a.Width * a.Height); });
            if (boxes.Count > 12) boxes.RemoveRange(12, boxes.Count - 12);
            skewDeg = SkewFromAngles(angles);
            return boxes;
        }

        // 各框主轴角 → 页面倾斜角。2θ 圆统计（长条框主轴无方向，α 与 α+180° 等价），
        // 剔除离主峰 >3° 的离群框再算一遍；框 <3 个或角度太散（平均向量 <0.85）→ NaN=证据不足。
        static double SkewFromAngles(List<double> a)
        {
            if (a == null || a.Count < 3) return double.NaN;
            double sc = 0, ss = 0;
            for (int i = 0; i < a.Count; i++)
            { double r = a[i] * Math.PI / 90.0; sc += Math.Cos(r); ss += Math.Sin(r); }
            double mean = Math.Atan2(ss, sc) / 2.0 * 180.0 / Math.PI;
            double sc2 = 0, ss2 = 0; int kept = 0;
            for (int i = 0; i < a.Count; i++)
            {
                double d = Math.Abs(((a[i] - mean + 540.0) % 360.0) - 180.0);
                if (d > 3.0) continue;
                double r = a[i] * Math.PI / 90.0; sc2 += Math.Cos(r); ss2 += Math.Sin(r); kept++;
            }
            if (kept < 3) return double.NaN;
            double len = Math.Sqrt(sc2 * sc2 + ss2 * ss2) / kept;
            if (len < 0.85) return double.NaN;
            return Math.Atan2(ss2, sc2) / 2.0 * 180.0 / Math.PI;
        }

        // ---------------- rec：CTC 解码 + 得分 ----------------

        static string[] EnsureKeys()
        {
            if (sKeys != null) return sKeys;
            sKeys = File.ReadAllLines(KeysPath, System.Text.Encoding.UTF8);
            if (sKeys.Length < 100) throw new Exception("keys.txt 行数异常（" + sKeys.Length + "）");
            return sKeys;
        }

        // rec 归一化：(v/255 - 0.5) / 0.5，BGR 序（与 Python 探针验证过的 0.953 置信度一致）
        static readonly float[] RecMean = { 0.5f, 0.5f, 0.5f };
        static readonly float[] RecStd = { 0.5f, 0.5f, 0.5f };

        // 对一组文本框做识别（不做任何翻转；镜像检验由 MirrorVerdict 整页翻转后再走本流程）。
        // 返回 Σ(每个识别出字符的置信度)；同时经 totalChars/meanConf/preview 带出统计。
        static double ScoreLines(Bitmap page, List<Rectangle> boxes,
                                 out int totalChars, out double meanConf)
        {
            Session rec = EnsureSession(RecPath, ref sRec, "rec");
            string[] keys = EnsureKeys();
            totalChars = 0;
            double confSum = 0;
            StringBuilder decoded = new StringBuilder();
            foreach (Rectangle box in boxes)
            {
                Rectangle rc = box;
                if (rc.Width < 8 || rc.Height < 8) continue;
                int ch = 48;
                int cw = (int)Math.Round((double)rc.Width * ch / rc.Height);
                if (cw < 16) cw = 16;
                if (cw > 640) cw = 640;
                Bitmap cropBmp;
                try { cropBmp = page.Clone(rc, PixelFormat.Format24bppRgb); }
                catch { continue; }
                float[] input;
                int iw, ih;
                try
                {
                    Bitmap scaled = ResizeBmp(cropBmp, cw, ch);
                    try { input = ToNchw(scaled, RecMean, RecStd, out iw, out ih); }
                    finally { scaled.Dispose(); }
                }
                finally { cropBmp.Dispose(); }
                long[] outDims;
                float[] outp = RunTensor(rec, input, new long[] { 1, 3, ih, iw }, out outDims);
                if (outp == null || outDims.Length != 3) continue;
                int T = (int)outDims[1], C = (int)outDims[2];
                int last = -1;
                for (int t = 0; t < T; t++)
                {
                    int best = 0; float bv = -1f;
                    for (int c = 0; c < C; c++)      // blank(0) 也参与比较：它最大就该跳过这一帧
                    {
                        float v = outp[t * C + c];
                        if (v > bv) { bv = v; best = c; }
                    }
                    if (best != 0 && best != last)   // 重复帧折叠
                    {
                        totalChars++;
                        confSum += bv;
                        if (decoded.Length < 40)
                            decoded.Append(best > 0 && best <= keys.Length ? keys[best - 1] : "?");
                    }
                    last = best;
                }
                Tr("rec 框 " + rc.Left + "," + rc.Top + " " + rc.Width + "x" + rc.Height +
                   " → " + decoded.ToString());
            }
            meanConf = totalChars > 0 ? confSum / totalChars : 0.0;
            return confSum;
        }

        // ---------------- 镜像判决 ----------------

        // 完整读一遍：det 找框 + rec 打分。skewDeg = det 文字框主轴统计出的页面倾斜角
        // （NaN = 框太少/角度散/无 AI；不关心时传 discard 变量）
        static double ReadScore(Bitmap page, out int chars, out double conf, out int boxCount, out double skewDeg)
        {
            double scaleBack;
            List<Rectangle> boxes = DetectBoxes(page, out scaleBack, out skewDeg);
            boxCount = boxes.Count;
            double score = ScoreLines(page, boxes, out chars, out conf);
            return score;
        }

        // 返回：true=镜像；false=确认正常；null=证据不足（空页/图版页/OCR 异常）→ 调用方不动它。
        // skewDeg = 正读 det 框统计出的页面倾斜角（度，图像坐标系 atan2 主轴；NaN=证据不足）。
        // ★ 倾斜兜底：镜像判定反正要跑一次 det，角度白捡 —— 归档流水线用它
        //   修复投影法对小角度（-3°）的盲区；镜像翻转后调用方要自行取反（水平翻转反号）。
        // （并行：公共入口只做锁转发，推理体在 Core 里整段串行）
        public static bool? MirrorVerdict(Bitmap page, out double skewDeg)
        {
            skewDeg = double.NaN;
            if (!Available) return null;
            lock (OcrGate) { return MirrorVerdictCore(page, out skewDeg); }
        }

        public static bool? MirrorVerdict(Bitmap page)
        {
            double s; return MirrorVerdict(page, out s);
        }

        static bool? MirrorVerdictCore(Bitmap page, out double skewDeg)
        {
            skewDeg = double.NaN;
            if (!Available) return null;
            try
            {
                int upChars; double upConf; int upBoxes;
                double upScore = ReadScore(page, out upChars, out upConf, out upBoxes, out skewDeg);
                Diag.Info("OCR", string.Format("正读: {0}框, {1}字, 均信 {2:0.000}, 分 {3:0.0}",
                    upBoxes, upChars, upConf, upScore));
                // 倾斜兜底的诊断：det 框主轴统计出的页面角（NaN=框太少/角度散）。
                // 图像坐标系（y 向下）的 atan2 主轴角：文字行向右下斜（页面顺时针歪）为正。
                Diag.Info("OCR", "OCR框角: " + (double.IsNaN(skewDeg) ? "证据不足" : skewDeg.ToString("0.00") + "°"));
                // 正着读已经读得很顺 → 不是镜像（绝大多数页面到此为止，不花第二次的钱）
                if (upChars >= 10 && upConf >= 0.80) return false;

                // 证据不充分：整页翻过来再完整读一遍（det 对镜像页的框质量会退化，
                // 所以必须连 det 一起重跑，不能只复用框）
                Bitmap flipped = Imaging.Flip(page, true);
                int flChars; double flConf; int flBoxes;
                double flScore;
                try { double discard; flScore = ReadScore(flipped, out flChars, out flConf, out flBoxes, out discard); }
                finally { flipped.Dispose(); }
                Diag.Info("OCR", string.Format("镜像检验: 正读 {0}字@{1:0.00}, 翻读 {2}字@{3:0.00}",
                    upChars, upConf, flChars, flConf));
                if (flScore > upScore * 1.3 && flScore >= 10 && flChars >= 10) return true;
                if (upScore > flScore * 1.3) return false;    // 正读明显更好 → 确认非镜像
                return null;                                  // 两边都读不动 → 不下结论
            }
            catch (Exception ex)
            {
                Diag.Log(ex, "OCR 镜像检测失败（不影响归档）");
                return null;
            }
        }

        // ---------------- 方向判决（「极致归档」专用）----------------
        //
        // 只回答一件事：这一页要**顺时针转几个 90°** 才是正的。
        // 返回 0~3 = 该转的次数；-1 = 证据不足，不下结论（调用方就不动它）。
        //
        // 为什么需要它：Orient.Detect 只认装订孔这个物理证据，无孔页一律不转
        //   （空表格页的几何统计容易误判无孔页）。于是"明显躺着
        //   但没孔"的页就一直转不过来。OCR 是**真正读懂内容**的证据：
        //   文字被转 90° 后变成竖排，rec 直接读不动，分数断崖式下跌。
        //
        // 代价：最坏多跑 3 次 det+rec（正着读很顺时一次就返回 0，多数页走这条快路径）。
        // 判据：
        //   · 正读够顺（≥12 字且均置信 ≥0.82）→ 0，不转；
        //   · 正读基本读不出东西（<4 字）→ 空页/图版页，不下结论（别拿噪声去赌）；
        //   · 否则读 90/180/270 取最高分，必须**显著**高于正读且**绝对分够**才认。
        //
        // 🔴 **180° 一律不认**（实测翻车教训）：
        //    实测样本本来就是正的（表头在上方、装订孔在左边），
        //    程序却判 180° 把整页转倒了。原因：PP-OCRv3 的 rec 认的是**字形**，
        //    汉字上下翻转后仍有大量形近字能兜住，倒着读的分数能和正读打平甚至反超
        //    —— 1.5 倍的门槛也没挡住。**转倒一张端正页，比漏转一张倒置页严重得多**
        //    （前者是破坏内容，后者只是要人工点一下旋转）。
        //    ⇒ 180° 只认装订孔那个物理证据（Orient.Detect：孔在右 = 倒置），OCR 不出这个结论。
        public static int OrientVerdict(Bitmap page)
        {
            if (!Available) return -1;
            lock (OcrGate) { return OrientVerdictCore(page); }
        }

        static int OrientVerdictCore(Bitmap page)
        {
            if (!Available) return -1;
            try
            {
                int upChars; double upConf; int upBoxes;
                double discard0;
                double upScore = ReadScore(page, out upChars, out upConf, out upBoxes, out discard0);
                Diag.Info("OCR", string.Format("定向: 正读 {0}框 {1}字 均信 {2:0.000} 分 {3:0.0}",
                    upBoxes, upChars, upConf, upScore));
                if (upChars >= 12 && upConf >= 0.82) return 0;    // 读得很顺 → 本来就是正的
                if (upChars < 4) return -1;                        // 读不出东西 → 不下结论

                int best = 0; double bestScore = upScore; int bestChars = upChars;

                // 只读 90° 与 270°：180° 的结论一律不认（见函数头），再跑一次纯属浪费
                // —— 每次 det+rec 是大头，省掉这趟能把"走了 OCR 分支"的页提速约三分之一。
                int[] tries = new int[] { 1, 3 };
                foreach (int t in tries)
                {
                    Bitmap rot = RotateTurns(page, t);
                    int c2; double cf2; int b2;
                    double sc;
                    try { double discard; sc = ReadScore(rot, out c2, out cf2, out b2, out discard); }
                    finally { if (rot != null && rot != page) rot.Dispose(); }
                    Diag.Info("OCR", string.Format("定向: 转 {0}x90° → {1}字 均信 {2:0.000} 分 {3:0.0}",
                        t, c2, cf2, sc));
                    if (sc > bestScore) { bestScore = sc; best = t; bestChars = c2; }
                }
                if (best == 0) return 0;
                if (best == 2) return -1;                     // 🔴 180° 不靠 OCR（见函数头注释）
                // 绝对分门槛：OCR 在这页上根本读不动时，方向分只是噪声 —— 别拿噪声去赌转向
                if (bestChars < 12 || bestScore < 30) return -1;
                if (bestScore >= upScore * 1.25) return best;
                return -1;
            }
            catch (Exception ex)
            {
                Diag.Log(ex, "OCR 方向检测失败（不影响归档）");
                return -1;
            }
        }

        // 转 turns×90° 的**副本**（绝不释放传入的 page —— Orient.Apply 会接管所有权，
        // 直接拿它转会把调用方的图一起销毁）
        static Bitmap RotateTurns(Bitmap page, int turns)
        {
            Bitmap cur = page;
            for (int k = 0; k < turns; k++)
            {
                Bitmap n = Imaging.Rotate90(cur, true);
                if (n != cur && cur != page) cur.Dispose();
                cur = n;
            }
            return cur;
        }

#else
        // 未生成互操作层时的降级：永远不下结论
        public static bool? MirrorVerdict(Bitmap page, out double skewDeg) { skewDeg = double.NaN; return null; }
        public static bool? MirrorVerdict(Bitmap page) { return null; }
        public static int OrientVerdict(Bitmap page) { return -1; }
        public static void Shutdown() { }
#endif
    }
}
