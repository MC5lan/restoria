// FileTools.cs — 文件管理：批量重命名 / 批量新建文件夹（纯逻辑，无 UI 依赖）
//
// 设计要点：
// - 重命名一律先出"计划"（旧路径 -> 新路径），确认后才执行；执行时可跳过冲突项。
// - 每次成功执行返回可逆操作列表（旧->新），交给调用方做"撤销上次重命名"。
// - 文件名只处理"主名"，扩展名原样保留；非法字符自动替换为下划线。
using System;
using System.Collections.Generic;
using System.IO;

namespace PicTool
{
    public class RenameOp
    {
        public string OldPath;      // 原文件完整路径
        public string NewPath;      // 新文件完整路径
        public string OldName { get { return Path.GetFileName(OldPath); } }
        public string NewName { get { return Path.GetFileName(NewPath); } }
    }

    public class RenameResult
    {
        public int Ok, Skip;
        public List<string> Errors = new List<string>();
        public List<RenameOp> Done = new List<RenameOp>();   // 用于撤销
    }

    public static class FileTools
    {
        // ---------- 批量重命名 ----------

        // 编号模式：文件名 = 前缀 + 四位序号（顺序即传入 files 的顺序，调用方用工作区排序）
        public static List<RenameOp> PlanNumbered(string[] files, string prefix, int start)
        {
            List<RenameOp> plan = new List<RenameOp>();
            if (prefix == null) prefix = "";
            prefix = CleanName(prefix);
            for (int i = 0; i < files.Length; i++)
            {
                RenameOp op = new RenameOp();
                op.OldPath = files[i];
                op.NewPath = Path.Combine(Path.GetDirectoryName(files[i]),
                    prefix + (start + i).ToString("0000") + Path.GetExtension(files[i]));
                plan.Add(op);
            }
            return plan;
        }

        // 查找替换模式：只替换文件主名（不区分大小写），扩展名不动
        public static List<RenameOp> PlanReplace(string[] files, string find, string repl)
        {
            List<RenameOp> plan = new List<RenameOp>();
            if (find == null || find.Length == 0) return plan;
            if (repl == null) repl = "";
            repl = CleanName(repl);
            for (int i = 0; i < files.Length; i++)
            {
                string name = Path.GetFileNameWithoutExtension(files[i]);
                string newName = name.Replace(find, repl);   // 大小写敏感的精确替换，行为可预期
                if (newName == name) continue;
                if (newName.Trim().Length == 0) newName = "_";
                RenameOp op = new RenameOp();
                op.OldPath = files[i];
                op.NewPath = Path.Combine(Path.GetDirectoryName(files[i]), newName + Path.GetExtension(files[i]));
                plan.Add(op);
            }
            return plan;
        }

        // 执行重命名计划。冲突（目标已存在 / 计划内部重名 / 名字没变）自动跳过并计数。
        // 注意：本项目只引用 .NET 2.0 运行库（无 System.Core），不能用 HashSet/LINQ。
        public static RenameResult Apply(List<RenameOp> plan)
        {
            RenameResult r = new RenameResult();
            List<string> used = new List<string>();
            for (int i = 0; i < plan.Count; i++)
            {
                RenameOp op = plan[i];
                string target = op.NewPath;
                if (string.Equals(op.OldPath, target, StringComparison.OrdinalIgnoreCase)) continue;   // 没变化
                bool exists = File.Exists(target);
                if (!exists)
                    for (int u = 0; u < used.Count; u++)
                        if (string.Equals(used[u], target, StringComparison.OrdinalIgnoreCase)) { exists = true; break; }
                if (exists)
                {
                    r.Skip++;
                    r.Errors.Add("跳过：" + op.OldName + " -> " + Path.GetFileName(target) + "（目标已存在）");
                    continue;
                }
                try
                {
                    File.Move(op.OldPath, target);
                    used.Add(target);
                    RenameOp done = new RenameOp();
                    done.OldPath = target;      // 反向：撤销时 从新 -> 回旧
                    done.NewPath = op.OldPath;
                    r.Done.Add(done);
                    r.Ok++;
                }
                catch (Exception ex)
                {
                    r.Skip++;
                    r.Errors.Add(op.OldName + "：" + ex.Message);
                }
            }
            return r;
        }

        // ---------- 批量新建文件夹 ----------

        // 在 parent 下创建 基础名01..基础名NN（位数按数量自动补零）。返回实际创建的文件夹数。
        public static int CreateNumberedFolders(string parent, string baseName, int count)
        {
            if (parent == null || parent.Length == 0) throw new Exception("没有指定位置。");
            if (count < 1 || count > 500) throw new Exception("数量需要在 1 ~ 500 之间。");
            if (baseName == null) baseName = "";
            baseName = CleanName(baseName);
            int pad = count >= 100 ? 3 : 2;
            int created = 0;
            for (int i = 1; i <= count; i++)
            {
                string dir = Path.Combine(parent, baseName + i.ToString("D" + pad));
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                    created++;
                }
            }
            return created;
        }

        // ---------- 工具 ----------

        // 去掉文件/文件夹名里的非法字符
        public static string CleanName(string s)
        {
            if (s == null) return "";
            char[] bad = Path.GetInvalidFileNameChars();
            for (int i = 0; i < bad.Length; i++)
                s = s.Replace(bad[i], '_');
            return s.Trim();
        }

