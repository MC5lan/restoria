// MainForm.Files.cs — 主窗体（partial）：左侧栏的文件操作
//
// 左侧栏要能**重命名 / 删除**，要能**归档单张图片**与
// **整个文件夹**（归档 = ArchiveProcessor 那条流水线，与命令行同一条），
// 并且归档完要**实时看到变化**（自动切到输出文件夹并刷新列表与缩略图）。
//
// 设计要点：
// - 危险操作（删除）一律：先确认 → 进回收站（不永久删）→ 刷新列表；
// - 重命名走文件系统改名（不是"复制+删"），失败原因原样报给用户；
// - 归档与批量归档共用同一套线程模型（后台线程 + BeginInvoke 回主线程），
//   完成后统一走 RefreshAfterArchive()：切到输出目录 + 报告结果。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace PicTool
{
    public partial class MainForm : Form
    {
        // ---------------- 左侧栏行菜单 ----------------

        void ShowRowMenu(int idx, Point screenPt)
        {
            if (idx < 0 || idx >= items.Count) return;
            WsItem it = items[idx];

            // ★ 右键菜单重构（实测"这个 UI 很丑"）：弃用 ListBox 纯文字
            //   DropList，改自绘 RowMenu——图标 + 分组分隔 + 危险项红色 + 悬停圆角高亮。
            //   action 与菜单项一一对应：1=打开 2=重命名 3=删除 4=归档这张 5=归档文件夹 6=资源管理器
            List<int> action = new List<int>();
            RowMenu pop = new RowMenu();
            if (it.IsParent) { pop.Add("打开上一级", "folder"); action.Add(1); }
            else if (it.IsFolder)
            {
                pop.Add("打开文件夹", "folder"); action.Add(1);
                pop.Add("归档整个文件夹（含子文件夹）", "wand"); action.Add(5);
            }
            else
            {
                pop.Add("打开这张图片", "image"); action.Add(1);
                pop.Add("归档这张图片", "wand"); action.Add(4);
            }
            pop.AddSep();
            pop.Add("重命名…", "text"); action.Add(2);
            pop.AddDanger("删除到回收站", "close"); action.Add(3);
            pop.AddSep();
            pop.Add("在资源管理器中显示", "external"); action.Add(6);
            pop.Closed += delegate
            {
                int p = pop.Picked;
                if (p < 0 || p >= action.Count) return;
                int a = action[p];
                // ★ 延迟一步再执行（）：Closed 回调里同步弹模态框
                //   （确认框/重命名窗）时菜单正处在释放流程，属重入。BeginInvoke 等菜单
                //   完全关掉后再动，动作内仍有 idx 越界检查，安全。
                try { BeginInvoke((MethodInvoker)delegate { DoRowAction(a, idx); }); } catch { }
            };
            // 宽度给足（"归档整个文件夹（含子文件夹）"较长），高度按项数自动算
            pop.ShowAt(screenPt, 244);
        }

        void DoRowAction(int act, int idx)
        {
            if (idx < 0 || idx >= items.Count) return;
            WsItem it = items[idx];
            switch (act)
            {
                case 1:
                    if (it.IsParent || it.IsFolder) OpenFolderPath(it.FullPath);
                    else if (it.DocIndex >= 0) SelectDoc(it.DocIndex);
                    break;
                case 2: RenameItem(idx); break;
                case 3: DeleteItem(idx); break;
                case 4: ArchiveOne(it); break;
                case 5: RunArchive(it.FullPath); break;   // 整树归档：保险丝+确认框在 RunArchive 里
                case 6: RevealInExplorer(it.FullPath); break;
            }
        }

        void RevealInExplorer(string path)
        {
            try
            {
                if (Directory.Exists(path)) System.Diagnostics.Process.Start("explorer.exe", "\"" + path + "\"");
                else System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
            }
            catch (Exception ex) { Msg("打开资源管理器失败：" + ex.Message); }
        }

        // ---------------- 重命名 ----------------

        void RenameItem(int idx)
        {
            if (busy) { Msg("正在处理中，请稍候。"); return; }
            if (idx < 0 || idx >= items.Count) return;
            WsItem it = items[idx];
            if (it.IsParent) { Status("“上一级”不能重命名"); return; }

            // ★ 正在编辑的图改名会丢未保存修改（）：改名后 BuildWorkspaceAndSelect
            //   会整体重建列表，旧 Doc 被 dispose、从磁盘重载，Edits 与位图修改静默丢失。
            //   弹名字前先问一句：有未保存修改就先保存（保存失败用户会看到错误框，仍然知情）。
            if (it.DocIndex == cur && Cur != null && Cur.Edits)
            {
                DialogResult ask = MessageBox.Show(this,
                    "这张图片还有没保存的修改。\n\n要先保存再改名吗？\n· 选「是」：先保存当前修改，再改名\n· 选「否」：直接改名，未保存的修改将丢失\n· 选「取消」：先不改名",
                    "重命名", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (ask == DialogResult.Cancel) return;
                if (ask == DialogResult.Yes) SaveCurrent();
            }

            bool isDir = it.IsFolder;
            string oldPath = it.FullPath;
            string dir = Path.GetDirectoryName(oldPath);
            string baseName = Path.GetFileName(oldPath);
            string ext = isDir ? "" : Path.GetExtension(oldPath);
            string mainName = isDir ? baseName : Path.GetFileNameWithoutExtension(oldPath);
            string target = null;

            using (NamePrompt dlg = new NamePrompt(isDir ? "重命名文件夹" : "重命名图片",
                isDir ? "新文件夹名" : "新文件名（扩展名 " + ext + " 不变）", mainName))
            {
                // ★ 校验进窗（Win7 实测反馈"弹窗要好几次才能关掉"）：
                //   重名/非法字符在对话框内原地红字提示，不再"关窗→弹失败框→重走全流程"
                dlg.Validator = delegate(string candidate)
                {
                    string w = FileTools.CleanName(candidate);
                    if (w.Length == 0) return "名称无效（含非法字符，或清理后为空）";
                    if (!isDir) w = Path.ChangeExtension(w, ext);
                    if (string.Equals(w, baseName, StringComparison.Ordinal)) return "与原名相同，不用改";
                    string t = Path.Combine(dir, w);
                    if (File.Exists(t) || Directory.Exists(t)) return "这个名字已经存在，换一个";
                    return null;
                };
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string want = FileTools.CleanName(dlg.Value);
                if (want.Length == 0) { Status("名称无效"); return; }
                if (!isDir) want = Path.ChangeExtension(want, ext);   // 扩展名固定
                if (string.Equals(want, baseName, StringComparison.Ordinal)) return;

                target = Path.Combine(dir, want);
                if (File.Exists(target) || Directory.Exists(target))
                {
                    Msg("这个名字已经存在：\n" + target + "\n\n换一个名字，或先删掉那个文件。");
                    return;
                }
                try
                {
                    if (isDir) Directory.Move(oldPath, target);
                    else File.Move(oldPath, target);
                    lastRenameOps = null;      // 手工改名后，批量重命名的"撤销上次"不再成立
                    Status("已重命名为 " + want);
                }
                catch (Exception ex)
                {
                    Msg("重命名失败：" + ex.Message);
                    return;
                }
            }
            BuildWorkspaceAndSelect(target);     // 刷新列表并选中新名字那一项
        }

        // 刷新当前文件夹并选中指定路径（重命名/删除后用；路径不存在就只刷新）
        void BuildWorkspaceAndSelect(string wantPath)
        {
            string folder = currentFolder;
            if (folder == null) return;
            BuildWorkspace(folder, false);
            if (wantPath == null) return;
            for (int i = 0; i < items.Count; i++)
            {
                if (string.Compare(items[i].FullPath, wantPath, true) == 0)
                {
                    ws.SelectIndex(i);
                    if (items[i].DocIndex >= 0) SelectDoc(items[i].DocIndex);
                    break;
                }
            }
        }

        // ---------------- 删除（回收站）----------------

        void DeleteItem(int idx)
        {
            if (busy) { Msg("正在处理中，请稍候。"); return; }
            if (idx < 0 || idx >= items.Count) return;
            WsItem it = items[idx];
            if (it.IsParent) { Status("“上一级”不能删除"); return; }

            bool isDir = it.IsFolder;
            string path = it.FullPath;
            int inner = 0;
            if (isDir) { try { inner = Directory.GetFiles(path).Length + Directory.GetDirectories(path).Length; } catch { } }

            string ask = isDir
                ? "把整个文件夹删除到回收站？\n\n" + path + (inner > 0 ? "\n（里面有 " + inner + " 个文件/子文件夹）" : "")
                : "把这张图片删除到回收站？\n\n" + Path.GetFileName(path);
            ask += "\n\n删除后可以从回收站还原。";

            using (ConfirmBox dlg = new ConfirmBox("删除到回收站", ask, "删除"))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
            }
            if (!FileTools.DeleteToRecycleBin(path))
            {
                Msg("删除失败：文件可能正被占用，或没有权限。\n" + path);
                return;
            }
            Status("已删除到回收站：" + Path.GetFileName(path));
            lastRenameOps = null;
            BuildWorkspaceAndSelect(null);
        }

        // ---------------- 归档单张图片 ----------------

        // 输出到 <文件夹>\已归档\<同名>（与"归档本文件夹"同一个输出目录、同一条流水线）
        void ArchiveOne(WsItem it)
        {
            if (busy) { Msg("正在处理中，请稍候。"); return; }
            if (it == null || it.IsFolder || it.IsParent) return;
            string src = it.FullPath;
            if (!File.Exists(src)) { Msg("文件不存在：" + src); return; }
            // ★ 保险丝（）：单张归档也要过 IsArchiveAllowed ——
            //   以前只有整树归档有，行菜单"归档这张"在桌面/用户主目录这类危险顶层文件夹里
            //   会往里写"已归档"子目录。与整树归档（MainForm.Batch.cs）同一道关。
            string reason;
            if (!FileTools.IsArchiveAllowed(Path.GetDirectoryName(src), out reason))
            { Msg("不能归档到那里：\n" + reason); return; }
            string outFolder = outDir;
            if (outFolder == null)
            {
                try { outFolder = Path.Combine(Path.GetDirectoryName(src), ArchivedDirName); }
                catch { }
            }
            ArchiveFiles(new string[] { src }, outFolder);
        }

        // ---------------- 归档：共用的后台执行 + 完成后刷新 ----------------

        // files = 要归档的文件；outFolder = 输出目录（单文件夹模式，全部进同一个目录）。
        // 跑的是 ArchiveProcessor（与 CLI 同一条）。
        void ArchiveFiles(string[] files, string outFolder)
        {
            if (files == null || files.Length == 0) { Status("没有要归档的图片"); return; }
            if (outFolder == null || outFolder.Length == 0) { Msg("输出文件夹无效"); return; }
            List<ArchiveJob> jobs = new List<ArchiveJob>();
            foreach (string f in files)
            {
                ArchiveJob j = new ArchiveJob();
                j.File = f;
                j.OutFolder = outFolder;
                jobs.Add(j);
            }
            ArchiveJobs(jobs, outFolder, outFolder);
        }

        // 归档核心（整树归档改造）：每个任务自带输出目录 ——
        // 单张/单层归档时全部同目录；整树归档时各归各的"图片所在文件夹\已归档"。
        // outDesc = 完成摘要里显示的输出说明；refreshFolder = 完成后刷新的目录。
        void ArchiveJobs(List<ArchiveJob> jobs, string outDesc, string refreshFolder)
        {
            if (jobs == null || jobs.Count == 0) { Status("没有要归档的图片"); return; }

            // 把涉及到的输出目录都先建好（建不出来就整体不动手，避免跑一半没地方放）
            List<string> outDirs = new List<string>();
            foreach (ArchiveJob j in jobs)
                if (!outDirs.Contains(j.OutFolder)) outDirs.Add(j.OutFolder);
            foreach (string d in outDirs)
            {
                try { if (!Directory.Exists(d)) Directory.CreateDirectory(d); }
                catch (Exception ex) { Msg("无法创建输出文件夹：\n" + d + "\n" + ex.Message); return; }
            }

            ArchiveOptions opt = new ArchiveOptions();   // 与 CLI 默认一致
            opt.Thorough = archiveThorough;              // 档位来自归档区的「普通 / 极致」
            // ★（P1-4）：把"取消"接进单页流水线内部 —— 点取消后最多再等一个步骤，
            //   而不是等整页跑完（原生 OCR 推理解不了，但随后那几步不必白等）。
            opt.CancelCheck = delegate() { return archiveCancel; };
            progress.Value = 0;
            progress.Visible = true;        // 进度条：跑的时候才出现（空轨道看着像多余横线）
            archiveCancel = false;
            busy = true;
            long sw = Environment.TickCount;
            int total = jobs.Count;
            SetArchiveButtons(false);
            Status("归档中… 0/" + total);

            System.Threading.Thread t = new System.Threading.Thread(delegate()
            {
                List<string> errors = new List<string>();
                int done = 0, skipped = 0;
                // ★ 批次报告：每页一行（动了什么/判据/耗时），成功也要留痕，
                //   对账/抽查/追溯用。并行按序号落位 reportLines[idx]，天然保序无需加锁。
                string[] reportLines = new string[jobs.Count];
                // ★ P2 管线并行：多页同时跑完整条流水线。页与页之间没有共享状态
                //   （ArchiveProcessor 是无状态静态类，Options/Result 每页各建），唯一的
                //   共享资源是 OCR 会话 —— 已在 Ocr.OcrGate 上整段串行。
                //   取消 = ①worker 取到下一页时看到标志就空手返回（队列迅速排空）；
                //   ②★（P1-4）已在跑的页由 opt.CancelCheck 在**步骤边界**打断，
                //     最多再等一个步骤；被取消的页不写盘（半成品是坏档案）。
                //   8GB 内存默认 2 页并行（每页峰值 ~200MB），上限 4 防呆。
                int workers = Math.Max(1, Math.Min(4, archiveParallel));
                object errGate = new object();
                System.Threading.Tasks.Parallel.For(0, jobs.Count,
                    new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = workers },
                    delegate(int idx)
                {
                    ArchiveJob job = jobs[idx];
                    if (archiveCancel) return;
                    Bitmap src = null, outBmp = null;
                    System.Diagnostics.Stopwatch pageSw = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        src = new Bitmap(job.File);
                        ArchiveResult info;
                        outBmp = ArchiveProcessor.Run(src, opt, out info);
                        // ★（P1-4）：取消的页 Run 返回 null —— 绝不能保存半成品。
                        if (info.Cancelled || outBmp == null) return;
                        Imaging.SaveImage(outBmp, Path.Combine(job.OutFolder, Path.GetFileName(job.File)), 92);
                        if (info.SkippedSealOnly || info.SkippedColorCover)
                            System.Threading.Interlocked.Increment(ref skipped);
                        pageSw.Stop();
                        reportLines[idx] = DescribeJob(Path.GetFileName(job.File), src, info, (int)pageSw.ElapsedMilliseconds);
                    }
                    catch (Exception ex)
                    {
                        string em = ex.Message;
                        pageSw.Stop();
                        lock (errGate) { errors.Add(Path.GetFileName(job.File) + "：" + em); }
                        reportLines[idx] = Path.GetFileName(job.File) + " | FAIL：" + em;
                    }
                    finally
                    {
                        if (src != null) { try { src.Dispose(); } catch { } }
                        if (outBmp != null) { try { outBmp.Dispose(); } catch { } }
                    }
                    int d2 = System.Threading.Interlocked.Increment(ref done);
                    int pct = d2 * 100 / total;
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            // 并行完成通知可能乱序到达（d2 大的比小的晚入队），进度条只进不退
                            if (pct > progress.Value) progress.Value = pct;
                            Status("归档中… " + d2 + "/" + total);
                        });
                    }
                    catch { }
                });

                // ★ 批次报告落盘：写进 refreshFolder（整树=树根；单层=输出目录）。
                //   为什么放这里不放进 UI 回调：写文件是后台线程的事，UI 回调只管显示。
                //   只新增不改原图的红线不受影响 —— 报告是纯新文件，覆盖的也只是它自己。
                double secAll = (Environment.TickCount - sw) / 1000.0;
                if (!archiveCancel)
                {
                    try
                    {
                        string rptDir = string.IsNullOrEmpty(refreshFolder) ? jobs[0].OutFolder : refreshFolder;
                        StringBuilder rp = new StringBuilder();
                        rp.AppendLine("【归档报告】" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                        rp.AppendLine("档位：" + (archiveThorough ? "极致（保守去黑点+撤销保险）" : "普通") +
                                      " · 计划 " + total + " 张 · 完成 " + done +
                                      "（原样跳过 " + skipped + " · 失败 " + errors.Count + "）" +
                                      " · 耗时 " + secAll.ToString("0.0") + " 秒 · " + workers + " 页并行");
                        rp.AppendLine(new string('-', 72));
                        for (int i = 0; i < reportLines.Length; i++)
                            if (reportLines[i] != null) rp.AppendLine(reportLines[i]);
                        if (archiveCancel) rp.AppendLine("（中途取消，以上为已完成部分）");
                        if (errors.Count > 0) rp.AppendLine("失败详情见同目录 归档日志.txt");
                        File.WriteAllText(Path.Combine(rptDir, "归档报告.txt"), rp.ToString(), System.Text.Encoding.UTF8);
                    }
                    catch { }      // 报告写不出来不影响归档本身
                }

                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        busy = false;
                        SetArchiveButtons(true);
                        double sec = secAll;
                        string summary;
                        string tag = archiveThorough ? "【极致】" : "【普通】";
                        if (errors.Count > 0)
                        {
                            string log = Path.Combine(jobs[0].OutFolder, "归档日志.txt");
                            try { File.WriteAllLines(log, errors.ToArray(), System.Text.Encoding.UTF8); } catch { }
                            summary = tag + "归档：成功 " + (done - errors.Count) + " 张，失败 " + errors.Count + " 张。";
                            Toast.Show(this, "归档有问题，" + errors.Count + " 张没成功", Toast.Kinds.Error,
                                "成功 " + (done - errors.Count) + " 张 · 详情见 归档日志.txt");
                        }
                        else
                        {
                            summary = tag + "归档完成：" + done + " 张（原样跳过 " + skipped + " 张），耗时 " +
                                      sec.ToString("0.0") + " 秒。";
                            Toast.Show(this, tag + "归档完成 " + done + " 张", Toast.Kinds.Ok,
                                (skipped > 0 ? "原样跳过 " + skipped + " 张 · " : "") + "耗时 " + sec.ToString("0.0") + " 秒");
                        }
                        progress.Visible = false;
                        lblBatchResult.Text = summary + "\r\n输出：" + outDesc;
                        Status(archiveCancel ? "归档已取消" : summary);
                        archiveCancel = false;
                        RefreshAfterArchive(refreshFolder);
                    });
                }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            });
            t.IsBackground = true;
            t.Start();
        }

        // 批次报告的一行：这页动了什么、判据是什么、花了多久。人读优先，" | "分隔。
        static string DescribeJob(string fileName, Bitmap src, ArchiveResult info, int ms)
        {
            StringBuilder b = new StringBuilder();
            b.Append(fileName);
            b.Append(" | ").Append(src.Width).Append('x').Append(src.Height);
            if (info.SkippedSealOnly) { b.Append(" | SKIP 纯印章页（原样保存）"); return b.ToString(); }
            if (info.SkippedColorCover) { b.Append(" | SKIP 彩色封面页（原样保存）"); return b.ToString(); }
            b.Append(" → ").Append(info.Width).Append('x').Append(info.Height);
            b.Append(" @").Append(info.DpiUsed).Append("dpi");
            b.Append(" | 方向 ");
            b.Append(info.OrientTurns > 0
                ? "+" + (info.OrientTurns * 90) + "°(" + (info.OrientHow.Length > 0 ? info.OrientHow : "?") + ")"
                : "0");
            b.Append(info.Mirrored ? " | 镜像已翻" : " | 镜像无");
            if (info.SkewDeg != 0)
                b.Append(" | 倾斜 ").Append(info.SkewDeg.ToString("0.00")).Append("°(").Append(info.SkewHow).Append(")");
            if (info.MoireRemoved)
                b.Append(" | 摩尔纹陷波(").Append(info.MoirePeriod.ToString("0")).Append("行)");
            if (info.SpecksUndone) b.Append(" | 去黑点已撤销(保字)");
            else b.Append(" | 黑点 ").Append(info.SpecksRemoved);
            b.Append(" | 脏块 ").Append(info.StainsFound);
            b.Append(" | 章 ").Append(info.SealsFound);
            b.Append(" | ").Append(ms).Append("ms");
            return b.ToString();
        }

        void SetArchiveButtons(bool idle)
        {
            // 只管归档自己的按钮。批量页的"开始处理"由 RunBatch 自己的流程管 ——
            // 之前这里顺手把 btnBatchRun 也藏了，归档跑着的时候批量页按钮莫名消失（交互怪象之一）。
            if (btnArchive != null) btnArchive.Visible = idle;
            if (btnArchiveCancel != null) btnArchiveCancel.Visible = !idle;
            // 「归档当前图片」不隐藏而是转圈：让按钮自己告诉用户"正在干活"，而不是悄悄消失
            if (btnArchiveOne != null)
            {
                btnArchiveOne.Busy = !idle;
                btnArchiveOne.Text = idle ? "归档当前图片" : "归档中…";
            }
        }

        // 归档完成后：切到输出文件夹、刷新列表与缩略图（"实时看到变化"）
        void RefreshAfterArchive(string outFolder)
        {
            if (outFolder == null || !Directory.Exists(outFolder)) return;
            if (!string.Equals(outFolder, currentFolder, StringComparison.OrdinalIgnoreCase))
            {
                BuildWorkspace(outFolder, true);      // 直接跳到结果文件夹，成品就在眼前
                Status("归档完成 → 已切换到输出文件夹（" + items.Count + " 张）");
            }
            else
            {
                BuildWorkspace(outFolder, true);      // 就地刷新（缩略图会重读磁盘）
            }
        }
    }
}
