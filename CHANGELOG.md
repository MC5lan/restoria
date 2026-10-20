# 更新日志

所有对外可见的变化都记在这里（新版本在前）。版本号规则：大功能/界面结构变化升主版本，新功能升次版本，修 bug 升修订。

> 本项目以 MIT 许可证开源发布，版本号从 1.0.0 起算。

---

## 1.5.7 — 2026-10-19

<!-- release-notes:start -->
Pre-open-source engineering cleanup. Image processing output is pixel-identical
to 1.5.6; nothing about how pages are processed changed.

**Distribution and compliance**

- Added THIRD_PARTY_NOTICES with the full licence text of every bundled
  component: ONNX Runtime (MIT), PP-OCRv3 (Apache-2.0), MI-GAN (MIT).
  Apache-2.0 requires the licence and NOTICE to ship with the binaries;
  their absence was a compliance gap.
- Installer assets are named in English and match the README download table:
  `Restoria-setup.exe` and `Restoria-portable.zip`.
- The installer and uninstaller compile at all. Unescaped quotes in the
  licence text had made the whole packaging chain fail, and nothing else
  exercised those files.

**Gates that mean something**

- The fixed test page is now in the repository. It used to live outside version
  control, so on a fresh clone two of the four gates skipped, and a skip was
  recorded as a pass: four gates became two.
- A missing test page is regenerated rather than skipped; only a failed
  regeneration fails the gate.
- The performance baseline records which machine recorded it, and a baseline
  from another machine is reported rather than compared against.
- The baseline no longer lowers itself when a run is faster. It only moves on an
  explicit `--regen-perf`, because auto-lowering meant one accidentally skipped
  step could permanently weaken the gate.
- GitHub Actions builds and runs the gates on every push.

**Reliability**

- The PDB now ships with the program. An optimized build without symbols
  produced stack traces of bare addresses, so crash reports could not be
  resolved to a line.
- The `--undotest` and `--cursortest` probes no longer touch user data. They
  deleted `%LOCALAPPDATA%\PicTool\recovery\s-*`, which holds real unfinished
  recovery snapshots, from inside the shipped executable.

**Code**

- Removed two functions with no callers from `Orient.cs`, leaving the reason
  each was abandoned in place so they are not reinstated.
- Fixed comments that contradicted the code they described.
- Corrected the Win7 shim documentation and added `tools/ProbeShim.cs`, which
  lets the Windows loader answer the question directly.
- Verified that the layout gate is sensitive at one pixel: `LayoutGateProbe`
  widens a control by 1px and the snapshot changes.

**Gates that can actually fail**

A gate that cannot fail is decoration. Three of them could not.

- `--layout` never set an exit code. CI ran `Restoria.exe --layout` followed by
  `if errorlevel 1`, and the second line could never fire, so the layout step
  was a green shell that checked nothing. Added `--layout-check`, which
  compares against `tools\golden-layout.txt` and exits 1 on any difference. It
  never rewrites the baseline; only `--regen-layout` does that.
- The snapshot's second line recorded RAM size and core count, which differ per
  machine, so the baseline was bound to the machine that produced it and could
  never pass anywhere else. The line is now fixed text; the real values go to a
  separate report.
- The snapshot also recorded measured AutoSize sizes, which depend on the
  installed font -- the same string measures 219x15 in YaHei UI and 229x16 in
  Segoe UI. Those are now reported as `auto` and the description label uses a
  constant height in diagnostic mode, so the snapshot is identical on any
  machine regardless of fonts. `PICTOOL_UI_FACE` forces a face for verifying
  this.
- The resource gate (unpaired LockBits/UnlockBits) now runs in CI as its own
  blocking step.
- The performance gate is tri-state. A baseline recorded on one machine says
  nothing about another CPU, so that case is now reported as inconclusive
  rather than failed, and marked separately in the summary.
