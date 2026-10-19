// AiFill.cs — AI 修复模式（MI-GAN / ONNX Runtime）
//
// 设计要点：
//  * 可选叠加：只在 exe 同级的 ai\ 目录里同时存在 onnxruntime.dll 与一个 .onnx 模型时才启用；
//    文件缺失、版本不符、模型不兼容等任何情况都只导致"AI 模式不可用"，绝不影响其它三个模式。
//  * 不猜结构体布局：OrtApi 的函数指针顺序由 tools\gen_ort_interop.ps1 从官方头文件生成
//    （src\OrtApi.Generated.cs）。若该文件不存在，本文件会被编译成"未生成互操作"的降级版本。
//  * 运行时只做 uint8/float、NCHW 两种最常见布局，并读取模型元信息校验；
//    不匹配时给出明确提示而不是硬跑。
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace PicTool
{
    static class AiFill
    {
        // ---------------- 素材探测 ----------------

        public static string AiDir
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ai"); }
        }

        public static string DllPath
        {
            get { return Path.Combine(AiDir, "onnxruntime.dll"); }
        }

        // ==================== 两套运行时 ====================
        //
        // 背景（Win11 上报"错误码 126"的根因）：
        //   为了让 ORT 1.14.1 能在 Win7 跑，我们把 onnxruntime.dll **导入表**里 4 个
        //   "Win8 才有"的 API 集名改成了普通文件名 w7shim-*.dll（见 tools\prepare_ai_dir.py）。
        //   改名一旦落进导入表，这 4 个文件就成了**所有系统上的硬依赖**；而安装器原来
        //   只在 Win8 以下复制垫片 —— 于是 Win10/11 上 LoadLibrary 直接 126（找不到模块），
        //   旧提示还把它说成"缺少 VC++ 运行库"，把排查带偏。
        //
        // 现在两套并存，**谁能加载用谁**（按顺序试，不做系统版本判断 —— 试失败的代价
        // 只是一次 LoadLibrary 返回 0，比"猜版本号"可靠得多）：
        //   ① ai\onnxruntime.dll        原版：Win8/10/11 走这条。不依赖任何垫片，
        //                               也不会用同名垫片去影子系统自带的 api-ms-* 实现。
        //   ② ai\win7\onnxruntime.dll   补丁版：Win7 走这条，垫片就在它自己目录里。
        public static string Win7DllPath
        {
            get { return Path.Combine(Path.Combine(AiDir, "win7"), "onnxruntime.dll"); }
        }

        // ---- 候选顺序：**按系统版本挑** ----
        //
        // 上一版是"原版先试、失败再试垫片版"。在 Win7 上这不成立：
        // 原版 onnxruntime.dll 导入的 kernel32.CreateFile2 在 Win7 的 kernel32 里**不存在**，
        // 加载时系统会**自己弹一个"无法定位程序输入点"的框**（加载器硬错误），
        // 用户看到的就是这个框 —— 不是我们能"静默重试"的那种失败。
        //
        // 所以改成：Win7/2008R2（6.1 及以下）**只**用 ai\win7\ 垫片版，碰都不碰原版；
        // Win8+ 用原版（原来的兼容层留着当后手，不会再触发弹框：它的 4 个依赖都在自己目录里）。
        // 版本号走 SysInfo 的 ntdll!RtlGetVersion（不受"无清单程序的版本虚拟化"影响）。
        // 逃生口 / 诊断开关：环境变量 PICTOOL_ORT_FORCE=win7|modern 可强制走某一支
        // （万一某台机器的系统判定不对，或要在现代系统上验证 Win7 那一支的 DLL 能不能加载）
        static int forcedVariant = -1;   // -1=还没读环境变量 0=未指定 1=强制现代 2=强制 Win7

        static int ForcedVariant()
        {
            if (forcedVariant >= 0) return forcedVariant;
            forcedVariant = 0;
            try
            {
                string s = Environment.GetEnvironmentVariable("PICTOOL_ORT_FORCE");
                if (s != null)
                {
                    s = s.Trim().ToLowerInvariant();
                    if (s == "win7" || s == "shim") forcedVariant = 2;
                    else if (s == "modern" || s == "std") forcedVariant = 1;
                }
            }
            catch { }
            return forcedVariant;
        }

        static string[] CandidateDlls()
        {
            bool win7 = ForcedVariant() == 2 || (ForcedVariant() == 0 && SysInfo.IsWindows7OrOlder);
            if (win7) return new string[] { Win7DllPath, DllPath };
            return new string[] { DllPath, Win7DllPath };
        }

        // 候选的文字描述（报告/日志用：目标机排查时一眼能看出"系统判定"和"加载顺序"对不对）
        public static string CandidateDesc
        {
            get
            {
                string[] c = CandidateDlls();
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                for (int i = 0; i < c.Length; i++)
                {
                    if (i > 0) sb.Append(" → ");
                    sb.Append(string.Equals(c[i], Win7DllPath, StringComparison.OrdinalIgnoreCase)
                        ? "ai\\win7（兼容层）" : "ai\\（标准版）");
                }
                string why = ForcedVariant() == 2 ? "｜**环境变量强制 Win7 支**  "
                           : ForcedVariant() == 1 ? "｜**环境变量强制现代支**  "
                           : SysInfo.IsWindows7OrOlder ? "｜Win7 判定：兼容层优先  "
                           : "｜现代系统：标准版优先  ";
                return SysInfo.OsDescribe() + why + sb.ToString();
            }
        }

        // 任一套运行时在位（素材检查不能只盯着原版那一个路径）
        public static bool RuntimePresent
        {
            get
            {
                string[] c = CandidateDlls();
                for (int i = 0; i < c.Length; i++) if (File.Exists(c[i])) return true;
                return false;
            }
        }

        static string chosenDll;   // 实际用上的那一个（报告/日志用，便于目标机排查）

        public static string ChosenDllPath { get { return chosenDll; } }

        public static string ChosenDllTag
        {
            get
            {
                if (chosenDll == null) return "未加载";
                return string.Equals(chosenDll, Win7DllPath, StringComparison.OrdinalIgnoreCase)
                    ? "Win7 兼容版(带垫片)" : "标准版";
            }
        }

        // 给 Ocr 用的公开入口（两处必须走同一条加载路径，否则报告里"实际使用"会不一致）
        public static IntPtr LoadAnyRuntimePublic(out string usedPath, out int lastErr)
        {
            return LoadAnyRuntime(out usedPath, out lastErr);
        }

        // 依次尝试候选：谁先加载成功就用谁。返回已加载模块句柄（0 = 全失败）。
        // 每次尝试都把 DLL 搜索目录指到**它自己所在目录** —— 垫片必须与补丁版同目录才找得到。
        static IntPtr LoadAnyRuntime(out string usedPath, out int lastErr)
        {
            usedPath = null; lastErr = 0;
            string[] c = CandidateDlls();
            for (int i = 0; i < c.Length; i++)
            {
                if (!File.Exists(c[i])) continue;
                try { SetDllDirectoryW(Path.GetDirectoryName(c[i])); } catch { }
                Tr("LoadLibrary " + c[i]);
                IntPtr h = LoadLibraryQuiet(c[i]);
                if (h != IntPtr.Zero)
                {
                    usedPath = c[i];
                    chosenDll = c[i];
                    Tr("LoadLibrary OK (" + ChosenDllTag + ")");
                    return h;
                }
                lastErr = Marshal.GetLastWin32Error();
                Tr("LoadLibrary 失败 err=" + lastErr + " → 试下一个候选");
            }
            return IntPtr.Zero;
        }

        // 加载 DLL 时**关掉系统自己的硬错误弹窗**（"无法定位程序输入点"弹框即源于此）：加载器在解析导入失败时默认会弹框，
        // 而 AI 运行时恰恰是"可能加载失败、失败了要优雅降级"的东西 —— 让失败静默返回错误码，
        // 由我们决定提示什么。用完恢复原来的模式，别影响程序其它部分（如 WIA 扫描的提示）。
        [DllImport("kernel32.dll")]
        static extern uint SetErrorMode(uint uMode);

        const uint SEM_FAILCRITICALERRORS = 0x0001;
        const uint SEM_NOOPENFILEERRORBOX = 0x8000;

        static IntPtr LoadLibraryQuiet(string path)
        {
            uint old = 0;
            try { old = SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOOPENFILEERRORBOX); } catch { }
            try { return LoadLibraryW(path); }
            finally { try { SetErrorMode(old); } catch { } }
        }

        public static string ModelPath
        {
            get
            {
                try
                {
                    if (!Directory.Exists(AiDir)) return null;
                    string[] files = Directory.GetFiles(AiDir, "*.onnx");
                    if (files.Length == 0) return null;
                    // 有多个时优先名字里带 migan 的
                    for (int i = 0; i < files.Length; i++)
                        if (Path.GetFileName(files[i]).ToLowerInvariant().IndexOf("migan") >= 0) return files[i];
                    return files[0];
                }
                catch { return null; }
            }
        }

        public static bool AssetsPresent
        {
            get { return RuntimePresent && ModelPath != null; }
        }

#if ORT_INTEROP
        public static bool InteropReady { get { return true; } }
#else
        public static bool InteropReady { get { return false; } }
