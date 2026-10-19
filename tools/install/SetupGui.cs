// SetupGui.cs — Restoria · 图形化安装程序（安装向导 + 卸载器共用界面零件）
//
// 编译：tools\build_setup.ps1  →  dist\setup-boot.exe（与内嵌载荷拼成一个单文件安装程序）
// 启动：用户双击安装包 → 引导层解压载荷后进入本向导（见 SetupBoot.cs）。
//       不带载荷直接跑 exe 时，以 exe 所在目录为安装源（开发态 / --shot 诊断）。
//
// 发行形态：标准商业安装
//   · 清单要求管理员权限（assets\setup.manifest），双击弹一次 UAC；
//     这样 Program Files、HKLM 卸载入口、全用户开始菜单/桌面快捷方式都能一次写对。
//   · 装到 %ProgramFiles%\Restoria
//   · 快捷方式：所有用户的桌面 + 开始菜单（含「卸载」入口）
//   · 控制面板「程序和功能」可见：HKLM\...\Uninstall\PicToolArchiveSuite
//   · 先过许可协议页（EulaForm），不接受则退出
//
// 界面：白底扁平，配色/字体全部取自主题（src\Ui\Theme.cs），与主程序同一套视觉。
//   四步指示（检查环境 → 选择位置 → 正在安装 → 完成）
//   环境/结果卡片、可改的安装位置、真进度条、折叠的详细日志。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using PicTool;

namespace PicToolSetup
{
    static class Native
    {
        [DllImport("kernel32.dll")]
        public static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

        // 用 RtlGetVersion 而不是 Environment.OSVersion 读系统版本：
        // Environment.OSVersion 走 GetVersionEx，会受应用程序清单里 supportedOS 的
        // 影响被"虚拟化"成旧版本号（在 Win10/11 上会报成 6.2.9200「Windows 8」）。
        // 本程序的清单（assets\setup.manifest）只声明了 requireAdministrator 和
        // supportedOS —— 清单里的 supportedOS 列表决定了虚拟化行为，改清单时要留意这条。
        // RtlGetVersion 读的是内核真实版本，不受清单影响，也不依赖注册表字符串。
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct RTL_OSVERSIONINFOEXW
        {
            public uint dwOSVersionInfoSize;
            public uint dwMajorVersion;
            public uint dwMinorVersion;
            public uint dwBuildNumber;
            public uint dwPlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szCSDVersion;
            public ushort wServicePackMajor;
            public ushort wServicePackMinor;
            public ushort wSuiteMask;
            public byte wProductType;
            public byte wReserved;
        }

        [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
        public static extern int RtlGetVersion(ref RTL_OSVERSIONINFOEXW info);

        public static bool TrueOsVersion(out int major, out int minor, out int build)
        {
            major = 0; minor = 0; build = 0;
            try
            {
                RTL_OSVERSIONINFOEXW v = new RTL_OSVERSIONINFOEXW();
                v.dwOSVersionInfoSize = (uint)Marshal.SizeOf(typeof(RTL_OSVERSIONINFOEXW));
                if (RtlGetVersion(ref v) != 0) return false;
                major = (int)v.dwMajorVersion;
                minor = (int)v.dwMinorVersion;
                build = (int)v.dwBuildNumber;
                return major > 0;
            }
            catch { return false; }
        }

        public static string OsName(int major, int minor, int build)
        {
            if (major == 6)
            {
                if (minor == 0) return "Windows Vista";
                if (minor == 1) return "Windows 7";
                if (minor == 2) return "Windows 8";
                if (minor == 3) return "Windows 8.1";
            }
            if (major == 10) return build >= 22000 ? "Windows 11" : "Windows 10";
            if (major > 10) return "Windows " + major;
            return "Windows " + major + "." + minor;
        }

        public const uint FO_DELETE = 3;
        public const ushort FOF_SILENT = 4;
        public const ushort FOF_NOCONFIRMATION = 16;
        public const ushort FOF_ALLOWUNDO = 64;
        public const ushort FOF_NOERRORUI = 1024;

        // 删到回收站（可撤销），绝不直接抹掉
        public static bool Recycle(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return false;
            try
            {
                SHFILEOPSTRUCT op = new SHFILEOPSTRUCT();
                op.wFunc = FO_DELETE;
                op.pFrom = path + "\0\0";   // 双 null 结尾（与主程序 FileTools 里的写法保持一致）
                op.fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI);
                int rc = SHFileOperation(ref op);
                return rc == 0 && !Directory.Exists(path);
            }
            catch { return false; }
        }
    }

    // FlatButton / FlatProgress 见 FlatButton.cs（安装器与卸载器共用同一份）

    // ---------- 卡片：一排"圆点 + 一行字" ----------
    class Card : Control
    {
        readonly List<Color> dots = new List<Color>();
        readonly List<string> lines = new List<string>();

        public Card()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Enabled = false;
        }