        // ---------- 删除到回收站 ----------
        // 用系统 SHFileOperation + FOF_ALLOWUNDO：走资源管理器同一条删除路径，
        // 用户误删还能从回收站捞回来（工具不提供"永久删除"——恢复区铁律的同一条
        // 原则：不覆盖、不销毁用户的原始文件）。
        // 为什么不用 Microsoft.VisualBasic.FileIO：那要加程序集引用，本项目坚持
        // 零额外依赖（单文件发布）。
        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential,
            CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        const uint FO_DELETE = 0x0003;
        const ushort FOF_ALLOWUNDO = 0x0040;      // 进回收站
        const ushort FOF_NOCONFIRMATION = 0x0010; // 不再弹系统确认框（界面里已确认过）
        const ushort FOF_SILENT = 0x0004;
        const ushort FOF_NOERRORUI = 0x0400;

        public static bool DeleteToRecycleBin(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (!File.Exists(path) && !Directory.Exists(path)) return false;
            SHFILEOPSTRUCT op = new SHFILEOPSTRUCT();
            op.wFunc = FO_DELETE;
            op.pFrom = path + "\0\0";     // 双 null 结尾：可放多个路径，单个也要两个 \0
            op.fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI);
            try { return SHFileOperation(ref op) == 0; }
            catch { return false; }
        }

        // 生成不与现有文件冲突的目标名（重命名用）：在 "名字 (2).ext" 上递增
        public static string UniquePath(string want)
        {
            if (!File.Exists(want) && !Directory.Exists(want)) return want;
            string dir = Path.GetDirectoryName(want);
            string name = Path.GetFileNameWithoutExtension(want);
            string ext = Path.GetExtension(want);
            for (int i = 2; i < 1000; i++)
            {
                string cand = Path.Combine(dir, name + " (" + i + ")" + ext);
                if (!File.Exists(cand) && !Directory.Exists(cand)) return cand;
            }
            return want;
        }

        // ---------- 归档路径保险丝（入口防护，拒绝危险目录） ----------
        //
        // 递归归档会"钻进"选中的文件夹树批量写文件 —— 万一选错对象（桌面、盘符根、
        // 系统目录）就是一场灾难。所以在入口处设一道保险丝：危险路径一律拒绝，
        // 并且给出小白能看懂的原因。只检查"选中的顶层文件夹"：
        // 子目录都在它下面，顶层合法则整棵树都合法。
        //
        // 拦截范围（含这些目录的所有子目录）：
        //   ① 盘符根目录（C:\ 、D:\ 这种）
        //   ② 系统与程序目录（Windows、Program Files 等）
        //   ③ 用户主目录整棵（含桌面/文档/图片/音乐/视频/下载/OneDrive 重定向后的桌面）
        //   ④ 工具自己的安装目录
        // 注意 ③ 用 SpecialFolder 取真实路径，桌面被 OneDrive 重定向后照样拦得住。
        public static bool IsArchiveAllowed(string path, out string reason)
        {
            reason = null;
            string p;
            if (path == null || path.Trim().Length == 0) { reason = "没有选中文件夹。"; return false; }
            try { p = Path.GetFullPath(path.Trim().Trim('"')); }
            catch { reason = "这个路径读不出来，不能归档。"; return false; }
            p = p.TrimEnd('\\', '/');
            if (p.Length < 3) { reason = "路径无效。"; return false; }

            // ① 盘符根目录
            string root = null;
            try { root = Path.GetPathRoot(p); } catch { }
            if (!string.IsNullOrEmpty(root) &&
                string.Equals(p, root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                reason = "这是盘符根目录（" + root + "），在这里归档会波及整块磁盘，已阻止。\n" +
                         "请把档案放进一个专门的文件夹（比如 D:\\档案库）再归档。";
                return false;
            }

            // ②③④ 同一或子目录 → 拦
            string[] sysRoots = new string[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            };
            foreach (string s in sysRoots)
                if (IsSameOrUnder(p, s))
                { reason = "这是系统/程序目录（" + s + "），不能在这里归档。"; return false; }

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (IsSameOrUnder(p, userProfile))
            {
                reason = "这是用户个人目录（" + userProfile + "）——桌面、下载、文档这些都在它下面，\n" +
                         "为了防止误伤，这一片不能归档。请把档案放进一个专门的数据文件夹（比如 D:\\档案库）。";
                return false;
            }

            string appDir = null;
            try { appDir = Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location); }
            catch { }
            if (IsSameOrUnder(p, appDir))
            { reason = "这是本工具自己的安装目录，不能在这里归档。"; return false; }

            return true;
        }

        // path 就是 baseDir 或在 baseDir 里面？（大小写不敏感；baseDir 为空返回 false）
        static bool IsSameOrUnder(string path, string baseDir)
        {
            if (string.IsNullOrEmpty(baseDir)) return false;
            string b = baseDir.TrimEnd('\\', '/');
            if (b.Length == 0) return false;
            if (string.Equals(path, b, StringComparison.OrdinalIgnoreCase)) return true;
            return path.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase);
        }
    }
}
