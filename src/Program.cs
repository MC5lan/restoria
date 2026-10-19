// Program.cs — 入口与全局异常兜底
using System;
using System.IO;
using System.Windows.Forms;

namespace PicTool
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            WriteEnvReport();          // 每次启动都更新运行环境报告（换机器排查用）
            CleanStaleTemp();          // 清理上次异常退出残留的临时目录

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
            {
                LogError(e.Exception);
                MessageBox.Show("程序遇到一个问题但已恢复。\n\n" + e.Exception.Message +
                    "\n\n如果反复出现，请把日志发给维护者：" + LogPath(),
                    "Restoria", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Exception ex = e.ExceptionObject as Exception;
                if (ex != null) LogError(ex);
            };

            // 诊断开关：PICTOOL_AI_TRACE=1 时把 ONNX Runtime 互操作每一步写进
            // %TEMP%\PicTool-ort-trace.txt（排查"跑到哪一步崩"用）
            try { if (Environment.GetEnvironmentVariable("PICTOOL_AI_TRACE") == "1") AiFill.Trace = true; } catch { }

            string startPath = (args != null && args.Length > 0) ? args[0] : null;
            if (startPath == "--ai-check" || startPath == "--ai-check-silent")
            {
                // AI 模式自检：加载运行时与模型、打印张量规格，结果写入临时文件
                // 安装程序用 --ai-check-silent：不弹窗，跑完即退出，便于无人值守核对
                string report = AiFill.Check();
                string path = Path.Combine(Path.GetTempPath(), "PicTool-ai-check.txt");
                try { File.WriteAllText(path, report, System.Text.Encoding.UTF8); } catch { }
                if (startPath == "--ai-check")
                    MessageBox.Show(report + "\n\n已保存到:\n" + path, "AI 修复自检", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (startPath == "--ort-probe")
            {
                // 隔离探测子进程（由 AiFill.ProbeRuntime 拉起来）：在本进程里真加载一次
                // onnxruntime.dll，把结论写进报告文件后退出。
                // 它要是被系统在加载期打死，父进程读不到报告文件就判定"本机不安全"，
                // 于是主程序永远不在自己进程里加载 ORT —— 整个程序不会因为 AI 打不开。
                string msg = AiFill.ProbeInProcess();
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "PicTool-ort-probe.txt"), msg, System.Text.Encoding.UTF8); } catch { }
                Environment.ExitCode = (msg != null && msg.StartsWith("OK")) ? 0 : 1;
                return;
            }
            if (startPath == "--ai-fill" && args.Length >= 3)
            {
                // 诊断用：--ai-fill <输入图> <输出图> [x y w h] [WorkSide]
                // 造一块"污渍"跑真实修复管线并落盘，用于离线比对画质与耗时
                string rep = AiFill.FillForTest(args[1], args[2], args);
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "PicTool-ai-fill.txt"), rep, System.Text.Encoding.UTF8); } catch { }
                return;
            }
            if (startPath == "--paper-fill" && args.Length >= 3)
            {
                // 诊断用：--paper-fill <输入图> <输出图> [x y w h]
                // 在整幅图上造一块矩形掩膜跑纸面重建并落盘，用于离线比对画质与耗时
                string rep = PaperFill.FillForTest(args[1], args[2], args);
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "PicTool-paper-fill.txt"), rep, System.Text.Encoding.UTF8); } catch { }
                return;
            }
            if (startPath == "--archive")
            {
                // 诊断/批处理用：--archive <输入文件或文件夹> <输出文件或文件夹> [强制dpi] [选项]
                // 一键归档流水线：转正 → 裁边 → 光照归一化 → 自动色阶 → 白底黑字 → 去黑点 → 标准 A4
                string rep = ArchiveProcessor.RunForTest(args);
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "PicTool-archive.txt"), rep, System.Text.Encoding.UTF8); } catch { }
                return;
            }
            if (startPath == "--scan-list" && args.Length >= 2)
            {
                // 32 位辅助进程的枚举模式（由 Scanner 在 64 位枚举为空时自动拉起）
                Scanner.RunListMode(args[1]);
                Environment.ExitCode = 0;
                return;
            }
            if (startPath == "--scan-run" && args.Length >= 2)
            {
                // 32 位辅助进程的扫描模式：参数与结果都走文件
                string r = Scanner.RunScanMode(args[1]);
                Environment.ExitCode = (r != null && r.IndexOf("ERR|") < 0) ? 0 : 1;
                return;
            }
            if (startPath == "--scan-diag")
            {
                // 扫描链路诊断：为什么看不到扫描仪？结果写入临时文件并弹窗
                string report = Scanner.Diagnose();
                string path = Path.Combine(Path.GetTempPath(), "PicTool-scan-diag.txt");
                try { File.WriteAllText(path, report, System.Text.Encoding.UTF8); } catch { }
                if (args.Length < 2 || args[1] != "--silent")
                    MessageBox.Show(report + "\n\n已保存到:\n" + path, "扫描诊断", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Environment.ExitCode = 0;
                return;
            }
            if (startPath == "--scan-props")
            {
                // 诊断用：--scan-props [设备序号] 把某台设备的全部属性倒出来
                int idx = 1;
                if (args.Length >= 2) { try { idx = int.Parse(args[1]); } catch { idx = 1; } }
                string rep = Scanner.DumpDevice(idx);
                string path = Path.Combine(Path.GetTempPath(), "PicTool-scan-props.txt");
                try { File.WriteAllText(path, rep, System.Text.Encoding.UTF8); } catch { }
                if (args.Length < 2 || args[1] != "--silent")
                    MessageBox.Show(rep + "\n\n已保存到:\n" + path, "设备属性", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (startPath == "--bench")
            {
                // P3 算法提速的基准：逐算子计时 + 产物指纹，
                // 一次运行同时回答"快了没有"和"改样了没有"。
                // 报告落 %TEMP%\PicTool-bench.txt（本进程是 winexe，没有控制台可读）；
                // 指纹基准文件 tools\bench-golden.txt，重建用 --bench --regen。
                string rep = Bench.RunAll(args);
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "PicTool-bench.txt"), rep, System.Text.Encoding.UTF8); } catch { }
                return;
            }
            if (startPath == "--make-fixture")
            {
                // 导出确定性合成测试页，供门禁与评测集使用。
                // 门禁的 AI 产物与性能冒烟都需要一张"固定测试页"；以前靠仓库外
                // 手工放置的 _probe\doc_test.png，别人 clone 下来没有，门禁只能
                // 跳过（skip 被当成通过）。改成由本程序生成，入库的那张与
                // --bench 用的是同一个 Fixture()，不存在漂移。
                string outPath = (args.Length > 1) ? args[1] : Path.Combine("_probe", "doc_test.png");
                string rep = Bench.ExportFixture(outPath);
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "PicTool-fixture.txt"), rep, System.Text.Encoding.UTF8); } catch { }
                return;
            }
            if (startPath == "--skew-test")
            {
                // 诊断用：在真实样本上验证倾斜检测的"黑边保险" ——
                // 既要证明正常页逐位不变，也要证明有黑边的图能救回来。
                // 用法：--skew-test <图片或文件夹> [页数]，报告落 %TEMP%\PicTool-skewtest.txt
                string rep = SkewProbe.RunAll(args);
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "PicTool-skewtest.txt"), rep, System.Text.Encoding.UTF8); } catch { }
                return;
            }
            if (startPath == "--selftest")
            {
                // 无界面自检：核心算法回归，实现见 src\Core\SelfTest.cs
                string rep = SelfTest.RunAll();
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "PicTool-selftest.txt"), rep, System.Text.Encoding.UTF8); } catch { }
                Environment.ExitCode = SelfTest.FailCount == 0 ? 0 : 1;
                return;
            }
            if (startPath == "--fstest")
            {
                // 诊断用：验证"删除到回收站"在本机可用。
                // 不放进常跑的自检 —— 每次自检都往回收站塞临时文件对用户不礼貌。
                try
                {
                    string tf = Path.Combine(Path.GetTempPath(), "PicTool-fstest-" + DateTime.Now.Ticks + ".txt");
                    File.WriteAllText(tf, "recycle test");
                    bool before = File.Exists(tf);
                    bool ok = FileTools.DeleteToRecycleBin(tf);
                    bool after = File.Exists(tf);
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "PicTool-fstest.txt"),
                        "created=" + before + " deleteOk=" + ok + " stillExists=" + after, System.Text.Encoding.UTF8);
                }
                catch { }
                return;
            }
            if (startPath != null && (startPath == "--wheeltest" || startPath.StartsWith("--wheeltest=")))
            {
                // 诊断用：验证"滚轮消息 → 统一路由 → 各区滚动"整条链路。
                // 无头打开窗体 → 程序化注入与真实滚轮相同的 WM_MOUSEWHEEL →
                // 读三区（画布/左栏/右栏）反应 → 写报告退出。
                // 支持 --wheeltest=<文件夹>：带上样本目录才能测左栏工作区
                //（无列表时左栏如实报"跳过"）。
                string wsSample = startPath.Length > 12 ? startPath.Substring(12).Trim('"') : null;
                MainForm f = new MainForm(null);
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new System.Drawing.Point(0, 0);
                f.WindowState = FormWindowState.Normal;
                f.ClientSize = new System.Drawing.Size(1080, 680);
                f.Show();
                Application.DoEvents();
                System.Threading.Thread.Sleep(300);
                Application.DoEvents();
                if (wsSample != null && wsSample.Length > 0 && Directory.Exists(wsSample))
                {
                    f.OpenFolderForProbe(wsSample);   // 加载样本目录 → 左栏有列表可测
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(600);
                    Application.DoEvents();
                }
                string rep = f.WheelTest();
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "PicTool-wheeltest.txt"), rep, System.Text.Encoding.UTF8); } catch { }
                f.Close();
                return;
            }
            if (startPath == "--undotest")
            {
                // 诊断用：撤销链全流程自检（根因曾是 UNDO_MAX=2）。报告落 %TEMP%\PicTool-undotest.txt。
                // 两个坑：① 探针会写恢复快照，非正常退出留下会话后，下次启动
                // OnShown 的 CheckRecovery 会弹"要恢复吗"卡死无头运行 —— 启动前预清；
                // ② Close 后偶发退出码 1（恢复 timer 退出竞态）—— 自检口直接 Environment.Exit(0)。
                //
                // 预清范围：只清**本次探针自己的沙箱**（%TEMP% 下的独立目录），
                // 绝不碰 %LOCALAPPDATA%\PicTool\recovery —— 那里是真实用户的
                // 未完成恢复快照，发布版 exe 有和用户一样的权限，删了就找不回来。
                try
                {
                    string sandbox = Path.Combine(Path.GetTempPath(), "PicTool-probe-recovery");
                    Environment.SetEnvironmentVariable("PICTOOL_RECOVERY_ROOT", sandbox);
                    if (Directory.Exists(sandbox))
                    {
                        try { Directory.Delete(sandbox, true); } catch { }
                    }
                }
                catch { }
                MainForm f = new MainForm(null);
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new System.Drawing.Point(0, 0);
                f.WindowState = FormWindowState.Normal;
                f.ClientSize = new System.Drawing.Size(1080, 680);
                f.Show();
                Application.DoEvents();
                System.Threading.Thread.Sleep(200);
                Application.DoEvents();
                string rep = f.UndoProbe();
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "PicTool-undotest.txt"), rep, System.Text.Encoding.UTF8); } catch { }
                try { f.Close(); } catch { }
                Environment.Exit(0);
            }
            if (startPath == "--cursortest")
            {
                // 诊断用：笔刷/橡皮光标圈自检（重画后验证）。
                // 报告 %TEMP%\PicTool-cursortest.txt、画布截图 %TEMP%\PicTool-cursor.png。
                // 同 --undotest：恢复区重定向到 %TEMP% 沙箱，不碰真实用户的恢复快照。
                try
                {
                    string sandbox = Path.Combine(Path.GetTempPath(), "PicTool-probe-recovery");
                    Environment.SetEnvironmentVariable("PICTOOL_RECOVERY_ROOT", sandbox);
                    if (Directory.Exists(sandbox))
                    {
                        try { Directory.Delete(sandbox, true); } catch { }
                    }
                }
                catch { }
                MainForm f = new MainForm(null);
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new System.Drawing.Point(0, 0);
                f.WindowState = FormWindowState.Normal;
                f.ClientSize = new System.Drawing.Size(1080, 680);
                f.Show();
                Application.DoEvents();
                System.Threading.Thread.Sleep(200);
                Application.DoEvents();
                string rep = f.CursorProbe();
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "PicTool-cursortest.txt"), rep, System.Text.Encoding.UTF8); } catch { }
                try { f.Close(); } catch { }
                Environment.Exit(0);
            }
            if (startPath == "--droptest")
            {
                // 诊断用：验证"拖放接收方都已开口"。拖放事件不冒泡，
                // 接收方必须自己 AllowDrop=true —— 之前只有主窗体开了，
                // 实际拖到窗口任何位置都进不去。报告落 %TEMP%\PicTool-droptest.txt。
                MainForm f = new MainForm(null);
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new System.Drawing.Point(0, 0);
                f.WindowState = FormWindowState.Normal;
                f.ClientSize = new System.Drawing.Size(1080, 680);
                f.Show();
                Application.DoEvents();
                System.Threading.Thread.Sleep(200);
                Application.DoEvents();
                string rep = f.DropProbe();
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "PicTool-droptest.txt"), rep, System.Text.Encoding.UTF8); } catch { }
                f.Close();
                return;
            }
            if (startPath == "--uityping")
            {
                // 诊断用：验证"焦点在输入框里时，全局快捷键不会抢走按键"这一规则。
                // 本程序的输入面有两类"壳里包着真 TextBox"的复合控件（FlatTextBox / 画布文字卡片），
                // 旧做法靠 ActiveControl 链会漏判。报告落 %TEMP%\PicTool-uityping.txt。
                MainForm f = new MainForm(null);
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new System.Drawing.Point(0, 0);
                f.WindowState = FormWindowState.Normal;
                f.ClientSize = new System.Drawing.Size(1080, 680);
                f.Show();
                f.Activate();
                Application.DoEvents();
                System.Threading.Thread.Sleep(300);
                Application.DoEvents();
                string rep = f.UiTypingProbe();
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "PicTool-uityping.txt"), rep, System.Text.Encoding.UTF8); } catch { }
                f.Close();
                return;
            }
            if (startPath == "--layout")
            {
                // 诊断用：输出所有控件实际布局后退出
                MainForm f = new MainForm(null);
                f.DumpLayout();
                return;
            }
            if (startPath == "--shot-shortcuts")
            {
                // 诊断用：把「快捷键设置」对话框渲染成 PNG，便于修改后核对效果。
                // 与 --shot=N 同一套路：不依赖屏幕截图，可无头运行。
                ShortcutSettingsForm sf = new ShortcutSettingsForm();
                sf.StartPosition = FormStartPosition.Manual;
                sf.Location = new System.Drawing.Point(0, 0);
                sf.Show();
                Application.DoEvents();
                System.Threading.Thread.Sleep(300);
                Application.DoEvents();
                System.Drawing.Bitmap sshot = new System.Drawing.Bitmap(sf.ClientSize.Width, sf.ClientSize.Height);
                try
                {
                    sf.DrawToBitmap(sshot, new System.Drawing.Rectangle(0, 0, sshot.Width, sshot.Height));
                    Imaging.SaveImage(sshot, Path.Combine(Path.GetTempPath(), "PicTool-shot-shortcuts.png"), 95);
                }
                catch { }
                finally { sshot.Dispose(); }
                sf.Close();
                return;
            }
            if (startPath != null && startPath.StartsWith("--shot="))
            {
                // 诊断用：把第 N 页渲染成 PNG 存到临时目录，
                // 用于改 UI 后的逐页人工/程序化核对（不依赖屏幕截图，可无头运行）
                int pg = 0;
                int.TryParse(startPath.Substring(7), out pg);
                MainForm f = new MainForm(null);
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new System.Drawing.Point(0, 0);
                f.WindowState = FormWindowState.Normal;      // 诊断模式固定尺寸（最大化会随屏幕变化）
                f.ClientSize = new System.Drawing.Size(1080, 680);
                f.Show();
                f.ShotPage(pg);
                Application.DoEvents();
                System.Threading.Thread.Sleep(400);
                Application.DoEvents();
                // 模拟鼠标悬停在画布上：自绘光标圈只在真实悬停时出现，否则截图里没有它；
                // 现在每张页面截图都带光标圈，避免这一盲区。
                // 坐标必须在画布可视区内（画布约 660px 宽，x=700 会被裁掉）。
                f.SimulateCursor(new System.Drawing.Point(400, 300));
                Application.DoEvents();
                System.Threading.Thread.Sleep(120);
                Application.DoEvents();
                System.Drawing.Bitmap shot = new System.Drawing.Bitmap(f.Width, f.Height);
                try
                {
                    f.DrawToBitmap(shot, new System.Drawing.Rectangle(0, 0, f.Width, f.Height));
                    string outp = Path.Combine(Path.GetTempPath(), "PicTool-shot-" + pg + ".png");
                    Imaging.SaveImage(shot, outp, 95);
                }
                catch { }
                finally { shot.Dispose(); }
                f.Close();
                return;
            }
            Recovery.Start();            // 异常恢复：先立好恢复区，后面所有编辑都会被记快照
            Diag.Info("启动", "程序启动，恢复区已就绪");
            Application.Run(new MainForm(startPath));
        }

        // 运行环境报告（覆盖写，放在临时目录，不影响程序目录权限）
        public static string EnvPath()
        {
            return Path.Combine(Path.GetTempPath(), "PicTool-env.txt");
        }

        static void WriteEnvReport()
        {
            try
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.AppendLine(AppInfo.Name + " 运行环境报告   版本 " + AppInfo.Version);
                sb.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                string osBits = SysInfo.Is64BitProcess ? "64 位系统" : (Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432") != null ? "64 位系统（程序按 32 位运行）" : "32 位系统");   // .NET 2.0 没有 Is64BitOperatingSystem
                sb.AppendLine("操作系统: " + Environment.OSVersion.VersionString + "  " + osBits);
                sb.AppendLine("托管运行时: CLR " + SysInfo.ClrVersion + "  " + SysInfo.ClrName);
                sb.AppendLine(SysInfo.Describe());
                sb.AppendLine("AI 修复  : " + AiFill.StatusText);
                sb.AppendLine(".NET 4.x : " + SysInfo.DotNet4x);
                sb.AppendLine("VC 运行库: " + SysInfo.VcRuntime);
                sb.AppendLine("程序目录: " + AppDomain.CurrentDomain.BaseDirectory);
                File.WriteAllText(EnvPath(), sb.ToString(), System.Text.Encoding.UTF8);
            }
            catch { }
        }

        // 清理超过一天的历史临时目录（程序被强杀时不会走正常清理路径）
        static void CleanStaleTemp()
        {
            try
            {
                string root = Path.GetTempPath();
                string[] dirs = Directory.GetDirectories(root, "PicTool_*");
                DateTime limit = DateTime.Now.AddHours(-24);
                for (int i = 0; i < dirs.Length; i++)
                {
                    try
                    {
                        if (Directory.GetCreationTime(dirs[i]) < limit) Directory.Delete(dirs[i], true);
                    }
                    catch { }
                }
            }
            catch { }
        }

        static string LogPath()
        {
            return Diag.LogPath();
        }

        static void LogError(Exception ex)
        {
            Diag.Log(ex, "界面异常");
        }
    }
}