- A gate that fails without saying why is barely better than one that never
  fails. Failure detail is now written to a UTF-8 file and surfaced through
  GitHub annotations, because step logs require authentication to download.
- The gate scripts crashed on English Windows before printing anything: the
  reports are in Chinese and code page 1252 cannot encode them, so the first
  print raised UnicodeEncodeError and the step failed for no reason at all.
  All four reporting scripts now force UTF-8 on their output streams.

**Documentation**

- README is now English-first, with Chinese, Japanese, German, French, Spanish
  and Russian translations.
- Added a Download section; the old README never pointed at the installer.
- Added before/after images generated from the deterministic test fixture.
- Known limitations moved up and expanded: pages without binding holes are not
  rotated automatically, orientation thresholds assume about 300 DPI, and the
  binaries are not code-signed.
<!-- release-notes:end -->

开源发布前的工程整改，功能代码基本未动（图像处理结果与 1.5.6 逐像素一致）。

**分发与合规**

- **补 `THIRD_PARTY_NOTICES`**：ONNX Runtime（MIT）、PP-OCRv3（Apache-2.0）、MI-GAN（MIT）各自的版本、版权、许可证全文。Apache-2.0 要求随包提供 License 与 NOTICE，此前缺失属于合规缺口
- **安装包资产名统一为英文**：`Restoria-setup.exe` / `Restoria-portable.zip`，与 README 下载表一致（此前脚本产出中文名、README 写英文名，Release 上出现过 `Restoria-.exe` 这种空连字符名）
- **安装程序可编译**：许可证正文里的中文引号在逐字字符串中未转义，安装器与卸载器此前根本无法编译，只在打包时才暴露

**门禁变得可信**

- **固定测试页入库**：`_probe/doc_test.png` 由 `--make-fixture` 确定性生成（8bpp 索引色，1.7 MB → 152 KB）。此前它在仓库外，别人 clone 下来两道门禁直接「跳过」，而跳过被记成通过 —— 四道门禁只剩两道
- **跳过改为失败**：测试图缺失时门禁现场重建，只有重建本身失败才判失败
- **性能基线记录机器标识**：来自其他机器的基线会被明确报出，而不是静默拿来比对
- **不再自动下调性能基线**：变快时只提示，基线仅在 `--regen-perf` 时重建 —— 原来每跑一次快的就改小，闸门只会越放越松
- **新增 GitHub Actions**：每次推送自动构建并跑门禁，报告作为 artifact 上传

**可靠性**

- **随包发布 PDB**：优化后的构建此前产生的堆栈只有地址没有行号，用户报错日志无法定位
- **测试探针不再碰用户数据**：`--undotest` / `--cursortest` 此前会删掉 `%LOCALAPPDATA%\PicTool\recovery\s-*`，即真实用户未完成的恢复快照。现在恢复区重定向到 `%TEMP%` 沙箱

**代码清理**

- 删除 `Orient.cs` 中零调用的 `Sharpness()` 与 `PunctGravitySaysUpsideDown()`，并在原处记录废弃理由，防止被重新捡回
- 修正与实现矛盾的注释（「走几何兜底」「行带 ≤ 2 阈值」等已不存在的逻辑）
- 修正 Win7 垫片文档：4 个 `w7shim-*` 是导入表改名补丁，20 个 `api-ms-win-core-*` 是 Win7 API 集覆盖不全时的兜底副本，Win7 上必需。新增 `tools/ProbeShim.cs` 让Windows 加载器自己回答这个问题

**文档**

- README 改为英文主版本，另出中文、日文、德文、法文、西文、俄文六份翻译
- 新增 Download 章节（此前 README 从未指向安装程序）
- 「已知限制」前置并扩写：无装订孔页不自动旋转、方向判据按约 300 DPI 标定、二进制未签名
- 说明 `PicTool` 是内部编译名、`Restoria` 是发布名

## 1.5.6 — 2026-10-15

