// Scanner.cs — 扫描仪接入（WIA，COM 晚绑定 + 反射，无需 interop DLL、无第三方依赖）
//
// 为什么选 WIA：Windows Vista/7/8/10/11 系统自带（wiaaut.dll），绝大多数扫描仪在 Win7 以后
// 都会提供 WIA 驱动；晚绑定通过 IDispatch 调用，编译期不需要引用任何 COM 库，
// 保持"单文件、零依赖、断网可用"的发布形态。
//
// 平板/单页设备扫一张即停；支持 ADF 的设备会自动连续进纸，直到纸尽或用户取消。
// 错误统一翻译成人话（卡纸/纸尽/离线/盖板未合……），详情写日志。
//
// ★ 重写要点：**64 位进程看不见 32 位扫描仪驱动**
//   程序按 AnyCPU 编译，在 64 位 Windows 上跑成 64 位进程。而大量老扫描仪（尤其 Win7 时代的
//   国产/日系机型）只装了 32 位的 WIA 驱动 —— 这种情况下 WIA 能枚举到 0 个设备，界面上就是
//   「没有找到扫描仪」，跟驱动没装的表现一模一样，用户完全无从判断。
//   对策：64 位下枚举为空时，自动造一个"32 位的自己"（复制到 %TEMP% 并把 CLR 头的
//   32BITREQUIRED 置位，等价于 CorFlags /32BIT+），用 `--scan-list` / `--scan-run` 让它去
//   枚举和扫描。这样既保住"单文件、可拷 U 盘"的形态，又能看见 32 位驱动。
//   另一条对策是诊断：枚举失败的原因绝不允许再被 catch{} 吞掉（老版的病根），
//   Diagnose() 会把"WIA 组件 / WIA 服务 / WIA 设备 / 系统成像设备 / TWAIN 数据源 /
//   32 位视角"六项都查一遍并给出结论。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace PicTool
{
    public class ScanDeviceInfo
    {
        public string Id;
        public string Name;
        public bool Via32Bit;           // 是否经由 32 位辅助进程看到的
        public override string ToString() { return Name; }
    }

    public class ScanOptions
    {
        public string DeviceId;
        public int Dpi = 300;
        public int ColorMode = 0;       // 0=彩色 1=灰度 2=黑白
        public string OutFolder;
        public string Prefix = "扫描";  // 输出文件名 = 前缀 + 四位序号
        public int MaxPages = 200;      // 安全上限，防止平板设备误开连扫
        // ★ 输出格式归一：默认跟随扫描仪（null 不转），
        //   用户主动选 JPG/PNG/TIF 才转存。WIA 落盘格式由驱动决定（不少驱动给 BMP/TIF），
        //   传输侧强改不可靠（ImageFile.FormatID 只读），用 GDI+ 后置转码：稳定、失败可降级。
        public string ForceExt = null;
    }

    public delegate void ScanProgress(int pagesDone, string note);
    public delegate bool ScanCancelled();

    public static class Scanner
    {
        const string JPEG_FMT = "{B96B3CAE-0728-11D3-9D7B-0000F81EF32E}";   // WIA 内置 JPEG 格式
        // 成像设备类 GUID（设备管理器里"图像设备"这一类）
        const string IMAGE_CLASS = @"{6bdd1fc6-810f-11d0-bec7-08002be2092f}";

        // ---------- 状态 ----------

        static bool use32;                       // 已确认要走 32 位辅助进程
        static string helper32;                  // 已就绪的 32 位副本路径
        static string lastEnumError;             // 上一次枚举的失败原因（不再吞掉）
        static string lastDiag;                  // 上一次诊断报告
        static int lastRawCount = -1;            // WIA 原始设备数（未按类型过滤），-1=没查
        static string lastHelperBits;            // 32 位副本实际跑出来的位数（"32"/"64"）

        public static bool Using32BitHelper { get { return use32; } }
        public static string LastEnumError { get { return lastEnumError; } }
        public static string LastDiagnosis { get { return lastDiag; } }
        public static bool Is64BitProcess { get { return IntPtr.Size == 8; } }
        public static bool Is64BitOS
        {
            get
            {
                try { return IntPtr.Size == 8 || Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432") != null; }
                catch { return IntPtr.Size == 8; }
            }
        }

        // 刷新设备列表前调一下：清掉上一次的 32 位判定，重新判断
        public static void ResetProbe() { use32 = false; lastEnumError = null; }

        // ---------- 设备枚举 ----------

        public static bool Available
        {
            get { return Type.GetTypeFromProgID("WIA.DeviceManager") != null; }
        }

        // 枚举设备。64 位下枚举为空时自动改用 32 位辅助进程再试一次。
        // allow32Bit=false 时只查本进程：界面首次自动加载用它，避免每次启动都去拉子进程（约 1~3 秒）
        public static List<ScanDeviceInfo> ListDevices() { return ListDevices(true); }

        public static List<ScanDeviceInfo> ListDevices(bool allow32Bit)
        {
            string err;
            List<ScanDeviceInfo> list = ListDevicesRaw(out err);
            lastEnumError = err;
            use32 = false;
            if (list.Count == 0 && allow32Bit && Is64BitProcess && Is64BitOS)
            {
                string err32;
                List<ScanDeviceInfo> l32 = ListDevicesVia32(out err32);
                if (l32.Count > 0)
                {
                    use32 = true;
                    return l32;
                }
                if (lastEnumError == null && err32 != null) lastEnumError = "32 位视角：" + err32;
            }
            return list;
        }

        // 本进程内枚举（不吞异常，原因通过 err 返回；并记录"卡在哪一步"）
        static List<ScanDeviceInfo> ListDevicesRaw(out string err)
        {
            err = null;
            List<ScanDeviceInfo> list = new List<ScanDeviceInfo>();
            Type t = Type.GetTypeFromProgID("WIA.DeviceManager");
            if (t == null) { err = "WIA 组件（wiaaut.dll）未注册"; return list; }
            object mgr = null;
            object infos = null;
            string step = "创建 WIA 管理器";
            try
            {
                mgr = Activator.CreateInstance(t);
                step = "读取设备集合（DeviceInfos）";
                infos = Invoke(mgr, "DeviceInfos");
                step = "读取设备数量";
                int n = (int)Invoke(infos, "Count");
                lastRawCount = n;
                for (int i = 1; i <= n; i++)
                {
                    step = "读取第 " + i + " 个设备";
                    object dev = Invoke(infos, "Item", i);
                    try
                    {
                        int type = 0;
                        try { type = (int)Invoke(dev, "Type"); } catch { }
                        if (type != 0 && type != 1) continue;   // 只要扫描仪（1）；0=未指定也收下
                        ScanDeviceInfo d = new ScanDeviceInfo();
                        try { d.Id = (string)Invoke(dev, "DeviceID"); } catch { d.Id = i.ToString(); }
                        // 设备名各驱动给得不一样：对象属性、属性集合、Desc 三路都试，
                        // 都不行才落到通用名。（实测 S2110 上对象自身的 Name 取不到，
                        // 但 Properties["Name"] 有 —— 这就是之前显示"扫描设备 1"的原因）
                        d.Name = TryStr(dev, "Name");
                        if (d.Name == null || d.Name.Length == 0) d.Name = TryPropStr(dev, "Name");
                        if (d.Name == null || d.Name.Length == 0) d.Name = TryPropStr(dev, "Description");
                        if (d.Name == null || d.Name.Length == 0) d.Name = TryStr(dev, "Desc");
                        if (d.Name == null || d.Name.Trim().Length == 0) d.Name = "扫描设备 " + i;
                        d.Name = d.Name.Trim();
                        list.Add(d);
                    }
                    finally { try { Marshal.ReleaseComObject(dev); } catch { } }
                }
            }
            catch (Exception ex) { err = "在「" + step + "」时失败：" + Describe(ex); }
            finally
            {
                try { if (infos != null) Marshal.ReleaseComObject(infos); } catch { }
                try { if (mgr != null) Marshal.ReleaseComObject(mgr); } catch { }
            }
            return list;
        }

        // ---------- 扫描 ----------

        // 必须在 STA 线程调用（MainForm 已保证）。progress/cancelled 可为 null。
        // 返回保存好的文件完整路径列表。
        public static List<string> Scan(ScanOptions opt, ScanProgress progress, ScanCancelled cancelled)
        {
            List<string> files = ScanCore(opt, progress, cancelled);
            return NormalizeFormats(files, opt == null ? null : opt.ForceExt, progress);
        }

        // 原 Scan 入口（本地/32 位双路径），改名给上面的格式归一包装让位
        static List<string> ScanCore(ScanOptions opt, ScanProgress progress, ScanCancelled cancelled)
        {
            if (opt == null || opt.OutFolder == null || opt.OutFolder.Length == 0)
                throw new Exception("没有设置扫描输出文件夹。");

            if (use32) return ScanVia32(opt, progress, cancelled);

            List<string> saved = new List<string>();
            Exception failure = null;
            try { ScanLocal(opt, saved, progress, cancelled); }
            catch (Exception ex) { failure = ex; }

            // 一张都没扫出来，而且本机是 64 位：可能是"设备在 32 位驱动上"，换 32 位再试一次。
            // 只在**一页都没成功**时才回退，避免把已经扫到的页重复扫一遍。
            if (failure != null && saved.Count == 0 && Is64BitProcess && Is64BitOS)
            {
                try
                {
                    use32 = true;
                    List<string> via32 = ScanVia32(opt, progress, cancelled);
                    if (via32.Count > 0) return via32;
                }
                catch (Exception ex32)
                {
                    throw new Exception(failure.Message + "\n\n（改用 32 位方式重试也失败：" + ex32.Message + "）", failure);
                }
                use32 = false;
            }
            if (failure != null && saved.Count == 0) throw failure;
            return saved;
        }

        // 扫描输出格式归一：
        // WIA 落盘的格式由驱动决定（不少驱动默认 BMP/TIF，一页几十 MB）；在传输侧强改不可靠
        // （ImageFile.FormatID 只读、各驱动表现不一），所以用 GDI+ 后置转码：稳定、失败可降级。
        // forceExt = null 表示"跟随扫描仪"不转。逐页回调进度；转码失败保留原格式文件。
        static List<string> NormalizeFormats(List<string> files, string forceExt, ScanProgress progress)
        {
            if (files == null || files.Count == 0 || forceExt == null || forceExt.Length == 0) return files;
            forceExt = forceExt.ToLowerInvariant();
            List<string> result = new List<string>(files.Count);
            int converted = 0;
            for (int i = 0; i < files.Count; i++)
            {
                string f = files[i];
                string cur = Path.GetExtension(f).ToLowerInvariant();
                if (cur == forceExt) { result.Add(f); continue; }
                string target = Path.ChangeExtension(f, forceExt);
                try
                {
                    using (Bitmap bmp = new Bitmap(f))
                    {
                        if (forceExt == ".jpg")
                        {
                            System.Drawing.Imaging.ImageCodecInfo jpeg = null;
                            foreach (System.Drawing.Imaging.ImageCodecInfo c in System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders())
                                if (c.MimeType == "image/jpeg") { jpeg = c; break; }
                            if (jpeg != null)
                            {
                                System.Drawing.Imaging.EncoderParameters ep = new System.Drawing.Imaging.EncoderParameters(1);
                                ep.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
                                bmp.Save(target, jpeg, ep);
                            }
                            else bmp.Save(target, System.Drawing.Imaging.ImageFormat.Jpeg);
                        }
                        else if (forceExt == ".png") bmp.Save(target, System.Drawing.Imaging.ImageFormat.Png);
                        else if (forceExt == ".tif") bmp.Save(target, System.Drawing.Imaging.ImageFormat.Tiff);
                        else { result.Add(f); continue; }
                    }
                    if (File.Exists(target))
                    {
                        try { File.Delete(f); } catch { Diag.Info("扫描", "原格式文件删除失败（被占用？），两份并存：" + f); }
                        result.Add(target);
                        converted++;
                    }
                    else result.Add(f);
                }
                catch
                {
                    // 转码失败：清掉半截目标文件（如有），保留原格式文件
                    try { if (File.Exists(target)) File.Delete(target); } catch { }
                    result.Add(f);
                }
                if (progress != null) progress(files.Count, "转存 " + forceExt.ToUpperInvariant().TrimStart('.') + "：" + (i + 1) + "/" + files.Count);
            }
            if (converted > 0) Diag.Info("扫描", "格式归一：" + converted + "/" + files.Count + " 页转存为 " + forceExt);
            return result;
        }

        // 本进程内扫描；已扫到的页追加进 saved（回退判断需要知道"扫到几张"）
        static void ScanLocal(ScanOptions opt, List<string> saved, ScanProgress progress, ScanCancelled cancelled)
        {
            Diag.Info("扫描", "开始：设备=" + (opt.DeviceId == null ? "(默认)" : opt.DeviceId)
                + "  " + opt.Dpi + "dpi  色彩=" + opt.ColorMode);
            object mgr = null, dev = null, item = null;
            object infos = null, info = null, items = null;   // 这三个 COM 对象需显式释放（漏释放会泄漏 COM 句柄）
            try
            {
                try
                {
                    mgr = Activator.CreateInstance(Type.GetTypeFromProgID("WIA.DeviceManager"));
                    infos = Invoke(mgr, "DeviceInfos");
                    object pick = (opt.DeviceId == null || opt.DeviceId.Length == 0) ? (object)1 : (object)opt.DeviceId;
                    info = Invoke(infos, "Item", pick);
                    dev = Invoke(info, "Connect");

                    items = Invoke(dev, "Items");
                    item = Invoke(items, "Item", 1);    // 扫描仪默认数据源
                }
                catch (Exception ex)
                {
                    // 连接阶段失败必须说清是哪一步、真实 HRESULT 是什么，
                    // 否则只会看到反射壳的"调用的目标发生了异常"
                    throw new Exception("连接扫描仪失败：" + Describe(ex), ex);
                }

                // 常用参数：能设就设，设备不支持就让它用自己的默认值
                SetItemProp(item, "Horizontal Resolution", opt.Dpi);
                SetItemProp(item, "Vertical Resolution", opt.Dpi);
                SetItemProp(item, "Data Type", opt.ColorMode == 0 ? 3 : (opt.ColorMode == 1 ? 2 : 4));

                // ★ 扫描范围必须跟分辨率一起重算 ★
                // 设备给的默认范围是一组固定像素数（这台 S2110 是 780×1133，在 200dpi 下 = 3.9"×5.7"）。
                // 只把 DPI 改成 300 而不动范围，实际扫到的就只有页面左上角一小块 —— 表面上"扫描成功"，
                // 拿到的却是废图。正确做法：范围 = 台面尺寸(千分之一英寸) × DPI / 1000，再夹进设备允许的区间。
                int bedW = TryIntProp(dev, "Horizontal Bed Size");
                int bedH = TryIntProp(dev, "Vertical Bed Size");
                if (bedW > 0) SetExtent(item, "Horizontal Extent", bedW * opt.Dpi / 1000);
                if (bedH > 0) SetExtent(item, "Vertical Extent", bedH * opt.Dpi / 1000);

                // 进纸器模式：设置成功说明设备支持 ADF，之后连扫到纸尽；失败则按平板单页处理
                bool feeder = TrySetItemProp(item, "Document Handling Select", 2);

                while (true)
                {
                    if (cancelled != null && cancelled()) break;
                    if (saved.Count >= opt.MaxPages)
                    {
                        if (progress != null) progress(saved.Count, "已达单次上限 " + opt.MaxPages + " 页，自动停止");
                        break;
                    }

                    object img;
                    try
                    {
                        img = Invoke(item, "Transfer", JPEG_FMT);
                    }
                    catch (COMException ex)
                    {
                        if (IsPaperEmpty(ex.ErrorCode) && saved.Count > 0) break;   // 纸扫完了，正常结束
                        try { img = Invoke(item, "Transfer"); }   // 有的驱动不认 JPEG，退回设备默认格式
                        catch (COMException ex2)
                        {
                            if (IsPaperEmpty(ex2.ErrorCode) && saved.Count > 0) break;
                            throw Translate(ex2, saved.Count == 0);
                        }
                    }

                    // 扩展名必须跟实际拿到的格式一致：退回默认格式的设备（比如这台偏好 BMP）
                    // 要是仍按 .jpg 命名，就是内容与扩展名不符，下游容易出怪问题
                    string path = UniquePath(opt.OutFolder, opt.Prefix, saved.Count + 1, ExtFor(img));

                    try { Invoke(img, "SaveFile", path); }
                    finally { try { Marshal.ReleaseComObject(img); } catch { } }

                    saved.Add(path);
                    if (progress != null) progress(saved.Count, "已扫描 " + saved.Count + " 页");
                    Diag.Info("扫描", "第 " + saved.Count + " 页已保存：" + path);
                    if (!feeder) break;   // 平板设备：一张即止
                }
                Diag.Info("扫描", "结束：共 " + saved.Count + " 页");
            }
            finally
            {
                try { if (item != null) Marshal.ReleaseComObject(item); } catch { }
                try { if (items != null) Marshal.ReleaseComObject(items); } catch { }
                try { if (dev != null) Marshal.ReleaseComObject(dev); } catch { }
                try { if (info != null) Marshal.ReleaseComObject(info); } catch { }
                try { if (infos != null) Marshal.ReleaseComObject(infos); } catch { }
                try { if (mgr != null) Marshal.ReleaseComObject(mgr); } catch { }
            }
        }

        // ---------- 32 位辅助进程 ----------

        // 把自身 exe 复制到 %TEMP%，置上 CLR 头的 32BITREQUIRED 位（等价 CorFlags /32BIT+），
        // 让它在 64 位系统上以 32 位进程启动。已存在就直接复用。
        public static string EnsureHelper32()
        {
            if (helper32 != null && File.Exists(helper32)) return helper32;
            string self = null;
            try
            {
                System.Reflection.Assembly asm = System.Reflection.Assembly.GetEntryAssembly();
                if (asm != null) self = asm.Location;
            }
            catch { }
            if (self == null || self.Length == 0 || !File.Exists(self))
            {
                try { self = Process.GetCurrentProcess().MainModule.FileName; } catch { }
            }
            if (self == null || !File.Exists(self)) throw new Exception("无法定位程序自身，不能启用 32 位兼容方式。");

            long stamp = 0;
            try { stamp = File.GetLastWriteTimeUtc(self).Ticks; } catch { }
            string dst = Path.Combine(Path.GetTempPath(), "PicTool-scan32-" + stamp.ToString("x") + ".exe");
            // 每次重编 exe 时间戳都会变，会在这里攒下旧的 32 位副本 —— 顺手清掉一天以前的
            try
            {
                string[] stale = Directory.GetFiles(Path.GetTempPath(), "PicTool-scan32-*.exe");
                for (int i = 0; i < stale.Length; i++)
                    if (stale[i] != dst && (DateTime.UtcNow - File.GetLastWriteTimeUtc(stale[i])).TotalDays > 1)
                        File.Delete(stale[i]);
            }
            catch { }

            if (!File.Exists(dst))
            {
                File.Copy(self, dst, true);
                try { File.Copy(self + ".config", dst + ".config", true); } catch { }
                PatchTo32Bit(dst);
            }
            helper32 = dst;
            return dst;
        }

        // 定位 PE 里 CLR 头 Flags 字段的文件偏移（COR20: cb(4)+主次版本(4)+元数据(8) → Flags）
        static int CorFlagsOffset(byte[] b)
        {
            int pe = BitConverter.ToInt32(b, 0x3C);
            if (pe <= 0 || pe + 24 >= b.Length || b[pe] != 'P' || b[pe + 1] != 'E') throw new Exception("不是有效的 PE 文件");
            int optHdr = pe + 24;
            int magic = BitConverter.ToUInt16(b, optHdr);
            int ddOff = optHdr + (magic == 0x20B ? 112 : 96);      // DataDirectory 起始
            int comRva = BitConverter.ToInt32(b, ddOff + 14 * 8);  // 第 14 项 = CLR 头
            if (comRva <= 0) throw new Exception("exe 里没有 CLR 头");
            int secOff = optHdr + BitConverter.ToUInt16(b, pe + 20);   // pe+20 = SizeOfOptionalHeader
            int nSec = BitConverter.ToUInt16(b, pe + 6);
            for (int i = 0; i < nSec; i++)
            {
                int s = secOff + i * 40;
                int va = BitConverter.ToInt32(b, s + 12);
                int vsz = BitConverter.ToInt32(b, s + 8);
                int rawSz = BitConverter.ToInt32(b, s + 16);
                int raw = BitConverter.ToInt32(b, s + 20);
                int span = Math.Max(vsz, rawSz);
                if (comRva >= va && comRva < va + span) return raw + (comRva - va) + 16;
            }
            throw new Exception("找不到 CLR 头所在区段");
        }

        // exe 是否已被标成"必须按 32 位运行"（自检用）
        public static bool IsMarked32Bit(string exePath)
        {
            byte[] b = File.ReadAllBytes(exePath);
            return (BitConverter.ToUInt32(b, CorFlagsOffset(b)) & 0x2) != 0;
        }

        // 确保 32 位副本就绪（自检用：返回路径并验证真的被标成 32 位）
        public static bool Verify32BitCopy(out string path)
        {
            path = EnsureHelper32();
            return IsMarked32Bit(path);
        }

        // 把 PE 里 CLR 头的 Flags 加上 COMIMAGE_FLAGS_32BITREQUIRED(0x2)，
        // 等价于 CorFlags /32BIT+：64 位系统上这会让它按 32 位进程启动
        static void PatchTo32Bit(string exePath)
        {
            byte[] b = File.ReadAllBytes(exePath);
            int off = CorFlagsOffset(b);
            uint flags = BitConverter.ToUInt32(b, off);
            if ((flags & 0x2) != 0) return;
            flags |= 0x2;
            Buffer.BlockCopy(BitConverter.GetBytes(flags), 0, b, off, 4);
            File.WriteAllBytes(exePath, b);
        }

        // 用 32 位副本枚举设备
        static List<ScanDeviceInfo> ListDevicesVia32(out string err)
        {
            err = null;
            List<ScanDeviceInfo> list = new List<ScanDeviceInfo>();
            string report = Path.Combine(Path.GetTempPath(), "PicTool-scan-list.txt");
            try
            {
                if (File.Exists(report)) File.Delete(report);
                string exe = EnsureHelper32();
                RunHelper(exe, "--scan-list \"" + report + "\"", 30000);
                if (!File.Exists(report)) { err = "32 位辅助进程没有写出结果（可能被系统拦住了）"; return list; }
                foreach (string line in File.ReadAllLines(report, Encoding.UTF8))
                {
                    if (line.StartsWith("DEV|"))
                    {
                        string[] p = line.Split('|');
                        if (p.Length >= 4)
                        {
                            ScanDeviceInfo d = new ScanDeviceInfo();
                            d.Name = p[2]; d.Id = p[3]; d.Via32Bit = true;
                            list.Add(d);
                        }
                    }
                    else if (line.StartsWith("ERR|")) err = line.Substring(4);
                    else if (line.StartsWith("BITS|")) lastHelperBits = line.Substring(5).Trim();
                }
            }
            catch (Exception ex) { err = Describe(ex); }
            return list;
        }

        // 用 32 位副本扫描：参数写文件交给子进程，进度/结果也从文件读回
        static List<string> ScanVia32(ScanOptions opt, ScanProgress progress, ScanCancelled cancelled)
        {
            string tmp = Path.GetTempPath();
            string args = Path.Combine(tmp, "PicTool-scan-args.txt");
            string report = Path.Combine(tmp, "PicTool-scan-report.txt");
            if (File.Exists(report)) File.Delete(report);
            StringBuilder a = new StringBuilder();
            a.AppendLine("out=" + opt.OutFolder);
            a.AppendLine("prefix=" + opt.Prefix);
            a.AppendLine("device=" + (opt.DeviceId == null ? "" : opt.DeviceId));
            a.AppendLine("dpi=" + opt.Dpi);
            a.AppendLine("color=" + opt.ColorMode);
            a.AppendLine("max=" + opt.MaxPages);
            a.AppendLine("report=" + report);
            File.WriteAllText(args, a.ToString(), Encoding.UTF8);

            string exe = EnsureHelper32();
            Process p = RunHelper(exe, "--scan-run \"" + args + "\"", 0);

            List<string> saved = new List<string>();
            int lastSeen = 0;
            Exception failure = null;
            while (true)
            {
                if (cancelled != null && cancelled())
                {
                    try { if (!p.HasExited) p.Kill(); } catch { }
                    break;
                }
                if (p.HasExited && !File.Exists(report)) break;
                if (File.Exists(report))
                {
                    string[] lines = ReadLinesShared(report);
                    foreach (string line in lines)
                    {
                        if (line.StartsWith("PAGE|"))
                        {
                            string[] q = line.Split('|');
                            int n = q.Length >= 2 ? ToInt(q[1], 0) : 0;
                            if (n > lastSeen)
                            {
                                lastSeen = n;
                                if (progress != null) progress(n, "已扫描 " + n + " 页");
                            }
                            if (q.Length >= 3 && q[2].Length > 0 && !saved.Contains(q[2])) saved.Add(q[2]);
                        }
                        else if (line.StartsWith("ERR|")) failure = new Exception(line.Substring(4));
                    }
                    if (lines.Length > 0 && lines[lines.Length - 1].StartsWith("DONE|")) break;
                }
                if (p.HasExited && File.Exists(report)) break;
                System.Threading.Thread.Sleep(250);
            }
            try { if (!p.HasExited) p.WaitForExit(5000); } catch { }
            try { File.Delete(args); } catch { }

            if (saved.Count == 0 && failure != null) throw failure;
            if (saved.Count == 0)
                throw new Exception("32 位方式也没能扫出内容。" + (lastEnumError == null ? "" : "（" + lastEnumError + "）"));
            return saved;
        }

        static string[] ReadLinesShared(string path)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader r = new StreamReader(fs, Encoding.UTF8))
                {
                    List<string> l = new List<string>();
                    string s;
                    while ((s = r.ReadLine()) != null) l.Add(s);
                    return l.ToArray();
                }
            }
            catch { return new string[0]; }
        }

        static int ToInt(string s, int d)
        {
            int v;
            return int.TryParse(s, out v) ? v : d;
        }

        static Process RunHelper(string exe, string args, int waitMs)
        {
            ProcessStartInfo si = new ProcessStartInfo(exe, args);
            si.UseShellExecute = false;
            si.CreateNoWindow = true;
            si.WorkingDirectory = Path.GetDirectoryName(exe);
            Process p = Process.Start(si);
            if (waitMs > 0) { try { p.WaitForExit(waitMs); } catch { } }
            return p;
        }

        // ---------- 辅助进程模式（由 Program 在命令行里分流过来） ----------

        // --scan-list <报告文件>：枚举设备并写报告
        public static string RunListMode(string reportPath)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("BITS|" + (Is64BitProcess ? "64" : "32"));
            string err;
            List<ScanDeviceInfo> list = ListDevicesRaw(out err);
            int i = 0;
            foreach (ScanDeviceInfo d in list)
            {
                i++;
                sb.AppendLine("DEV|" + i + "|" + d.Name + "|" + d.Id);
            }
            if (err != null) sb.AppendLine("ERR|" + err);
            try { File.WriteAllText(reportPath, sb.ToString(), Encoding.UTF8); } catch { }
            return sb.ToString();
        }

        // --scan-run <参数文件>：按参数文件扫描，进度/结果写报告文件
        public static string RunScanMode(string argsPath)
        {
            ScanOptions opt = new ScanOptions();
            string report = Path.Combine(Path.GetTempPath(), "PicTool-scan-report.txt");
            try
            {
                foreach (string line in File.ReadAllLines(argsPath, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq), v = line.Substring(eq + 1);
                    if (k == "out") opt.OutFolder = v;
                    else if (k == "prefix") opt.Prefix = v;
                    else if (k == "device") opt.DeviceId = v.Length == 0 ? null : v;
                    else if (k == "dpi") opt.Dpi = ToInt(v, 300);
                    else if (k == "color") opt.ColorMode = ToInt(v, 0);
                    else if (k == "max") opt.MaxPages = ToInt(v, 200);
                    else if (k == "report") report = v;
                }
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(report, "ERR|读参数文件失败：" + ex.Message + "\nDONE|\n", Encoding.UTF8); } catch { }
                return null;
            }

            StringBuilder sb = new StringBuilder();
            try
            {
                List<string> saved = new List<string>();
                ScanLocal(opt, saved, delegate(int n, string note)
                {
                    sb.AppendLine("PAGE|" + n + "|" + (saved.Count > 0 ? saved[saved.Count - 1] : ""));
                    Flush(report, sb);
                }, null);
                sb.AppendLine("DONE|" + saved.Count);
            }
            catch (Exception ex)
            {
                sb.AppendLine("ERR|" + Describe(ex));
                sb.AppendLine("DONE|-");
            }
            Flush(report, sb);
            return sb.ToString();
        }

        static void Flush(string report, StringBuilder sb)
        {
            try { File.WriteAllText(report, sb.ToString(), Encoding.UTF8); } catch { }
        }

        // ---------- 诊断 ----------

        // 把"为什么看不到扫描仪"查清楚。六项都查，最后给结论。
        public static string Diagnose()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("扫描仪诊断报告");
            sb.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine();
            sb.AppendLine("进程位数 : " + (Is64BitProcess ? "64 位" : "32 位")
                + "（程序按 AnyCPU 编译，64 位系统上跑成 64 位进程；只装了 32 位驱动的扫描仪会看不见）");
            sb.AppendLine("操作系统 : " + Environment.OSVersion.VersionString + (Is64BitOS ? "  64 位" : "  32 位"));
            sb.AppendLine("CLR 版本 : " + Environment.Version);
            sb.AppendLine();

            Type t = null;
            try { t = Type.GetTypeFromProgID("WIA.DeviceManager"); } catch { }
            sb.AppendLine("① WIA 组件 : " + (t == null ? "未注册 —— 系统缺少 wiaaut.dll（可能是精简版系统）" : "已注册"));
            sb.AppendLine("② WIA 服务 : " + ServiceState("stisvc"));
            if (t == null)
                sb.AppendLine("             （服务名 stisvc = Windows Image Acquisition；停用的话所有程序都看不到扫描仪）");

            string err;
            List<ScanDeviceInfo> list = ListDevicesRaw(out err);
            sb.AppendLine("③ WIA 设备 : " + list.Count + " 个（WIA 原始数 "
                + (lastRawCount < 0 ? "?" : lastRawCount.ToString()) + "，按类型过滤后剩下这些）");
            foreach (ScanDeviceInfo d in list) sb.AppendLine("             · " + d.Name + "    [" + d.Id + "]");
            if (err != null) sb.AppendLine("             枚举报错：" + err);

            sb.AppendLine("④ 系统成像设备（设备管理器的「图像设备」）：");
            string[] img = ImageClassDevices();
            if (img.Length == 0) sb.AppendLine("             无记录");
            else foreach (string s in img) sb.AppendLine("             · " + s);

            sb.AppendLine("⑤ TWAIN 数据源（只装了 TWAIN 驱动的机器在这里能看到）：");
            string[] tw = TwainSources();
            if (tw.Length == 0) sb.AppendLine("             无");
            else foreach (string s in tw) sb.AppendLine("             · " + s);

            sb.AppendLine("⑥ 32 位视角：");
            if (!Is64BitProcess)
                sb.AppendLine("             当前就是 32 位进程，无需另测");
            else
            {
                string err32;
                List<ScanDeviceInfo> l32 = ListDevicesVia32(out err32);
                sb.AppendLine("             32 位副本实际位数："
                    + (lastHelperBits == null ? "未知（没能跑起来）" : lastHelperBits + " 位"));
                sb.AppendLine("             32 位方式枚举到 " + l32.Count + " 个设备");
                foreach (ScanDeviceInfo d in l32) sb.AppendLine("             · " + d.Name + "    [" + d.Id + "]");
                if (err32 != null) sb.AppendLine("             报错：" + err32);
            }

            sb.AppendLine();
            sb.AppendLine("结论：");
            foreach (string s in Conclude(list.Count, err, img.Length, tw.Length)) sb.AppendLine("  " + s);

            lastDiag = sb.ToString();
            return lastDiag;
        }

        // 只给"看得到的原因"（界面提示用）
        public static string[] BriefDiagnosis()
        {
            try
            {
                string err;
                List<ScanDeviceInfo> list = ListDevicesRaw(out err);
                string[] img = ImageClassDevices();
                string[] tw = TwainSources();
                return Conclude(list.Count, err, img.Length, tw.Length);
            }
            catch (Exception ex) { return new string[] { "诊断出错：" + ex.Message }; }
        }

        static string[] Conclude(int wiaCount, string wiaErr, int imgCount, int twainCount)
        {
            List<string> c = new List<string>();
            if (wiaCount > 0)
            {
                c.Add("WIA 里能看到 " + wiaCount + " 个扫描设备，设备本身没问题。");
                return c.ToArray();
            }
            if (wiaErr != null && wiaErr.IndexOf("未注册") >= 0)
            {
                c.Add("系统里没有 WIA 组件（wiaaut.dll 未注册），本程序用不了扫描功能。");
                c.Add("这通常是精简版/优化过的系统。可以试着在别的电脑上拷一份 wiaaut.dll 并注册，或改用能扫的系统。");
                return c.ToArray();
            }
            if (wiaErr != null)
            {
                c.Add("WIA 枚举时报了错：" + wiaErr);
                c.Add("这往往不是驱动问题，而是 WIA 组件本身出错。先重启一次「Windows Image Acquisition」服务再试。");
            }
            if (wiaCount == 0 && lastRawCount > 0)
            {
                c.Add("WIA 报出 " + lastRawCount + " 个设备，但它们都不是扫描仪类型（WIA 把它们当成相机/摄像机），没有进列表。");
                c.Add("这类设备一般只能用厂商自带的软件扫描，与本程序的 WIA 接入方式不兼容。");
            }
            if (imgCount == 0 && twainCount == 0)
            {
                c.Add("系统里没有任何扫描设备的记录 —— 驱动没装，或者设备根本没被 Windows 认出来。");
                c.Add("先做一步：打开「控制面板 - 设备和打印机」，看那里有没有这台扫描仪；没有就是驱动/连接问题，跟本程序无关。");
            }
            else if (imgCount > 0)
            {
                c.Add("Windows 认识 " + imgCount + " 个成像设备，但 WIA 里看不到 —— 典型的「设备在、WIA 拿不到」。");
                c.Add("三个常见原因：① 该设备只有 TWAIN 驱动（本程序读的是 TWAIN 数据源数：" + twainCount + " 个）");
                c.Add("              ② 驱动是 32 位的，需要 32 位方式（本程序会自动重试，若仍不行说明是驱动本身的问题）");
                c.Add("              ③ WIA 服务被停用 —— 用 services.msc 把「Windows Image Acquisition」设为自动并启动");
                c.Add("最有用的一步验证：打开系统自带的「Windows 传真和扫描」，那里也看不到这台扫描仪，就与本程序无关。");
            }
            if (twainCount > 0 && imgCount == 0)
            {
                c.Add("检测到 " + twainCount + " 个 TWAIN 数据源，但没有成像设备记录。");
                c.Add("说明这台扫描仪只装了 TWAIN 驱动。本程序走 WIA，需要厂商的 WIA 驱动（官网驱动包里通常同时含 WIA 和 TWAIN）。");
            }
            return c.ToArray();
        }

        static string ServiceState(string name)
        {
            try
            {
                ProcessStartInfo si = new ProcessStartInfo("sc.exe", "query " + name);
                si.UseShellExecute = false;
                si.CreateNoWindow = true;
                si.RedirectStandardOutput = true;
                using (Process p = Process.Start(si))
                {
                    string outp = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(4000);
                    if (outp.IndexOf("RUNNING") >= 0) return "已启动";
                    if (outp.IndexOf("STOPPED") >= 0) return "已停止 —— 需要启动它（services.msc → Windows Image Acquisition）";
                    if (outp.IndexOf("1060") >= 0 || outp.IndexOf("FAILED") >= 0) return "不存在（系统缺少 WIA 服务）";
                    foreach (string l in outp.Split('\n'))
                        if (l.IndexOf("STATE") >= 0) return l.Trim();
                    return "状态未知";
                }
            }
            catch (Exception ex) { return "查询失败：" + ex.Message; }
        }

        static string[] ImageClassDevices()
        {
            List<string> list = new List<string>();
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Class\" + IMAGE_CLASS))
                {
                    if (k == null) return list.ToArray();
                    foreach (string sub in k.GetSubKeyNames())
                    {
                        if (sub.Length != 4) continue;                 // 只认 0000/0001 这种实例键
                        try
                        {
                            using (RegistryKey ik = k.OpenSubKey(sub))
                            {
                                if (ik == null) continue;
                                string desc = ik.GetValue("DriverDesc") as string;
                                string id = ik.GetValue("MatchingDeviceId") as string;
                                if (desc == null) desc = "(未命名)";
                                list.Add(desc + (id == null ? "" : "    " + id));
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return list.ToArray();
        }

        static string[] TwainSources()
        {
            List<string> list = new List<string>();
            string[] paths = {
                @"SOFTWARE\Twain\Data Sources",
                @"SOFTWARE\WOW6432Node\Twain\Data Sources",
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\StillImage\Data Sources"
            };
            foreach (string p in paths)
            {
                try
                {
                    using (RegistryKey k = Registry.LocalMachine.OpenSubKey(p))
                    {
                        if (k == null) continue;
                        foreach (string v in k.GetValueNames())
                            if (v.Length > 0 && !list.Contains(v)) list.Add(v);
                    }
                }
                catch { }
            }
            return list.ToArray();
        }

        // ---------- 工具 ----------

        // 安全读一个字符串属性；读不到返回 null（WIA 各版本/各驱动暴露的属性名不统一）
        static string TryStr(object obj, string name)
        {
            try
            {
                object v = Invoke(obj, name);
                return v == null ? null : v.ToString();
            }
            catch { return null; }
        }

        // --scan-props：把某台设备的所有属性倒出来（诊断"为什么名字/参数不对"用）
        public static string DumpDevice(int index)
        {
            StringBuilder sb = new StringBuilder();
            object mgr = null, dev = null, item = null;
            try
            {
                mgr = Activator.CreateInstance(Type.GetTypeFromProgID("WIA.DeviceManager"));
                object infos = Invoke(mgr, "DeviceInfos");
                int n = (int)Invoke(infos, "Count");
                sb.AppendLine("WIA 设备总数: " + n);
                if (index < 1 || index > n) { sb.AppendLine("序号超出范围"); return sb.ToString(); }
                object info = Invoke(infos, "Item", index);
                sb.AppendLine("设备 " + index + " 的属性：");
                DumpProps(sb, Invoke(info, "Properties"));
                try { dev = Invoke(info, "Connect"); }
                catch (Exception ex) { sb.AppendLine("连接失败：" + Describe(ex)); return sb.ToString(); }
                sb.AppendLine();
                sb.AppendLine("连接后的设备属性：");
                DumpProps(sb, Invoke(dev, "Properties"));
                object items = Invoke(dev, "Items");
                sb.AppendLine("Items.Count = " + Invoke(items, "Count"));
                item = Invoke(items, "Item", 1);
                sb.AppendLine();
                sb.AppendLine("数据源（Item 1）的属性：");
                DumpProps(sb, Invoke(item, "Properties"));
            }
            catch (Exception ex) { sb.AppendLine("失败：" + Describe(ex)); }
            finally
            {
                try { if (item != null) Marshal.ReleaseComObject(item); } catch { }
                try { if (dev != null) Marshal.ReleaseComObject(dev); } catch { }
                try { if (mgr != null) Marshal.ReleaseComObject(mgr); } catch { }
            }
            return sb.ToString();
        }

        static void DumpProps(StringBuilder sb, object props)
        {
            try
            {
                int n = (int)Invoke(props, "Count");
                for (int i = 1; i <= n; i++)
                {
                    object pr = null;
                    try
                    {
                        pr = Invoke(props, "Item", i);
                        string name = TryStr(pr, "Name");
                        if (name == null) name = "#" + i;
                        object val = null;
                        bool hasVal = true;
                        try { val = Invoke(pr, "Value"); } catch { hasVal = false; }
                        sb.Append("  ").Append(name).Append(" = ");
                        if (!hasVal) sb.AppendLine("(不可读)");
                        else if (val == null) sb.AppendLine("(空)");
                        else if (val is Array)
                        {
                            object[] a = (object[])val;
                            sb.Append('[');
                            for (int k = 0; k < a.Length && k < 8; k++) sb.Append(k == 0 ? "" : ", ").Append(a[k]);
                            sb.AppendLine(a.Length > 8 ? ", ...]" : "]");
                        }
                        else sb.AppendLine(val.ToString());
                    }
                    catch (Exception ex) { sb.AppendLine("  属性 " + i + " 读取失败：" + ex.Message); }
                    finally { try { if (pr != null) Marshal.ReleaseComObject(pr); } catch { } }
                }
            }
            catch (Exception ex) { sb.AppendLine("  属性集合读取失败：" + Describe(ex)); }
        }

        static string UniquePath(string folder, string prefix, int index, string ext)
        {
            if (ext == null || ext.Length == 0) ext = ".jpg";
            // 注：实际写文件的是 WIA 的 SaveFile（COM 侧），调用侧无法用"独占创建"防住
            // 检查-写入之间的竞态；这里只负责尽量避开已有文件，冲突窗口极小
            string p;
            do
            {
                p = Path.Combine(folder, prefix + "_" + index.ToString("0000") + ext);
                index++;
            } while (File.Exists(p));
            return p;
        }

        // 按传输回来的实际格式定扩展名（WIA 图像格式 GUID → 扩展名）
        static string ExtFor(object img)
        {
            string fmt = TryStr(img, "FormatID");
            if (fmt != null)
            {
                string f = fmt.Trim('{', '}');
                if (f == "B96B3CAE-0728-11D3-9D7B-0000F81EF32E") return ".jpg";   // JPEG
                if (f == "B96B3CAF-0728-11D3-9D7B-0000F81EF32E") return ".png";   // PNG
                if (f == "B96B3CAB-0728-11D3-9D7B-0000F81EF32E") return ".bmp";   // BMP
                if (f == "B96B3CB1-0728-11D3-9D7B-0000F81EF32E") return ".tif";   // TIFF
            }
            string e = TryStr(img, "FilenameExtension");
            if (e == null) e = TryStr(img, "Filename extension");
            if (e != null && e.Length > 0 && e.Length <= 5) return "." + e.ToLowerInvariant();
            return ".jpg";
        }

        // 读一个整型属性（读不到返回 0）。和名字一样：很多值只在 Properties 集合里，
        // 对象自身属性上没有 —— 这里两路都试。
        static int TryIntProp(object obj, string name)
        {
            try { return Convert.ToInt32(Invoke(obj, name)); }
            catch { }
            try
            {
                object props = Invoke(obj, "Properties");
                object pr = Invoke(props, "Item", name);
                return Convert.ToInt32(Invoke(pr, "Value"));
            }
            catch { return 0; }
        }

        // 从对象的 Properties 集合里按名字读一个值（WIA 的很多信息只在集合里，对象属性上没有）
        static string TryPropStr(object obj, string name)
        {
            try
            {
                object props = Invoke(obj, "Properties");
                object pr = Invoke(props, "Item", name);
                object v = Invoke(pr, "Value");
                return v == null ? null : v.ToString();
            }
            catch { return null; }
        }

        // 把"想要的范围"夹进设备允许的区间再写（Property 有 Min/Max/Inc 时用得上）
        static void SetExtent(object item, string prop, int want)
        {
            try
            {
                object props = Invoke(item, "Properties");
                object p = Invoke(props, "Item", prop);
                int v = want;
                int max = TryIntProp(p, "Max");
                int min = TryIntProp(p, "Min");
                int inc = TryIntProp(p, "Inc");
                if (inc > 1 && max > min) v = min + (v - min) / inc * inc;
                if (max > 0 && v > max) v = max;
                if (min > 0 && v < min) v = min;
                if (v <= 0) return;
                Set(p, "Value", v);
            }
            catch { }       // 设备不认就不动它：宁可保持出厂范围，也不写一个非法值
        }

        static void SetItemProp(object item, string name, object value)
        {
            try
            {
                object props = Invoke(item, "Properties");
                object p = Invoke(props, "Item", name);
                Set(p, "Value", value);
            }
            catch { }
        }

        static bool TrySetItemProp(object item, string name, object value)
        {
            try
            {
                object props = Invoke(item, "Properties");
                object p = Invoke(props, "Item", name);
                Set(p, "Value", value);
                return true;
            }
            catch { return false; }
        }

        static bool IsPaperEmpty(int hresult)
        {
            return hresult == unchecked((int)0x80210003);   // WIA_ERROR_PAPER_EMPTY
        }

        // 把异常翻成人话（含 HRESULT），诊断报告和界面提示共用。
        // 必须把反射的壳剥掉：IDispatch 晚绑定抛的是 TargetInvocationException，
        // 真正的 COM 错误（HRESULT）藏在 InnerException 里 —— 只报外壳等于什么都没说，
        // 这正是"看不到扫描仪却不知道为什么"的根子。
        public static string Describe(Exception ex)
        {
            Exception root = ex;
            while (root.InnerException != null && root.InnerException != root) root = root.InnerException;
            COMException ce = root as COMException;
            StringBuilder sb = new StringBuilder();
            if (ce != null)
            {
                string known = WiaText(ce.ErrorCode);
                sb.Append(known == null ? "设备/系统返回错误" : known);
                sb.Append("（0x").Append((ce.ErrorCode & 0xFFFFFFFF).ToString("X8")).Append("）");
                if (!string.IsNullOrEmpty(ce.Message)) sb.Append("  ").Append(ce.Message.Trim());
            }
            else
            {
                sb.Append(root.GetType().Name).Append(": ").Append(root.Message);
            }
            if (!object.ReferenceEquals(root, ex) && !string.IsNullOrEmpty(ex.Message))
                sb.Append("  [").Append(ex.GetType().Name).Append("]");
            return sb.ToString();
        }

        // WIA/COM 错误码 → 人话。查不到就返回 null，由调用方把原始码原样带出去 ——
        // 宁可显示"0x80210067（未知）"也不要编一个可能错的解释。表里这些码来自 WIA 的
        // 标准错误列表（wia.h）；设备驱动还可能返回厂商自定义码，那种只能靠原码去查。
        static string WiaText(int hr)
        {
            switch (hr)
            {
                // --- 通用 COM ---
                case unchecked((int)0x80004005): return "未指定的错误 E_FAIL（WIA 服务或驱动内部失败）";
                case unchecked((int)0x80040154): return "组件未注册（wiaaut.dll 或驱动没装好）";
                case unchecked((int)0x80070005): return "拒绝访问（被安全软件/权限拦住，或服务账户不对）";
                case unchecked((int)0x8007000E): return "内存不足";
                case unchecked((int)0x8007007E): return "找不到模块（WIA 组件或驱动文件缺失）";
                case unchecked((int)0x80010105): return "服务端抛出异常（驱动崩溃了，重启 WIA 服务再试）";
                case unchecked((int)0x80020003): return "成员不存在（WIA 接口版本与预期不符）";
                // --- WIA 标准错误 ---
                case unchecked((int)0x80210001): return "设备通用错误";
                case unchecked((int)0x80210002): return "进纸器卡纸，请打开盖板取出卡住的纸";
                case unchecked((int)0x80210003): return "进纸器里没有纸";
                case unchecked((int)0x80210004): return "纸张有问题（尺寸不符或没放好），请重新放纸";
                case unchecked((int)0x80210005): return "扫描仪处于离线状态（确认电源和数据线）";
                case unchecked((int)0x80210006): return "扫描仪正忙（别的程序可能正在用）";
                case unchecked((int)0x80210007): return "扫描仪正在预热，等几秒再试";
                case unchecked((int)0x80210008): return "设备需要人工处理（装纸/合盖等）";
                case unchecked((int)0x80210009): return "该设备项已被删除，请刷新设备列表重试";
                case unchecked((int)0x8021000A): return "与设备通信失败";
                case unchecked((int)0x8021000B): return "设备不支持该命令";
                case unchecked((int)0x8021000C): return "驱动不兼容（装的驱动与这台设备/系统不匹配）";
                case unchecked((int)0x8021000D): return "盖板打开了，请合上";
                case unchecked((int)0x8021000E): return "扫描灯未就绪";
                case unchecked((int)0x8021000F): return "目标不可用（输出路径或图片格式不被设备接受）";
                case unchecked((int)0x80210010): return "进纸器多张纸重叠，请分开重放";
                case unchecked((int)0x80210011): return "单次传输超过驱动缓冲上限（把扫描 DPI 调低再试）";
                default: return null;
            }
        }

        // 把 COM 异常翻译成用户能看懂的话；firstPage=true 时提示侧重"为什么一张都没扫出来"
        static Exception Translate(COMException ex, bool firstPage)
        {
            string hex = "0x" + (ex.ErrorCode & 0xFFFFFFFF).ToString("X8");
            string known = WiaText(ex.ErrorCode);
            string msg;
            if (known == null)
                msg = firstPage
                    ? "无法开始扫描。请确认扫描仪已接好、驱动已安装，并且没有其它程序正在使用它。\n（设备返回 " + hex + "）"
                    : "扫描中途出错（设备返回 " + hex + "，已扫描的页面已保存）。";
            else
                msg = firstPage ? known + "（" + hex + "）" : "扫描中途出错：" + known + "（已扫描的页面已保存）";
            return new Exception(msg, ex);
        }

        // ★ 一律走这个入口，不要直接用 Call/Get ★
        // WIA 自动化层（wiaaut.dll）里，同一个成员在不同 Windows 版本上可能被当作方法或属性：
        //   DeviceManager.DeviceInfos / DeviceInfo.Properties / Device.Items 是"属性"，
        //   DeviceInfos.Item / Properties.Item / Device.Connect / Item.Transfer 是"方法"，
        //   而 Item 这类集合成员有的版本还实现成"带参数的属性"。
        // 老代码固定用 InvokeMethod 调所有成员，于是取 DeviceInfos 属性时在严格的实现上直接
        // 返回 DISP_E_MEMBERNOTFOUND(0x80020003)，被 catch{} 吞掉 —— 界面上就是"没有找到扫描仪"，
        // 而且完全看不出原因。两种写法都试一遍，机器之间才不会时好时坏。
        static object Invoke(object obj, string name, params object[] args)
        {
            object[] a = args ?? new object[0];
            Exception last = null;
            if (a.Length == 0)
            {
                try { return Get(obj, name); } catch (Exception ex) { last = ex; }
                try { return Call(obj, name); } catch (Exception ex) { last = ex; }
            }
            else
            {
                try { return Call(obj, name, a); } catch (Exception ex) { last = ex; }
                try
                {
                    return obj.GetType().InvokeMember(name,
                        System.Reflection.BindingFlags.GetProperty, null, obj, a);
                }
                catch (Exception ex) { last = ex; }
            }
            throw last;
        }

        static object Call(object obj, string name, params object[] args)
        {
            return obj.GetType().InvokeMember(name,
                System.Reflection.BindingFlags.InvokeMethod, null, obj, args ?? new object[0]);
        }

        static object Get(object obj, string name)
        {
            return obj.GetType().InvokeMember(name,
                System.Reflection.BindingFlags.GetProperty, null, obj, null);
        }

        static void Set(object obj, string name, object value)
        {
            obj.GetType().InvokeMember(name,
                System.Reflection.BindingFlags.SetProperty, null, obj, new object[] { value });
        }
    }
}
