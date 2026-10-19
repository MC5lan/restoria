// UiSettings.cs — 界面外观设置（目前只有主题），存 %AppData%\Restoria\ui.ini
//
// 为什么单独一个文件而不是塞进 Shortcuts：快捷键是"按键映射"，外观是"看起来的样子"，
// 两件事混在一个 ini 里以后加设置会互相污染（删文件恢复默认也分不开）。
// 存 %AppData% 而不是程序目录：装在 Program Files 下程序目录不可写，而且重装会丢设置。
using System;
using System.IO;

namespace PicTool
{
    public static class UiSettings
    {
        static string path;
        static bool loaded;

        public static bool Dark;        // true = 深色工作台；false = 浅色（墨蓝·纸感）

        // ★ 归档档位：false=普通 / true=极致，启动时记住上次选择。
        public static bool ArchiveThorough;

        static string ResolvePath()
        {
            try
            {
                // 优先程序目录（绿色版：设置跟着程序走），不可写再退 AppData
                string self = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                string p = Path.Combine(self, "ui.ini");
                try
                {
                    using (FileStream fs = File.Open(p, FileMode.OpenOrCreate, FileAccess.Write)) { }
                    return p;
                }
                catch { }
            }
            catch { }
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Restoria");
                try { Directory.CreateDirectory(dir); } catch { }
                return Path.Combine(dir, "ui.ini");
            }
            catch { }
            return null;
        }

        public static string FilePath { get { if (path == null) path = ResolvePath(); return path; } }

        public static void Load()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                string p = FilePath;
                if (p == null || !File.Exists(p)) return;
                string[] lines = File.ReadAllLines(p, System.Text.Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string ln = lines[i].Trim();
                    if (ln.Length == 0 || ln[0] == '#') continue;
                    int eq = ln.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = ln.Substring(0, eq).Trim();
                    string v = ln.Substring(eq + 1).Trim();
                    if (string.Equals(k, "theme", StringComparison.OrdinalIgnoreCase))
                        Dark = string.Equals(v, "dark", StringComparison.OrdinalIgnoreCase);
                    else if (string.Equals(k, "archive", StringComparison.OrdinalIgnoreCase))
                        ArchiveThorough = string.Equals(v, "thorough", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { }
        }

        public static void Save()
        {
            try
            {
                string p = FilePath;
                if (p == null) return;
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.AppendLine("# Restoria 界面设置（可手改；删掉本文件即恢复默认浅色+普通档）");
                sb.AppendLine("theme=" + (Dark ? "dark" : "light"));
                sb.AppendLine("archive=" + (ArchiveThorough ? "thorough" : "normal"));
                File.WriteAllText(p, sb.ToString(), System.Text.Encoding.UTF8);
            }
            catch { }
        }
    }
}
