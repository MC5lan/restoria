// SysInfo.cs — 运行环境探测与资源上限自适应
//
// 同一份 exe 要同时服务两类机器：
//   * 强机（如 i5-7500 / 8GB / 64 位 Win7）：内存预算、并行度、各阈值都放开，跑得更快、支持更大图；
//   * 弱机（32 位 Win7 / 2GB）：保持保守策略，绝不因为"给强机优化"而变不稳。
// 因此所有上限都从这里按物理内存 + 进程位数 + 核心数推导，而不是硬编码。
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace PicTool
{
    static class SysInfo
    {
        [StructLayout(LayoutKind.Sequential)]
        struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        static readonly long ramBytes;
        static readonly bool is64;

        static SysInfo()
        {
            try
            {
                MEMORYSTATUSEX m = new MEMORYSTATUSEX();
                m.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                if (GlobalMemoryStatusEx(ref m) && m.ullTotalPhys > 0)
                    ramBytes = (long)m.ullTotalPhys;
            }
            catch { }
            if (ramBytes <= 0) ramBytes = 2L * 1024 * 1024 * 1024;   // 探测失败：按 2GB 保守处理
            is64 = IntPtr.Size == 8;
        }

        public static long RamBytes { get { return ramBytes; } }
        public static bool Is64BitProcess { get { return is64; } }
        public static int Cores { get { return Environment.ProcessorCount; } }

        // 工作线程数：内容感知填充与批量处理都用它。目标机 4 核 → 4；老双核 → 2。
        public static int WorkerThreads
        {
            get
            {
                int n = Cores;
                if (n > 8) n = 8;
                if (n < 1) n = 1;
                if (DocBudgetBytes < 400L * 1024 * 1024 && n > 2) n = 2;   // 内存紧张时别开太多并行
                return n;
            }
        }

        // 全分辨率位图的内存预算：
        //   64 位进程地址空间大 → 取物理内存 30%，上限 1.4GB；
        //   32 位进程地址空间只有 2GB → 取 15%，上限 500MB（给 GDI+ 和碎片留余地）。
        public static long DocBudgetBytes
        {
            get
            {
                long b;
                if (is64)
                {
                    b = ramBytes / 100 * 30;
                    if (b > 1400L * 1024 * 1024) b = 1400L * 1024 * 1024;
                    if (b < 256L * 1024 * 1024) b = 256L * 1024 * 1024;
                }
                else
                {
                    b = ramBytes / 100 * 15;
                    if (b > 500L * 1024 * 1024) b = 500L * 1024 * 1024;
                    if (b < 160L * 1024 * 1024) b = 160L * 1024 * 1024;
                }
                return b;
            }
        }

        // 单次最多载入的图片数：缩略图常驻内存，按预算折算（每张约 130KB 缩略图）
        public static int MaxFiles
        {
            get
            {
                long perFile = 240L * 170 * 3 + 4096;          // 缩略图 + 每条记录开销
                int byMem = (int)(DocBudgetBytes / 3 / perFile); // 缩略图最多占预算的 1/3
                if (byMem > 2000) byMem = 2000;
                if (byMem < 300) byMem = 300;
                return byMem;
            }
        }

        // 内容感知填充的 ROI 像素上限（内存和耗时都要顾：2 万像素级 ≈ 数秒，百万级 ≈ 分钟）
        public static long MaxRoiPixels
        {
            get
            {
                long byMem = DocBudgetBytes / 40;              // 填充过程约 40 字节/像素
                if (byMem > 20000000L) byMem = 20000000L;
                if (byMem < 4000000L) byMem = 4000000L;
                return byMem;
            }
        }

        // 超过这个面积就先弹确认（按机器快慢给不同阈值）
        public static int LargeAreaWarnPixels
        {
            get { return Cores >= 4 ? 600000 : 350000; }
        }

        // 实时预览用的最长边（强机放更细）
        public static int PreviewMaxSide
        {
            get { return (is64 && ramBytes >= 4L * 1024 * 1024 * 1024) ? 2200 : 1400; }
        }

        // .NET Framework 4.x 是否安装（读注册表，供判断能否走 .NET 4.x 路线）
        public static string DotNet4x
        {
            get
            {
                try
                {
                    Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                        @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full");
                    if (k == null) return "未安装";
                    object o = k.GetValue("Release");
                    k.Close();
                    if (o == null) return "已安装（版本号不可读）";
                    int r = Convert.ToInt32(o);
                    string v = "4.5 或更早";
                    if (r >= 533320) v = "4.8.1";
                    else if (r >= 528040) v = "4.8";
                    else if (r >= 461808) v = "4.7.2";
                    else if (r >= 461308) v = "4.7.1";
                    else if (r >= 460798) v = "4.7";
                    else if (r >= 394802) v = "4.6.2";
                    else if (r >= 394254) v = "4.6.1";
                    else if (r >= 393295) v = "4.6";
                    else if (r >= 379893) v = "4.5.2";
                    else if (r >= 378675) v = "4.5.1";
                    else if (r >= 378389) v = "4.5";
                    return v + "（Release " + r + "）";
                }
                catch { return "读取失败"; }
            }
        }

        // VC++ 运行库（ONNX Runtime 依赖）是否就位
        public static string VcRuntime
        {
            get
            {
                try
                {
                    string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
                    if (File.Exists(Path.Combine(sys, "msvcp140.dll")) && File.Exists(Path.Combine(sys, "vcruntime140.dll")))
                        return "已安装";
                    return "未找到（AI 模式需要）";
                }
                catch { return "读取失败"; }
            }
        }

        // 当前托管程序集的 CLR 版本（2.0.50727 = .NET 3.5 运行时；4.0.30319 = .NET 4.x 运行时）
        public static string ClrVersion
        {
            get
            {
                try { return Environment.Version.ToString(); }
                catch { return "?"; }
            }
        }

        // .NET Framework 4.x 的 Release 号（注册表）。528040 及以上 = 4.8 / 4.8.1。
        // 读不到返回 0（不算错误：可能确实没装 4.x，或注册表权限受限）。
        // 为什么值得读：本程序硬性需要 4.8，现场排查第一个要问的就是"目标机装没装"，
        // 而这个数字现在只能靠手敲 reg query 看 —— 不如让程序自己报出来。
        public static int Net4xRelease
        {
            get
            {
                try
                {
                    using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine
                        .OpenSubKey("SOFTWARE\\Microsoft\\NET Framework Setup\\NDP\\v4\\Full"))
                    {
                        if (k == null) return 0;
                        object o = k.GetValue("Release");
                        if (o == null) return 0;
                        return Convert.ToInt32(o);
                    }
                }
                catch { return 0; }
            }
        }

        public static string ClrName
        {
            get
            {
                string v = ClrVersion;
                if (v.StartsWith("2.0")) return ".NET 3.5 运行时（Win7 自带）—— 2.3.0 起本程序需要 4.8";
                if (v.StartsWith("4.0")) return ".NET 4.x 运行时，本程序要求 4.8 或更高";
                return "未知运行时";
            }
        }

        // ClrName 里**刻意不掺注册表** —— 因为它会进 --layout 快照，而注册表读取在不同
        // 启动方式/沙箱下可能失败，快照就会飘，回归检查会跟着假报警（实为此坑踩过一次）。
        // 需要具体 Release 号的地方（自检报告、诊断）单独调这两个。
        public static string ClrDetail
        {
            get
            {
                int rel = Net4xRelease;
                if (rel <= 0) return ClrName + "（Release 读不到）";
                return ClrName + "（Release " + rel + (rel >= 528040 ? "，已达 4.8" : "，低于 4.8！") + "）";
            }
        }

        // 写进诊断信息，便于远程排查
        // ==================== 真实系统版本 ====================
        //
        // 为什么不能用 Environment.OSVersion：**没有应用程序清单的 .NET 程序**在 Win8.1+
        // 上会被系统"虚拟化"，一律报 6.2.9200（于是 Win11 显示成 Windows 8）—— 安装器
        // （tools\install\SetupGui.cs）早就踩过这个坑，这里用同一套办法：
        // 直接读 ntdll!RtlGetVersion，它从 Win2000 起就有、且从不做虚拟化。
        //
        // 用途：**决定加载哪一套 ONNX Runtime**（见 AiFill.CandidateDlls）。
        // Win7 必须走 ai\win7\ 的垫片版；Win8+ 走原版 —— 这个判断不能出错，
        // 所以宁可读内核版本号，也不用被虚拟化过的 API。
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct RTL_OSVERSIONINFOEX
        {
            public uint dwOSVersionInfoSize;
            public uint dwMajorVersion;
            public uint dwMinorVersion;
            public uint dwBuildNumber;
            public uint dwPlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szCSDVersion;
        }

        [DllImport("ntdll.dll", SetLastError = true)]
        static extern int RtlGetVersion(ref RTL_OSVERSIONINFOEX info);

        static int osMajor, osMinor, osBuild;
        static bool osKnown, osProbed;

        static void ProbeOs()
        {
            if (osProbed) return;
            osProbed = true;
            try
            {
                RTL_OSVERSIONINFOEX v = new RTL_OSVERSIONINFOEX();
                v.dwOSVersionInfoSize = (uint)Marshal.SizeOf(typeof(RTL_OSVERSIONINFOEX));
                if (RtlGetVersion(ref v) == 0 && v.dwMajorVersion > 0)
                {
                    osMajor = (int)v.dwMajorVersion;
                    osMinor = (int)v.dwMinorVersion;
                    osBuild = (int)v.dwBuildNumber;
                    osKnown = true;
                    return;
                }
            }
            catch { }
            try   // 兜底：托管 API 给的版本（Win8+ 上可能被虚拟化成 6.2，但至少不是 0）
            {
                Version e = Environment.OSVersion.Version;
                osMajor = e.Major; osMinor = e.Minor; osBuild = e.Build;
                osKnown = osMajor > 0;
            }
            catch { osKnown = false; }
        }

        public static bool OsKnown { get { ProbeOs(); return osKnown; } }
        public static int OsMajor { get { ProbeOs(); return osMajor; } }
        public static int OsMinor { get { ProbeOs(); return osMinor; } }
        public static int OsBuild { get { ProbeOs(); return osBuild; } }

        // Windows 7 / 2008 R2 及更老（6.1 及以下）。**Win8 是 6.2** —— 分界线就在 6.2。
        public static bool IsWindows7OrOlder
        {
            get { ProbeOs(); return osKnown && (osMajor < 6 || (osMajor == 6 && osMinor <= 1)); }
        }

        public static string OsName
        {
            get
            {
                ProbeOs();
                if (!osKnown) return "未知系统";
                if (osMajor == 6 && osMinor == 0) return "Windows Vista";
                if (osMajor == 6 && osMinor == 1) return "Windows 7";
                if (osMajor == 6 && osMinor == 2) return "Windows 8";
                if (osMajor == 6 && osMinor == 3) return "Windows 8.1";
                if (osMajor == 10 && osBuild >= 22000) return "Windows 11";
                if (osMajor == 10) return "Windows 10";
                return "Windows " + osMajor + "." + osMinor;
            }
        }

        public static string OsDescribe()
        {
            ProbeOs();
            return osKnown ? (OsName + " (" + osMajor + "." + osMinor + "." + osBuild + ")") : "未知系统";
        }

        public static string Describe()
        {
            // ⚠️ 这行会进自检报告 → 布局快照比对。**不能带环境相关信息**
            //    （系统名/版本号每台机器都不同，会造成快照比对假报警）。
            //    要看系统版本用 OsDescribe()（AI 自检报告里有独立的"系统判定"行）。
            return "RAM " + (ramBytes / 1024 / 1024) + "MB, " + (is64 ? "64 位进程" : "32 位进程") +
                   ", " + Cores + " 核, 并行 " + WorkerThreads +
                   ", 图片内存预算 " + (DocBudgetBytes / 1024 / 1024) + "MB" +
                   ", 最多 " + MaxFiles + " 张" +
                   ", CLR " + ClrVersion + "（" + ClrName + "）";
        }
    }

    public delegate void RangeDelegate(int from, int to);

    // 轻量并行助手：把 [0,count) 按线程数切块并行执行（.NET 2.0 没有 Task/Parallel）
    static class ParallelHelper
    {
        sealed class Box { public Exception Error; }
        sealed class Job
        {
            public RangeDelegate Body; public int From, To; public Box Err;
            public void Run() { try { Body(From, To); } catch (Exception ex) { lock (Err) { if (Err.Error == null) Err.Error = ex; } } }
        }

        public static void Run(int count, RangeDelegate body)
        {
            Run(count, 0, body);
        }

        // minCount：任务太小就别开线程（开线程本身有开销，小图反而变慢）
        public static void Run(int count, int minCount, RangeDelegate body)
        {
            if (count <= 0) return;
            int threads = SysInfo.WorkerThreads;
            if (threads <= 1 || count < minCount)
            {
                body(0, count);
                return;
            }
            int chunk = (count + threads - 1) / threads;
            int n = 0;
            System.Threading.Thread[] ts = new System.Threading.Thread[threads];
            Box box = new Box();
            for (int i = 0; i < threads; i++)
            {
                int from = i * chunk;
                int to = from + chunk;
                if (from >= count) break;
                if (to > count) to = count;
                Job job = new Job();
                job.Body = body; job.From = from; job.To = to; job.Err = box;
                System.Threading.Thread t = new System.Threading.Thread(new System.Threading.ThreadStart(job.Run));
                t.IsBackground = true;
                ts[n++] = t;
                t.Start();
            }
            for (int i = 0; i < n; i++) ts[i].Join();
            if (box.Error != null) throw box.Error;      // 子线程的异常要带回主线程
        }
    }

    // 统一日志：任何模块都能写，%TEMP%\PicTool-error.log。
    //
    // 分级（级别由环境变量 PICTOOL_LOG 控制：0=只记错误 1=默认 2=连关键路径节点也记）：
    //   Error —— 异常，含完整堆栈。排查"只弹一句没用的话"全靠它。
    //   Warn  —— 出了问题但程序自己扛过去了（写恢复记录失败、设备参数被拒等）。
    //   Info  —— 关键路径节点（启动、扫描开始/结束、AI 探测结论），默认不记，排查时打开。
    //
    // 滚动：超过 1MB 改名 .old（只保留上一份），不无限增长。
    public static class Diag
    {
        public enum Level { Error = 0, Warn = 1, Info = 2 }

        static int minLevel = -1;                       // -1 = 还没读过环境变量
        static int MinLevel
        {
            get
            {
                if (minLevel < 0)
                {
                    int v = 1;                          // 默认 Warn 及以上
                    try
                    {
                        string s = Environment.GetEnvironmentVariable("PICTOOL_LOG");
                        if (s != null && s.Length > 0) v = int.Parse(s.Trim());
                    }
                    catch { }
                    if (v < 0) v = 0;
                    if (v > 2) v = 2;
                    minLevel = v;
                }
                return minLevel;
            }
        }

        public static string LogPath()
        {
            return Path.Combine(Path.GetTempPath(), "PicTool-error.log");
        }

        // 兼容旧调用：等价 Error 级
        public static void Log(Exception ex) { Log(ex, null); }

        public static void Log(Exception ex, string what)
        {
            if (ex == null) return;
            Write(Level.Error, what, ex.ToString());
        }

        public static void Warn(string what, string detail)
        {
            Write(Level.Warn, what, detail);
        }

        public static void Info(string what, string detail)
        {
            Write(Level.Info, what, detail);
        }

        // ★ 归档多页并行后 Diag 会从多个线程同时写 ——
        //   AppendAllText 并发打开同一文件会 IOException（外层 catch 吞掉 = 丢日志行）。
        //   补一把锁，日志完整比吞吐重要；写日志本来就不是热点路径。
        static readonly object logGate = new object();

        static void Write(Level lv, string what, string detail)
        {
            if ((int)lv < MinLevel) return;
            try
            {
                lock (logGate)
                {
                string path = LogPath();
                RollIfNeeded(path);
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.Append(" [").Append(lv.ToString().ToUpperInvariant()).Append(']');
                if (what != null && what.Length > 0) sb.Append("  ").Append(what);
                if (detail != null && detail.Length > 0) sb.Append("  ").Append(detail.Replace("\r\n", " | ").Replace("\n", " | "));
                sb.Append("\r\n");
                if (lv == Level.Error)
                {
                    sb.Append("  环境: ").Append(SysInfo.Describe()).Append("\r\n");
                    if (detail != null && detail.Length > 0 && detail.IndexOf("   at ") >= 0)
                        sb.Append(detail).Append("\r\n");     // 带堆栈的整段保留
                }
                File.AppendAllText(path, sb.ToString(), System.Text.Encoding.UTF8);
                }
            }
            catch { }
        }

        // 超过 1MB → 改名 .old（覆盖上一份）。不弹任何提示。
        static void RollIfNeeded(string path)
        {
            try
            {
                FileInfo fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 1024 * 1024)
                {
                    string old = Path.ChangeExtension(path, ".old");
                    try { if (File.Exists(old)) File.Delete(old); } catch { }
                    File.Move(path, old);
                }
            }
            catch { }
        }
    }
}