        public void SetRows(Color[] d, string[] t)
        {
            dots.Clear(); lines.Clear();
            for (int i = 0; i < t.Length; i++)
            {
                dots.Add(i < d.Length ? d[i] : Theme.TextMuted);
                lines.Add(t[i]);
            }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent == null ? Color.White : Parent.BackColor);
            Theme.FillRound(g, new Rectangle(0, 0, Width - 1, Height - 1), 8, Theme.FieldBg);
            Theme.StrokeRound(g, new Rectangle(0, 0, Width - 1, Height - 1), 8, Theme.Border, 1f);
            int y = 12;
            for (int i = 0; i < lines.Count; i++)
            {
                using (SolidBrush b = new SolidBrush(dots[i]))
                    g.FillEllipse(b, 16, y + 6, 7, 7);
                Theme.TextLeft(g, lines[i], Theme.F12, 31, y, Width - 48, Theme.Text);
                y += 22;
            }
        }
    }

    // ---------- 四步指示 ----------
    class StepBar : Control
    {
        int step;
        readonly string[] names = { "检查环境", "选择位置", "正在安装", "完成" };

        public StepBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 50;
            Enabled = false;
        }

        public int Step
        {
            get { return step; }
            set { if (step != value) { step = value; Invalidate(); } }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent == null ? Color.White : Parent.BackColor);
            int n = names.Length;
            int seg = Width / n;
            int cy = 16;
            for (int i = 0; i < n; i++)
            {
                int cx = seg * i + seg / 2;
                bool done = i < step || step >= n - 1;
                bool cur = i == step && step < n - 1;
                Color fill, line, fg;
                if (done) { fill = Theme.GreenSoft; line = Theme.Green; fg = Theme.Green; }
                else if (cur) { fill = Theme.AccentSoft; line = Theme.AccentHi; fg = Theme.Accent; }
                else { fill = Theme.DisabledBg; line = Theme.BorderDeep; fg = Theme.TextMuted; }

                using (SolidBrush b = new SolidBrush(fill)) g.FillEllipse(b, cx - 11, cy - 11, 22, 22);
                using (Pen p = new Pen(line, 1f)) g.DrawEllipse(p, cx - 11, cy - 11, 22, 22);
                Theme.TextCenter(g, (i + 1).ToString(), Theme.F11,
                    new Rectangle(cx - 11, cy - 11, 22, 22), fg);
                Theme.TextCenter(g, names[i], Theme.F11, new Rectangle(cx - seg / 2, 32, seg, 18), fg);

                if (i < n - 1)
                {
                    int x1 = cx + 16;
                    int x2 = seg * (i + 1) + seg / 2 - 16;
                    using (Pen p = new Pen(i < step ? Theme.Green : Theme.Border, 1f))
                        g.DrawLine(p, x1, cy, x2, cy);
                }
            }
        }
    }

    class Job
    {
        public string Src;
        public string Dst;
        public string Show;
    }

    class SetupForm : Form
    {
        const int W = 656;
        const int BaseH = 516;
        const int LogH = 618;

        readonly string pkg;
        readonly string logPath;
        readonly bool autoStart;
        readonly bool autoExit;

        string target;
        string appVer = "未知";
        int netRelease;
        bool appOk;
        long aiBytes;
        int aiFiles;
        bool hasAi;
        bool isWin7;
        string osText = "";

        PictureBox picIcon;
        Label lblTitle, lblSub;
        StepBar steps;
        Card card;
        Label lblPath, lblHint, lblStatus, lblDetail, lnkLog, lblLogPath;
        TextBox txtPath, txtLog;
        FlatButton btnBrowse, btnGo, btnCancel, btnNet;
        FlatProgress bar;
        Panel footer;
        System.Windows.Forms.Timer marqueeTimer;

        Thread worker;
        volatile bool cancel;
        volatile bool running;
        volatile bool done;
        bool logShown;

        public SetupForm(string pkgDir, string presetDir, bool auto, bool exitWhenDone)
        {
            pkg = pkgDir;
            autoStart = auto;
            autoExit = exitWhenDone;
            logPath = Path.Combine(Path.GetTempPath(), "PicTool-install.log");

            Text = "Restoria 安装程序";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(W, BaseH);
            BackColor = Color.White;
            Font = Theme.F12;

            // 默认装到 Program Files（标准商业安装的惯例位置）。
            // %ProgramFiles% 在极少数环境下取不到，退到 Program Files (x86)；
            // 再取不到就退回用户目录（保证向导不至于因为环境怪就起不来）。
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (string.IsNullOrEmpty(pf))
                pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (string.IsNullOrEmpty(pf))
                pf = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string def = Path.Combine(pf, SetupInfo.Name);
            target = string.IsNullOrEmpty(presetDir) ? def : presetDir;

            ScanEnvironment();
            BuildUi();
            FillEnvCard();

            // 必须在 Load 里启动：构造函数阶段窗口句柄还没建立，
            // 那时 BeginInvoke（安装线程上报进度要用）会直接抛异常。
            if (autoStart && appOk)
                Load += delegate { StartInstall(); };
        }

        // ================= 环境体检（构造时同步做，都是毫秒级） =================
        void ScanEnvironment()
        {
            int maj = 0, min = 0, bld = 0;
            bool trueVer = Native.TrueOsVersion(out maj, out min, out bld);
            if (!trueVer)
            {
                Version vv = Environment.OSVersion.Version;
                maj = vv.Major; min = vv.Minor; bld = vv.Build;
            }
            osText = Native.OsName(maj, min, bld) + "（Build " + bld + "）· " +
                     (IntPtr.Size == 8 ? "64 位" : "32 位");
            // 只用于日志说明（2026-09-20 起 AI 素材不再按系统版本挑文件了 —— 整棵 ai\ 一起装，
            // 程序自己按"谁能加载用谁"选运行时；这里记录一下方便看日志对分布）
            isWin7 = (maj == 6 && min <= 1);
            Log("系统 " + osText + (isWin7 ? "  [Win7 目标机：会走 ai\\win7\\ 兼容层]" : "  [现代系统：走 ai\\onnxruntime.dll 原版]"));

            netRelease = GetNetRelease();

            string exe = Path.Combine(pkg, "Restoria.exe");
            appOk = File.Exists(exe);
            if (appOk)
            {
                try { appVer = FileVersionInfo.GetVersionInfo(exe).FileVersion; } catch { }
            }

            string ai = Path.Combine(pkg, "ai");
            aiBytes = 0; aiFiles = 0;
            if (Directory.Exists(ai))
            {
                foreach (string f in Directory.GetFiles(ai, "*", SearchOption.AllDirectories))
                {
                    try { aiBytes += new FileInfo(f).Length; aiFiles++; } catch { }
                }
            }
            hasAi = File.Exists(Path.Combine(ai, "onnxruntime.dll")) &&
                    Directory.GetFiles(ai, "*.onnx").Length > 0;
        }

        static int GetNetRelease()
        {
            for (int view = 0; view < 2; view++)
            {
                RegistryKey k = null;
                try
                {
                    RegistryView rv = view == 0 ? RegistryView.Registry64 : RegistryView.Registry32;
                    RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, rv);
                    if (baseKey != null)
                        k = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full");
                    if (k != null)
                    {
                        object o = k.GetValue("Release");
                        k.Close();
                        if (o != null) return Convert.ToInt32(o);
                    }
                }
                catch { }
            }
            return 0;
        }

        // ================= 界面 =================
        void BuildUi()
        {
            try
            {
                picIcon = new PictureBox();
                picIcon.Location = new Point(24, 18);
                picIcon.Size = new Size(28, 28);
                picIcon.SizeMode = PictureBoxSizeMode.StretchImage;
                Icon ico = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (ico != null) picIcon.Image = ico.ToBitmap();
                Controls.Add(picIcon);
            }
            catch { }

            lblTitle = MakeLabel("Restoria", 64, 17, 500, Theme.F15B, Theme.Text);
            lblSub = MakeLabel("安装程序 · 程序版本 " + appVer, 64, 43, 460, Theme.F11, Theme.TextMuted);

            AddDivider(84);

            steps = new StepBar();
            steps.Location = new Point(24, 92);
            steps.Width = W - 48;
            Controls.Add(steps);

            AddDivider(148);

            card = new Card();
            card.Location = new Point(24, 158);
            card.Size = new Size(W - 48, 104);
            Controls.Add(card);

            lblPath = MakeLabel("安装位置", 24, 276, 300, Theme.F12, Theme.TextSub);

            txtPath = new TextBox();
            txtPath.Location = new Point(24, 296);
            txtPath.Size = new Size(W - 48 - 96, 30);
            txtPath.Font = Theme.F12;
            txtPath.BackColor = Theme.FieldBg;
            txtPath.BorderStyle = BorderStyle.FixedSingle;
            txtPath.Text = target;
            Controls.Add(txtPath);

            btnBrowse = new FlatButton();
            btnBrowse.Text = "浏览…";
            btnBrowse.Location = new Point(W - 24 - 88, 296);
            btnBrowse.Size = new Size(88, 30);
            btnBrowse.Click += delegate { PickFolder(); };
            Controls.Add(btnBrowse);

            lblHint = MakeLabel("", 24, 334, W - 48, Theme.F11, Theme.TextMuted);

            lblStatus = MakeLabel("准备就绪，点右下角「开始安装」。", 24, 366, W - 48, Theme.F12, Theme.Text);

            bar = new FlatProgress();
            bar.Location = new Point(24, 392);
            bar.Width = W - 48;
            bar.Visible = false;
            Controls.Add(bar);

            lblDetail = MakeLabel("", 24, 406, W - 48, Theme.F11, Theme.TextMuted);

            lnkLog = MakeLabel("显示详细日志  ▾", 24, 432, 200, Theme.F11, Theme.TextSub);
            lnkLog.Cursor = Cursors.Hand;
            lnkLog.Click += delegate { ToggleLog(); };

            txtLog = new TextBox();
            txtLog.Location = new Point(24, 458);
            txtLog.Size = new Size(W - 48, 96);
            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.WordWrap = false;
            txtLog.BorderStyle = BorderStyle.FixedSingle;
            txtLog.BackColor = Theme.FieldBg;
            txtLog.Font = new Font("Consolas", 8.25f);
            txtLog.Visible = false;
            Controls.Add(txtLog);

            footer = new Panel();
            footer.Dock = DockStyle.Bottom;
            footer.Height = 56;
            footer.BackColor = Color.White;
            footer.Paint += delegate(object s, PaintEventArgs e)
            {
                Theme.HLine(e.Graphics, 0, footer.Width, 0, Theme.Border);
            };
            Controls.Add(footer);

            lblLogPath = new Label();
            lblLogPath.AutoSize = false;
            lblLogPath.Location = new Point(24, 20);
            lblLogPath.Size = new Size(300, 18);
            lblLogPath.Font = Theme.F11;
            lblLogPath.ForeColor = Theme.TextMuted;
            lblLogPath.Text = "日志：%TEMP%\\PicTool-install.log";
            footer.Controls.Add(lblLogPath);

            btnNet = new FlatButton();
            btnNet.Text = "安装 .NET 4.8";
            btnNet.Location = new Point(W - 24 - 120 - 8 - 88 - 8 - 112, 12);
            btnNet.Size = new Size(112, 32);
            btnNet.Visible = false;
            btnNet.Click += delegate { InstallNet48(); };
            footer.Controls.Add(btnNet);

            btnCancel = new FlatButton();
            btnCancel.Text = "取消";
            btnCancel.Location = new Point(W - 24 - 120 - 8 - 88, 12);
            btnCancel.Size = new Size(88, 32);
            btnCancel.Click += delegate { OnCancel(); };
            footer.Controls.Add(btnCancel);

            btnGo = new FlatButton();
            btnGo.Text = "开始安装";
            btnGo.Primary = true;
            btnGo.Location = new Point(W - 24 - 120, 12);
            btnGo.Size = new Size(120, 32);
            btnGo.Click += delegate { OnGo(); };
            footer.Controls.Add(btnGo);

            marqueeTimer = new System.Windows.Forms.Timer();
            marqueeTimer.Interval = 60;
            marqueeTimer.Tick += delegate { bar.Advance(); };
            marqueeTimer.Start();

            UpdateHint();
        }

        Label MakeLabel(string text, int x, int y, int w, Font f, Color c)
        {
            Label l = new Label();
            l.AutoSize = false;
            l.Location = new Point(x, y);
            l.Size = new Size(w, 20);
            l.Font = f;
            l.ForeColor = c;
            l.BackColor = Color.Transparent;
            l.Text = text;
            Controls.Add(l);
            return l;
        }

        void AddDivider(int y)
        {
            Panel p = new Panel();
            p.Location = new Point(0, y);
            p.Size = new Size(W, 1);
            p.BackColor = Theme.Border;
            Controls.Add(p);
        }

        void UpdateHint()
        {
            if (!appOk)
            {
                lblHint.ForeColor = Theme.Danger;
                lblHint.Text = "安装包里找不到「Restoria.exe」，这个包不完整，请重新获取。";
                btnGo.Enabled = false;
                return;
            }
            if (netRelease > 0 && netRelease < 528040)
            {
                lblHint.ForeColor = Theme.Warn;
                lblHint.Text = "系统里的 .NET Framework 低于 4.8，程序装好后可能起不来 —— 建议先点「安装 .NET 4.8」。";
                btnNet.Visible = true;
            }
            else if (netRelease >= 528040)
            {
                lblHint.ForeColor = Theme.TextMuted;
                lblHint.Text = "装好后会在桌面与开始菜单生成快捷方式，并登记到控制面板的「程序和功能」里，随时可卸载。";
            }
            else
            {
                lblHint.ForeColor = Theme.Warn;
                lblHint.Text = "读不到 .NET Framework 版本信息，若装好后无法启动，请先安装 .NET 4.8。";
                btnNet.Visible = File.Exists(Path.Combine(pkg, "ndp48-x86-x64-allos-enu.exe"));
            }
        }

        void FillEnvCard()
        {
            Color ok = Theme.Green, bad = Theme.Danger, warn = Theme.Warn;
            string[] rows = new string[4];
            Color[] dots = new Color[4];

            rows[0] = "系统：" + osText;
            dots[0] = ok;

            if (netRelease >= 528040)
            {
                rows[1] = ".NET Framework 4.8 已就绪（Release " + netRelease + "）";
                dots[1] = ok;
            }
            else if (netRelease > 0)
            {
                rows[1] = ".NET Framework 版本偏低（Release " + netRelease + "），需要 4.8";
                dots[1] = bad;
            }
            else
            {
                rows[1] = "读不到 .NET Framework 4.x 的版本信息";
                dots[1] = warn;
            }

            if (appOk)
            {
                long kb = 0;
                try { kb = new FileInfo(Path.Combine(pkg, "Restoria.exe")).Length / 1024; } catch { }
                rows[2] = "程序包：Restoria " + appVer + " 就绪（" + kb + " KB）";
                dots[2] = ok;
            }
            else
            {
                rows[2] = "程序包：找不到 Restoria.exe";
                dots[2] = bad;
            }

            if (hasAi)
            {
                rows[3] = "AI 素材：含 AI 修复与 OCR，" + aiFiles + " 个文件（约 " + MB(aiBytes) + "）";
                dots[3] = ok;
            }
            else
            {
                rows[3] = "AI 素材：未包含（装好为普通/增强/混合三模式版，功能正常）";
                dots[3] = warn;
            }

            card.SetRows(dots, rows);
        }

        static string MB(long bytes)
        {
            return ((double)bytes / 1048576.0).ToString("0.0") + " MB";
        }

        // 诊断用：把界面切到某个状态，供 --shot 渲染（只改显示，不做任何真实安装）
        public void Demo(string state)
        {
            if (state == "working" || state == "marquee")
            {
                steps.Step = 2;
                txtPath.ReadOnly = true;
                btnBrowse.Enabled = false;
                btnGo.Enabled = false;
                btnGo.Text = "安装中…";
                btnCancel.Text = "中断";
                bar.Visible = true;
                if (state == "marquee")
                {
                    bar.Marquee = true;
                    lblStatus.Text = "正在自检（加载 AI 运行时与模型，老机器可能要十几秒）…";
                    lblDetail.Text = "不确定环节用滑动条表示，不硬凑百分比";
                }
                else
                {
                    bar.Value = 62;
                    lblStatus.Text = "正在复制 ai\\migan.onnx…";
                    lblDetail.Text = "42.1 MB / 68.4 MB";
                }
            }
            else if (state == "done")
            {
                done = true;
                steps.Step = 3;
                bar.Visible = true;
                bar.Value = 100;
                txtPath.ReadOnly = true;
                btnBrowse.Enabled = false;
                lblStatus.Text = "安装完成";
                lblStatus.Font = Theme.F13B;
                lblDetail.Text = "程序位置：" + target;
                btnGo.Text = "打开程序";
                btnCancel.Text = "关闭";
                aiOk = true;
                wiaCount = 1;
                ShortcutOk = true;
                ShortcutAllUsers = true;
                UninstallEntryOk = true;
                ShowResultCard();
            }
            else if (state == "log")
            {
                txtLog.Text =
                    "=== 检查系统环境 ===\r\n" +
                    "  [成功] Windows 7，目标平台\r\n" +
                    "  [成功] .NET Framework 4.8 已就绪（Release 528372）\r\n" +
                    "=== 复制程序文件 ===\r\n" +
                    "  [成功] 已复制 9 个文件（68.4 MB）\r\n" +
                    "  [成功] 已核对：Restoria.exe 748.1 KB 就位\r\n" +
                    "=== 自检与快捷方式 ===\r\n" +
                    "  [成功] 自检通过：AI 修复可用\r\n" +
                    "  [成功] 已创建快捷方式：桌面 + 开始菜单（所有用户），含「卸载」入口\r\n" +
                    "  [成功] 已在控制面板登记卸载入口";
                ToggleLog();
            }
        }

        // ================= 交互 =================
        void PickFolder()
        {
            FolderBrowserDialog d = new FolderBrowserDialog();
            d.Description = "选择安装位置（会在这个文件夹里放程序）";
            d.ShowNewFolderButton = true;
            if (Directory.Exists(txtPath.Text)) d.SelectedPath = txtPath.Text;
            if (d.ShowDialog(this) == DialogResult.OK) txtPath.Text = d.SelectedPath;
        }

        void ToggleLog()
        {
            logShown = !logShown;
            txtLog.Visible = logShown;
            lnkLog.Text = logShown ? "隐藏详细日志  ▴" : "显示详细日志  ▾";
            ClientSize = new Size(W, logShown ? LogH : BaseH);
        }

        void OnGo()
        {
            if (done)
            {
                string exe = Path.Combine(target, "Restoria.exe");
                try { Process.Start(exe); } catch { }
                Close();
                return;
            }
            StartInstall();
        }

        void OnCancel()
        {
            if (done) { Close(); return; }
            if (running)
            {
                DialogResult r = MessageBox.Show(this,
                    "安装正在进行，确定要中断吗？已经复制过去的文件会留在安装目录里。",
                    "确认中断", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r == DialogResult.Yes) cancel = true;
                return;
            }
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (running && !cancel && !done)
            {
                DialogResult r = MessageBox.Show(this,
                    "安装正在进行，关闭窗口会中断安装。确定要关闭吗？",
                    "确认关闭", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) { e.Cancel = true; return; }
                cancel = true;
            }
            base.OnFormClosing(e);
        }

        void InstallNet48()
        {
            string ndp = Path.Combine(pkg, "ndp48-x86-x64-allos-enu.exe");
            if (!File.Exists(ndp))
            {
                MessageBox.Show(this, "安装包内没有 .NET Framework 4.8 离线安装程序，请另行下载安装。",
                    "缺少安装包", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            btnNet.Enabled = false;
            btnNet.Text = "安装中…";
            Application.DoEvents();
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(ndp);
                psi.UseShellExecute = true;
                Process p = Process.Start(psi);
                if (p != null) p.WaitForExit();
            }
            catch (Exception ex) { Log("[注意] 启动 .NET 4.8 安装程序失败：" + ex.Message); }
            netRelease = GetNetRelease();
            FillEnvCard();
            UpdateHint();
            btnNet.Text = "安装 .NET 4.8";
            btnNet.Enabled = true;
            btnNet.Visible = !(netRelease >= 528040);
            if (netRelease >= 528040) Log("[成功] .NET Framework 4.8 已就绪（Release " + netRelease + "）");
        }

        // ================= 安装主流程 =================
        void StartInstall()
        {
            if (running) return;
            target = (txtPath.Text == null ? "" : txtPath.Text.Trim().TrimEnd('\\'));
            if (target.Length == 0)
            {
                MessageBox.Show(this, "请先填写安装位置。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                target = Path.GetFullPath(target);
            }
            catch
            {
                MessageBox.Show(this, "安装位置不是有效的路径。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!IsWritable(target))
            {
                AskElevate();
                return;
            }

            running = true;
            cancel = false;
            steps.Step = 2;
            txtPath.ReadOnly = true;
            btnBrowse.Enabled = false;
            btnGo.Enabled = false;
            btnGo.Text = "安装中…";
            btnCancel.Text = "中断";
            btnCancel.Enabled = true;
            lblStatus.Font = Theme.F12;
            lblStatus.ForeColor = Theme.Text;
            lblStatus.Text = "正在准备安装目录…";
            bar.Value = 0;
            bar.Marquee = false;
            bar.Visible = true;
            lblDetail.Visible = true;
            Log("");
            Log("========================================");
            Log("   Restoria — 安装程序（图形版）");
            Log("   时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            Log("   安装源: " + pkg);
            Log("   安装到: " + target);
            Log("========================================");

            worker = new Thread(Worker);
            worker.IsBackground = true;
            worker.Start();
        }

        static bool IsWritable(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".pictool-wtest");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        void AskElevate()
        {
            DialogResult r = MessageBox.Show(this,
                "「" + target + "」这个位置需要管理员权限才能写入。\r\n\r\n" +
                "点「是」以管理员身份重新启动安装程序（会弹一次权限确认）；\r\n" +
                "点「否」换一个位置（例如「文档」或桌面下的文件夹）。",
                "需要管理员权限", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (r != DialogResult.Yes) return;

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath);
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                // 把"已经解压好的安装源"直接传过去，提权后的进程不必再解压一遍
                // （安装包正常带 requireAdministrator，这条路只在手工指定了
                //   受保护目录、而进程又恰好没提权时才会走到）
                psi.Arguments = "--pkg \"" + pkg + "\" --dir \"" + target.TrimEnd('\\') + "\" --auto";
                Process p = Process.Start(psi);
                if (p == null) return;
                // 必须等提权那个进程干完再退出：安装源还在我们这个进程名下，
                // 提前退出会让 finally 里的清理把子进程正在用的目录删掉。
                Visible = false;
                ShowInTaskbar = false;
                p.WaitForExit();
                Close();
            }
            catch (Exception ex)
            {
                Log("[注意] 提权重启失败：" + ex.Message);
                MessageBox.Show(this, "提权失败，请换成「文档」或桌面下的文件夹再试。",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void Worker()
        {
            bool ok = false;
            string failMsg = null;
            try
            {
                string exeDst = Path.Combine(target, "Restoria.exe");

                Report(2, "正在准备安装目录…", false);
                if (!Directory.Exists(target)) Directory.CreateDirectory(target);

                // ---------- 复制 ----------
                List<Job> jobs = BuildJobs();
                if (jobs.Count == 0)
                {
                    failMsg = "安装包里没有可复制的文件（包不完整？）";
                }
                else
                {
                    long total = 0;
                    for (int i = 0; i < jobs.Count; i++)
                    {
                        try { total += new FileInfo(jobs[i].Src).Length; } catch { }
                    }
                    if (total <= 0) total = 1;

                    long doneBytes = 0;
                    int lastPct = -1;
                    for (int i = 0; i < jobs.Count; i++)
                    {
                        if (cancel) throw new OperationCanceledException();
                        Job j = jobs[i];
                        string dir = Path.GetDirectoryName(j.Dst);
                        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                        CopyStream(j.Src, j.Dst, ref doneBytes, total, j.Show, ref lastPct);
                    }
                    Log("[成功] 已复制 " + jobs.Count + " 个文件（" + MB(total) + "）");

                    // ---------- 就地核对：不搞"只报成功" ----------
                    if (!File.Exists(exeDst))
                    {
                        failMsg = "安装目录里没有主程序，复制环节没成功";
                    }
                    else
                    {
                        long kb = new FileInfo(exeDst).Length / 1024;
                        if (kb < 40) failMsg = "主程序只有 " + kb + " KB，明显不完整";
                        else Log("[成功] 已核对：Restoria.exe " + kb + " KB 就位");
                        if (hasAi && !File.Exists(Path.Combine(target, "ai\\migan.onnx")))
                        {
                            bool anyModel = Directory.Exists(Path.Combine(target, "ai")) &&
                                            Directory.GetFiles(Path.Combine(target, "ai"), "*.onnx").Length > 0;
                            if (!anyModel) failMsg = "AI 模型没有复制到安装目录";
                        }
                    }
                }

                if (failMsg == null && !cancel)
                {
                    // ---------- VC++ 运行库 ----------
                    Report(61, "正在检查 VC++ 运行库…", true);
                    vcOk = CheckVc();
                    if (hasAi && !vcOk) InstallVc();

                    // ---------- 自检 ----------
                    if (!cancel)
                    {
                        Report(70, "正在自检（加载 AI 运行时与模型，老机器可能要十几秒）…", true);
                        AiSelfCheck();
                    }
                    // ---------- 扫描诊断 ----------
                    if (!cancel)
                    {
                        Report(88, "正在检查扫描设备…", true);
                        ScanDiag();
                    }
                    // ---------- 快捷方式 / 安装标记 / 卸载入口 ----------
                    if (!cancel)
                    {
                        Report(94, "正在创建快捷方式…", false);
                        ShortcutOk = CreateShortcut();
                        if (ShortcutOk)
                            Log("[成功] 已创建快捷方式：桌面 + 开始菜单" +
                                (ShortcutAllUsers ? "（所有用户）" : "（当前用户）") + "，含「卸载」入口");
                        else Log("[注意] 快捷方式创建失败（可手动进入安装目录双击程序）");
                        WriteMarker();
                        CleanOldVersion();
                    }
                    if (!cancel)
                    {
                        Report(97, "正在登记卸载入口…", false);
                        UninstallEntryOk = WriteUninstallEntry();
                        if (UninstallEntryOk)
                            Log("[成功] 已在控制面板登记卸载入口（「程序和功能」里可卸载）");
                        else Log("[注意] 卸载入口登记失败（可在安装目录运行「" + SetupInfo.UninstallerName + "」卸载）");
                    }
                }
                ok = (failMsg == null) && !cancel;
            }
            catch (OperationCanceledException)
            {
                failMsg = "安装被中断";
            }
            catch (Exception ex)
            {
                failMsg = ex.Message;
                Log("[失败] " + ex);
            }

            FinalUi(ok, failMsg);
        }

        void CopyStream(string src, string dst, ref long doneBytes, long total, string show, ref int lastPct)
        {
            byte[] buf = new byte[1024 * 256];
            using (FileStream fi = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (FileStream fo = new FileStream(dst, FileMode.Create, FileAccess.Write))
            {
                int n;
                while ((n = fi.Read(buf, 0, buf.Length)) > 0)
                {
                    if (cancel) throw new OperationCanceledException();
                    fo.Write(buf, 0, n);
                    doneBytes += n;
                    int pct = 5 + (int)(58L * doneBytes / total);
                    if (pct != lastPct)
                    {
                        lastPct = pct;
                        Report(pct, "正在复制 " + show + "…", false);
                        ReportDetail(MB(doneBytes) + " / " + MB(total));
                    }
                }
            }
            try
            {
                File.SetLastWriteTime(dst, File.GetLastWriteTime(src));
            }
            catch { }
        }

        List<Job> BuildJobs()
        {
            List<Job> l = new List<Job>();
            AddJobIfExists(l, Path.Combine(pkg, "Restoria.exe"),
                              Path.Combine(target, "Restoria.exe"), "主程序");
            AddJobIfExists(l, Path.Combine(pkg, "Restoria.exe.config"),
                              Path.Combine(target, "Restoria.exe.config"), "程序配置");
            AddJobIfExists(l, Path.Combine(pkg, "使用说明.txt"),
                              Path.Combine(target, "使用说明.txt"), "使用说明");
            // 卸载器：装到安装目录，控制面板的"卸载"就指向它
            AddJobIfExists(l, Path.Combine(pkg, SetupInfo.UninstallerName),
                              Path.Combine(target, SetupInfo.UninstallerName), "卸载程序");

            string sai = Path.Combine(pkg, "ai");
            string dai = Path.Combine(target, "ai");
            if (hasAi)
            {
                AddJobIfExists(l, Path.Combine(sai, "onnxruntime.dll"),
                                  Path.Combine(dai, "onnxruntime.dll"), "ai\\onnxruntime.dll");
                string[] models = Directory.GetFiles(sai, "*.onnx");
                for (int i = 0; i < models.Length; i++)
                {
                    string fn = Path.GetFileName(models[i]);
                    AddJobIfExists(l, models[i], Path.Combine(dai, fn), "ai\\" + fn);
                }
                // Win7 兼容层：整棵 ai\win7\ 复制（**不再按系统版本挑文件** —— 2026-09-20 修）
                // 根因：为了让 ORT 1.14.1 能在 Win7 跑，onnxruntime.dll 的导入表里 4 个 API 集名
                // 被改成了普通文件名 w7shim-*.dll（tools\prepare_ai_dir.py）。改名落进导入表后，
                // 这 4 个文件就成了**所有系统上的硬依赖**，旧版"只在 Win8 以下复制垫片"于是让
                // Win10/11 缺文件、LoadLibrary 直接 126，提示还误写成"缺 VC++ 运行库"。
                // 现在两套并存，程序按"谁能加载用谁"依次试：ai\onnxruntime.dll 是原版（Win8+ 用），
                // ai\win7\onnxruntime.dll 是补丁版 + 同目录垫片（Win7 用）。
                string sw7 = Path.Combine(sai, "win7");
                if (Directory.Exists(sw7))
                {
                    string[] fs = Directory.GetFiles(sw7);
                    for (int i = 0; i < fs.Length; i++)
                    {
                        string fn = Path.GetFileName(fs[i]);
                        AddJobIfExists(l, fs[i], Path.Combine(dai, "win7", fn), "ai\\win7\\" + fn);
                    }
                    Log("[成功] Win7 兼容层 " + fs.Length + " 个文件将一并复制（Win10/11 不加载它）");
                }
            }
            string socr = Path.Combine(sai, "ocr");
            if (Directory.Exists(socr))
            {
                string[] fs = Directory.GetFiles(socr);
                for (int i = 0; i < fs.Length; i++)
                {
                    string fn = Path.GetFileName(fs[i]);
                    AddJobIfExists(l, fs[i], Path.Combine(dai, "ocr", fn), "ai\\ocr\\" + fn);
                }
            }
            string doc = Path.Combine(pkg, "docs\\AI_SETUP.md");
            if (File.Exists(doc)) AddJobIfExists(l, doc, Path.Combine(target, "AI_SETUP.md"), "AI_SETUP.md");
            return l;
        }

        static void AddJobIfExists(List<Job> l, string src, string dst, string show)
        {
            if (!File.Exists(src)) return;
            Job j = new Job();
            j.Src = src; j.Dst = dst; j.Show = show;
            l.Add(j);
        }

        // ---------- VC++ ----------
        bool vcOk;
        bool ShortcutOk;
        bool ShortcutAllUsers;
        bool UninstallEntryOk;
        bool aiOk;
        bool aiSkipped;
        int wiaCount = -1;

        bool CheckVc()
        {
            string s = Environment.GetFolderPath(Environment.SpecialFolder.System);
            bool ok = File.Exists(Path.Combine(s, "vcruntime140.dll")) &&
                      File.Exists(Path.Combine(s, "msvcp140.dll"));
            if (!hasAi) Log("   未装 AI 素材，VC++ 运行库用不上，跳过");
            else if (ok) Log("[成功] VC++ 运行库已安装");
            return ok;
        }

        void InstallVc()
        {
            string rd = Path.Combine(pkg, "vc_redist\\vc_redist.x64.exe");
            if (!File.Exists(rd))
            {
                Log("[注意] 缺 VC++ 运行库，包内也没有安装程序；AI 模式可能无法启动");
                return;
            }
            Log("   系统缺 VC++ 运行库，正在自动安装（会弹一次权限确认，请点「是」）…");
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(rd);
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                psi.Arguments = "/install /quiet /norestart";
                Process p = Process.Start(psi);
                if (p == null) { Log("[注意] VC++ 运行库安装未启动"); return; }
                if (!p.WaitForExit(180000)) { try { p.Kill(); } catch { } Log("[注意] VC++ 安装超时"); return; }
                if (p.ExitCode == 0 || p.ExitCode == 3010) Log("[成功] VC++ 运行库安装完成");
                else Log("[注意] VC++ 安装返回代码 " + p.ExitCode + "，如 AI 模式报错请手动运行 vc_redist\\vc_redist.x64.exe");
            }
            catch (Exception ex)
            {
                Log("[注意] VC++ 运行库自动安装未完成：" + ex.Message + "（可稍后手动运行 vc_redist\\vc_redist.x64.exe）");
            }
        }

        // ---------- 自检（真把程序跑一次） ----------
        void AiSelfCheck()
        {
            string exe = Path.Combine(target, "Restoria.exe");
            string rep = Path.Combine(Path.GetTempPath(), "PicTool-ai-check.txt");
            try { File.Delete(rep); } catch { }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, "--ai-check-silent");
                psi.UseShellExecute = false;
                psi.WorkingDirectory = target;
                Process p = Process.Start(psi);
                if (p == null) { Log("[失败] 自检进程没起来"); return; }
                if (!p.WaitForExit(120000))
                {
                    try { p.Kill(); } catch { }
                    Log("[注意] 自检超时（2 分钟），已终止");
                    return;
                }
            }
            catch (Exception ex)
            {
                Log("[注意] 自检启动失败：" + ex.Message);
                return;
            }
            if (!File.Exists(rep))
            {
                Log("[失败] 自检没有生成报告，程序可能无法启动");
                return;
            }
            string text = "";
            try { text = File.ReadAllText(rep, Encoding.UTF8); } catch { }
            foreach (string ln in text.Replace("\r\n", "\n").Split('\n'))
                if (ln.Trim().Length > 0) Log("   " + ln);

            aiSkipped = !hasAi;
            aiOk = Regex.IsMatch(text, @"AI\s*可用\s*:\s*是");
            if (aiOk) Log("[成功] 自检通过：AI 修复可用");
            else if (aiSkipped) Log("[成功] 自检通过：程序可正常运行（三模式版，未包含 AI 素材）");
            else Log("[注意] AI 素材已装好，但自检没能用上（多半是缺 VC++ 运行库）");
        }

        // ---------- 扫描就绪诊断 ----------
        void ScanDiag()
        {
            string exe = Path.Combine(target, "Restoria.exe");
            string rep = Path.Combine(Path.GetTempPath(), "PicTool-scan-diag.txt");
            try { File.Delete(rep); } catch { }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, "--scan-diag --silent");
                psi.UseShellExecute = false;
                psi.WorkingDirectory = target;
                Process p = Process.Start(psi);
                if (p == null) return;
                if (!p.WaitForExit(60000)) { try { p.Kill(); } catch { } }
            }
            catch { return; }
            if (!File.Exists(rep)) { Log("[注意] 扫描诊断没有生成报告（不拦安装，之后可手动运行 --scan-diag）"); return; }
            string text = "";
            try { text = File.ReadAllText(rep, Encoding.UTF8); } catch { }
            Match m = Regex.Match(text, @"WIA\s*设备\s*:\s*([0-9]+)");
            if (m.Success)
            {
                wiaCount = 0;
                try { wiaCount = int.Parse(m.Groups[1].Value); } catch { }
            }
            if (wiaCount > 0) Log("[成功] 扫描就绪：本机能看到 " + wiaCount + " 个扫描设备");
            else Log("[注意] 扫描未就绪：本机 WIA 看不到任何扫描设备（装好后可再查一次）");
        }

        // ---------- 快捷方式（WScript.Shell，必须在 STA 的 UI 线程上做） ----------
        // 标准商业安装：桌面（所有用户）+ 开始菜单文件夹（所有用户，含「卸载」入口）。
        // 先试"所有用户"，失败再退到"当前用户"（比如目标机组策略锁了公共桌面）。
        bool CreateShortcut()
        {
            bool ok = false;
            MethodInvoker mi = new MethodInvoker(delegate () { ok = CreateShortcutsCore(); });
            try { Invoke(mi); } catch { }
            return ok;
        }

        bool CreateShortcutsCore()
        {
            string exe = Path.Combine(target, SetupInfo.MainExe);
            string uninst = Path.Combine(target, SetupInfo.UninstallerName);
            bool all = true;

            // ① 桌面
            string desk = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                SetupInfo.DesktopShortcut);
            if (!MakeLink(desk, exe, target, SetupInfo.Name + " " + appVer))
            {
                all = false;
                desk = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    SetupInfo.DesktopShortcut);
                if (!MakeLink(desk, exe, target, SetupInfo.Name + " " + appVer)) return false;
            }

            // ② 开始菜单（一个文件夹，两项：程序 + 卸载）
            string menuBase = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                "Programs", SetupInfo.StartMenuFolder);
            try { if (!Directory.Exists(menuBase)) Directory.CreateDirectory(menuBase); }
            catch { }
            if (!MakeLink(Path.Combine(menuBase, SetupInfo.Name + ".lnk"), exe, target,
                          SetupInfo.Name + " " + appVer))
            {
                all = false;
                menuBase = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                    "Programs", SetupInfo.StartMenuFolder);
                try { if (!Directory.Exists(menuBase)) Directory.CreateDirectory(menuBase); }
                catch { }
                MakeLink(Path.Combine(menuBase, SetupInfo.Name + ".lnk"), exe, target,
                         SetupInfo.Name + " " + appVer);
            }
            if (File.Exists(uninst))
                MakeLink(Path.Combine(menuBase, "卸载 " + SetupInfo.Name + ".lnk"), uninst,
                         target, "卸载 " + SetupInfo.Name);

            ShortcutAllUsers = all;
            return true;
        }

        /// <summary>用 WScript.Shell 建一个 .lnk。返回文件是否真的存在。</summary>
        static bool MakeLink(string lnkPath, string targetPath, string workDir, string desc)
        {
            object sh = null, lnk = null;
            try
            {
                string dir = Path.GetDirectoryName(lnkPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return false;
                sh = Activator.CreateInstance(t);
                lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh,
                                     new object[] { lnkPath });
                Type lt = lnk.GetType();
                lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { targetPath });
                lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { workDir });
                lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { desc });
                lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
                return File.Exists(lnkPath);
            }
            catch { return false; }
            finally
            {
                try { if (lnk != null) Marshal.ReleaseComObject(lnk); } catch { }
                try { if (sh != null) Marshal.ReleaseComObject(sh); } catch { }
            }
        }

        // ---------- 注册表卸载入口（控制面板「程序和功能」） ----------
        // 两个视图都写：64 位系统上程序可能被以 32 位/64 位不同方式打开，
        // 只写一个视图时偶尔会出现"控制面板看不到"的情况。
        bool WriteUninstallEntry()
        {
            bool any = false;
            any |= WriteUninstallKey(RegistryView.Registry64);
            any |= WriteUninstallKey(RegistryView.Registry32);
            return any;
        }

        bool WriteUninstallKey(RegistryView view)
        {
            try
            {
                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (RegistryKey k = baseKey.CreateSubKey(SetupInfo.UninstallKeyPath))
                {
                    if (k == null) return false;
                    long sizeKb = 0;
                    try
                    {
                        foreach (string f in Directory.GetFiles(target, "*", SearchOption.AllDirectories))
                            sizeKb += new FileInfo(f).Length / 1024;
                    }
                    catch { }

                    k.SetValue("DisplayName", SetupInfo.Name, RegistryValueKind.String);
                    k.SetValue("DisplayVersion", SetupInfo.Version, RegistryValueKind.String);
                    k.SetValue("Publisher", SetupInfo.Publisher, RegistryValueKind.String);
                    k.SetValue("InstallLocation", target, RegistryValueKind.String);
                    k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"), RegistryValueKind.String);
                    k.SetValue("DisplayIcon", Path.Combine(target, SetupInfo.MainExe), RegistryValueKind.String);
                    k.SetValue("UninstallString",
                        "\"" + Path.Combine(target, SetupInfo.UninstallerName) + "\"", RegistryValueKind.String);
                    k.SetValue("QuietUninstallString",
                        "\"" + Path.Combine(target, SetupInfo.UninstallerName) + "\" --silent",
                        RegistryValueKind.String);
                    k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    k.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, sizeKb), RegistryValueKind.DWord);
                    if (SetupInfo.HomePage.Length > 0)
                        k.SetValue("URLInfoAbout", SetupInfo.HomePage, RegistryValueKind.String);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log("[注意] 写注册表卸载入口失败（" + view + "）：" + ex.Message);
                return false;
            }
        }

        /// <summary>安装标记文件：卸载器靠它确认"这个目录确实是我们装的"，避免误删同名目录。</summary>
        void WriteMarker()
        {
            try
            {
                string[] lines = new string[] {
                    "product=" + SetupInfo.Name,
                    "version=" + SetupInfo.Version,
                    "appid=" + SetupInfo.AppId,
                    "publisher=" + SetupInfo.Publisher,
                    "installed=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    "source=" + osText
                };
                File.WriteAllLines(Path.Combine(target, SetupInfo.MarkerFile), lines, new UTF8Encoding(false));
            }
            catch (Exception ex) { Log("[注意] 写安装标记失败：" + ex.Message); }
        }

        // ---------- 清掉旧版（图片工具箱）：只动我们自己的目录，且走回收站 ----------
        void CleanOldVersion()
        {
            try
            {
                string oldDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                             "图片工具箱");
                // 必须看到我们自己的旧 exe 才认，避免误删同名文件夹
                if (Directory.Exists(oldDir) && File.Exists(Path.Combine(oldDir, "图片工具箱.exe")))
                {
                    if (Native.Recycle(oldDir)) Log("[成功] 旧版安装目录「图片工具箱」已移入回收站");
                    else Log("[注意] 旧版目录「" + oldDir + "」没能移入回收站，可手动删除（不影响使用）");
                }
            }
            catch (Exception ex) { Log("[注意] 旧版目录处理跳过：" + ex.Message); }

            try
            {
                string oldLnk = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "图片工具箱.lnk");
                if (File.Exists(oldLnk)) { File.Delete(oldLnk); Log("[成功] 已清理旧版桌面快捷方式"); }
            }
            catch { }
        }

        // 收尾 ----------
        void CloseLater()
        {
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 600;
            t.Tick += delegate { t.Stop(); t.Dispose(); Close(); };
            t.Start();
        }

        void FinalUi(bool ok, string failMsg)
        {
            try
            {
                Invoke(new MethodInvoker(delegate
                {
                    running = false;
                    bar.Marquee = false;
                    if (ok)
                    {
                        done = true;
                        steps.Step = 3;
                        bar.Value = 100;
                        lblStatus.Text = "安装完成";
                        lblStatus.Font = Theme.F13B;
                        lblDetail.Text = "程序位置：" + target;
                        txtPath.ReadOnly = true;
                        btnGo.Text = "打开程序";
                        btnGo.Enabled = true;
                        btnCancel.Text = "关闭";
                        btnBrowse.Enabled = false;
                        lnkLog.Text = logShown ? "隐藏详细日志  ▴" : "显示详细日志  ▾";
                        ShowResultCard();
                        Log("");
                        Log("[成功] 安装完成。程序位置: " + Path.Combine(target, "Restoria.exe"));
                    }
                    else
                    {
                        steps.Step = 1;
                        bar.Value = 0;
                        bar.Visible = false;
                        lblStatus.Text = cancel ? "安装已中断" : "安装没有完成";
                        lblStatus.ForeColor = Theme.Danger;
                        lblDetail.Text = failMsg == null ? "" : failMsg;
                        if (!cancel)
                        {
                            txtPath.ReadOnly = false;
                            btnBrowse.Enabled = true;
                            btnGo.Text = "重新安装";
                            btnGo.Enabled = true;
                            btnCancel.Text = "关闭";
                            btnCancel.Enabled = true;
                            clientFailed();
                        }
                        else
                        {
                            btnGo.Text = "重新安装";
                            btnGo.Enabled = true;
                            btnCancel.Text = "关闭";
                            txtPath.ReadOnly = false;
                            btnBrowse.Enabled = true;
                        }
                        Log("");
                        Log("[失败] " + (failMsg == null ? "安装未完成" : failMsg));
                        Log("   日志文件: " + logPath);
                    }
                    if (!logShown) txtLog.SelectionStart = txtLog.TextLength;
                    txtLog.ScrollToCaret();
                    txtLog.SelectionStart = txtLog.TextLength;
                    if (autoExit) CloseLater();
                }));
            }
            catch { }
        }

        void clientFailed()
        {
            try
            {
                MessageBox.Show(this,
                    "安装没有完成：" + (lblDetail.Text == "" ? "原因未知" : lblDetail.Text) +
                    "\r\n\r\n日志文件：" + logPath + "\r\n把它发给维护者即可定位。",
                    "安装未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch { }
        }

        void ShowResultCard()
        {
            Color ok = Theme.Green, warn = Theme.Warn, bad = Theme.Danger;
            string[] rows = new string[4];
            Color[] dots = new Color[4];

            long kb = 0;
            try { kb = new FileInfo(Path.Combine(target, "Restoria.exe")).Length / 1024; } catch { }
            rows[0] = "程序文件：Restoria " + appVer + " 已安装（" + kb + " KB）";
            dots[0] = ok;

            if (aiSkipped)
            {
                rows[1] = "AI 修复：未包含素材（普通/增强/混合三模式版，功能正常）";
                dots[1] = warn;
            }
            else if (aiOk)
            {
                rows[1] = "AI 修复：可用（运行时与模型都加载成功）";
                dots[1] = ok;
            }
            else
            {
                rows[1] = "AI 修复：素材已装但未生效，多半是缺 VC++ 运行库";
                dots[1] = bad;
            }

            if (wiaCount > 0)
            {
                rows[2] = "扫描就绪：本机能看到 " + wiaCount + " 个扫描设备";
                dots[2] = ok;
            }
            else if (wiaCount == 0)
            {
                rows[2] = "扫描未就绪：本机看不到扫描设备（不影响图片处理）";
                dots[2] = warn;
            }
            else
            {
                rows[2] = "扫描就绪：未取得诊断结果（可再手动运行 --scan-diag）";
                dots[2] = warn;
            }

            // 第 4 行把"快捷方式"和"卸载入口"合并说清楚：卡片高度是定死的（见 BuildUi），
            // 多一行会把下面那排控件顶掉。
            if (ShortcutOk && UninstallEntryOk)
            {
                rows[3] = "快捷方式与卸载：桌面/开始菜单已建立，控制面板可卸载";
                dots[3] = ok;
            }
            else if (ShortcutOk)
            {
                rows[3] = "快捷方式：已建立；卸载入口未登记（可运行安装目录里的卸载程序）";
                dots[3] = warn;
            }
            else if (UninstallEntryOk)
            {
                rows[3] = "卸载入口：已登记；快捷方式未建立，可进安装目录双击程序";
                dots[3] = warn;
            }
            else
            {
                rows[3] = "快捷方式与卸载入口：均未建立，可进安装目录直接运行程序";
                dots[3] = warn;
            }

            card.SetRows(dots, rows);
        }

        // ================= 日志与进度上报（线程安全） =================
        readonly object logLock = new object();

        void Log(string line)
        {
            try
            {
                lock (logLock)
                    File.AppendAllText(logPath, line + "\r\n", new UTF8Encoding(false));
            }
            catch { }
            AppendLogBox(line);
        }

        void AppendLogBox(string line)
        {
            try
            {
                if (!IsHandleCreated) return;
                BeginInvoke(new MethodInvoker(delegate
                {
                    txtLog.AppendText(line + "\r\n");
                }));
            }
            catch { }
        }

        int lastPct = -1;
        long lastReportTick;

        void Report(int pct, string text, bool marquee)
        {
            long now = Environment.TickCount;
            if (!marquee && pct == lastPct && (now - lastReportTick) < 250) return;
            lastPct = pct;
            lastReportTick = now;
            try
            {
                if (!IsHandleCreated) return;
                BeginInvoke(new MethodInvoker(delegate
                {
                    bar.Marquee = marquee;
                    if (!marquee) bar.Value = pct;
                    if (text != null) lblStatus.Text = text;
                }));
            }
            catch { }
        }

        void ReportDetail(string text)
        {
            try
            {
                if (!IsHandleCreated) return;
                BeginInvoke(new MethodInvoker(delegate { lblDetail.Text = text; }));
            }
            catch { }
        }
    }

    static class Entry
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string pkg = null;          // 安装源目录
            string extracted = null;    // 本进程解压出来的临时目录（收尾要删）
            string dir = null;
            bool auto = false;
            bool autoExit = false;
            string shot = null;
            string shotEula = null;
            string shotState = "ready";
            string listing = null;
            string selfExtract = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--dir" && i + 1 < args.Length) { dir = args[i + 1]; i++; }
                else if (args[i] == "--pkg" && i + 1 < args.Length) { pkg = args[i + 1]; i++; }
                else if (args[i].StartsWith("--list-payload=")) listing = args[i].Substring(15);
                else if (args[i].StartsWith("--self-extract=")) selfExtract = args[i].Substring(15);
                else if (args[i] == "--auto") auto = true;
                else if (args[i] == "--exit") autoExit = true;
                else if (args[i].StartsWith("--shot=")) shot = args[i].Substring(7);
                else if (args[i].StartsWith("--shot-eula=")) shotEula = args[i].Substring(12);
                else if (args[i].StartsWith("--shot-state=")) shotState = args[i].Substring(13);
            }

            string self = Application.ExecutablePath;

            // 打包脚本用它核对"包内到底装了什么"（走的就是安装时那条读取代码）
            if (listing != null) { Boot.WriteListing(self, listing); return; }

            // 诊断用：把许可协议页渲染成 PNG（改文案后看一眼，不用真去点安装）
            if (shotEula != null)
            {
                using (EulaForm ef = new EulaForm())
                {
                    ef.StartPosition = FormStartPosition.Manual;
                    ef.Location = new Point(0, 0);
                    ef.Show();
                    Application.DoEvents();
                    Thread.Sleep(250);
                    Application.DoEvents();
                    Bitmap b = new Bitmap(ef.ClientSize.Width, ef.ClientSize.Height);
                    try
                    {
                        ef.DrawToBitmap(b, new Rectangle(0, 0, b.Width, b.Height));
                        b.Save(shotEula, System.Drawing.Imaging.ImageFormat.Png);
                    }
                    catch { }
                    finally { b.Dispose(); }
                    ef.Close();
                }
                return;
            }

            // 诊断用：把载荷原样解压到指定目录后退出（不加界面，供打包/门禁脚本验证解压链路）
            if (selfExtract != null)
            {
                try
                {
                    Payload.Extract(self, selfExtract, null);
                    return;
                }
                catch (Exception ex)
                {
                    File.WriteAllText(selfExtract.TrimEnd('\\') + ".err",
                        "解压失败: " + ex, new UTF8Encoding(false));
                    Environment.ExitCode = 3;
                    return;
                }
            }

            // 许可协议：正式发行的第一屏（UAC 之后、解压之前）。
            // --auto 是"提权重装/自动化"那条路，视为已经接受；--shot 是诊断，不弹。
            if (!auto && shot == null)
            {
                using (EulaForm ef = new EulaForm())
                {
                    if (ef.ShowDialog() != DialogResult.OK)
                    {
                        Boot.Cleanup(extracted);
                        return;
                    }
                }
            }

            // 安装源三选一：
            //   ① 命令行指定（提权重装时父进程把已解压目录传进来；开发调试也是它）
            //   ② exe 自带载荷 → 白底进度窗解压到临时目录（正式发行的路径）
            //   ③ 都没有 → 就当普通安装器，用 exe 所在目录（开发态）
            try
            {
                if (!string.IsNullOrEmpty(pkg)) { }
                else if (Boot.HasPayload(self))
                {
                    extracted = Boot.NewExtractDir();
                    Boot.ExtractWithUi(self, extracted);
                    if (!File.Exists(Path.Combine(extracted, "Restoria.exe")))
                        throw new InvalidDataException("解压完成，但安装源里没有主程序（安装包可能不完整）。");
                    pkg = extracted;
                }
                else
                {
                    pkg = AppDomain.CurrentDomain.BaseDirectory;
                }
            }
            catch (Exception ex)
            {
                Boot.Cleanup(extracted);
                MessageBox.Show("准备安装文件时出错：\r\n" + ex.Message +
                    "\r\n\r\n请重新获取安装包后再试。",
                    "Restoria", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // 诊断用：把某个界面状态渲染成 PNG 后退出，不依赖屏幕截图，可无头运行。
            // 改界面后逐个状态看一眼，比开窗口肉眼验收可靠。
            if (shot != null) { Shot(pkg, shot, shotState); Boot.Cleanup(extracted); return; }

            try
            {
                Application.Run(new SetupForm(pkg, dir, auto, autoExit));
            }
            catch (Exception ex)
            {
                MessageBox.Show("安装程序启动失败：" + ex.Message, "Restoria",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                // 收尾：向导退出后才删（提权重装时父进程会等提权子进程干完，
                // 顺序正确，临时目录不会被子进程"用着用着没了"）
                Boot.Cleanup(extracted);
            }
        }

        static void Shot(string pkg, string path, string state)
        {
            SetupForm f = new SetupForm(pkg, pkg, false, false);
            f.Demo(state);
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(0, 0);
            f.Show();
            Application.DoEvents();
            Thread.Sleep(300);
            Application.DoEvents();
            Bitmap bmp = new Bitmap(f.ClientSize.Width, f.ClientSize.Height);
            try
            {
                f.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            catch { }
            finally { bmp.Dispose(); }
            f.Close();
        }
    }
}