#endif

        // ---- 可用性判定（廉价：只看文件与互操作层，绝不在这里加载 DLL）----
        // 设计要点：**启动路径上永远不加载 onnxruntime.dll**。
        // 加载期硬失败（系统里的 api-ms-win-* 映射不兼容、缺 VC 运行库、DLL 被换坏）是 .NET
        // 捕获不到的：进程会在 main 之前被系统打死（典型现场表现：整个程序打不开）。
        // 现在改成：真正加载之前先拉一个子进程（--ort-probe）去试；子进程要是被打死，父进程读不到
        // 结果文件就判定"本机不安全"，于是**永远不会在本进程里加载**，程序照常以三模式运行。
        public static bool Available
        {
            get { return AssetsPresent && InteropReady; }
        }

        // ProbeRuntime 的共享态锁 —— 并行归档/多 worker 同时进入时会
        //   重复拉起探测子进程并竞争写 probeState（共享状态必须加锁，这是项目的并发约束）。
        //   探测是低频操作（成功一次即命中 probeState==1 缓存），整段加锁开销可忽略。
        static readonly object probeGate = new object();
        static int probeState = 0;      // 0=未探测 1=可以 2=不行 3=探测子进程被打死（加载期硬失败）
        static string probeError = null;
        static string probeVersion = null;
        static DateTime probeAt = DateTime.MinValue;
        static int lastLoadError;
        const int ProbeRetrySeconds = 10;   // 失败后隔一会儿允许重试（装完运行库不重启程序也能用上）

        public static string RuntimeProbeError { get { return probeError; } }
        public static bool Probed { get { return probeState != 0; } }
        public static string ProbeVersion { get { return probeVersion; } }

        public static void ResetProbe()
        {
            lock (probeGate) { probeState = 0; probeError = null; probeVersion = null; }
        }

        // 带缓存的隔离探测：子进程里真加载一次，父进程只读结果
        // 整段加 probeGate —— 见上方 probeGate 注释。
        //   （Diag.Write 只在自己那把 logGate 内写文件、不回调本方法，锁序无死锁风险。）
        public static bool ProbeRuntime()
        {
            lock (probeGate)
            {
                if (!InteropReady) return false;
                if (probeState == 1) return true;
                if (probeState != 0 && (DateTime.Now - probeAt).TotalSeconds < ProbeRetrySeconds) return false;
                probeAt = DateTime.Now;
                if (!RuntimePresent) { probeState = 2; probeError = "ai\\onnxruntime.dll 不存在（ai 目录不完整）"; return false; }
                if (RunChildProbe()) { probeState = 1; probeError = null; return true; }
                if (probeState == 0) probeState = 2;
                return false;
            }
        }

        // 用 AI 之前必须过这一关：探测不过就带着原因抛，而不是去本进程里加载
        static void EnsureProbe()
        {
            if (!ProbeRuntime())
                throw new Exception("AI 修复不可用：" + (probeError == null ? "未知原因" : probeError));
        }

        // 子进程入口（--ort-probe）用：在本进程里真的加载一次，返回 "OK <版本>" 或 "ERR <原因>"
        public static string ProbeInProcess()
        {
            try
            {
                if (!InteropReady) return "ERR 缺少互操作层（需先运行 tools\\gen_ort_interop.ps1）";
                if (!RuntimePresent) return "ERR ai\\onnxruntime.dll 不存在（ai 目录不完整）";
                // 报告里带上"用的是哪一套"，目标机排查时一眼能看出走的是标准版还是 Win7 兼容版
                if (LoadRuntimeOnly()) return "OK " + (probeVersion == null ? "" : probeVersion)
                                            + " [" + ChosenDllTag + "]";
                return "ERR onnxruntime.dll 无法加载（系统错误码 " + lastLoadError + "）。常见原因："
                     + "① 缺 VC++ 2015-2022 x64 运行库；② ai 目录不完整（缺 ai\\onnxruntime.dll，"
                     + "或 Win7 上缺 ai\\win7\\ 兼容层）；③ 系统 api-ms-win-* 映射与本程序不匹配";
            }
            catch (Exception ex) { return "ERR " + ex.Message; }
        }

        static string EntryExePath()
        {
            try
            {
                System.Reflection.Assembly asm = System.Reflection.Assembly.GetEntryAssembly();
                if (asm != null)
                {
                    string p = asm.Location;
                    if (p != null && p.Length > 0 && File.Exists(p)) return p;
                }
            }
            catch { }
            try
            {
                string[] argv = Environment.GetCommandLineArgs();
                if (argv != null && argv.Length > 0 && File.Exists(argv[0])) return argv[0];
            }
            catch { }
            return null;
        }

        // 真·隔离探测：父进程只负责拉子进程 + 读结果文件
        static bool RunChildProbe()
        {
            string exe = EntryExePath();
            if (exe == null) { probeError = "定位不到自身程序文件，无法做隔离探测"; return false; }
            string rep = Path.Combine(Path.GetTempPath(), "PicTool-ort-probe.txt");
            try { if (File.Exists(rep)) File.Delete(rep); } catch { }
            bool hardFail = false;
            try
            {
                System.Diagnostics.ProcessStartInfo si =
                    new System.Diagnostics.ProcessStartInfo(exe, "--ort-probe");
                si.UseShellExecute = false;
                si.CreateNoWindow = true;
                si.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
                System.Diagnostics.Process p = System.Diagnostics.Process.Start(si);
                if (p == null) { probeError = "无法启动隔离探测进程"; probeState = 2; return false; }
                Diag.Info("AI", "隔离探测已发起（子进程 " + System.IO.Path.GetFileName(exe) + "）");
                if (!p.WaitForExit(90000))
                {
                    try { p.Kill(); } catch { }
                    probeError = "隔离探测超时（90 秒）。老机器首次加载会慢，稍后再试即可。";
                    probeState = 2;
                    return false;
                }
                if (!File.Exists(rep))
                {
                    hardFail = true;
                    probeError = "加载 onnxruntime.dll 时进程被系统直接终止（退出码 " + p.ExitCode + "）—— "
                        + "通常是系统里的 api-ms-win-* 映射与本程序不兼容或运行库不匹配；"
                        + "程序会继续以普通/增强/混合三模式运行，AI 修复暂时不可用。";
                    return false;
                }
                string txt = File.ReadAllText(rep).Trim();
                if (txt.StartsWith("OK"))
                {
                    probeVersion = txt.Length > 2 ? txt.Substring(2).Trim() : "";
                    return true;
                }
                probeError = txt.StartsWith("ERR") ? txt.Substring(3).Trim() : txt;
                return false;
            }
            catch (Exception ex) { probeError = "隔离探测失败：" + ex.Message; return false; }
            finally { probeState = hardFail ? 3 : 2; }
        }

        // 给界面与日志用的一句话状态。廉价：不加载 DLL；
        // 已经探测过就把结论带出来（没探测过就说明"首次使用时检测"，不吓人）。
        public static string StatusText
        {
            get
            {
                if (!InteropReady) return "AI 修复未构建：缺少互操作层（需先用 tools\\gen_ort_interop.ps1 从头文件生成）";
                if (!Directory.Exists(AiDir)) return "AI 修复未安装：把 onnxruntime.dll 与模型文件放进程序目录的 ai\\ 文件夹即可";
                if (!RuntimePresent) return "AI 修复未安装：ai\\onnxruntime.dll 不存在";
                if (ModelPath == null) return "AI 修复未安装：ai\\ 里没有 .onnx 模型文件";
                if (probeState == 1) return "AI 修复可用（" + Path.GetFileName(ModelPath) + "）";
                if (probeState == 2 || probeState == 3)
                    return "AI 素材已就位，但运行时不可用：" + (probeError == null ? "未知原因" : probeError);
                return "AI 素材已就位（" + Path.GetFileName(ModelPath) + "），首次使用时会自动做一次加载检测";
            }
        }

