// MainForm.cs — 主窗体（partial）：字段 / 构造 / OnShown / 崩溃恢复检查
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PicTool
{
    // 滑条数值的显示格式（黑场/Gamma 等各有各的读法）
    // 注意：本声明在类体之外（namespace 作用域），切分脚本的类体区间不覆盖它，不能被丢掉。
    public delegate string FormatValueDelegate(double v);

    public partial class MainForm : Form, IPageHost
    {
        // 诊断模式（--layout / --layout-check）下让布局与字体度量无关。
        //
        // 为什么需要：页面说明标签用 AutoSize，高度是字体实测值。同一句话
        // 在「微软雅黑 UI」量出来 219x15，在「Segoe UI」是 229x16 —— 差 1px，
        // 而 y += 高度 + 12 会把它放大到整页所有后续控件。基线在一台机器上生成、
        // 在另一台上必然对不上，CI（英文镜像，没有雅黑）因此永远红。
        //
        // 打开它只在诊断时生效，产品运行时的外观完全不变。
        public static bool DeterministicLayout;

        // ---------- 数据 ----------
        List<Doc> docs = new List<Doc>();
        List<WsItem> items = new List<WsItem>();
        int cur = -1;
        long lru;
        string tempDir, currentFolder, outDir;
        volatile bool busy;   // 与 batchCancel 拆开（后者是旧「批量处理」的残留，已删）
        int lastWsWidth = 244;
        bool overwriteMode;
        bool overwriteWarned;        // 用户勾选"不再提醒"
        bool syncingSaveMode;
        int contentScroll;
        volatile string progressNote = "";   // 后台线程只写这个字段，由 UI 定时器读出来显示（不跨线程碰控件）
        const long MEM_CAP = 220L * 1024 * 1024;
        // 撤销保留步数（原值 2 是设计失误：编辑第三次时最旧的快照被挤掉，编辑工作流完全不可用。
        // 改为 10 步 + 每图 96MB 字节预算（TrimUndo）+ 切图时旧图裁到 3 步（SelectDoc），内存有界（约 ≤400MB））
        const int UNDO_MAX = 10;
        // 每张图 undo 位图快照的总字节预算：超大图（600dpi 扫描）即使不到 10 步也会被
        // 字节帽裁住，防止 10 步 × 100MB/张 的内存爆炸
        const long UNDO_BYTES_PER_DOC = 96L * 1024 * 1024;
        // 切走时旧图 undo 裁到的步数：当前图享受完整 10 步，旧图切回来仍可撤 3 步
        const int UNDO_KEEP_BACKGROUND = 3;
        static readonly int MAX_FILES = SysInfo.MaxFiles;         // 单次载入上限同样按内存自适应

        // 预览
        Bitmap previewBase, previewBuf, previewRot;
        Doc previewDoc;
        int pendingPreview;

        // 调色 / 旋转的"基准图"与滑条语义：
        //   滑条上的数值永远表示"相对基准图"的量，所以松手提交后滑条不归零（归零会让人以为没生效，
        //   再拖一次就变成在已调整的图上又调一遍，越拖越偏）。
        //   提交时用基准图重新算，反复拖动是幂等的；把滑条拖回 0 就退回基准图。
        Bitmap colorBaseFull, rotBaseFull;
        Doc colorBaseDoc, rotBaseDoc;
        bool sliderCommit;      // 当前这次 PushUndo 是滑条提交（不算"其它编辑"）
        bool adjustStale;       // 由后台线程置位：需要回 UI 线程重置滑条与基准

        // ---------- 控件 ----------
        CanvasControl canvas;
        WorkspacePanel ws;
        FlatSplitter splitter;
        NavRail navRail;                    // 左侧功能导航轨（9 个功能入口 = 9 格抽屉，页序见 MainForm.UiKit.cs SwitchSection）
        FlatButton btnOpenFolder, btnOpenFiles, btnUndo, btnRevert, btnSave, btnSaveAs, btnKeys;
        Label lblZoom;
        FlatButton btnZoomIn, btnZoomOut, btnFit;
        FlatButton btnWorkspace;
        Label lblDocName, lblDocMeta, lblStatus, lblInfo;
        FlatDot dotEdited;
        FlatButton btnToolBrush, btnToolRect, btnToolErase, btnToolText;   // 顶栏工具组
        volatile bool fixCancel;                            // 修复进行中要求取消（跨线程读写：UI 线程写、修复线程读）
        int sectionIndex;                   // 当前检查器页（由导航轨驱动）
        Panel content;
        Panel pnlRemove, pnlCrop, pnlRotate, pnlColor, pnlSeal, pnlBatch, pnlText;   // 各页容器（壳里指向 pageXxx）
        // 区域上色：涂抹/框选区域 + 标准色块，把圈内文字染成所选颜色
        // 印章页类化——控件移入 SealPage，这里留只读别名
        SealPage pageSeal;              // 印章页（控件在类内）
        Bitmap sealFrameMask;           // 识别出的外框掩码（印章页与 Crop 页清除辅助共用）
        FlatButton btnSealDetect { get { return pageSeal.btnSealDetect; } }
        FlatButton btnSealColor { get { return pageSeal.btnSealColor; } }
        FlatButton btnSealClear { get { return pageSeal.btnSealClear; } }
        Segmented segSealRange { get { return pageSeal.segSealRange; } }
        FlatSlider sSeal { get { return pageSeal.sSeal; } }
        Label vSealStrength { get { return pageSeal.vSealStrength; } }
        // 区域上色（构建在印章页段）
        ColorPicker pickRecolor { get { return pageSeal.pickRecolor; } }
        FlatSlider sRecolor { get { return pageSeal.sRecolor; } }
        Label vRecolor { get { return pageSeal.vRecolor; } }
        int toolIndex;                      // 当前画布工具：0笔刷 1框选 2橡皮
        // 去除页类化——控件移入 RemovePage，这里留只读别名
        RemovePage pageRemove;          // 去除页（控件在类内）
        FlatSlider sBrush { get { return pageRemove.sBrush; } }
        Label vBrush { get { return pageRemove.vBrush; } }
        Segmented segFix { get { return pageRemove.segFix; } }
        FlatButton btnInpaint { get { return pageRemove.btnInpaint; } }
        FlatButton btnClearMask { get { return pageRemove.btnClearMask; } }
        // 归档控件已搬入归档页（BatchPage）——别名见上方 pageBatch 区
        // 归档流程状态（RunArchive/ArchiveJobs 引用，留主窗体）
        volatile bool archiveThorough;      // 归档档位：false=普通 / true=极致（补 volatile：后台归档线程要读）
        volatile bool archiveCancel;        // 一键归档进行中要求取消
        // 方向页类化——控件移入 RotatePage 类自治，
        // 这里只留只读别名属性（外部引用零改动）。其余页面按此模式逐个迁移。
        RotatePage pageRotate;              // 方向页（控件在类内）
        FlatSlider sAngle { get { return pageRotate.sAngle; } }
        Label vAngle { get { return pageRotate.vAngle; } }
        FlatSwitch swCrop { get { return pageRotate.swCrop; } }
        FlatSwitch swRoiOnly { get { return pageRotate.swRoiOnly; } }
        FlatButton btnApplyRotate { get { return pageRotate.btnApplyRotate; } }
        FlatButton btnRotL { get { return pageRotate.btnRotL; } }
        FlatButton btnRotR { get { return pageRotate.btnRotR; } }
        FlatButton btnDeskew { get { return pageRotate.btnDeskew; } }
        FlatButton btnFlipH { get { return pageRotate.btnFlipH; } }
        FlatButton btnFlipV { get { return pageRotate.btnFlipV; } }
        // 调色页类化——控件与滑条工厂移入 ColorPage，这里留只读别名
        ColorPage pageColor;            // 调色页（控件在类内）
        FlatSlider sBright { get { return pageColor.sBright; } }
        FlatSlider sContr { get { return pageColor.sContr; } }
        FlatSlider sSat { get { return pageColor.sSat; } }
        FlatSlider sTemp { get { return pageColor.sTemp; } }
        Label vBright { get { return pageColor.vBright; } }
        Label vContr { get { return pageColor.vContr; } }
        Label vSat { get { return pageColor.vSat; } }
        Label vTemp { get { return pageColor.vTemp; } }
        FlatButton btnAuto { get { return pageColor.btnAuto; } }
        FlatButton btnResetColor { get { return pageColor.btnResetColor; } }
        FlatButton btnApplyColor { get { return pageColor.btnApplyColor; } }
        FlatSlider sBlack { get { return pageColor.sBlack; } }
        FlatSlider sWhite { get { return pageColor.sWhite; } }
        FlatSlider sGamma { get { return pageColor.sGamma; } }
        FlatSlider sSharp { get { return pageColor.sSharp; } }
        Label vBlack { get { return pageColor.vBlack; } }
        Label vWhite { get { return pageColor.vWhite; } }
        Label vGamma { get { return pageColor.vGamma; } }
        Label vSharp { get { return pageColor.vSharp; } }
        HistogramView histView { get { return pageColor.histView; } }
        // histDoc/histData/btnRecolor/btnRecolorClear 在主窗体侧被赋值（直方图重算在
        // Workspace/History，按钮构建在印章页段）—— 这四个用可写别名
        Doc histDoc { get { return pageColor.histDoc; } set { pageColor.histDoc = value; } }
        long[] histData { get { return pageColor.histData; } set { pageColor.histData = value; } }
        FlatButton btnRecolor { get { return pageColor.btnRecolor; } set { pageColor.btnRecolor = value; } }
        FlatButton btnRecolorClear { get { return pageColor.btnRecolorClear; } set { pageColor.btnRecolorClear = value; } }
        // 批量页演进为「归档」页——批量处理控件删除，
        // 一键归档区从去除页搬入。归档控件别名指向 pageBatch；批量控件别名已删。
        BatchPage pageBatch;            // 归档页（控件在类内；容器名 pnlBatch/BatchPage 沿用）
        Segmented segArchiveMode { get { return pageBatch.segArchiveMode; } }
        FlatProgress progress { get { return pageBatch.progress; } }
        Label lblBatchResult { get { return pageBatch.lblBatchResult; } }
        Label lblOutDir { get { return pageBatch.lblOutDir; } }
        FlatButton btnPickOut { get { return pageBatch.btnPickOut; } }
        FlatButton btnOpenOut { get { return pageBatch.btnOpenOut; } }
        FlatButton btnArchive { get { return pageBatch.btnArchive; } }
        FlatButton btnArchiveCancel { get { return pageBatch.btnArchiveCancel; } }
        FlatButton btnArchiveOne { get { return pageBatch.btnArchiveOne; } }
        Segmented segSave;              // 顶栏的保存模式分段（全局，不属于任何一页）
        // 归档同时处理的页数：8GB 目标机默认 2（每页流水线峰值
        //   内存约 200MB 上下，2 页并行增量 ~0.5GB，安全）；CLI --parallel 可调 1~4。
        //   OCR 推理在 Ocr.OcrGate 上整段串行，收益主要来自非 OCR 步骤。
        //   UI 不暴露这个设置：默认 2 就是最优，少一个开关少一分困惑。
        volatile int archiveParallel = 2;  // 补 volatile：后台归档线程要读
        FlatButton btnTheme;       // 主题切换（浅色 / 深色工作台）


        // 裁剪页类化——控件移入 CropPage，这里留只读别名
        CropPage pageCrop;              // 裁剪 / A4 画板页（控件在类内）
        Segmented segCropAspect { get { return pageCrop.segCropAspect; } }
        Segmented segA4Dpi { get { return pageCrop.segA4Dpi; } }
        Segmented segA4Orient { get { return pageCrop.segA4Orient; } }
        Segmented segA4Mode { get { return pageCrop.segA4Mode; } }
        FlatButton btnCropDo { get { return pageCrop.btnCropDo; } }
        FlatButton btnTrimBlank { get { return pageCrop.btnTrimBlank; } }
        FlatButton btnA4Crop { get { return pageCrop.btnA4Crop; } }
        FlatButton btnA4Only { get { return pageCrop.btnA4Only; } }
        FlatButton btnPersPts { get { return pageCrop.btnPersPts; } }
        FlatButton btnPersApply { get { return pageCrop.btnPersApply; } }
        FlatButton btnPersClear { get { return pageCrop.btnPersClear; } }


        FlatScroll panelScroll;         // 检查器长页面的细滚动条
        bool syncingScroll;             // 同步滚动条位置时抑制回环
        // sTextSize/vTextSize 已随 TextPage 类化移入 MainForm.Text.cs（本文件有别名）

        // 扫描页类化——控件移入 ScanPage，这里留只读别名
        ScanPage pageScan;              // 扫描页（控件在类内）
        Panel pnlScan;                  // 切页/滚动机制（指向 pageScan）
        string scanDir;                 // “指定文件夹”模式的保存位置
        volatile bool scanCancel;       // 扫描进行中要求取消
        FlatProgress scanProgress { get { return pageScan.scanProgress; } }
        Label lblScanResult { get { return pageScan.lblScanResult; } }
        Panel scanHintCard { get { return pageScan.scanHintCard; } }
        Label lblScanDev { get { return pageScan.lblScanDev; } }
        FlatButton btnDevPrev { get { return pageScan.btnDevPrev; } }
        FlatButton btnDevNext { get { return pageScan.btnDevNext; } }
        Segmented segScanDpi { get { return pageScan.segScanDpi; } }
        Segmented segScanColor { get { return pageScan.segScanColor; } }
        Segmented segScanTarget { get { return pageScan.segScanTarget; } }
        Segmented segScanFmt { get { return pageScan.segScanFmt; } }
        Label lblScanDir { get { return pageScan.lblScanDir; } }
        FlatButton btnPickScanDir { get { return pageScan.btnPickScanDir; } }
        FlatButton btnOpenScanDir { get { return pageScan.btnOpenScanDir; } }
        FlatButton btnScanRun { get { return pageScan.btnScanRun; } }
        FlatButton btnScanCancel { get { return pageScan.btnScanCancel; } }
        List<ScanDeviceInfo> scanDevices; int scanDevIdx;   // 设备状态（RefreshDevices/RunScan 引用）
        bool scanDevQueried;

        // 文件页（批量重命名 / 批量新建文件夹）
        Panel pnlFiles;
        // 文件页类化——控件移入 FilePage，这里留只读别名
        FilePage pageFiles;             // 文件页（控件在类内）
        List<RenameOp> lastRenameOps;   // 撤销上次重命名（换文件夹即失效；Workspace/Files 也引用）
        Segmented segRenameMode { get { return pageFiles.segRenameMode; } }
        Label lblRenamePrefix { get { return pageFiles.lblRenamePrefix; } }
        Label lblRenameStart { get { return pageFiles.lblRenameStart; } }
        Label lblFind { get { return pageFiles.lblFind; } }
        Label lblReplace { get { return pageFiles.lblReplace; } }
        FlatTextBox txtRenamePrefix { get { return pageFiles.txtRenamePrefix; } }
        FlatTextBox txtRenameStart { get { return pageFiles.txtRenameStart; } }
        FlatTextBox txtFind { get { return pageFiles.txtFind; } }
        FlatTextBox txtReplace { get { return pageFiles.txtReplace; } }
        Label lblRenamePreview { get { return pageFiles.lblRenamePreview; } }
        FlatButton btnRenameRun { get { return pageFiles.btnRenameRun; } }
        FlatButton btnRenameUndo { get { return pageFiles.btnRenameUndo; } }
        Label lblRenameLast { get { return pageFiles.lblRenameLast; } }
        Label lblMkBase { get { return pageFiles.lblMkBase; } }
        Label lblMkCount { get { return pageFiles.lblMkCount; } }
        FlatTextBox txtMkBase { get { return pageFiles.txtMkBase; } }
        FlatTextBox txtMkCount { get { return pageFiles.txtMkCount; } }
        FlatButton btnMkRun { get { return pageFiles.btnMkRun; } }
        Label lblMkResult { get { return pageFiles.lblMkResult; } }
        ToolTip tips;
        System.Windows.Forms.Timer previewTimer, uiTimer;

        bool IPageHost.archiveCancel { get { return archiveCancel; } set { archiveCancel = value; } }
        bool IPageHost.archiveThorough { get { return archiveThorough; } set { archiveThorough = value; } }
        CanvasControl IPageHost.canvas { get { return canvas; } }
        Panel IPageHost.content { get { return content; } }
        int IPageHost.pendingPreview { get { return pendingPreview; } set { pendingPreview = value; } }
        System.Windows.Forms.Timer IPageHost.previewTimer { get { return previewTimer; } }
        bool IPageHost.scanCancel { get { return scanCancel; } set { scanCancel = value; } }
        bool IPageHost.syncingText { get { return syncingText; } set { syncingText = value; } }
        ToolTip IPageHost.tips { get { return tips; } }
        // Cw 已是 public 属性，隐式实现接口

        public MainForm() : this(null) { }

        public MainForm(string startPath)
        {
            Text = AppInfo.Title;            // 标题栏带版本号：现场一眼看出装的是哪一版
            Font = Theme.F12;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.WindowBg;
            ClientSize = new Size(1080, 680);
            MinimumSize = new Size(1060, 640);
            StartPosition = FormStartPosition.CenterScreen;
            WindowState = FormWindowState.Maximized;   // 打开就是最大化
            AllowDrop = true;
            KeyPreview = true;

            tempDir = Path.Combine(Path.GetTempPath(), "PicTool_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try { Directory.CreateDirectory(tempDir); } catch { }

            BuildUi();

            previewTimer = new System.Windows.Forms.Timer();
            previewTimer.Interval = 120;
            previewTimer.Tick += delegate { OnPreviewTimer(); };
            uiTimer = new System.Windows.Forms.Timer();
            uiTimer.Interval = 300;
            uiTimer.Tick += delegate { RefreshInfoLine(); };
            uiTimer.Start();

            if (startPath != null && startPath.Length > 0)
            {
                try
                {
                    if (Directory.Exists(startPath)) OpenFolderPath(startPath);
                    else if (File.Exists(startPath)) OpenSingleFile(startPath);
                }
                catch { }
            }

            // 异常恢复检查放到窗体显示之后再问（构造函数里窗口句柄还没建好，BeginInvoke 会抛异常）
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // 父链可见之后子控件的 Visible 才可信：首次显示把右栏滚动量重算一遍
            //（构建期 PageEnd 存的高度全是 14，见 ContentMax 里的注释）
            ApplyContentScroll();
            BeginInvoke((MethodInvoker)delegate { CheckRecovery(); });
        }

        // 上次没走正常退出时，把"改了还没存"的图找回来（详见 src\Core\Recovery.cs）
        void CheckRecovery()
        {
            List<List<Recovery.Item>> sessions;
            try { sessions = Recovery.PendingSessions(); }
            catch { return; }
            int total = 0;
            foreach (List<Recovery.Item> s in sessions) total += s.Count;
            if (total == 0) return;

            DialogResult r = MessageBox.Show(this,
                "上次没有正常退出，有 " + total + " 张改过但还没保存的图。\n\n" +
                "要恢复吗？\n· 选「是」：它们会以无损 PNG 复制到程序自己的恢复文件夹并自动打开，之后由你决定怎么正式保存\n" +
                "· 选「否」：这些修改将被丢弃",
                "异常恢复", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes)
            {
                Recovery.DiscardAll();
                Status("已丢弃上次未保存的修改");
                return;
            }

            // 恢复落点（救援行为不该污染用户的工作目录/桌面）：以前"同一目录
            // → 放源目录的 _已恢复；否则放桌面"——桌面放一张图、崩一次就多一个文件夹。
            // 现在统一放程序自己的地盘：%LOCALAPPDATA%\PicTool\已恢复\<时间戳>\，
            // 恢复完自动打开，用户自己决定去留。
            string folder = RecoveryTargetFolder();
            try
            {
                foreach (List<Recovery.Item> s in sessions) Recovery.RestoreTo(folder, s);
                Recovery.DiscardAll();
                OpenFolderPath(folder);
                Status("已恢复 " + total + " 张图到：" + folder);
            }
            catch (Exception ex)
            {
                Msg("恢复失败：" + ex.Message + "\n\n恢复区的文件还在，可手动查看。");
            }
        }

        // 恢复落点唯一出处（由自检保证：永不指向桌面，也不指向任何源文件目录）
        public static string RecoveryTargetFolder()
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (baseDir == null || baseDir.Length == 0) baseDir = Path.Combine(Path.GetTempPath(), "PicTool");
            return Path.Combine(Path.Combine(baseDir, "PicTool"),
                "已恢复_" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        }

    }
}