- **缩放下限改为「整页适合窗口」**：大图不能缩到比整页可见更小，小图可缩到 100% 原尺寸为止；到达下限时状态栏明确提示，不再静默无反应
- **光标最小半径 8px**：缩小状态下涂抹指示标仍然清晰
- **滚轮三处修复**：①处理中不再整条路由拦截（左栏/右栏照常可滚，只跳过画布）；②命中判定加 3px 容差（吸收面板边缘缝隙）；③左右分隔条归入左栏滚动
- `--wheeltest` 扩为三区自检（画布/左栏/右栏），支持带样本目录运行

## 1.5.5 — 2026-10-15

- **光标重画**：深色细圈 + 白色衬边 + 中心小点（废弃遮挡细节的十字），橡皮用虚线圈区分擦除/涂刷
- **`--shot` 截图盲区修复**：自绘光标只在真实悬停出现，静态截图永远拍不到；现在每页截图都模拟悬停，盲区消除
- 新诊断口 `--cursortest`：无头载入合成图 + 模拟悬停 + 画布截图留证

## 1.5.4 — 2026-10-14

- **滑条数值支持手动键入**：调色页 8 条滑条 + 方向页角度滑条，点数值即变输入框，Enter/失焦提交、Esc 取消，输入自动钳到滑条定义的上下限
- 提交与拖动松手同一条链；TextBox 首次点击才创建，布局基线零变化

## 1.5.3 — 2026-10-14

- **页面动作守卫统一**（`GuardAction`）：没图给提示、处理中静默挡，15 个入口一把尺
- 编码规矩立档：涉及用户数据落盘的 catch 必须状态栏/弹窗/日志三选一；诊断口三件套（报告落 %TEMP%、退出码干净、无头不弹框）
- `--undotest` 判据细化：每步编辑存画面指纹，撤销逐帧比对必须精确回到对应历史状态

## 1.5.2 — 2026-10-14

