// Recovery.cs — 异常恢复：把"改了还没保存"的图定期写进本地恢复区，崩溃后能找回来。
//
// 要解决的问题：档案录入常一坐几个小时，改一张图要好几分钟；程序一旦崩了（或被强杀、
// 断电），内存里没保存的全没了 —— 这比画质问题更伤人。
//
// 设计原则（与"单文件、零依赖"一致，不引任何库、不动用户文件）：
//   · 恢复区在 %LOCALAPPDATA%\PicTool\recovery\<会话>\，只写我们自己创建的文件；
//   · 存**无损 PNG** —— 恢复出来的东西必须和用户当时看到的完全一样，不能用有损 JPEG 应付；
//   · **去抖 + 覆盖**：同一张图反复改，只保留最新一份（文件名固定），不会越攒越多；
//   · 写盘在后台线程，UI 只做一次位图深拷贝（毫秒级）就返回；
//   · 正常退出把会话目录整个删掉；只有"没走正常退出"的会话才会留下来 —— 留下来就是待恢复。
//
// 恢复语义（诚实版）：把图恢复成 PNG 放进一个「已恢复」文件夹并自动打开，
// 让用户自己确认后再正式保存。**不直接覆盖原文件** —— 恢复本身不该引入新的风险。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading;

namespace PicTool
{
    public static class Recovery
    {
        public class Item
        {
            public string SrcPath;      // 原文件全路径（新扫描的图可能为 null）
            public long SrcSize;
            public long SrcTime;
            public string SaveName;     // 建议保存的文件名
            public string ImgFile;      // 恢复区里的 PNG 全路径
        }

        class Slot
        {
            public Bitmap Img;          // 主线程拷出来的副本，写完盘就释放
            public string SrcPath;
            public long SrcSize;
            public long SrcTime;
            public string SaveName;
            public string File;         // 恢复区里的 png 名（不含路径）
            public bool Busy;           // 写盘线程正在 Save 这张图：
                                        // 此期间主线程不得 Dispose 它，否则 GDI+ 报"对象正在其他地方使用"
        }

        static string root;
        static string session;
        static Dictionary<string, Slot> slots;
        static object gate = new object();
        static Timer timer;
        static int seq;
        static bool writing;

        // ---------- 生命周期 ----------

