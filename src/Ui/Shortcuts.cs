// Shortcuts.cs — 可自定义的键盘快捷键：动作表 + keys.ini 持久化
//
// 设计：一个动作一个键（可带 Ctrl/Shift/Alt 修饰），存在程序目录的 keys.ini（写不进去就退到 %APPDATA%）。
// 与界面无关，方便单独测试。
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;

namespace PicTool
{
    public sealed class ShortcutAction
    {
        public string Id;
        public string Name;
        public string Group;
        public Keys Def;
        public Keys Key;

        public ShortcutAction(string id, string group, string name, Keys def)
        {
            Id = id; Group = group; Name = name; Def = def; Key = def;
        }
    }

    public static class Shortcuts
    {
        static List<ShortcutAction> items;
        static string path;
        static string pathUsed;

        // 显式确保"动作表已建 + keys.ini 已读"（：FilePath 原来写
        // `All.ToString()` 借属性访问骗加载，语义诡异；抽成具名方法，All 与 FilePath 共用）
        static void EnsureLoaded()
        {
            if (items == null) { items = Build(); Load(); }
        }

        public static List<ShortcutAction> All
        {
            get
            {
                EnsureLoaded();
                return items;
            }
        }

        public static string FilePath { get { EnsureLoaded(); return pathUsed; } }

        static List<ShortcutAction> Build()
        {
            return new List<ShortcutAction>(new ShortcutAction[] {
                new ShortcutAction("tool.brush", "工具", "笔刷", Keys.B),
                new ShortcutAction("tool.rect",  "工具", "框选", Keys.R),
                new ShortcutAction("tool.erase", "工具", "橡皮", Keys.E),
                new ShortcutAction("tool.text",  "工具", "文字", Keys.T),

                new ShortcutAction("tool.inpaint", "操作", "开始修复", Keys.Space),
                new ShortcutAction("crop.do",      "操作", "裁剪到选框", Keys.Control | Keys.K),
                new ShortcutAction("crop.trim",    "操作", "自动裁白边", Keys.Control | Keys.T),
                new ShortcutAction("crop.a4",      "操作", "裁剪并做成 A4", Keys.Control | Keys.Shift | Keys.A),
                new ShortcutAction("seal.detect",  "操作", "识别印章", Keys.Control | Keys.D),
                new ShortcutAction("seal.color",   "操作", "给印章上色", Keys.Control | Keys.L),

                new ShortcutAction("edit.undo",   "编辑", "撤销", Keys.Control | Keys.Z),
                new ShortcutAction("edit.revert", "编辑", "还原（放弃全部修改）", Keys.None),
                new ShortcutAction("file.save",   "编辑", "保存", Keys.Control | Keys.S),
                new ShortcutAction("file.saveas", "编辑", "另存为", Keys.Control | Keys.Shift | Keys.S),

                new ShortcutAction("view.fit",    "视图", "适应窗口", Keys.F),
                new ShortcutAction("view.actual", "视图", "实际大小 100%", Keys.Control | Keys.D0),
                new ShortcutAction("view.zoomIn", "视图", "放大", Keys.Control | Keys.Oemplus),
                new ShortcutAction("view.zoomOut","视图", "缩小", Keys.Control | Keys.OemMinus),
                new ShortcutAction("nav.prev",    "视图", "上一张", Keys.Left),
                new ShortcutAction("nav.next",    "视图", "下一张", Keys.Right),
                new ShortcutAction("brush.plus",  "视图", "笔刷变大", Keys.OemCloseBrackets),
                new ShortcutAction("brush.minus", "视图", "笔刷变小", Keys.OemOpenBrackets),

                // 归档并入去除页：9 分区（Ctrl+1…Ctrl+9）
                // 实测：文字与批量对调位置，批量页演进为「归档」页
                new ShortcutAction("section.1", "分区", "去除", Keys.Control | Keys.D1),
                new ShortcutAction("section.2", "分区", "裁剪", Keys.Control | Keys.D2),
                new ShortcutAction("section.3", "分区", "方向", Keys.Control | Keys.D3),
                new ShortcutAction("section.4", "分区", "调色", Keys.Control | Keys.D4),
                new ShortcutAction("section.5", "分区", "印章", Keys.Control | Keys.D5),
                new ShortcutAction("section.6", "分区", "文字", Keys.Control | Keys.D6),
                new ShortcutAction("section.7", "分区", "扫描", Keys.Control | Keys.D7),
                new ShortcutAction("section.8", "分区", "文件", Keys.Control | Keys.D8),
                new ShortcutAction("section.9", "分区", "归档", Keys.Control | Keys.D9)
            });
        }