- **撤销预算重写**：每图 10 步 + 96MB 字节帽（超大图自动减步数，撤销链至少留 1 条）；切图时旧图裁到 3 步；撤光后明确提示
- **恢复落点修正**：异常恢复统一落 `%LOCALAPPDATA%\PicTool\已恢复\<时间戳>\`，永不污染桌面/工作目录

## 1.5.1 — 2026-10-14

- 拖放劫持修复：文字编辑卡打开时拒绝一切拖入（未保存修改不再被顶掉）
- 主窗体裸区拖放修复（原来只有 AllowDrop 没挂事件）
- 画布缓存加内存帽 + 构建异常降级直画（绝不让 OnPaint 抛异常）
- 打包自校验不再被无提权会话炸掉

## 1.5.0 — 2026-10-14

- 新增身份色「朱砂」（印泥红）：导航轨选中指示条、工作区页眉锚点——只做点缀绝不做按钮底
- 画布空状态改为「纸+印」组合图（白纸+文字线+右下角朱砂印），深浅两套配色

## 1.4.9 — 2026-10-13

- **拖放修复**：WinForms 拖放事件不冒泡，画布/工作区/右栏/导航轨/工具栏统一开口
- 画布空状态「纸感」改版：浅色主题画布底改暖白，全界面统一浅色

## 1.4.8 — 2026-10-13

UI 与交互层全面审计修复 27 项，要点：

- Toast「悬停暂停」名不副实修复（Enter 停表 / Leave 起表）
- 快捷键设置窗等待态加守卫（Enter/Tab/固定功能键不再被绑走）
- 画布文字编辑卡开着时切工具先提交文字
- 空格+左键平移松左键后 panning 标志不复位修复
- 深色主题主按钮对比度修正（新增 TextOnAccent 令牌）
- 单张归档补保险丝；非滑条编辑后基准 UI 线程立即作废
- 画布加当前 zoom 预缩放缓存（平移/悬停不再每帧全图重采样）

## 1.4.7 — 2026-10-09

- 「开始修复」按钮在修复进行中可点（取消修复不再只能靠快捷键）
- 输入框打字不再被全局快捷键抢键（判据改为直接扫焦点 TextBox）
- 方向页「只纠正框选范围」自动切框选时状态栏文案按页覆盖

## 1.4.6 — 2026-10-09

- 空格键两层含义还原：有修复动作时 = 开始修复；没有时 = 按住平移
- 快捷键设置面板补「固定按键（不可修改）」说明

## 1.4.5 — 2026-10-09

- **黑边保险**：带黑边的图（旋转留黑角、扫描仪黑背景）不再把黑角当墨迹带偏倾斜检测；只在确实有黑边时动手，用绝对灰度线判据
- 倾斜检测范围 ±12° → ±20°（投影桶 1px 宽，|角|≤12° 既有结果不变）
- 诊断口 `--skew-test`；逃生口 `PICTOOL_SKEW_NOGUARD=1`

## 1.4.4 — 2026-10-09

- 修两处页序调整时的快捷键索引遗留（Ctrl+D/Ctrl+L 跳错页、进归档页误切文字工具）
- 快捷键设置对话框按类别分组（工具/操作/编辑/视图/分区）

## 1.4.3 — 2026-10-09

- **上帝类拆分**（纯结构重构，行为零变化）：DocClean.cs 2081 行 → 8 个 partial；MainForm.cs 1648 行 → 10 个文件

## 1.4.2 — 2026-10-08

并发/生命周期七修：

- 去黑点撤销保险并行失效修复（静态字段改返回值传递）
- 关窗原生崩溃修复（Shutdown 与推理共用一把锁）
- OCR 追踪日志改开关默认关（不再拖慢热路径）
- 归档取消粒度细化到单页流水线的每个大步骤边界（点取消不再等整页跑完）
- 页内并行 × 页间并行叠加修复（页间并行时页内陷波退化串行）

## 1.4.1 — 2026-10-08

- 界面重组：工作区右键菜单自绘化（图标+分组+危险色）；导航对调（文字页第 6 位、归档页最后）；去除页回归单一职责

## 1.4.0 — 2026-10-08

- 扫描页新增输出格式选择：跟随扫描仪/JPG/PNG/TIF
- 重命名对话框校验进窗（重名/非法字符原地红字提示）
- 批量重命名确认框换自绘；页面全部类化为 Panel 子类，依赖收敛进 IPageHost 接口

## 1.3.0 — 2026-09-28

- **P2 管线并行**：批量归档多页同时流水（UI 固定 2 页；CLI `--parallel=1~4`）；OCR 推理全局串行保安全；进度条只进不退

## 1.2.0 — 2026-09-28

- 双主题（浅色「墨蓝·纸感」/ 深色「深色工作台」，记住选择）
- 归档边框修复三件套：去扫描黑框、去边缘阴影（判「灰」不判「黑」）、环状透光装订孔识别（中心亮洞判据）
- 极致档方向定案：OCR 只判 90/270，180° 只认装订孔物理证据

## 1.1.0 — 2026-09-24

- 归档新增「普通/极致」两档：极致档去黑点只删最确定的尘点 + 删过头自动撤销保险；无装订孔的页用 OCR 判方向
- A4 版面自动匹配内容横竖；输出文件夹统一「已归档」

## 1.0.0 — 2026-09-21

**首个正式版本（现以 MIT 许可证开源发布）。**

- 一键归档流水线：转正 → 裁边 → 光照归一化 → 自动色阶 → 白底黑字 → 去黑点 → 标准 A4
- 单页编辑：去除（五种修复模式）/ 裁剪 / 方向 / 调色 / 印章 / 文字 / 扫描 / 文件 / 归档，九个功能页
- 可选 AI 修复（ONNX Runtime + MI-GAN，子进程隔离探测，Win7 垫片兼容）
- 自研单文件安装程序（无命令行窗口）+ 控制面板卸载入口
- 134 项无头自检 + 布局基线回归 + 资源门禁