        public static void Start()
        {
            if (session != null) return;
            try
            {
                string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (baseDir == null || baseDir.Length == 0) baseDir = Path.GetTempPath();
                root = Path.Combine(Path.Combine(baseDir, "PicTool"), "recovery");
                Directory.CreateDirectory(root);
            }
            catch
            {
                try { root = Path.Combine(Path.GetTempPath(), "PicTool-recovery"); Directory.CreateDirectory(root); }
                catch { return; }                       // 连恢复区都建不出来：恢复功能静默降级
            }
            // 安全阀：30 天前的旧会话直接清掉（用户一直不打开程序也不会无限堆积）
            try
            {
                string[] old = Directory.GetDirectories(root, "s-*");
                for (int i = 0; i < old.Length; i++)
                    if ((DateTime.UtcNow - Directory.GetLastWriteTimeUtc(old[i])).TotalDays > 30)
                        Directory.Delete(old[i], true);
            }
            catch { }

            session = Path.Combine(root, "s-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            try { Directory.CreateDirectory(session); } catch { session = null; return; }
            slots = new Dictionary<string, Slot>();
            timer = new Timer(delegate { Pump(); }, null, 2500, 2500);
        }

        // 正常退出：会话目录整个删掉（走不到这里 = 崩溃/被杀 = 待恢复）
        public static void Shutdown() { Shutdown(true); }

        // deleteSession=false：保留盘上的会话文件，只释放内存资源。
        // 用途：退出时有图片保存失败 —— 快照必须留下来，下次启动才能找回。
        public static void Shutdown(bool deleteSession)
        {
            try { if (timer != null) { timer.Dispose(); timer = null; } } catch { }
            if (!deleteSession)
            {
                try { FlushForTest(); }        // 把还在内存里没落盘的快照同步写完，再谈"保留"
                catch { }
            }
            lock (gate)
            {
                if (slots != null)
                    foreach (Slot s in slots.Values)
                        try { if (s.Img != null) s.Img.Dispose(); } catch { }
                slots = null;
            }
            if (deleteSession)
                try { if (session != null && Directory.Exists(session)) Directory.Delete(session, true); } catch { }
            session = null;
        }

        // ---------- 记录 ----------

        // 主线程调用：深拷一份交给后台线程写盘。current 是应用正在用的位图，绝不能跨线程直接用。
        public static void Note(Bitmap current, string filePath)
        {
            if (session == null || current == null) return;
            string key = filePath == null || filePath.Length == 0 ? "@new" : filePath.ToLowerInvariant();
            Bitmap copy;
            try { copy = Clone(current); }
            catch { return; }
            Slot s = new Slot();
            s.Img = copy;
            s.SrcPath = filePath;
            s.SaveName = (filePath == null || filePath.Length == 0) ? "恢复_" + DateTime.Now.ToString("HHmmss") + ".png"
                                                                    : Path.GetFileName(filePath);
            try
            {
                FileInfo fi = new FileInfo(filePath);
                s.SrcSize = fi.Length;
                s.SrcTime = fi.LastWriteTimeUtc.Ticks;
            }
            catch { }
            lock (gate)
            {
                if (slots == null) { try { copy.Dispose(); } catch { } return; }
                Slot old;
                if (slots.TryGetValue(key, out old))
                {
                    s.File = old.File;                  // 同一张图：固定用同一个文件名，覆盖旧的
                    // 写盘线程正拿着 old.Img 在 Save：此时绝不能 Dispose，
                    // 让写盘线程写完后自己释放；本次快照被跳过，下一轮 Pump 会写新的
                    if (!old.Busy) try { if (old.Img != null) old.Img.Dispose(); } catch { }
                }
                if (s.File == null) s.File = "d" + (++seq).ToString("000") + ".png";
                slots[key] = s;
            }
        }

        // 这张图已经正式保存 / 已关闭：把恢复记录删掉
        public static void Clear(string filePath)
        {
            if (filePath == null || filePath.Length == 0 || slots == null) return;
            string key = filePath.ToLowerInvariant();
            Slot s = null;
            lock (gate)
            {
                if (slots.TryGetValue(key, out s)) slots.Remove(key);
            }
            if (s == null) return;
            if (!s.Busy) try { if (s.Img != null) s.Img.Dispose(); } catch { }   // Busy 时交给写盘线程释放
            if (s.File != null)
            {
                try { File.Delete(Path.Combine(session, s.File)); } catch { }
                try { File.Delete(Path.Combine(session, Path.ChangeExtension(s.File, ".txt"))); } catch { }
            }
        }

        // ---------- 后台写盘 ----------

        static void Pump()
        {
            if (session == null) return;
            List<Slot> todo = null;
            lock (gate)
            {
                if (slots == null || writing) return;
                foreach (Slot s in slots.Values)
                    if (s.Img != null)
                    {
                        if (todo == null) todo = new List<Slot>();
                        s.Busy = true;                   // 主线程见 Busy 就不再动 Img（Note/Clear）
                        todo.Add(s);
                    }
                if (todo == null) return;
                writing = true;                          // 一次只允许一个写盘线程，避免半张 PNG
            }
            try
            {
                if (!Directory.Exists(session)) Directory.CreateDirectory(session);
                foreach (Slot s in todo)
                {
                    string png = Path.Combine(session, s.File);
                    string txt = Path.ChangeExtension(png, ".txt");
                    try
                    {
                        if (s.Img != null) s.Img.Save(png, ImageFormat.Png);
                        StringBuilder m = new StringBuilder();
                        m.AppendLine("src=" + (s.SrcPath == null ? "" : s.SrcPath));
                        m.AppendLine("srcsize=" + s.SrcSize);
                        m.AppendLine("srctime=" + s.SrcTime);
                        m.AppendLine("name=" + s.SaveName);
                        m.AppendLine("time=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                        File.WriteAllText(txt, m.ToString(), Encoding.UTF8);
                    }
                    catch { }
                    try { if (s.Img != null) s.Img.Dispose(); } catch { }
                    s.Img = null;
                    lock (gate) { s.Busy = false; }      // 主线程从现在起可以安全接管这个槽位了
                }
            }
            finally { writing = false; }
        }

        // 自检用：同步把当前挂起的快照写完（正常路径由定时器异步做）
        public static void FlushForTest() { Pump(); }

        // ---------- 启动时恢复 ----------

        // 找出所有"没走正常退出"的会话，返回每个会话里的待恢复项
        public static List<List<Item>> PendingSessions()
        {
            List<List<Item>> outList = new List<List<Item>>();
            try
            {
                if (root == null || !Directory.Exists(root)) return outList;
                string[] dirs = Directory.GetDirectories(root, "s-*");
                Array.Sort(dirs);
                for (int i = 0; i < dirs.Length; i++)
                {
                    List<Item> items = ReadSession(dirs[i]);
                    if (items.Count > 0) outList.Add(items);
                    else { try { Directory.Delete(dirs[i], true); } catch { } }   // 空会话：清掉
                }
            }
            catch { }
            return outList;
        }

        static List<Item> ReadSession(string dir)
        {
            List<Item> list = new List<Item>();
            try
            {
                string[] pngs = Directory.GetFiles(dir, "d*.png");
                Array.Sort(pngs);
                for (int i = 0; i < pngs.Length; i++)
                {
                    string txt = Path.ChangeExtension(pngs[i], ".txt");
                    if (!File.Exists(txt)) continue;
                    Item it = new Item();
                    it.ImgFile = pngs[i];
                    foreach (string line in File.ReadAllLines(txt, Encoding.UTF8))
                    {
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string k = line.Substring(0, eq), v = line.Substring(eq + 1);
                        if (k == "src") it.SrcPath = v;
                        else if (k == "srcsize") { long n; if (long.TryParse(v, out n)) it.SrcSize = n; }
                        else if (k == "srctime") { long n; if (long.TryParse(v, out n)) it.SrcTime = n; }
                        else if (k == "name" && v.Length > 0) it.SaveName = v;
                    }
                    if (it.SaveName == null || it.SaveName.Length == 0) it.SaveName = Path.GetFileName(pngs[i]);
                    list.Add(it);
                }
            }
            catch { }
            return list;
        }

        // 把恢复区的图落到一个真实文件夹（用户自己决定之后怎么保存）
        public static string RestoreTo(string folder, List<Item> items)
        {
            Directory.CreateDirectory(folder);
            foreach (Item it in items)
            {
                string name = SanitizeName(it.SaveName);
                string dst = Unique(Path.Combine(folder, name));
                File.Copy(it.ImgFile, dst, true);
            }
            return folder;
        }

        // 恢复完（无论成功与否）清掉全部旧会话
        public static void DiscardAll()
        {
            try
            {
                if (root != null && Directory.Exists(root))
                    foreach (string d in Directory.GetDirectories(root, "s-*"))
                        try { Directory.Delete(d, true); } catch { }
                // 本进程的会话目录也被删了：下次写盘时 Pump 会按需重建（见上），
                // 所以这里不需要也不能“跳过当前会话”——丢弃就该丢干净，否则语义会骗人。
            }
            catch { }
        }

        // ---------- 工具 ----------

        static Bitmap Clone(Bitmap src)
        {
            Bitmap b = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(b))
                g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height));
            return b;
        }

        static string SanitizeName(string name)
        {
            if (name == null || name.Length == 0) return "恢复.png";
            char[] bad = Path.GetInvalidFileNameChars();
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                bool badChar = false;
                for (int j = 0; j < bad.Length; j++) if (bad[j] == name[i]) { badChar = true; break; }
                sb.Append(badChar ? '_' : name[i]);
            }
            string s = sb.ToString();
            return s.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? s : s + ".png";
        }

        static string Unique(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path), name = Path.GetFileNameWithoutExtension(path),
                   ext = Path.GetExtension(path);
            for (int i = 2; i < 1000; i++)
            {
                string p = Path.Combine(dir, name + "_" + i + ext);
                if (!File.Exists(p)) return p;
            }
            return path;
        }
    }
}
