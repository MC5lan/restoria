// MainForm.Workspace.cs — 主窗体（partial）：工作区列表与打开文件 / 文档选择与内存 / 后台任务
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PicTool
{
    public partial class MainForm : Form
    {
        // ================= 工作区 / 打开文件 =================

        void OpenFolder()
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = "选择要处理的图片文件夹";
                d.ShowNewFolderButton = false;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                OpenFolderPath(d.SelectedPath);
            }
        }

        void OpenFiles()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "打开图片（可按住 Ctrl 多选）";
                d.Filter = "图片|*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff|全部文件|*.*";
                d.Multiselect = true;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                for (int i = 0; i < d.FileNames.Length; i++)
                {
                    if (!Directory.Exists(Path.GetDirectoryName(d.FileNames[i])))
                    {
                        Msg("找不到文件所在文件夹。");
                        return;
                    }
                }
                OpenSingleFile(d.FileNames[0]);
            }
        }

        void OpenSingleFile(string file)
        {
            if (busy) { Msg("正在处理中，请稍候。"); return; }   // 后台任务在跑时不要重建工作区
            string dir = Path.GetDirectoryName(Path.GetFullPath(file));
            BuildWorkspace(dir, false);
            for (int i = 0; i < items.Count; i++)
                if (string.Compare(items[i].FullPath, Path.GetFullPath(file), true) == 0)
                {
                    SelectDoc(items[i].DocIndex);
                    break;
                }
        }

        void OpenFolderPath(string folder)
        {
            if (busy) { Msg("正在处理中，请稍候。"); return; }
            if (folder == null) return;
            string p = folder.Trim().Trim('"');
            if (p.Length == 0) return;
            try { p = Path.GetFullPath(p); } catch { Msg("路径无效：" + folder); return; }
            if (!Directory.Exists(p)) { Msg("找不到这个文件夹：\n" + p); return; }
            BuildWorkspace(p, true);
        }

        // ================= 文件夹视图记忆（用户：返回文件夹不要回到顶部） =================
        //
        // 之前的体验：进子文件夹逛一圈，点"上一级"回来 —— 滚动位置回顶、选中清空、
        // 画布还被强制切到第一张图。原因：BuildWorkspace 每次都整体重建列表，
        // SetFolder 会把 scrollY 归零、Selected 清空，然后 autoSelectFirst 再选第一张。
        //
        // 现在的规则：
        //   · 离开一个文件夹时，把「滚动位置 + 当时选中的文件」记下来（按完整路径做键）；
        //   · 回到这个文件夹时原样恢复（滚动 + 选中的还是原来那张图）；
        //   · 第一次进的文件夹维持原行为（滚到顶、自动选第一张）；
        //   · 刷新（F5/刷新按钮）也不再跳顶 —— 同一个文件夹的记忆会被原样恢复。
        // 只记最近 64 个文件夹（够用且不占内存）。
        class FolderView { public int ScrollY; public string SelectedFile; }
        readonly Dictionary<string, FolderView> folderViews =
            new Dictionary<string, FolderView>(StringComparer.OrdinalIgnoreCase);
        readonly List<string> folderViewOrder = new List<string>();
        const int FolderViewCap = 64;

        void RememberFolderView(string path, int scrollY, string selectedFile)
        {
            if (string.IsNullOrEmpty(path)) return;
            FolderView v;
            if (!folderViews.TryGetValue(path, out v))
            {
                if (folderViews.Count >= FolderViewCap)
                {
                    string oldest = folderViewOrder[0];
                    folderViewOrder.RemoveAt(0);
                    folderViews.Remove(oldest);
                }
                v = new FolderView();
                folderViews[path] = v;
                folderViewOrder.Add(path);
            }
            v.ScrollY = scrollY;
            v.SelectedFile = selectedFile;
        }

        FolderView RecallFolderView(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            FolderView v;
            return folderViews.TryGetValue(path, out v) ? v : null;
        }

        // 读取文件夹 -> 生成工作区条目
        void BuildWorkspace(string folder, bool autoSelectFirst)
        {
            Cursor = Cursors.WaitCursor;
            try
            {
                // ★ 先把"当前文件夹离开时的样子"记下来（必须在 SetFolder 清空之前取）
                int prevScroll = ws.GetScrollY();
                string prevSel = (cur >= 0 && cur < docs.Count) ? docs[cur].FilePath : null;
                string prevFolder = currentFolder;

                // 先让画布/工作区脱离旧位图，再释放，避免重绘到已释放的对象
                canvas.SetImage(null, null, false);
                ws.SetFolder("", "", new List<WsItem>());
                for (int i = 0; i < docs.Count; i++) docs[i].Dispose();
                docs.Clear();
                items.Clear();

                string[] files = BatchProcessor.ListImages(folder, MAX_FILES);
                string[] dirs = new string[0];
                try { dirs = Directory.GetDirectories(folder); } catch { }
                Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
                if (dirs.Length > 200) { string[] cut = new string[200]; Array.Copy(dirs, cut, 200); dirs = cut; }

                // 上一级
                try
                {
                    DirectoryInfo parent = Directory.GetParent(folder);
                    if (parent != null)
                    {
                        WsItem up = new WsItem();
                        up.IsParent = true;
                        up.FullPath = parent.FullName;
                        up.Name = "上一级";
                        up.Meta = parent.FullName;
                        items.Add(up);
                    }
                }
                catch { }

                for (int i = 0; i < dirs.Length; i++)
                {
                    WsItem it = new WsItem();
                    it.IsFolder = true;
                    it.FullPath = dirs[i];
                    try { it.Name = new DirectoryInfo(dirs[i]).Name; } catch { it.Name = dirs[i]; }
                    it.Meta = "文件夹";
                    items.Add(it);
                }

                for (int i = 0; i < files.Length; i++)
                {
                    Doc d = new Doc();
                    d.FilePath = files[i];
                    WsItem it = new WsItem();
                    it.FullPath = files[i];
                    it.Name = Path.GetFileName(files[i]);
                    it.Meta = "";
                    it.Loading = true;
                    it.Doc = d;
                    it.DocIndex = docs.Count;
                    d.WsIndex = items.Count;
                    docs.Add(d);
                    items.Add(it);
                }

                string folder0 = currentFolder;   // 记住旧文件夹：用于判断是否真的换目录了
                if (prevFolder != null) RememberFolderView(prevFolder, prevScroll, prevSel);
                currentFolder = folder;
                if (!string.Equals(folder, folder0, StringComparison.OrdinalIgnoreCase))
                    lastRenameOps = null;   // 换了文件夹：重命名撤销失效（撤销目标已不在当前列表里）
                // ★ 输出文件夹统一叫「已归档」；旧名「_已处理」同样认，
                //   免得打开老目录时又在外面套一层
                string dirName = Path.GetFileName(folder);
                if (dirName.Equals(ArchivedDirName, StringComparison.OrdinalIgnoreCase) ||
                    dirName.Equals("_已处理", StringComparison.OrdinalIgnoreCase))
                    outDir = folder;                                  // 已在输出文件夹里，不再套一层
                else
                    outDir = Path.Combine(folder, ArchivedDirName);
                lblOutDir.Text = outDir;
                UpdateScanDirLabel();      // 扫描页“保存到当前文件夹”的路径提示跟着变

                string title;
                try { title = new DirectoryInfo(folder).Name; } catch { title = folder; }
                if (title.Length == 0) title = folder;
                ws.SetFolder(title, folder, items);
                canvas.SetImage(null, null, false);
                canvas.EmptyTitle = docs.Count == 0 ? "这个文件夹里没有图片" : "点左侧工作区里的图片开始编辑";
                canvas.EmptySub = docs.Count == 0 ? "换一个文件夹试试，或把文件夹拖进来" : "支持 JPG / PNG / BMP / TIFF";
                cur = -1;
                lblDocName.Text = "未打开图片";
                lblDocMeta.Text = "";
                SyncCanvasTexts();       // 画布脱离旧文档的文字列表
                textSnapshot = null;
                InvalidatePreview();

                StartThumbWorker();

                // ★ 回到"来过"的文件夹：恢复滚动位置 + 当时选中的图（没有记忆才选第一张）
                //   注意顺序：先选（SelectIndex 会把滚动条对到选中行），最后再覆盖成记住的滚动值
                //   —— 用户实际滚动到的地方和选中行不一定是一回事。
                FolderView v = RecallFolderView(folder);
                if (autoSelectFirst && docs.Count > 0)
                {
                    bool picked = false;
                    if (v != null && v.SelectedFile != null)
                        for (int i = 0; i < items.Count; i++)
                            if (!items[i].IsFolder && !items[i].IsParent &&
                                string.Compare(items[i].FullPath, v.SelectedFile, true) == 0)
                            {
                                SelectDoc(items[i].DocIndex);
                                picked = true;
                                break;
                            }
                    if (!picked) SelectDoc(0);
                    else if (cur == -1) SelectDoc(0);   // 记忆里的图加载失败（已损坏/被占用）→ 退回第一张
                }
                if (v != null) ws.SetScrollY(v.ScrollY);   // autoSelectFirst=false 的调用方自己选图，这里只还位置
                Status("已载入 " + docs.Count + " 张图片" + (docs.Count >= MAX_FILES ? "（已达上限 " + MAX_FILES + " 张）" : ""));
            }
            catch (Exception ex)
            {
                Msg("打开文件夹失败：" + ex.Message);
            }
            finally { Cursor = Cursors.Default; }
        }

        void HandleDrop(IDataObject data)
        {
            // ★ 文字编辑卡开着时拒绝一切拖入（dev-expert 复审）：输入卡浮在
            //   画布上且也在拖放接收树里，正打字时往卡片上拖文件夹会直接换目录——
            //   未保存的位图修改和文字全部被顶掉。语义层一刀切（所有入口一致）：先收尾再拖。
            if (canvas != null && canvas.TextCardOpen)
            { Status("先把文字编辑收尾（Enter 确定 / Esc 取消），再拖入文件"); return; }
            string[] drops = data.GetData(DataFormats.FileDrop) as string[];
            if (drops == null || drops.Length == 0) return;
            if (Directory.Exists(drops[0])) OpenFolderPath(drops[0]);
            else OpenSingleFile(drops[0]);
        }

        // ================= 文档选择 / 内存管理 =================

        // 统一动作入口守卫（全域落地）：以前入口守卫两种写法并存——
        // 合并式 `if (d == null || busy) return;`（连"没打开图"都静默）与分离式
        // `if (d == null) { Status(...); } if (busy) return;`（漏写 busy 的机会更大）。
        // 现在收敛成一把尺：没图给提示、busy 静默挡。**新页面动作入口一律用它**。
        bool GuardAction(Doc d)
        {
            if (d == null) { Status("请先打开图片"); return false; }
            if (busy) return false;
            return true;
        }

        Doc Cur { get { return cur >= 0 && cur < docs.Count ? docs[cur] : null; } }

        WsItem ItemOf(Doc d)
        {
            if (d == null) return null;
            int i = d.WsIndex;
            return (i >= 0 && i < items.Count) ? items[i] : null;
        }

        void SelectDoc(int i)
        {
            if (busy || i < 0 || i >= docs.Count || i == cur) return;
            // 切走旧图：撤销深度降到 3 步（TrimUndoBackground）——多图同开时撤销内存有界
            if (cur >= 0 && cur < docs.Count && docs[cur] != null) TrimUndoBackground(docs[cur]);
            Doc d = docs[i];
            Cursor = Cursors.WaitCursor;
            try
            {
                if (d.Spilled) d.Restore();
                if (d.Current == null)
                {
                    d.Original = Imaging.LoadFile(d.FilePath);
                    d.Current = Imaging.Ensure24bpp(new Bitmap(d.Original));
                }
            }
            catch (Exception ex)
            {
                Cursor = Cursors.Default;
                Msg("打开图片失败：" + ex.Message);
                return;
            }
            Cursor = Cursors.Default;

            cur = i;
            d.LastUse = ++lru;
            ResetAdjustSessions(true);   // 换图了：滑条数值与新图的基准无关，归零
            InvalidatePreview();
            canvas.SetImage(d.Current, d.Overlay, true);
            SyncCanvasTexts();           // 文字列表与提交点快照跟着换文档
            textSnapshot = CloneTexts(d.Texts);
            canvas.NotifyToolChanged();
            ws.SelectIndex(d.WsIndex);
            RefreshHeader();
            EnforceMemoryCap();
        }

        void EnforceMemoryCap()
        {
            long total = 0;
            for (int i = 0; i < docs.Count; i++) total += docs[i].MemBytes;
            while (total > MEM_CAP)
            {
                Doc victim = null;
                for (int i = 0; i < docs.Count; i++)
                {
                    Doc d = docs[i];
                    if (i == cur || d.Spilled || d.Current == null) continue;
                    if (victim == null || d.LastUse < victim.LastUse) victim = d;
                }
                if (victim == null) break;
                try { victim.Spill(tempDir); } catch { break; }
                total = 0;
                for (int i = 0; i < docs.Count; i++) total += docs[i].MemBytes;
            }
        }

        // ================= 后台任务 =================

        // 弹给用户的文案：像"未将对象引用设置到对象的实例"这种对用户没有任何意义的内部异常，
        // 换成一句人话 + 日志位置（完整堆栈已由 Diag 写进日志）。
        static string Explain(Exception ex)
        {
            if (ex == null) return "程序内部出错了，本次操作已取消。\n\n详细原因见日志：" + Diag.LogPath();
            string m = ex.Message;
            bool empty = (m == null || m.Trim().Length == 0);
            bool nre = (ex is NullReferenceException) || (m != null && m.IndexOf("未将对象引用") >= 0);
            if (!empty && !nre) return m;
            return "程序内部出错了（" + ex.GetType().Name + "），本次操作已取消，图片没有被改动。\n\n详细原因已记入日志：\n" + Diag.LogPath();
        }

        void RunBusy(string statusMsg, MethodInvoker work, MethodInvoker done)
        {
            if (busy) return;
            busy = true;
            progressNote = statusMsg;
            Status(statusMsg);
            Cursor = Cursors.WaitCursor;
            System.Threading.Thread t = new System.Threading.Thread(delegate()
            {
                try
                {
                    work();
                    BeginInvoke((MethodInvoker)delegate
                    {
                        busy = false;
                        Cursor = Cursors.Default;
                        if (done != null) done();
                    });
                }
                catch (Exception ex)
                {
                    Diag.Log(ex, statusMsg);      // 带完整堆栈写日志（%TEMP%\PicTool-error.log）
                    BeginInvoke((MethodInvoker)delegate
                    {
                        busy = false;
                        Cursor = Cursors.Default;
                        Status("操作失败");
                        Msg(Explain(ex));
                    });
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        void PushUndo(Doc d, Bitmap oldCurrent) { PushUndo(d, oldCurrent, null); }

        // oldTexts 非 null 表示这次编辑同时改变了文字集合（烙印），撤销时一起还原
        void PushUndo(Doc d, Bitmap oldCurrent, List<TextItem> oldTexts)
        {
            EditRecord r = new EditRecord();
            r.Img = oldCurrent;
            r.Texts = oldTexts;
            d.Undo.Add(r);
            TrimUndo(d);
        }

        // 位图快照保留策略（重写，原 UNDO_MAX=2 被实测"撤销只能撤销两次"）：
        //   · 每图最多 UNDO_MAX=10 步；
        //   · 每图 undo 位图总字节 ≤ UNDO_BYTES_PER_DOC=96MB（超大图步数自动变少）；
        //   · 文字快照是纯数据，不计字节（存量极小），只受"50 条"总量护栏兜底。
        // 切图时旧图另裁到 UNDO_KEEP_BACKGROUND=3 步（见 SelectDoc）——当前图享受完整
        // 撤销深度，旧图切回来仍可撤 3 步；总内存上界有数（≈10+N×3 张 A4@200dpi）。
        // 这里可能在后台线程被调用，所以只置标志，真正重置交给 UI 定时器。
        void TrimUndo(Doc d)
        {
            int imgCount = 0;
            long bytes = 0;
            for (int i = d.Undo.Count - 1; i >= 0; i--)
            {
                EditRecord r = d.Undo[i];
                if (r.Img != null)
                {
                    imgCount++;
                    long w = (long)r.Img.Width * r.Img.Height * 3;
                    bytes += w;
                    // 双帽：步数超限，或字节超限——但至少保留 1 条位图步（撤销链不能断头）
                    if (imgCount > UNDO_MAX || (bytes > UNDO_BYTES_PER_DOC && imgCount > 1))
                    {
                        r.Dispose(); d.Undo.RemoveAt(i);
                        imgCount--; bytes -= w;
                    }
                }
            }
            while (d.Undo.Count > 50) { d.Undo[0].Dispose(); d.Undo.RemoveAt(0); }
            // 别的编辑（修复/印章/翻转/90°…）会改变图像基准，调色与旋转的滑条 session 就此失效。
            // ★ ：以前无论线程只置 adjustStale，靠 300ms 心跳愈合 ——
            //   这 300ms 里拖滑条，EnsureBase 还会返回改动前的旧基准并提交，刚做的纠偏/裁剪
            //   被悄悄丢掉。现在 UI 线程调用时立即作废（与 FlattenIfNeeded 的立即复位一致）；
            //   后台线程路径仍然只置标志（不能跨线程碰基准位图），心跳兜底不变。
            if (!sliderCommit)
            {
                if (InvokeRequired) adjustStale = true;
                else ResetAdjustSessions(false);
            }
        }

        // 切走旧图时调用：撤销深度降到 UNDO_KEEP_BACKGROUND=3 步（当前图享受完整 10 步，
        // 旧图切回来仍可撤 3 步；多图同开时内存有界）。从最旧的位图步开始挤，文字步不动。
        void TrimUndoBackground(Doc d)
        {
            int imgCount = 0;
            for (int i = d.Undo.Count - 1; i >= 0; i--)
                if (d.Undo[i].Img != null) imgCount++;
            for (int i = 0; i < d.Undo.Count && imgCount > UNDO_KEEP_BACKGROUND; i++)
            {
                if (d.Undo[i].Img != null)
                {
                    d.Undo[i].Dispose(); d.Undo.RemoveAt(i);
                    imgCount--; i--;   // 删了当前位，回退一步让 for 的 i++ 回到同一位
                }
            }
        }

        // ---------- 调色 / 旋转的基准图 ----------

        Bitmap EnsureBase(ref Bitmap slot, ref Doc slotDoc, Doc d)
        {
            if (slot != null && slotDoc == d && Imaging.Alive(slot)) return slot;
            if (slot != null) { try { slot.Dispose(); } catch { } slot = null; }
            slot = SafeCopy(d.Current);
            slotDoc = (slot == null) ? null : d;
            return slot;
        }

        void DropAdjustBases()
        {
            if (colorBaseFull != null) { try { colorBaseFull.Dispose(); } catch { } colorBaseFull = null; }
            if (rotBaseFull != null) { try { rotBaseFull.Dispose(); } catch { } rotBaseFull = null; }
            colorBaseDoc = null; rotBaseDoc = null;
        }

        // 重置滑条与基准（只在 UI 线程调用）
        void ResetAdjustSessions(bool alsoSliders)
        {
            DropAdjustBases();
            if (alsoSliders)
            {
                if (sBright != null) { sBright.ResetValue(0); vBright.Text = "0"; }
                if (sContr != null) { sContr.ResetValue(0); vContr.Text = "0"; }
                if (sSat != null) { sSat.ResetValue(0); vSat.Text = "0"; }
                if (sTemp != null) { sTemp.ResetValue(0); vTemp.Text = "0"; }
                if (sBlack != null) { sBlack.ResetValue(0); vBlack.Text = "0"; }
                if (sWhite != null) { sWhite.ResetValue(255); vWhite.Text = "255"; }
                if (sGamma != null) { sGamma.ResetValue(100); vGamma.Text = "1.00"; }
                if (sSharp != null) { sSharp.ResetValue(0); vSharp.Text = "0%"; }
                if (sAngle != null) { sAngle.ResetValue(0); vAngle.Text = "0°"; }
            }
        }

        static string SgnVal(int v) { return (v > 0 ? "+" : "") + v; }

        void AfterEdit(bool clearMask)
        {
            Doc d = Cur;
            if (d == null) return;
            d.Edits = true;
            d.LastUse = ++lru;
            histDoc = null;     // 图像内容变了：直方图重算
            // 先把画布指向新的位图，再释放旧对象，避免重绘到已释放的位图
            if (clearMask)
            {
                canvas.SetImage(d.Current, null, false);
                d.ClearMask();
            }
            else canvas.SetImage(d.Current, d.Overlay, false);
            SyncCanvasTexts();      // 文字列表跟着当前文档走（画布渲染/命中都要用）
            InvalidatePreview();
            RegenerateThumb(d);
            RefreshHeader();
            ws.Invalidate();
            // 异常恢复：这张图改了还没存，记一份快照（后台写盘）。
            // 有未烙印文字时先合成一份再记 —— 恢复出来的 PNG 里必须带字。
            NoteRecoveryFlattened(d);
            EnforceMemoryCap();
        }

        // 文字元素列表换绑到当前文档（换图/撤销/还原后都要调）
        void SyncCanvasTexts()
        {
            Doc d = Cur;
            canvas.Texts = d == null ? null : d.Texts;
            if (canvas.SelText >= (canvas.Texts == null ? 0 : canvas.Texts.Count)) canvas.SelText = -1;
            RefreshTextInspector();
            canvas.Invalidate();
        }

        // 异常恢复快照：优先带烙印文字（不改 d.Current，只为快照合成一份）
        void NoteRecoveryFlattened(Doc d)
        {
            if (d == null || d.Current == null || !Imaging.Alive(d.Current)) return;
            if (d.Texts == null || d.Texts.Count == 0) { Recovery.Note(d.Current, d.FilePath); return; }
            Bitmap snap = SafeCopy(d.Current);
            if (snap == null) { Recovery.Note(d.Current, d.FilePath); return; }
            try { TextStamp.Rasterize(snap, d.Texts); Recovery.Note(snap, d.FilePath); }
            finally { snap.Dispose(); }
        }

        // 把未烙印的文字落进位图（保存/恢复/图像类操作前的统一兜底）。
        // 烙印 = 一次完整编辑：位图旧样与文字旧样进同一条撤销记录，一次 Ctrl+Z 整步退回。
        void FlattenIfNeeded(Doc d)
        {
            if (d == null || d.Current == null || d.Texts == null || d.Texts.Count == 0) return;
            if (!Imaging.Alive(d.Current)) return;
            Bitmap work = SafeCopy(d.Current);
            if (work == null) return;
            try { TextStamp.Rasterize(work, d.Texts); }
            catch { work.Dispose(); return; }
            PushUndo(d, d.Current, CloneTexts(d.Texts));
            d.Current = work;
            d.Texts.Clear();
            textSnapshot = new List<TextItem>();   // 文字已清空，提交点快照同步归零
            ResetAdjustSessions(false);            // 位图变了：调色/旋转的基准图立即作废（不能等 300ms 定时器）
            canvas.SetImage(d.Current, d.Overlay, false);
            SyncCanvasTexts();
            InvalidatePreview();
            d.Edits = true;
        }

        static List<TextItem> CloneTexts(List<TextItem> src)
        {
            List<TextItem> dst = new List<TextItem>();
            if (src != null) for (int i = 0; i < src.Count; i++) dst.Add(src[i].Clone());
            return dst;
        }

        void RegenerateThumb(Doc d)
        {
            Bitmap fresh = Imaging.MakeThumb(d.Current, 240, 170);   // 先造新的，再换旧的
            Bitmap old = d.Thumb;
            Bitmap oldIt = null;
            WsItem it = ItemOf(d);
            if (it != null)
            {
                // 列表项的缩略图可能是独立位图（缩略图线程先造的那份）：与 Doc.Thumb
                // 不同引用时也要释放，否则换一次图漏一张
                if (!ReferenceEquals(it.Thumb, old)) oldIt = it.Thumb;
                it.Thumb = fresh;
            }
            d.Thumb = fresh;
            if (old != null) old.Dispose();
            if (oldIt != null) oldIt.Dispose();
        }

    }
}