        static void ResolvePath()
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string p1 = Path.Combine(exeDir, "keys.ini");
            try
            {
                // 能写就用程序目录（便携；U 盘拷走设置也跟着走）
                using (FileStream fs = new FileStream(p1, FileMode.Append, FileAccess.Write)) { }
                path = p1;
            }
            catch
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Restoria");
                try { Directory.CreateDirectory(dir); } catch { }
                path = Path.Combine(dir, "keys.ini");
            }
            pathUsed = path;
        }

        public static void Load()
        {
            if (path == null) ResolvePath();
            try
            {
                if (!File.Exists(path)) { ResetDefaults(false); return; }
                string[] lines = File.ReadAllLines(path, System.Text.Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string ln = lines[i].Trim();
                    if (ln.Length == 0 || ln[0] == '#') continue;
                    int eq = ln.IndexOf('=');
                    if (eq <= 0) continue;
                    string id = ln.Substring(0, eq).Trim();
                    string val = ln.Substring(eq + 1).Trim();
                    ShortcutAction a = ById(id);
                    if (a == null) continue;
                    if (val.Length == 0 || val == "None") { a.Key = Keys.None; continue; }
                    try { a.Key = (Keys)Enum.Parse(typeof(Keys), val, true); }
                    catch { }
                }
            }
            catch { }
        }

        public static void Save()
        {
            if (path == null) ResolvePath();
            try
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.AppendLine("# Restoria 快捷键设置（可手改；删掉本文件即恢复默认）");
                for (int i = 0; i < All.Count; i++)
                    sb.AppendLine(All[i].Id + "=" + All[i].Key.ToString());
                File.WriteAllText(path, sb.ToString(), System.Text.Encoding.UTF8);
            }
            catch { }
        }

        public static void ResetDefaults(bool save)
        {
            for (int i = 0; i < All.Count; i++) All[i].Key = All[i].Def;
            if (save) Save();
        }

        public static ShortcutAction ById(string id)
        {
            for (int i = 0; i < All.Count; i++) if (All[i].Id == id) return All[i];
            return null;
        }

        public static Keys Get(string id)
        {
            ShortcutAction a = ById(id);
            return a == null ? Keys.None : a.Key;
        }

        // 该动作是否被这个按键触发（Keys.None 表示未设置，永不触发）
        public static bool Hit(Keys keyData, string id)
        {
            Keys k = Get(id);
            return k != Keys.None && k == keyData;
        }

        // 这个按键被哪个动作占用了（冲突检测）；返回 null 表示没占用
        public static ShortcutAction WhoHas(Keys keyData, ShortcutAction except)
        {
            if (keyData == Keys.None) return null;
            for (int i = 0; i < All.Count; i++)
            {
                ShortcutAction a = All[i];
                if (a == except) continue;
                if (a.Key != Keys.None && a.Key == keyData) return a;
            }
            return null;
        }

        // ---------- 保留键 ----------
        // 快捷键设置面板以前只查"可改集"里的占用，导致两类键也能被绑走：
        //   ① 固定功能键 —— 写死在 MainForm.ProcessCmdKey / WndProc 里（1-4 切工具、
        //      0 适应窗口、Ctrl+O 打开、F2 重命名、Delete 删除）。绑上去会造成双触发
        //      （固定处理和自定义动作各响应一次）；
        //   ② 骨架键 —— Enter / Tab。绑 Enter 会废掉所有对话框的默认键，
        //      绑 Tab 会废掉焦点遍历。
        // 面板底部的"固定按键"只读说明与这里的拒绝判据共用同一份数据，不再各说各话。
        static readonly Keys[] FixedFuncKeys = new Keys[] {
            Keys.D1, Keys.NumPad1, Keys.D2, Keys.NumPad2, Keys.D3, Keys.NumPad3, Keys.D4, Keys.NumPad4,
            Keys.D0, Keys.NumPad0,
            Keys.Control | Keys.O, Keys.F2, Keys.Delete,
        };

        // 返回 null = 可以绑；否则返回拒绝原因（"fixed" 固定功能键 / "skeleton" 骨架键）
        public static string RejectReason(Keys keyData)
        {
            Keys body = keyData & Keys.KeyCode;
            if (body == Keys.Enter || body == Keys.Tab) return "skeleton";
            for (int i = 0; i < FixedFuncKeys.Length; i++)
                if (FixedFuncKeys[i] == keyData) return "fixed";
            return null;
        }

        // 面板底部"固定按键"说明与拒绝提示共用的一句解释（skeleton 键单列，避免和上面冲突）
        public const string FixedKeysNote =
            "固定按键（不可修改）：1 / 2 / 3 / 4 切工具（笔刷 / 框选 / 橡皮 / 文字） · 0 或双击画面 适应窗口 · "
            + "Ctrl+O 打开 · F2 重命名 · Delete 删除到回收站 · Ctrl+滚轮 调笔刷大小 · Shift+滚轮 水平平移"
            + "（Enter / Tab 为系统骨架键，不可绑定；「开始修复」默认空格，可在上方修改）";

        // 显示用：Ctrl+Shift+Z
        public static string Text(Keys k)
        {
            if (k == Keys.None) return "未设置";
            string s = "";
            if ((k & Keys.Control) == Keys.Control) s += "Ctrl+";
            if ((k & Keys.Shift) == Keys.Shift) s += "Shift+";
            if ((k & Keys.Alt) == Keys.Alt) s += "Alt+";
            Keys body = k & Keys.KeyCode;
            if (body == Keys.Oemplus || body == Keys.Add) s += "+";
            else if (body == Keys.OemMinus || body == Keys.Subtract) s += "-";
            else if (body == Keys.OemOpenBrackets) s += "[";
            else if (body == Keys.OemCloseBrackets) s += "]";
            else if (body == Keys.Space) s += "空格";
            else if (body == Keys.Left) s += "←";
            else if (body == Keys.Right) s += "→";
            else if (body == Keys.Up) s += "↑";
            else if (body == Keys.Down) s += "↓";
            else if (body >= Keys.D0 && body <= Keys.D9) s += ((int)body - (int)Keys.D0).ToString();   // Ctrl+D4 → Ctrl+4
            else s += body.ToString();
            return s;
        }
    }
}
