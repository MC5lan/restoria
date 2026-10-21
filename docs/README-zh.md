# Restoria

**把一摞装订档案的平面扫描件，离线变成方向端正、干净、标准 A4 的归档页——Windows 7 也能跑。**

用装订孔判断方向，去掉灰尘与不均匀光照，保留红章的红，输出 A4 归档副本，原图一个字节不动。单个可执行文件，无需安装，不联网。

**适用**：装订成册或打孔装订的档案，平面扫描仪，约 300 DPI。
**不适用**：手机拍照（有透视变形）、胶装书（没有装订孔可读）、书刊扫描仪的书脊弯曲校正。

六页、一次点击、一个 A4 文件夹——这是本版本的真实录屏，不是做的动效：

![Restoria：六页扫描件一键归档成 A4](images/demo.gif)

> 录屏里的页面是下面那张唐写经的裁切，灰度扫描件。更高画质的 MP4 在 `docs/images/demo.mp4`。

- **运行环境**：Windows 7 SP1 ~ Windows 11（.NET Framework 4.8，AnyCPU）
- **界面**：GDI+ 全自绘 + 双缓冲，老机器不卡；浅色/深色双主题
- **代码**：约 2.6 万行 C#，零第三方 NuGet 依赖，只用系统自带编译器构建

---

## 下载