#if ORT_INTEROP

        // ---------------- ONNX Runtime 互操作 ----------------

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int OrtLoggingLevel();
        delegate IntPtr GetApiBaseDelegate();   // OrtGetApiBase：返回 OrtApiBase* 的函数
        delegate IntPtr GetApiDelegate(uint version);
        delegate IntPtr GetVersionStringDelegate();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr CreateEnvDelegate(int level, string logid, out IntPtr env);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr CreateSessionOptionsDelegate(out IntPtr so);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr SetIntraOpNumThreadsDelegate(IntPtr so, int n);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr SetInterOpNumThreadsDelegate(IntPtr so, int n);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr SetSessionGraphOptimizationLevelDelegate(IntPtr so, int level);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr CreateSessionDelegate(IntPtr env, IntPtr so, [MarshalAs(UnmanagedType.LPWStr)] string modelPath, out IntPtr session);
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
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr SessionGetInputTypeInfoDelegate(IntPtr session, IntPtr index, out IntPtr typeInfo);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr SessionGetOutputTypeInfoDelegate(IntPtr session, IntPtr index, out IntPtr typeInfo);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr CastTypeInfoToTensorInfoDelegate(IntPtr typeInfo, out IntPtr tensorInfo);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr GetTensorElementTypeDelegate(IntPtr tensorInfo, out int type);
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

        // 常量（来自 onnxruntime_c_api.h）
        const int LOGGING_ERROR = 3;
        const int ORT_ARENA_ALLOCATOR = 0;
        const int ORT_MEM_TYPE_DEFAULT = 0;
        const int TYPE_FLOAT = 1;
        const int TYPE_UINT8 = 2;
        const int GRAPH_OPT_ALL = 3;

        sealed class Ort
        {
            public IntPtr lib, env, session, memInfo, allocator;
            public OrtApiRaw api;
            public CreateEnvDelegate CreateEnv;
            public CreateSessionOptionsDelegate CreateSessionOptions;
            public SetIntraOpNumThreadsDelegate SetIntraOpNumThreads;
            public SetSessionGraphOptimizationLevelDelegate SetGraphOpt;
            public CreateSessionDelegate CreateSession;
            public CreateSessionFromArrayDelegate CreateSessionFromArray;
            public CreateCpuMemoryInfoDelegate CreateCpuMemoryInfo;
            public CreateTensorWithDataAsOrtValueDelegate CreateTensor;
            public RunDelegate Run;
            public GetTensorMutableDataDelegate GetTensorData;
            public GetTensorTypeAndShapeDelegate GetTensorShape;
            public SessionGetInputCountDelegate InputCount;
            public SessionGetOutputCountDelegate OutputCount;
            public SessionGetInputNameDelegate InputName;
            public SessionGetOutputNameDelegate OutputName;
            public SessionGetInputTypeInfoDelegate InputType;
            public SessionGetOutputTypeInfoDelegate OutputType;
            public CastTypeInfoToTensorInfoDelegate CastTensor;
            public GetTensorElementTypeDelegate ElemType;
            public GetDimensionsCountDelegate DimCount;
            public GetDimensionsDelegate Dims;
            public GetTensorShapeElementCountDelegate ElemCount;
            public GetAllocatorWithDefaultOptionsDelegate GetAllocator;
            public ReleaseDelegate ReleaseEnv, ReleaseSession, ReleaseOptions, ReleaseValue, ReleaseMemInfo;
            public ReleaseDelegate ReleaseTypeInfo, ReleaseTensorInfo, ReleaseAllocator, ReleaseStatus;
            public GetErrorMessageDelegate ErrorMessage;
        }

        public static bool Trace = false;
        public static bool DebugLog = false;   // true 时让 ONNX Runtime 输出详细日志到 stderr
        public static bool DisableMemPattern = false;
        public static bool InvertMask = true;   // MI-GAN 管线约定：255=保留、0=待填充
        public static bool RgbOrder = true;     // 模型输入通道顺序：true=RGB, false=BGR
        // 模型画像：不同模型对"掩膜极性 / 量纲 / 工作尺寸"的约定不一样，
        // 搞错了就是"该填的没填、不该动的全动"。按文件名关键字识别，认不出就按 MI-GAN 一套。
        sealed class ModelProfile
        {
            public string Kind;
            public int WorkSide;
            public bool HoleIsZero;      // true: 0=待填充（MI-GAN）；false: 1=待填充（LaMa）
            public bool MaskUnitScale;   // float 掩膜是否用 0..1 量纲
            public ModelProfile(string kind, int workSide, bool holeIsZero, bool maskUnitScale)
            {
                Kind = kind; WorkSide = workSide; HoleIsZero = holeIsZero; MaskUnitScale = maskUnitScale;
            }
        }

        static ModelProfile ProfileOf(string modelFileName)
        {
            string n = (modelFileName == null ? "" : modelFileName).ToLowerInvariant();
            if (n.IndexOf("lama") >= 0)
                return new ModelProfile("LaMa", 512, false, true);    // LaMa：mask 1=待填充、0..1 量纲
            return new ModelProfile("MI-GAN", 512, true, false);       // MI-GAN（默认）：mask 0=待填充、0/255
        }

        public static string ModelKind { get { return ProfileOf(Path.GetFileName(ModelPath)).Kind; } }

        public static int WorkSide = 512;       // 送模型的工作边长（MI-GAN/LaMa 都在 512 尺度上训练）
        public static int FeatherPx = 6;        // 合成羽化像素：涂抹边界向外过渡这么宽，避免硬接缝
        public static int MaskDilatePx = 3;     // 送模型前把洞外扩这么多（模型尺度）：躲开污渍那圈硬边
        public static Bitmap LastRawOutput;     // 诊断用：模型原始输出（未做任何合成）   // 变通：某些动态形状模型需要关掉内存模式
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr DisableMemPatternDelegate(IntPtr so);   // 诊断用：把互操作每一步打到控制台
        static void Tr(string s)
        {
            if (!Trace) return;
            try
            {
                // 程序是 WinExe，没有控制台，跟踪写文件（%TEMP%\PicTool-ort-trace.txt）
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "PicTool-ort-trace.txt"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + Environment.NewLine,
                    System.Text.Encoding.UTF8);
            }
            catch { }
        }

        static Ort ort;

        // ★ 推理与释放互斥 —— 关窗时 UI 线程在 Shutdown() 里 Release+FreeLibrary，
        //   而修复线程可能正卡在 o.Run() 用同一个原生 session → use-after-free = 原生崩溃。
        //   锁粒度=整次推理：与 Ocr.OcrGate 同一取舍（AI 修复本就是单发、无并发的）。
        //   Run 内的进度回调只做 BeginInvoke（异步），不会反向等 UI 线程，故无死锁面。
        static readonly object ortGate = new object();

        // 只加载运行时与 API（不需要模型）：用于在目标机上确认 ORT 能否加载。
        // 只做探测，不碰静态 ort（探测对象用完即释放）。
        static bool LoadRuntimeOnly()
        {
            Ort o = new Ort();
            try
            {
                // 两套运行时依次试（原版 → ai\win7 兼容版），谁加载成功用谁
                string used; int err;
                o.lib = LoadAnyRuntime(out used, out err);
                if (o.lib == IntPtr.Zero)
                {
                    lastLoadError = err;
                    Tr("LoadLibrary 全部候选失败 err=" + lastLoadError);
                    return false;
                }
                IntPtr pBase = GetProcAddress(o.lib, "OrtGetApiBase");
                if (pBase == IntPtr.Zero) { Tr("GetProcAddress 失败"); return false; }
                Tr("OrtGetApiBase = " + pBase.ToString("X"));
                IntPtr pApiBase = Fn<GetApiBaseDelegate>(pBase, "OrtGetApiBase")();   // 必须先调用，才拿到 OrtApiBase 结构体指针
                if (pApiBase == IntPtr.Zero) { Tr("OrtGetApiBase() 返回空"); return false; }
                IntPtr pGetApi = Marshal.ReadIntPtr(pApiBase, 0);
                GetApiDelegate getApi = Fn<GetApiDelegate>(pGetApi, "OrtApiBase.GetApi");
                GetVersionStringDelegate getVer = Fn<GetVersionStringDelegate>(Marshal.ReadIntPtr(pApiBase, IntPtr.Size), "OrtApiBase.GetVersionString");
                string ver = Marshal.PtrToStringAnsi(getVer());
                probeVersion = ver;
                Tr("版本字符串 = " + ver);
                return ver != null && ver.Length > 0;
            }
            finally
            {
                Release(o);
            }
        }

        static T Fn<T>(IntPtr p, string name) where T : class
        {
            if (p == IntPtr.Zero)
                throw new Exception("ONNX Runtime 缺少接口 " + name + "（运行时版本与程序不匹配？）");
            try { return (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T)); }
            catch (Exception ex) { throw new Exception("ONNX Runtime 接口 " + name + " 无法绑定：" + ex.Message); }
        }

        // 取错误文本必须带着对应的 Ort 实例：初始化期间静态 ort 还是 null，
        // 早先这里直接用 ort 会把真实原因吞成"未知错误"。
        static string StatusMessageOf(Ort o, IntPtr status)
        {
            if (status == IntPtr.Zero || o == null || o.ErrorMessage == null) return "未知错误";
            try
            {
                IntPtr msg = o.ErrorMessage(status);
                string s = Marshal.PtrToStringAnsi(msg);
                return s == null || s.Length == 0 ? "未知错误" : s;
            }
            catch { return "未知错误"; }
        }

        static string StatusMessage(IntPtr status)
        {
            return StatusMessageOf(ort, status);
        }

        static void Check(Ort o, IntPtr status, string what)
        {
            if (status == IntPtr.Zero) return;
            string m = StatusMessageOf(o, status);
            try { if (o != null && o.ReleaseStatus != null) o.ReleaseStatus(status); } catch { }
            throw new Exception(what + " 失败：" + m);
        }

        // 加载 DLL 并建会话（只做一次）
        static void EnsureLoaded(ProgressHandler progress)
        {
            Tr("EnsureLoaded 进入");
            if (ort != null) { Tr("已有会话，直接返回"); return; }

            // 保护机制：先让子进程去试加载。子进程被系统打死时这里就抛异常，
            // 本进程永远不会碰 LoadLibraryW —— 这是"AI 不再把整个程序拖死"的关键一步。
            EnsureProbe();

            string model = ModelPath;
            if (!RuntimePresent) throw new Exception("找不到 ai\\onnxruntime.dll（ai 目录不完整）");
            if (model == null) throw new Exception("ai\\ 目录里没有 .onnx 模型文件");

            // 关键：整个初始化过程只写局部对象 o，全部成功后才赋给静态 ort。
            // 早先的写法是进来就 ort = o，于是一旦中途失败（缺 VC++ 运行库、模型损坏、内存不足），
            // ort 会停在"半成品"状态：下次点击看到 ort != null 直接跳过初始化，
            // 接着访问还是 null 的接口字段，就抛出那句毫无线索的"未将对象引用设置到对象的实例"。
            // 现在失败即释放并抛出可读原因，下次点击会重新完整初始化（用户装好运行库后无需重启程序）。
            Ort o = new Ort();
            try
            {
                // 两套运行时依次试（原版 → ai\win7 兼容版），谁加载成功用谁；
                // 每次尝试都把依赖 DLL 的搜索目录指到它自己所在目录（VC 运行库/垫片都在那儿解析）
                Tr("准备 LoadLibrary（候选：标准版 → ai\\win7 兼容版）");
                string used; int err;
                o.lib = LoadAnyRuntime(out used, out err);
                if (o.lib == IntPtr.Zero)
                    throw new Exception("加载 onnxruntime.dll 失败（错误码 " + err + "）。常见原因："
                        + "① 缺 VC++ 2015-2022 x64 运行库；② ai 目录不完整（缺 ai\\onnxruntime.dll，"
                        + "或 Win7 上缺 ai\\win7\\ 兼容层）。");
                IntPtr pBase = GetProcAddress(o.lib, "OrtGetApiBase");
                if (pBase == IntPtr.Zero) throw new Exception("onnxruntime.dll 里找不到 OrtGetApiBase，可能不是官方运行时");

                // OrtApiBase { GetApi, GetVersionString }：注意 OrtGetApiBase 是函数，必须先调用拿到结构体指针
                IntPtr pApiBase = Fn<GetApiBaseDelegate>(pBase, "OrtGetApiBase")();
                if (pApiBase == IntPtr.Zero) throw new Exception("OrtGetApiBase() 返回空，运行时可能不完整");
                GetApiDelegate getApi = Fn<GetApiDelegate>(Marshal.ReadIntPtr(pApiBase, 0), "OrtApiBase.GetApi");
                GetVersionStringDelegate getVer = Fn<GetVersionStringDelegate>(Marshal.ReadIntPtr(pApiBase, IntPtr.Size), "OrtApiBase.GetVersionString");
                string ver = Marshal.PtrToStringAnsi(getVer());

                // 请求 API 版本：优先用生成时的版本，失败则逐级降级
                IntPtr apiPtr = IntPtr.Zero;
                for (int v = OrtApiInfo.ApiVersion; v >= 1 && apiPtr == IntPtr.Zero; v--)
                    apiPtr = getApi((uint)v);
                if (apiPtr == IntPtr.Zero)
                    throw new Exception("ONNX Runtime 版本不兼容（运行时 " + ver + "，期望 API " + OrtApiInfo.ApiVersion + "）");
                o.api = (OrtApiRaw)Marshal.PtrToStructure(apiPtr, typeof(OrtApiRaw));

                o.CreateEnv = Fn<CreateEnvDelegate>(o.api.CreateEnv, "CreateEnv");
                o.CreateSessionOptions = Fn<CreateSessionOptionsDelegate>(o.api.CreateSessionOptions, "CreateSessionOptions");
                o.SetIntraOpNumThreads = Fn<SetIntraOpNumThreadsDelegate>(o.api.SetIntraOpNumThreads, "SetIntraOpNumThreads");
                o.SetGraphOpt = Fn<SetSessionGraphOptimizationLevelDelegate>(o.api.SetSessionGraphOptimizationLevel, "SetSessionGraphOptimizationLevel");
                o.CreateSession = Fn<CreateSessionDelegate>(o.api.CreateSession, "CreateSession");
                o.CreateSessionFromArray = Fn<CreateSessionFromArrayDelegate>(o.api.CreateSessionFromArray, "CreateSessionFromArray");
                o.CreateCpuMemoryInfo = Fn<CreateCpuMemoryInfoDelegate>(o.api.CreateCpuMemoryInfo, "CreateCpuMemoryInfo");
                o.CreateTensor = Fn<CreateTensorWithDataAsOrtValueDelegate>(o.api.CreateTensorWithDataAsOrtValue, "CreateTensorWithDataAsOrtValue");
                o.Run = Fn<RunDelegate>(o.api.Run, "Run");
                o.GetTensorData = Fn<GetTensorMutableDataDelegate>(o.api.GetTensorMutableData, "GetTensorMutableData");
                o.GetTensorShape = Fn<GetTensorTypeAndShapeDelegate>(o.api.GetTensorTypeAndShape, "GetTensorTypeAndShape");
                o.InputCount = Fn<SessionGetInputCountDelegate>(o.api.SessionGetInputCount, "SessionGetInputCount");
                o.OutputCount = Fn<SessionGetOutputCountDelegate>(o.api.SessionGetOutputCount, "SessionGetOutputCount");
                o.InputName = Fn<SessionGetInputNameDelegate>(o.api.SessionGetInputName, "SessionGetInputName");
                o.OutputName = Fn<SessionGetOutputNameDelegate>(o.api.SessionGetOutputName, "SessionGetOutputName");
                o.InputType = Fn<SessionGetInputTypeInfoDelegate>(o.api.SessionGetInputTypeInfo, "SessionGetInputTypeInfo");
                o.OutputType = Fn<SessionGetOutputTypeInfoDelegate>(o.api.SessionGetOutputTypeInfo, "SessionGetOutputTypeInfo");
                o.CastTensor = Fn<CastTypeInfoToTensorInfoDelegate>(o.api.CastTypeInfoToTensorInfo, "CastTypeInfoToTensorInfo");
                o.ElemType = Fn<GetTensorElementTypeDelegate>(o.api.GetTensorElementType, "GetTensorElementType");
                o.DimCount = Fn<GetDimensionsCountDelegate>(o.api.GetDimensionsCount, "GetDimensionsCount");
                o.Dims = Fn<GetDimensionsDelegate>(o.api.GetDimensions, "GetDimensions");
                o.ElemCount = Fn<GetTensorShapeElementCountDelegate>(o.api.GetTensorShapeElementCount, "GetTensorShapeElementCount");
                o.GetAllocator = Fn<GetAllocatorWithDefaultOptionsDelegate>(o.api.GetAllocatorWithDefaultOptions, "GetAllocatorWithDefaultOptions");
                o.ReleaseEnv = Fn<ReleaseDelegate>(o.api.ReleaseEnv, "ReleaseEnv");
                o.ReleaseSession = Fn<ReleaseDelegate>(o.api.ReleaseSession, "ReleaseSession");
                o.ReleaseOptions = Fn<ReleaseDelegate>(o.api.ReleaseSessionOptions, "ReleaseSessionOptions");
                o.ReleaseValue = Fn<ReleaseDelegate>(o.api.ReleaseValue, "ReleaseValue");
                o.ReleaseMemInfo = Fn<ReleaseDelegate>(o.api.ReleaseMemoryInfo, "ReleaseMemoryInfo");
                o.ReleaseTypeInfo = Fn<ReleaseDelegate>(o.api.ReleaseTypeInfo, "ReleaseTypeInfo");
                o.ReleaseTensorInfo = Fn<ReleaseDelegate>(o.api.ReleaseTensorTypeAndShapeInfo, "ReleaseTensorTypeAndShapeInfo");
                o.ReleaseAllocator = Fn<ReleaseDelegate>(o.api.ReleaseAllocator, "ReleaseAllocator");
                o.ReleaseStatus = Fn<ReleaseDelegate>(o.api.ReleaseStatus, "ReleaseStatus");
                o.ErrorMessage = Fn<GetErrorMessageDelegate>(o.api.GetErrorMessage, "GetErrorMessage");

                if (progress != null) progress(5);
                Check(o, o.CreateEnv(DebugLog ? 0 : LOGGING_ERROR, "PicTool", out o.env), "创建推理环境");
                IntPtr so;
                Check(o, o.CreateSessionOptions(out so), "创建会话选项");
                int threads = SysInfo.WorkerThreads;
                // 诊断/兼容开关：某些模型（如 LaMa 的 FFT 结构）在旧版 ORT 上多线程会崩，
                // 设 PICTOOL_AI_THREADS=1 可强制单线程跑，用来判断"是不是并发问题"。
                try
                {
                    string ov = Environment.GetEnvironmentVariable("PICTOOL_AI_THREADS");
                    if (ov != null && ov.Length > 0) threads = int.Parse(ov);
                }
                catch { }
                Tr("会话线程数 = " + threads);
                try { Check(o, o.SetIntraOpNumThreads(so, threads), "设置线程数"); } catch { }
                try { Check(o, o.SetGraphOpt(so, GRAPH_OPT_ALL), "设置图优化"); } catch { }
                if (DisableMemPattern)
                {
                    try
                    {
                        if (o.api.DisableMemPattern != IntPtr.Zero)
                        {
                            DisableMemPatternDelegate dmp = Fn<DisableMemPatternDelegate>(o.api.DisableMemPattern, "DisableMemPattern");
                            Check(o, dmp(so), "关闭内存模式");
                            Tr("已关闭内存模式规划");
                        }
                    }
                    catch (Exception ex) { Tr("关闭内存模式失败: " + ex.Message); }
                }
                // 优先把模型读进内存再交给 ORT（CreateSessionFromArray）：绕开路径编码问题，中文目录也能用
                byte[] modelBytes = File.ReadAllBytes(model);
                IntPtr mb = Marshal.AllocHGlobal(modelBytes.Length);
                bool loaded = false;
                try
                {
                    Marshal.Copy(modelBytes, 0, mb, modelBytes.Length);
                    IntPtr st = o.CreateSessionFromArray(o.env, mb, (IntPtr)modelBytes.Length, so, out o.session);
                    if (st == IntPtr.Zero) loaded = true;
                    else
                    {
                        string m1 = StatusMessageOf(o, st);
                        try { o.ReleaseStatus(st); } catch { }
                        Tr("CreateSessionFromArray 失败: " + m1 + "，改用文件路径方式");
                    }
                }
                finally { Marshal.FreeHGlobal(mb); }
                if (!loaded) Check(o, o.CreateSession(o.env, so, model, out o.session), "加载模型 " + Path.GetFileName(model));
                o.ReleaseOptions(so);
                Check(o, o.CreateCpuMemoryInfo(ORT_ARENA_ALLOCATOR, ORT_MEM_TYPE_DEFAULT, out o.memInfo), "创建内存描述");
                Check(o, o.GetAllocator(out o.allocator), "获取分配器");
                if (progress != null) progress(15);
            }
            catch (Exception ex)
            {
                Release(o);
                Diag.Log(ex, "AI 初始化失败");
                throw new Exception("AI 初始化失败：" + ex.Message, ex);
            }
            ort = o;   // 到这里才算真正可用
            Tr("EnsureLoaded 完成");
        }

        // 读取第 index 个输入/输出张量的描述
        static void Describe(IntPtr session, int index, bool input, out int elemType, out long[] dims, out string name)
        {
            Tr("Describe[" + index + "] 取类型信息");
            IntPtr typeInfo;
            IntPtr st = input ? ort.InputType(session, (IntPtr)index, out typeInfo)
                              : ort.OutputType(session, (IntPtr)index, out typeInfo);
            Check(ort, st, "读取张量类型");
            IntPtr ti;
            Check(ort, ort.CastTensor(typeInfo, out ti), "解析张量类型");
            int t;
            Check(ort, ort.ElemType(ti, out t), "读取元素类型");
            IntPtr n;
            Check(ort, ort.DimCount(ti, out n), "读取维数");
            int nd = (int)n;
            dims = new long[nd];
            Tr("  维数=" + nd);
            if (nd > 0) Check(ort, ort.Dims(ti, dims, n), "读取形状");
            Tr("  形状读取完成");
            // 注意：CastTypeInfoToTensorInfo 返回的是借用指针（由 typeInfo 持有），不能单独释放
            ort.ReleaseTypeInfo(typeInfo);
            Tr("  取张量名");
            IntPtr pName;
            st = input ? ort.InputName(session, (IntPtr)index, ort.allocator, out pName)
                       : ort.OutputName(session, (IntPtr)index, ort.allocator, out pName);
            Check(ort, st, "读取张量名");
            name = Marshal.PtrToStringAnsi(pName);
            elemType = t;   // 名字字符串由 ORT 的默认分配器持有，进程内一次性少量占用，不释放
        }

        // ---------------- 自检（--ai-check） ----------------

        public static string Check()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("AI 修复自检");
            sb.AppendLine("程序目录: " + AppDomain.CurrentDomain.BaseDirectory);
            sb.AppendLine("ai 目录  : " + AiDir + (Directory.Exists(AiDir) ? "（存在）" : "（不存在）"));
            sb.AppendLine("运行时   : " + DllPath + (File.Exists(DllPath) ? "（存在）" : "（缺失）")
                        + "  ｜ Win7 兼容层: " + Win7DllPath
                        + (File.Exists(Win7DllPath) ? "（存在）" : "（缺失）"));
            if (ChosenDllPath != null) sb.AppendLine("实际使用 : " + ChosenDllPath + "  [" + ChosenDllTag + "]");
            sb.AppendLine("模型     : " + (ModelPath == null ? "（未找到 .onnx）" : ModelPath));
            sb.AppendLine("互操作层 : " + (InteropReady ? "已构建" : "未构建（需先运行 tools\\gen_ort_interop.ps1）"));
            sb.AppendLine("系统判定 : " + CandidateDesc);
            sb.AppendLine("环境     : " + SysInfo.Describe());
            // 唯一权威判定：先跑隔离探测（子进程里真加载一次）。
            // 子进程被系统打死时主进程毫发无伤，这正是隔离探测要保住的设计目标。
            bool rtOk = ProbeRuntime();
            sb.AppendLine("隔离探测 : " + (probeState == 3
                ? "子进程被系统终止（加载期硬失败，主进程已受保护）"
                : rtOk ? "通过（子进程加载成功）" : "未通过"));
            if (probeVersion != null && probeVersion.Length > 0) sb.AppendLine("运行时版本: " + probeVersion);
            sb.AppendLine("AI 可用  : " + (rtOk ? "是" : "否") + "  — " + StatusText);
            if (!rtOk) return sb.ToString();
            if (ModelPath == null)
            {
                sb.AppendLine("结论     : 运行时可用，但 ai\\ 里还没有 .onnx 模型文件");
                return sb.ToString();
            }
            try
            {
                Tr("Check: 调用 EnsureLoaded");
                EnsureLoaded(null);
                Tr("Check: EnsureLoaded 返回");
                IntPtr ic, oc;
                Check(ort, ort.InputCount(ort.session, out ic), "读取输入个数");
                Check(ort, ort.OutputCount(ort.session, out oc), "读取输出个数");
                sb.AppendLine("输入个数 : " + (int)ic + "   输出个数: " + (int)oc);
                for (int i = 0; i < (int)ic; i++)
                {
                    int t; long[] dims; string nm;
                    Describe(ort.session, i, true, out t, out dims, out nm);
                    sb.AppendLine("  输入[" + i + "] 名=" + nm + " 类型=" + TypeName(t) + " 形状=[" + Join(dims) + "]");
                }
                for (int i = 0; i < (int)oc; i++)
                {
                    int t; long[] dims; string nm;
                    Describe(ort.session, i, false, out t, out dims, out nm);
                    sb.AppendLine("  输出[" + i + "] 名=" + nm + " 类型=" + TypeName(t) + " 形状=[" + Join(dims) + "]");
                }
                sb.AppendLine("结论     : 运行时与模型加载正常");
            }
            catch (Exception ex)
            {
                sb.AppendLine("结论     : 失败 — " + ex.Message);
            }
            return sb.ToString();
        }

        static string TypeName(int t)
        {
            if (t == TYPE_FLOAT) return "float32";
            if (t == TYPE_UINT8) return "uint8";
            return "类型" + t;
        }

        static string Join(long[] a)
        {
            string s = "";
            for (int i = 0; i < a.Length; i++) { if (i > 0) s += ","; s += a[i]; }
            return s;
        }

        // ---------------- 推理 ----------------

        // 返回填充后的新图（不改动入参）
        public static Bitmap Run(Bitmap src, Bitmap mask, ProgressHandler progress)
        {
            return Run(src, mask, progress, null);
        }

        // cancelled：用户取消检查（推理前的等待/加载阶段与推理后立即响应；推理本身无法中断）
        public static Bitmap Run(Bitmap src, Bitmap mask, ProgressHandler progress, CancelCheck cancelled)
        {
            // 与 Shutdown() 同一把锁：保证"释放原生运行时"与"在跑的推理"不会同时发生
            lock (ortGate) { return RunCore(src, mask, progress, cancelled); }
        }

        static Bitmap RunCore(Bitmap src, Bitmap mask, ProgressHandler progress, CancelCheck cancelled)
        {
            if (cancelled != null && cancelled()) throw new OpCancelledException();
            if (!Available) throw new Exception(StatusText);
            EnsureLoaded(progress);
            if (cancelled != null && cancelled()) throw new OpCancelledException();

            // 取本地引用：这期间若窗体关闭调用 Shutdown()，静态字段可能被清空
            Ort o = ort;
            if (o == null || o.session == IntPtr.Zero) throw new Exception("AI 修复未就绪：" + StatusText);

            int w = src.Width, h = src.Height;

            // 只处理"涂抹区域 + 一圈上下文"，并按模型训练尺度（WorkSide）推理。
            // 旧实现把整页原图喂进去、输出再整页 SourceCopy 贴回：老机器十几秒，
            // 而且涂抹之外的地方也被生成模型重绘（整页发糊），这是"修图很垃圾"的主因。
            Rectangle box = MaskBounds(mask, w, h);
            if (box.Width <= 0 || box.Height <= 0) return Imaging.Ensure24bpp(new Bitmap(src));
            Rectangle crop = ExpandBox(box, w, h);
            if (progress != null) progress(25);

            // 读输入规格
            int nIn;
            IntPtr c;
            Check(o, o.InputCount(o.session, out c), "读取输入个数");
            nIn = (int)c;
            int imgIdx = 0, mskIdx = nIn > 1 ? 1 : 0;
            int imgType, mskType;
            long[] imgDims, mskDims;
            string imgName, mskName;
            Describe(o.session, imgIdx, true, out imgType, out imgDims, out imgName);
            if (nIn > 1) Describe(o.session, mskIdx, true, out mskType, out mskDims, out mskName);
            else { mskType = imgType; mskDims = imgDims; mskName = imgName; }

            // 按模型画像走：极性与量纲搞反 = 该填的没填、不该动的全动
            ModelProfile prof = ProfileOf(Path.GetFileName(ModelPath));
            bool fixedSize = HasFixedSize(imgDims);
            int mw, mh;
            if (fixedSize)
            {
                // 固定尺寸模型（例如纯生成器 512×512）：把这个裁剪块缩放到模型尺寸
                mw = (int)FindDim(imgDims, 3);
                mh = (int)FindDim(imgDims, 2);
                if (mw <= 0 || mh <= 0) throw new Exception("模型输入形状无法识别：[" + Join(imgDims) + "]");
            }
            else
            {
                // 动态形状模型：长边对齐 WorkSide，短边按比例（取 8 的倍数，避免补边痕迹）
                double k = (double)prof.WorkSide / Math.Max(crop.Width, crop.Height);
                mw = Round8((int)Math.Round(crop.Width * k));
                mh = Round8((int)Math.Round(crop.Height * k));
            }
            if (progress != null) progress(30);

            // 准备张量数据
            IntPtr imgData = Marshal.AllocHGlobal(mw * mh * 3);
            IntPtr mskData = Marshal.AllocHGlobal(mw * mh);
            IntPtr imgVal = IntPtr.Zero, mskVal = IntPtr.Zero, outVal = IntPtr.Zero;
            // float 输入模型（如 LaMa）需要独立的归一化缓冲：CreateTensor 不拷贝数据，
            // 张量在 Run 期间一直引用这块内存 —— 必须活到推理结束，绝不能建完张量就释放
            // （原先建完张量立即 FreeHGlobal，是 use-after-free；
            //   uint8 输入的 MI-GAN 走不到这条路所以没炸，换 float 模型必炸）
            IntPtr imgFBuf = IntPtr.Zero, mskFBuf = IntPtr.Zero;
            Bitmap scaled = null, scaledMask = null;
            try
            {
                // 裁剪块 → 缩放到模型尺度；掩膜单独按最近邻采样（保持边缘干净）
                scaled = ResizeCrop(src, crop, mw, mh);
                byte[] a = BgrToRgbPlanar(scaled);
                byte[] b = MaskPlaneScaled(mask, crop, mw, mh, prof.HoleIsZero);
                Marshal.Copy(a, 0, imgData, a.Length);
                Marshal.Copy(b, 0, mskData, b.Length);

                long[] shapeImg = ShapeNchw(imgDims, 3, mw, mh);
                long[] shapeMsk = ShapeNchw(mskDims, 1, mw, mh);
                // 模型声明的元素类型决定数据格式：uint8 直传；float 需要归一化到 0..1
                byte[] imgBuf = imgType == TYPE_FLOAT ? ToFloatBuffer(imgData, mw * mh * 3, 255f) : null;
                IntPtr imgPtr = imgData;
                int imgLen = mw * mh * 3;
                if (imgBuf != null) { imgFBuf = Marshal.AllocHGlobal(imgBuf.Length); Marshal.Copy(imgBuf, 0, imgFBuf, imgBuf.Length); imgPtr = imgFBuf; imgLen = imgBuf.Length; }
                Check(o, o.CreateTensor(o.memInfo, imgPtr, (IntPtr)imgLen, shapeImg,
                        (IntPtr)shapeImg.Length, imgType, out imgVal), "创建图像张量");
                if (nIn > 1)
                {
                    if (mskType == TYPE_FLOAT)
                    {
                        byte[] mb = ToFloatBuffer(mskData, mw * mh, prof.MaskUnitScale ? 255f : 1f);
                        mskFBuf = Marshal.AllocHGlobal(mb.Length);
                        Marshal.Copy(mb, 0, mskFBuf, mb.Length);
                        Check(o, o.CreateTensor(o.memInfo, mskFBuf, (IntPtr)mb.Length, shapeMsk,
                                (IntPtr)shapeMsk.Length, mskType, out mskVal), "创建掩码张量");
                    }
                    else
                        Check(o, o.CreateTensor(o.memInfo, mskData, (IntPtr)(mw * mh), shapeMsk,
                                (IntPtr)shapeMsk.Length, mskType, out mskVal), "创建掩码张量");
                }
                if (progress != null) progress(40);

                // 输出规格
                int outType; long[] outDims; string outName;
                Describe(o.session, 0, false, out outType, out outDims, out outName);
                long need = 0;   // 动态形状模型：加载时的形状是 -1，必须跑完后从输出张量读真实形状

                IntPtr nameImg = Marshal.StringToHGlobalAnsi(imgName);
                IntPtr nameMsk = Marshal.StringToHGlobalAnsi(mskName);
                IntPtr nameOut = Marshal.StringToHGlobalAnsi(outName);
                IntPtr[] inNames, inVals;
                try
                {
                    if (nIn > 1)
                    {
                        inNames = new IntPtr[] { nameImg, nameMsk };
                        inVals = new IntPtr[] { imgVal, mskVal };
                    }
                    else
                    {
                        inNames = new IntPtr[] { nameImg };
                        inVals = new IntPtr[] { imgVal };
                    }
                    IntPtr[] outNames = new IntPtr[] { nameOut };
                    IntPtr[] outs = new IntPtr[1];
                    Tr("准备推理: 输入 " + nIn + " 个, 图像 " + mw + "x" + mh + " 类型 " + imgType);
                    Check(o, o.Run(o.session, IntPtr.Zero, inNames, inVals, (IntPtr)inNames.Length,
                            outNames, (IntPtr)1, outs), "推理");
                if (cancelled != null && cancelled()) throw new OpCancelledException();
                    outVal = outs[0];
                    Tr("推理返回, 输出张量句柄 " + outVal.ToString("X"));
                }
                finally
                {
                    Marshal.FreeHGlobal(nameImg);
                    Marshal.FreeHGlobal(nameMsk);
                    Marshal.FreeHGlobal(nameOut);
                }
                if (progress != null) progress(85);

                // 读回结果：先拿到输出张量的真实形状（动态形状模型必须这么做）
                int ow = mw, oh = mh;
                IntPtr shapeInfo;
                if (o.GetTensorShape(outVal, out shapeInfo) == IntPtr.Zero && shapeInfo != IntPtr.Zero)
                {
                    IntPtr cnt;
                    if (o.ElemCount(shapeInfo, out cnt) == IntPtr.Zero) need = (long)cnt;
                    IntPtr ndc;
                    if (o.DimCount(shapeInfo, out ndc) == IntPtr.Zero)
                    {
                        int ndn = (int)ndc;
                        long[] od = new long[ndn];
                        if (ndn > 0 && o.Dims(shapeInfo, od, ndc) == IntPtr.Zero && od.Length == 4)
                        {
                            oh = (int)od[2]; ow = (int)od[3];
                        }
                    }
                    Tr("输出张量: " + ow + "x" + oh + " 元素 " + need);
                    o.ReleaseTensorInfo(shapeInfo);
                }
                // need 拿到的是"元素个数"，按模型声明的类型换算成字节数。
                // 修 bug：原来直接拿元素数当字节数用 —— uint8 输出的 MI-GAN 看不出问题，
                // 换 float32 输出的模型（如 LaMa）就只分配了 1/4 大小，回读时越界 → 进程访问冲突。
                // 再修：need>0 时完全相信张量报告（原来"小于 ow*oh*3 就强行放大"会对
                // 少通道输出越界读原生内存）；缓冲下限仍按 3 通道给，保住 PlaneToBitmap 的读取安全。
                Tr("输出元素数 need=" + need + " ow=" + ow + " oh=" + oh + " outType=" + outType);
                long needBytes = need > 0 ? ((outType == TYPE_FLOAT) ? need * 4 : need) : 0;
                long minPixels = (long)ow * oh * 3;
                long minBytes = (outType == TYPE_FLOAT) ? minPixels * 4 : minPixels;
                if (needBytes < minBytes) needBytes = minBytes;
                long copyN = need > 0 ? ((outType == TYPE_FLOAT) ? need * 4 : need) : minBytes;   // 只拷张量真实拥有的字节数
                if (copyN > needBytes) copyN = needBytes;
                IntPtr pOut;
                Check(o, o.GetTensorData(outVal, out pOut), "读取输出数据");
                byte[] outBytes = new byte[needBytes];
                Tr("分配输出缓冲 " + needBytes + " 字节, 拷贝 " + copyN + ", 开始");
                Marshal.Copy(pOut, outBytes, 0, (int)copyN);
                Tr("输出拷贝完成");
                float outScale = 255f;
                if (outType == TYPE_FLOAT)
                {
                    // 采样判断量纲：全部 <=1.5 视为 0..1，否则视为 0..255
                    float mx = 0f;
                    int step = Math.Max(1, (int)(copyN / 4000 / 4));
                    for (int ii = 0; ii + 4 <= copyN; ii += step * 4)
                    {
                        float v = BitConverter.ToSingle(outBytes, ii);
                        if (v > mx) mx = v;
                    }
                    outScale = mx <= 1.5f ? 255f : 1f;
                }

                Bitmap rawOut = new Bitmap(ow, oh, PixelFormat.Format24bppRgb);
                Tr("开始转位图 " + ow + "x" + oh + " scale=" + outScale);
                PlaneToBitmap(outBytes, rawOut, ow, oh, outType, outScale, null);
                Tr("位图转换完成");
                // 上一轮的原始输出先释放：不释放的话每次 AI 修复泄漏一整张位图
                try { if (LastRawOutput != null) LastRawOutput.Dispose(); } catch { }
                LastRawOutput = rawOut;
                if (progress != null) progress(92);

                // 合成：只替换涂过的地方，边界按羽化权重过渡 —— 没涂到的像素保持与原图逐 bit 一致
                Bitmap patch = new Bitmap(crop.Width, crop.Height, PixelFormat.Format24bppRgb);
                using (Graphics gg = Graphics.FromImage(patch))
                {
                    gg.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    gg.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    gg.DrawImage(rawOut, new Rectangle(0, 0, crop.Width, crop.Height));
                }
                Bitmap result = Imaging.Ensure24bpp(new Bitmap(src));
                BlendPatch(result, patch, crop, AlphaPlane(mask, crop, FeatherPx));
                patch.Dispose();
                if (progress != null) progress(100);
                return result;
            }
            finally
            {
                if (imgVal != IntPtr.Zero) o.ReleaseValue(imgVal);
                if (mskVal != IntPtr.Zero) o.ReleaseValue(mskVal);
                if (outVal != IntPtr.Zero) o.ReleaseValue(outVal);
                // float 输入缓冲在张量值释放之后才能释放（张量引用着它）
                if (imgFBuf != IntPtr.Zero) Marshal.FreeHGlobal(imgFBuf);
                if (mskFBuf != IntPtr.Zero) Marshal.FreeHGlobal(mskFBuf);
                Marshal.FreeHGlobal(imgData);
                Marshal.FreeHGlobal(mskData);
                if (scaled != null) scaled.Dispose();
                if (scaledMask != null) scaledMask.Dispose();
            }
        }

        // 诊断入口（命令行 --ai-fill <输入图> <输出图> [x y w h] [WorkSide]）：
        // 在图上抹一块纯色当"污渍"，跑一遍真实修复管线并存盘，方便离线比对画质与耗时。
        public static string FillForTest(string inPath, string outPath, string[] argv)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            Bitmap src = null, mask = null, res = null;
            try
            {
                src = Imaging.Ensure24bpp(new Bitmap(inPath));
                int w = src.Width, h = src.Height;
                int x = w / 2 - Math.Max(8, w / 8), y = h / 2 - Math.Max(8, h / 8);
                int bw = Math.Max(16, w / 4), bh = Math.Max(16, h / 4);
                if (argv != null && argv.Length >= 7)
                {
                    x = int.Parse(argv[3]); y = int.Parse(argv[4]);
                    bw = int.Parse(argv[5]); bh = int.Parse(argv[6]);
                }
                if (argv != null && argv.Length >= 8 && argv[7] != null && argv[7].Length > 0)
                    WorkSide = int.Parse(argv[7]);

                using (Graphics g = Graphics.FromImage(src))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(70, 70, 74)))
                    g.FillRectangle(b, x, y, bw, bh);

                sb.AppendLine("输入 : " + inPath + "  " + w + "x" + h);
                sb.AppendLine("污渍 : " + x + "," + y + " " + bw + "x" + bh + "   WorkSide=" + WorkSide);
                sb.AppendLine("状态 : " + StatusText);

                mask = Imaging.MakeMask(w, h);
                {
                    // 8bpp 索引格式不能建 Graphics（MakeMask 注释里写过）：缓冲里写 255 再转位图
                    PixelBuffer mbuf = new PixelBuffer(w, h, 1);
                    for (int yy = y; yy < y + bh && yy < h; yy++)
                    {
                        int ro = yy * mbuf.Stride;
                        for (int xx = x; xx < x + bw && xx < w; xx++)
                            if (xx >= 0 && yy >= 0) mbuf.Data[ro + xx] = 255;
                    }
                    Bitmap nm = BitmapBridge.BufferToMask(mbuf);
                    mask.Dispose();
                    mask = nm;
                }

                DateTime t0 = DateTime.Now;
                Bitmap res2 = Run(src, mask, null);
                res = res2;
                double ms = (DateTime.Now - t0).TotalMilliseconds;
                res.Save(outPath, ImageFormat.Png);
                sb.AppendLine("输出 : " + outPath);
                sb.AppendLine("耗时 : " + (int)ms + " ms");
                if (LastRawOutput != null)
                {
                    LastRawOutput.Save(outPath + ".raw.png", ImageFormat.Png);
                    sb.AppendLine("原始输出: " + outPath + ".raw.png  " + LastRawOutput.Width + "x" + LastRawOutput.Height);
                }
            }
            catch (Exception ex) { sb.AppendLine("失败 : " + ex.Message); }
            finally
            {
                // 异常路径也要释放：CLI 进程短命，但诊断入口不该自己漏
                try { if (mask != null) mask.Dispose(); } catch { }
                try { if (src != null) src.Dispose(); } catch { }
                try { if (res != null) res.Dispose(); } catch { }
            }
            return sb.ToString();
        }

        // 释放一个 Ort 实例（可能是半成品：句柄为 0 或委托还是 null，都要安全跳过）
        static void Release(Ort o)
        {
            if (o == null) return;
            try { if (o.session != IntPtr.Zero && o.ReleaseSession != null) o.ReleaseSession(o.session); } catch { }
            try { if (o.memInfo != IntPtr.Zero && o.ReleaseMemInfo != null) o.ReleaseMemInfo(o.memInfo); } catch { }
            try { if (o.env != IntPtr.Zero && o.ReleaseEnv != null) o.ReleaseEnv(o.env); } catch { }
            try { if (o.lib != IntPtr.Zero) FreeLibrary(o.lib); } catch { }
            o.session = IntPtr.Zero;
            o.memInfo = IntPtr.Zero;
            o.env = IntPtr.Zero;
            o.lib = IntPtr.Zero;
        }

        public static void Shutdown()
        {
            // ★ 与 Run 同一把锁 —— 等正在跑的推理结束再释放，杜绝 use-after-free
            lock (ortGate) { Release(ort); ort = null; }
        }

        // ---------------- 数据转换 ----------------

        static bool HasFixedSize(long[] dims)
        {
            for (int i = 0; i < dims.Length; i++)
                if (dims[i] > 0 && (i == 2 || i == 3)) return true;   // 高/宽固定
            return false;
        }

        static long FindDim(long[] dims, int axis)
        {
            if (dims.Length == 4) return dims[axis];
            return -1;
        }

        // 动态尺寸时把 -1 换成本图尺寸
        static long[] ShapeNchw(long[] dims, int channels, int w, int h)
        {
            if (dims.Length == 4) return new long[] { 1, channels, h, w };
            if (dims.Length == 3) return new long[] { channels, h, w };
            return new long[] { 1, channels, h, w };
        }

        static byte[] BgrToRgbPlanar(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            byte[] outBuf = new byte[w * h * 3];
            PixelBuffer buf = BitmapBridge.ToBuffer(bmp);
            for (int y = 0; y < h; y++)
            {
                int ro = y * buf.Stride;
                int plane = w * h;
                for (int x = 0; x < w; x++)
                {
                    int p = ro + x * 3;
                    int i = y * w + x;
                    outBuf[i] = RgbOrder ? buf.Data[p + 2] : buf.Data[p];              // 平面 0
                    outBuf[plane + i] = buf.Data[p + 1];                               // 平面 1（绿）
                    outBuf[plane * 2 + i] = RgbOrder ? buf.Data[p] : buf.Data[p + 2];  // 平面 2
                }
            }
            return outBuf;
        }

        static byte[] MaskPlane(Bitmap mask, int w, int h)
        {
            byte[] outBuf = new byte[w * h];
            PixelBuffer buf = BitmapBridge.MaskToBuffer(mask);
            for (int y = 0; y < h; y++)
            {
                int ro = y * buf.Stride;
                for (int x = 0; x < w; x++)
                {
                    bool hole = buf.Data[ro + x] >= 128;   // 内部约定：非 0 = 要去除
                    // 模型约定：InvertMask=true 时 255=保留 / 0=填充；false 时相反
                    outBuf[y * w + x] = (byte)(InvertMask ? (hole ? 0 : 255) : (hole ? 255 : 0));
                }
            }
            return outBuf;
        }

        // 把模型输出（RGB 平面，uint8 或 float）写回 24bpp 位图
        static void PlaneToBitmap(byte[] data, Bitmap dst, int w, int h, int elemType,
                                  float scale, Bitmap origMask)
        {
            int plane = w * h;
            PixelBuffer buf = new PixelBuffer(w, h, 3);
            for (int y = 0; y < h; y++)
            {
                int ro = y * buf.Stride;
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    int p = ro + x * 3;
                    if (elemType == TYPE_FLOAT)
                    {
                        buf.Data[p + 2] = ToByte(BitConverter.ToSingle(data, i * 4) * scale);
                        buf.Data[p + 1] = ToByte(BitConverter.ToSingle(data, (plane + i) * 4) * scale);
                        buf.Data[p] = ToByte(BitConverter.ToSingle(data, (plane * 2 + i) * 4) * scale);
                    }
                    else
                    {
                        buf.Data[p + 2] = data[i];
                        buf.Data[p + 1] = data[plane + i];
                        buf.Data[p] = data[plane * 2 + i];
                    }
                }
            }
            BitmapBridge.WriteBack(dst, buf);
        }

        // 把 HGlobal 里的 uint8 数据转成 float 缓冲（按 max 归一化，例如 /255 或 /1）
        static byte[] ToFloatBuffer(IntPtr src, int count, float div)
        {
            byte[] tmp = new byte[count];
            Marshal.Copy(src, tmp, 0, count);
            byte[] outp = new byte[count * 4];
            for (int i = 0; i < count; i++)
            {
                float f = tmp[i] / div;
                byte[] fb = BitConverter.GetBytes(f);
                Buffer.BlockCopy(fb, 0, outp, i * 4, 4);
            }
            return outp;
        }

        static byte ToByte(float v)
        {
            if (v <= 0f) return 0;
            if (v >= 255f) return 255;
            return (byte)v;
        }

        static Bitmap Resize(Bitmap src, int w, int h)
        {
            Bitmap b = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, new Rectangle(0, 0, w, h));
            }
            return b;
        }

        // ---------------- 裁剪 / 掩膜 / 合成（AI 修复的画质关键三件套） ----------------

        // 涂抹区域的外接矩形（约定：掩膜里 >=128 表示"要去掉"）
        static Rectangle MaskBounds(Bitmap mask, int w, int h)
        {
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
            PixelBuffer buf = BitmapBridge.MaskToBuffer(mask);
            for (int y = 0; y < h; y++)
            {
                int ro = y * buf.Stride;
                for (int x = 0; x < w; x++)
                {
                    if (buf.Data[ro + x] < 128) continue;
                    if (x < x0) x0 = x;
                    if (x > x1) x1 = x;
                    if (y < y0) y0 = y;
                    if (y > y1) y1 = y;
                }
            }
            if (x1 < 0) return Rectangle.Empty;
            return Rectangle.FromLTRB(x0, y0, x1 + 1, y1 + 1);
        }

        // 涂抹框 → 裁剪框：外扩一圈上下文，并保证裁剪块不小于 WorkSide（原图像素够的话），
        // 这样缩到模型尺度时周围还有真实细节可参考。
        static Rectangle ExpandBox(Rectangle box, int w, int h)
        {
            int side = Math.Max(box.Width, box.Height);
            int pad = Math.Max(32, side / 2);
            if (side + pad * 2 < WorkSide) pad = Math.Max(pad, (WorkSide - side) / 2);
            int x0 = Math.Max(0, box.X - pad), y0 = Math.Max(0, box.Y - pad);
            int x1 = Math.Min(w, box.Right + pad), y1 = Math.Min(h, box.Bottom + pad);
            return Rectangle.FromLTRB(x0, y0, x1, y1);
        }

        static int Round8(int v)
        {
            int r = (v / 8) * 8;
            return r < 8 ? 8 : r;
        }

        // 把原图的一块裁剪出来缩放到模型输入尺寸
        static Bitmap ResizeCrop(Bitmap src, Rectangle crop, int w, int h)
        {
            Bitmap b = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, w, h), crop, GraphicsUnit.Pixel);
            }
            return b;
        }

        // 裁剪块内的掩膜 → 模型尺度（最近邻，保持硬边；极性按 InvertMask 约定）
        // 掩膜往外多放 MaskDilatePx 个模型像素：不让模型看到污渍那一圈硬边，
        // 否则它会把边缘的暗色"补"出来，修完沿轮廓留一圈很淡的灰线。
        static byte[] MaskPlaneScaled(Bitmap mask, Rectangle crop, int mw, int mh, bool holeIsZero)
        {
            byte[] hole = new byte[mw * mh];
            PixelBuffer buf = BitmapBridge.MaskToBuffer(mask);
            for (int y = 0; y < mh; y++)
            {
                int sy = crop.Y + (int)((long)y * crop.Height / mh);
                if (sy >= mask.Height) sy = mask.Height - 1;
                int ro = sy * buf.Stride;
                for (int x = 0; x < mw; x++)
                {
                    int sx = crop.X + (int)((long)x * crop.Width / mw);
                    if (sx >= mask.Width) sx = mask.Width - 1;
                    hole[y * mw + x] = (byte)(buf.Data[ro + sx] >= 128 ? 255 : 0);
                }
            }

            if (MaskDilatePx > 0) hole = DilateHole(hole, mw, mh, MaskDilatePx);

            byte[] outBuf = new byte[mw * mh];
            for (int i = 0; i < outBuf.Length; i++)
            {
                bool h = hole[i] >= 128;
                outBuf[i] = (byte)(holeIsZero ? (h ? 0 : 255) : (h ? 255 : 0));
            }
            return outBuf;
        }

        // 合成权重：涂过的地方 255，边缘做一次盒滤波当羽化，避免生硬的接缝
        static byte[] AlphaPlane(Bitmap mask, Rectangle crop, int feather)
        {
            int cw = crop.Width, ch = crop.Height;
            byte[] a = new byte[cw * ch];
            PixelBuffer buf = BitmapBridge.MaskToBuffer(mask);
            for (int y = 0; y < ch; y++)
            {
                int ro = (crop.Y + y) * buf.Stride;
                for (int x = 0; x < cw; x++)
                    a[y * cw + x] = (byte)(buf.Data[ro + crop.X + x] >= 128 ? 255 : 0);
            }
            if (feather <= 0) return a;

            // 先把"洞"外扩一圈再羽化：否则羽化权重在洞口 >0，会把污渍边缘按比例留下来，
            // 修完沿涂抹轮廓留一圈灰晕（实测肉眼可见）。
            a = DilateHole(a, cw, ch, feather);

            byte[] t = new byte[cw * ch];
            for (int y = 0; y < ch; y++)
                for (int x = 0; x < cw; x++)
                {
                    int sum = 0, cnt = 0;
                    for (int k = -feather; k <= feather; k++)
                    {
                        int xx = x + k;
                        if (xx < 0 || xx >= cw) continue;
                        sum += a[y * cw + xx]; cnt++;
                    }
                    t[y * cw + x] = (byte)(sum / cnt);
                }
            for (int y = 0; y < ch; y++)
                for (int x = 0; x < cw; x++)
                {
                    int sum = 0, cnt = 0;
                    for (int k = -feather; k <= feather; k++)
                    {
                        int yy = y + k;
                        if (yy < 0 || yy >= ch) continue;
                        sum += t[yy * cw + x]; cnt++;
                    }
                    a[y * cw + x] = (byte)(sum / cnt);
                }
            return a;
        }

        // 二值膨胀（分离式最大值滤波，半径 r）
        static byte[] DilateHole(byte[] a, int w, int h, int r)
        {
            byte[] t = new byte[a.Length], o = new byte[a.Length];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    byte v = 0;
                    for (int k = -r; k <= r; k++)
                    {
                        int xx = x + k;
                        if (xx < 0 || xx >= w) continue;
                        byte s = a[y * w + xx];
                        if (s > v) v = s;
                    }
                    t[y * w + x] = v;
                }
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    byte v = 0;
                    for (int k = -r; k <= r; k++)
                    {
                        int yy = y + k;
                        if (yy < 0 || yy >= h) continue;
                        byte s = t[yy * w + x];
                        if (s > v) v = s;
                    }
                    o[y * w + x] = v;
                }
            return o;
        }

        // 按权重把 patch 贴回原图；alpha=0 的像素一个字节都不动
        static void BlendPatch(Bitmap dst, Bitmap patch, Rectangle crop, byte[] alpha)
        {
            int cw = crop.Width, ch = crop.Height;
            PixelBuffer dbuf = BitmapBridge.ToBuffer(dst);
            PixelBuffer pbuf = BitmapBridge.ToBuffer(patch);
            for (int y = 0; y < ch; y++)
            {
                int dr = (crop.Y + y) * dbuf.Stride + crop.X * 3;
                int pr = y * pbuf.Stride;
                for (int x = 0; x < cw; x++)
                {
                    int al = alpha[y * cw + x];
                    if (al == 0) continue;
                    int o = x * 3;
                    if (al >= 255)
                    {
                        dbuf.Data[dr + o] = pbuf.Data[pr + o];
                        dbuf.Data[dr + o + 1] = pbuf.Data[pr + o + 1];
                        dbuf.Data[dr + o + 2] = pbuf.Data[pr + o + 2];
                    }
                    else
                    {
                        int ia = 255 - al;
                        dbuf.Data[dr + o] = (byte)((dbuf.Data[dr + o] * ia + pbuf.Data[pr + o] * al) / 255);
                        dbuf.Data[dr + o + 1] = (byte)((dbuf.Data[dr + o + 1] * ia + pbuf.Data[pr + o + 1] * al) / 255);
                        dbuf.Data[dr + o + 2] = (byte)((dbuf.Data[dr + o + 2] * ia + pbuf.Data[pr + o + 2] * al) / 255);
                    }
                }
            }
            BitmapBridge.WriteBack(dst, dbuf);
        }

        static Bitmap ResizeMask(Bitmap src, int w, int h)
        {
            Bitmap b = Imaging.MakeMask(w, h);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.DrawImage(src, new Rectangle(0, 0, w, h));
            }
            return b;
        }

#else
        // 未生成互操作层时的降级实现：只报告状态，不做任何事
        public static Bitmap Run(Bitmap src, Bitmap mask, ProgressHandler progress)
        {
            throw new Exception(StatusText);
        }
        public static Bitmap Run(Bitmap src, Bitmap mask, ProgressHandler progress, CancelCheck cancelled)
        {
            throw new Exception(StatusText);
        }
        public static string Check() { return "AI 修复自检\n" + StatusText + "\n环境: " + SysInfo.Describe(); }
        public static void Shutdown() { }
#endif
    }
}