发行版发布在 [Releases 页面](https://github.com/MC5lan/restoria/releases)。

| 文件 | 说明 |
|---|---|
| `Restoria-core.zip` | **约 1 MB，无 AI、无模型。** 解压后直接运行 `Restoria.exe`。想知道它合不合用，下这个最快。 |
| `Restoria-setup.exe` | 完整安装程序，约 45 MB，含 AI 素材。 |
| `Restoria-portable.zip` | 同样内容，免安装：解压后运行 `setup-boot.exe`。 |

两个大包都含可选 AI 素材与 Win7 兼容层。core 包刻意两者都不带：程序会在子进程里探测 AI 运行时，加载不了就关闭相关功能并如实提示。核心流水线在两种包里完全一样。

> **尚未代码签名。** 因为二进制没有签名证书，Windows 会提示"未知发布者"，选"更多信息 → 仍要运行"即可。

**从源码构建**：`build.bat` 产出 `dist\Restoria.exe`。AI 功能另需把 `ai\` 目录复制到旁边（见[启用 AI 功能](#启用-ai-功能)）。

---

## 其他语言

[English](../README.md) · [日本語](README-ja.md) · [Deutsch](README-de.md) · [Français](README-fr.md) · [Español](README-es.md) · [Русский](README-ru.md)

---

## 它对一页纸做了什么

真实页前后对比——唐代写经残卷，公共领域：

![唐代写经残卷的前后对比](images/real-page.png)

> 《大宝积经》残卷，唐，约公元 700 年。大都会艺术博物馆藏，公共领域（CC0）。
> 馆方拍摄的是黑底彩色照片；从整图中裁出纸面后转为灰度——这也是扫描仪处理文字页时给出的形态。
> 命令：`Restoria.exe --archive page.tif out\ 300`。完整 A4 成品见 [这里](images/real-a4.png)。

下面三张图由确定性测试页生成，用于保证可复现。
整页、普通档——灰纸、不均匀光照、灰尘进去；白纸黑字、A4 版式出来：

![前后对比：普通档把发灰、带脏点的扫描页变成干净的 A4 归档页](images/pipeline.png)

同一页的局部。灰尘与灰底没了，每一笔字都在，红章仍然是红的，没有被压成黑块：

![局部前后对比：灰尘与底色被去掉，文字与红章保留](images/detail.png)

歪着进纸的页，在同一次运行里被拉正：

![前后对比：约 3° 的倾斜页在归档流水线里被纠正](images/deskew.png)

> 这些图由 `tools\MakeDemo.bat` 从一张合成测试页生成，不是真实档案——本工具实际处理的档案涉密。测试页与门禁用的是同一张确定性图片，所以演示图不会和代码实际行为脱节。

---

## 功能特性

### 一键归档流水线

方向检测 → 镜像检测 → 去噪 → 去斜 → 黑白电平/自动色调 → A4 标准输出。

- **普通档**：速度与干净度平衡。方向只认装订孔物理证据。
- **极致档**：只删最确定的尘点，删过头自动撤销（**宁可留黑点，也不吃掉字**）；
  无装订孔的页用 OCR 判断方向。适合字迹淡、内容重要的档案。

归档全程**只新增文件**，原图一个字节都不会动；中断后重跑自动跳过已完成页。每卷归档生成报告：每页的尺寸变化、旋转角度与依据、清点数量、耗时。

### 单页编辑

| 页签 | 功能 |
|---|---|
| 去除 | 涂抹去污（笔刷/框选），五种修复模式：普通/增强/混合/纸面重建/AI |
| 裁剪 | 手动裁剪 + A4 画板 |
| 方向 | 手动旋转、一键自动纠偏（投影法倾斜检测，±20°） |
| 调色 | 亮度/对比度/饱和度/色温、色阶、Gamma、USM 锐化（数值可点击直接输入） |
| 印章 | 给褪色的红章补色 |
| 批量 | 按固定参数批量处理（旋转/调色/二值化） |
| 扫描 | WIA 扫描仪直连，输出格式可选（JPG/PNG/TIF） |
| 文件 | 批量重命名、文件夹管理 |
| 文字 | 往图上加文字标注 |

### 可选 AI 修复（MI-GAN）

- 基于 ONNX Runtime 1.14.1 + MI-GAN 模型的涂抹区修复，能把表格线这类长直线延伸过去
- **子进程隔离探测**：启动路径永不加载 onnxruntime.dll，加载失败不影响主程序任何功能
- Win7 兼容：导入表改名的垫片方案（详见 `src/Ai/AiFill.cs` 注释）

`ai/` 目录共 30 个文件、约 59 MB，**仅**服务于 AI 修复与印章 OCR。

### 工程质量

- **134 项无头自检**（`--selftest`）：算法判据、撤销预算、恢复落点、快捷键冲突
- **布局基线回归**（`--layout-check`）：与入库基线逐行比对，有差异就退出 1——这才叫证据，而不只是一份打印
- **四道门禁**（`tools/gate.py`）：自检、布局基线、AI 产物、性能冒烟
- **持续集成**：GitHub Actions 每次推送都会构建并跑门禁
- 归档输出有并行回归背书（极致档 parallel=1/2/4 逐字节一致）

---

## 已知限制

用之前先看这一节，别默认它一定能处理你的材料。

- **方向检测需要物理证据。** 180° 判断依赖装订孔。没有装订孔的页——胶装书、单页散件、双孔装订——**不会**被自动旋转。这是刻意取舍：把本来就端正的表格页转反，比漏掉一页、让人没发现更糟。这类页请手动旋转，或用极致档（会退回 OCR 判断）。
- **针对扫描仪输出的平面扫描件设计。** 手机拍照有透视变形，不保证效果。
- **方向判据按约 300 DPI 标定。** 600 DPI 下装订孔证据落在标定窗口之外，工具会退回"不旋转"。
- **AI 运行时钉死在 ONNX Runtime 1.14.1**（Win7 能用的最后一版）。换模型前先用 `python tools\onnx_ops.py --diff ai\migan.onnx 候选.onnx` 核对算子兼容性（LaMa 因 FFT 算子在该版本 CPU 内核上有已知问题被弃用）。
- **归档保险丝**：桌面、C 盘根目录、系统文件夹、用户主目录不允许整树归档。
- **尚未配置代码签名证书**，首次运行 Windows 会提示未知发布者。

---

## 从源码构建

**环境要求**：Windows + .NET Framework 4.x。别的都不需要——不需要 Visual Studio，不需要还原 NuGet。

```bat
build.bat
```

产物是 `dist\Restoria.exe`。构建脚本会：

1. 检测 `vendor\onnxruntime_c_api.h`。存在则生成 AI 互操作层（`src\Ai\OrtApi.Generated.cs`）并启用 AI 模式；不存在则构建无 AI 版本，其余功能不变。
2. 嵌入图标（`assets\app.ico`，由 `tools\IconGen.cs` 以 GDI+ 路径几何绘制，源码即唯一设计源）与清单（`assets\app.manifest`，声明 Win7~11 支持、asInvoker 不提权）。
3. 生成 `.config`（声明 .NET 4.x 运行时）。
4. 在 exe 旁输出 PDB 并随产品名一起改名，使现场机器的崩溃报告能对应到匹配的符号。

> **关于产物名**：`build.bat` 先编译出内部名 `dist\PicTool.exe`，再按 `appname.txt` 复制为发布名 `dist\Restoria.exe`。两者字节完全相同。`PicTool` 是内部标识符与程序集命名空间；用户看得见的名称一律是 Restoria。

### 构建小体积 core 包

`tools\make_core_package.ps1` 生成约 1 MB 的无 AI 包（只有 exe + config），也可以直接上传到现有 release：

```powershell
.\tools\make_core_package.ps1                       # 生成 dist\Restoria-core.zip
.\tools\make_core_package.ps1 -Tag 1.5.8 -Upload    # 挂到该 release 上
```

### 启用 AI 功能

把 `ai\` 目录复制到 exe 旁边：

```bat
xcopy /e /y ai dist\ai
```

含 `onnxruntime.dll`、`migan.onnx`、`ocr\` 模型与 `win7\` 垫片，共 30 个文件、约 59 MB。不需要就删掉 `ai\` 再构建。

### 跑检查

```bat
build.bat                                  :: 编译
dist\Restoria.exe --selftest               :: ① 算法自检（退出码 0）
dist\Restoria.exe --layout-check           :: ② 布局与基线比对，有差异退出 1
python tools\check_resources.py --strict   :: ③ 资源门禁（LockBits 配对）
python tools\gate.py                       :: ④ 总门禁（含性能冒烟）
```

布局基线在 `tools\golden-layout.txt`。UI 有意变更后用 `--regen-layout` 重新生成。

AI 产物与性能冒烟两道门禁需要固定测试页 `_probe\doc_test.png`，由 `Restoria.exe --make-fixture` 确定性生成。它已入库，所以新克隆的仓库里就有；万一丢失，门禁会现场重建，只有重建本身失败才判失败。

性能基线会记录机器标识。发现基线来自另一台机器时会明确提示而不是静默比对——本机跑 `--regen-perf` 重建即可。

---

## 命令行诊断口

无头模式，供脚本与排查问题用。报告落 `%TEMP%\PicTool-*.txt`，退出码干净，不弹框。

| 参数 | 用途 |
|---|---|
| `--selftest` | 134 项算法/交互自检，退出码 0 = 全过 |
| `--layout` | 导出布局快照（永远退出 0，仅供查看） |
| `--layout-check` | 同一份快照与 `tools\golden-layout.txt` 比对，有差异退出 1 |
| `--archive <输入> <输出> [dpi] [选项]` | 跑归档流水线 |
| `--skew-test <图或文件夹> [页数]` | 倾斜检测专项（含黑边保险对照） |
| `--ai-check` / `--ai-check-silent` | AI 运行时与模型自检（silent 供安装器无人值守用） |
| `--ai-fill <输入> <输出> [x y w h]` | 单图 AI 修复并落盘（离线比对用） |
| `--paper-fill <输入> <输出> [x y w h]` | 单图纸面重建并落盘 |
| `--make-fixture [路径]` | 导出门禁用的确定性测试页 |
| `--bench [基线] [--regen]` | 逐算子计时 + 产物指纹 |
| `--wheeltest[=文件夹]` | 滚轮三区（画布/左栏/右栏）链路自检 |
| `--uityping` | 输入框焦点判据自检（新旧判据并排） |
| `--undotest` | 撤销预算与指纹级回退自检 |
| `--cursortest` | 自绘光标形态自检（截图留证） |
| `--droptest` | 拖放接收树自检 |

环境变量开关：`PICTOOL_AI_TRACE=1`、`PICTOOL_OCR_TRACE=1`、`PICTOOL_SKEW_NOGUARD=1`，以及 `PICTOOL_RECOVERY_ROOT`（重定向恢复区，探针用它确保绝不碰真实用户数据）。

---

## 目录结构

```
├── src/                  全部源码（68 个 .cs，约 2.6 万行）
│   ├── Program.cs        入口、全局异常兜底、诊断模式分发
│   ├── Core/             自检套件、基准、版本、恢复、位图桥
│   ├── Platform/         系统探测、WIA 扫描、文件工具
│   ├── Imaging/          纯图像算法（不依赖 UI）：清洁算子、增强、修复、归档
│   ├── Ai/               可选 AI 叠加：ONNX 互操作、MI-GAN、OCR 方向裁判
│   └── Ui/               界面与交互（唯一依赖 WinForms 的地方）
├── assets/               图标与清单。图标由 tools\IconGen.cs 以 GDI+ 路径几何绘制
├── _probe/               门禁用的固定测试页（用 --make-fixture 可重新生成）
├── tools/                构建与验证工具链
│   ├── gate.py           四道门禁：自检 + 布局 + AI 产物 + 性能
│   ├── check_resources.py 资源门禁（LockBits 配对）
│   ├── onnx_ops.py       ONNX 算子兼容性扫描（换模型前先跑）
│   ├── archive_demo.py   归档前后对比图生成
│   ├── make_core_package.ps1  生成/上传约 1 MB 的无 AI 包
│   ├── make_package.ps1  打包链（编译→组装→签名→自校验）
│   └── install/          安装器与卸载器源码
├── ai/                   可选 AI 素材：ONNX 运行时 + MI-GAN + OCR 模型 + Win7 垫片
├── vendor/               onnxruntime_c_api.h（生成互操作层用）
├── docs/                 多语言 README 与架构说明
├── build.bat             一键构建
├── CHANGELOG.md          版本变更记录
└── THIRD_PARTY_NOTICES   全部第三方组件的许可证
```

依赖方向（单向，不要反向引用）：

```
Ui ──→ Imaging / Ai / Platform / Core
Ai ──→ Platform（SysInfo）
Imaging ──→ Platform（少量）
Platform ──→ （无）
```

`Imaging` 与 `Ai` 里不允许出现 `System.Windows.Forms`；算法只接收参数、返回新位图，不改全局状态。

---

## 第三方素材与许可证

本仓库代码以 [MIT](../LICENSE) 发布。以下第三方组件随发布包分发，各保留其自身版权与许可证：

| 组件 | 版本 | 版权 | 许可证 |
|---|---|---|---|
| ONNX Runtime（`ai\onnxruntime.dll`、`ai\win7\onnxruntime.dll`） | 1.14.1 | © Microsoft Corporation | MIT |
| PP-OCRv3 检测/识别模型 + 字表（`ai\ocr\`） | PP-OCRv3 | © PaddlePaddle Authors | Apache-2.0 |
| MI-GAN 修复模型（`ai\migan.onnx`） | 512, ICCV 2023 | © 2024 Picsart AI Research | MIT |
| Win7 API-set 兼容垫片（`ai\win7\w7shim-*.dll`） | 本项目构建 | — | MIT |
| Win7 API-set 兜底副本（`ai\win7\api-ms-win-core-*.dll`） | Windows 7 SP1 | © Microsoft Corporation | 见 THIRD_PARTY_NOTICES |

各组件的完整许可证文本与说明见 **[THIRD_PARTY_NOTICES](../THIRD_PARTY_NOTICES)**。

上述组件**仅**服务于可选的 AI 修复与印章 OCR。核心流水线（方向、纠偏、去噪、去渍、黑白电平、色彩、A4 输出）是纯 C# 实现，不含任何原生依赖，不需要 `ai/` 目录也能完整运行。程序会先在子进程中探测 AI 运行时能否加载，失败时干净地关闭 AI 功能，不影响主程序。

## 致谢

- [ONNX Runtime](https://github.com/microsoft/onnxruntime) — 跨平台推理引擎
- [MI-GAN](https://github.com/Picsart-AI-Research/MI-GAN) — 图像修复模型（ICCV 2023）
- [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR) — PP-OCRv3 检测/识别模型
