# Restoria

给老旧 Windows 机器（包括 2010 年左右的 Win7 学生机）用的**本地档案扫描件处理工具**。
把扫描好的老档案图片，一键变成标准 A4、白底黑字、方向端正、干净无污的归档件。

**特点：单文件、双击即用、零联网、无 Python 无 PyTorch。**

- 目标平台：Windows 7 SP1 ~ Windows 11（.NET Framework 4.8，AnyCPU）
- 界面：GDI+ 全自绘 + 双缓冲，老机器不卡；浅色/深色双主题
- 代码：约 2.6 万行 C#，零第三方 NuGet 依赖，构建只需系统自带的 csc.exe

---

## 功能特性

### 一键归档流水线

方向检测 → 镜像检测 → 去噪 → 去斜 → 黑白电平/自动色调 → A4 标准输出。
点一次按钮，原来手工要做的事情（转方向、裁边、去污、去黑点、排 A4 版面）全部自动完成。

- **普通档**：速度与干净度平衡；方向只认装订孔物理证据
- **极致档**：只删最确定的尘点，删过头自动撤销（**宁可留黑点，也不吃掉字**）；
  无装订孔的页用 OCR 判断方向。适合字迹淡、内容重要的档案
- 归档全程**只新增文件**，原图一个字节都不会动；中断后重跑自动跳过已完成页
- 每卷归档生成「归档报告.txt」：每页的尺寸变化、旋转角度与依据、清点数量、耗时

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
- 模型与运行时已随仓库提供（`ai/` 目录，30 个文件，约 59 MB）

### 工程质量

- **134 项无头自检**（`--selftest`）：算法判据、撤销预算、恢复落点、快捷键冲突等
- **布局基线回归**（`--layout`）：UI 重构的「行为零变化」客观证据
- **资源门禁**（`tools/check_resources.py`）：LockBits/UnlockBits 配对检查
- 归档输出有并行回归背书（极致档 parallel=1/2/4 逐字节一致）

---

## 快速开始

### 方式一：直接用发布包（推荐普通用户）

如果你拿到了打包好的安装程序，双击安装即可（含 AI 素材、VC++ 运行库检查、静默 AI 自检）。
卸载走控制面板「程序和功能」。

不想安装也可以：把 `dist\Restoria.exe`（连同同名 `.config`）拷到目标机双击就能跑，
AI 素材需自己放进 exe 同级的 `ai\` 子目录（可直接复制本仓库的 `ai/`）。

### 方式二：从源码构建（开发者）

**环境要求**：Windows + .NET Framework 4.x（构建用的是系统自带 csc.exe，无需 Visual Studio）

```bat
build.bat
```

产物在 `dist\Restoria.exe`。构建脚本会自动：

1. 检测 `vendor\onnxruntime_c_api.h`，存在则生成 AI 互操作层（`src\Ai\OrtApi.Generated.cs`）并启用 AI 模式；不存在则构建无 AI 版本，其余功能不变
2. 应用图标（`assets\app.ico`）与清单（`assets\app.manifest`，声明 Win7~11 支持、asInvoker 不提权）
3. 生成 `.config`（声明 .NET 4.x 运行时）

> **关于产物名**：`build.bat` 先编译出内部名 `dist\PicTool.exe`，再按 `appname.txt`
> 的内容复制一份为发布名 `dist\Restoria.exe`。两者字节完全相同，只是文件名不同。
> `PicTool` 是内部标识符（程序集命名空间仍为 `PicTool`），对外名称一律是 Restoria。

**启用 AI 功能**：把仓库里的 `ai\` 目录整个复制到 `dist\` 旁边即可
（`ai\onnxruntime.dll` + `ai\migan.onnx` + `ai\ocr\` + `ai\win7\` 垫片，
共 30 个文件 / 约 59 MB）。

**跑自检**：

```bat
dist\Restoria.exe --selftest
```

退出码 0 = 全部通过。更多诊断口见下文。

---

## 命令行诊断口

主程序支持无头诊断模式（报告落 `%TEMP%\PicTool-*.txt`，退出码干净，不弹框）：

| 参数 | 用途 |
|---|---|
| `--selftest` | 134 项算法/交互自检，退出码 0 = 全过 |
| `--layout` | 布局快照（重构前后对比应为逐行一致） |
| `--archive <输入> <输出> [dpi] [选项]` | 一键归档流水线（可脚本化） |
| `--skew-test <图或文件夹> [页数]` | 倾斜检测专项（含黑边保险对照） |
| `--ai-check` / `--ai-check-silent` | AI 运行时与模型自检（silent 供安装器无人值守用） |
| `--ai-fill <输入> <输出> [x y w h]` | 单图 AI 修复并落盘（离线比对用） |
| `--paper-fill <输入> <输出> [x y w h]` | 单图纸面重建并落盘 |
| `--wheeltest[=文件夹]` | 滚轮三区（画布/左栏/右栏）链路自检 |
| `--uityping` | 输入框焦点判据自检（新旧判据并排） |
| `--undotest` | 撤销预算与指纹级回退自检 |
| `--cursortest` | 自绘光标形态自检（截图留证） |
| `--droptest` | 拖放接收树自检 |

环境变量开关：`PICTOOL_AI_TRACE=1`（ONNX 每步追踪）、`PICTOOL_OCR_TRACE=1`（OCR 追踪）、
`PICTOOL_SKEW_NOGUARD=1`（关闭黑边保险）。

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
├── assets/               图标与清单。图标由 tools\IconGen.cs 用 GDI+ 路径几何绘制，
│                         源码即唯一设计源（make_icon.bat 可重新生成所有尺寸）
├── tools/                构建与验证工具链
│   ├── gate.py           门禁：自检 + 布局基线 + AI 产物 + 性能冒烟
│   ├── check_resources.py 资源门禁（LockBits 配对）
│   ├── onnx_ops.py       ONNX 模型算子兼容性扫描（换模型前先跑）
│   ├── archive_demo.py   归档前后对比图生成（人眼验收用）
│   ├── make_package.ps1  打包链（编译→组装→签名→自校验）
│   └── install/          安装器与卸载器源码
├── ai/                   AI 素材：ONNX 运行时 + MI-GAN 模型 + OCR 模型 + Win7 垫片
├── vendor/               onnxruntime_c_api.h（生成互操作层用）
├── docs/                 使用手册、架构说明
├── build.bat             一键构建
└── CHANGELOG.md          版本变更记录
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

## 开发协议（改代码必跑）

```bat
build.bat                                    :: 编译
dist\Restoria.exe --selftest                  :: ① 算法自检（退出码 0）
dist\Restoria.exe --layout                    :: ② 布局快照（与基线逐行对比）
python tools\check_resources.py --strict     :: ③ 资源门禁
python tools\gate.py                         :: ④ 总门禁（含性能冒烟）
```

布局基线存在 `tools\` 的 golden 快照里；UI 有意变更时用 `--regen-layout` 重新生成基线。

---

## 已知限制

- 针对**扫描仪输出的平面扫描件**设计；手机拍照有透视变形，不保证效果
- AI 运行时钉死在 ONNX Runtime 1.14.1（Win7 能用的最后一版）；换模型前先用
  `python tools\onnx_ops.py --diff ai\migan.onnx 候选.onnx` 核对算子兼容性
  （LaMa 因 FFT 算子在该版本 CPU 内核上有已知问题被弃用）
- 极致档方向检测：180° 只认装订孔物理证据（实测误转端正页比漏转更糟）
- 归档保险丝：桌面、C 盘根目录、系统文件夹、用户主目录不允许整树归档

---

## 第三方素材与许可证

本仓库代码以 [MIT](LICENSE) 发布。以下第三方组件随发布包分发，各保留其自身版权与许可证：

| 组件 | 版本 | 版权 | 许可证 |
|---|---|---|---|
| ONNX Runtime（`ai\onnxruntime.dll`、`ai\win7\onnxruntime.dll`） | 1.14.1 | © Microsoft Corporation | MIT |
| PP-OCRv3 检测/识别模型 + 字表（`ai\ocr\`） | PP-OCRv3 | © PaddlePaddle Authors | Apache-2.0 |
| MI-GAN 修复模型（`ai\migan.onnx`） | 512, ICCV 2023 | © 2024 Picsart AI Research | MIT |
| Win7 API-set 兼容垫片（`ai\win7\*.dll`） | — | Microsoft 前向器 | MIT |

各组件的完整许可证文本与说明见 **[THIRD_PARTY_NOTICES](THIRD_PARTY_NOTICES)**。

上述组件**仅**服务于可选的 AI 修复与印章 OCR。核心流水线（方向、纠偏、去噪、
去渍、黑白电平、色彩、A4 输出）是纯 C# 实现，不含任何原生依赖，不需要 `ai/`
目录也能完整运行。程序会先在子进程中探测 AI 运行时能否加载，失败时干净地关闭
AI 功能，不影响主程序。

## 致谢

- [ONNX Runtime](https://github.com/microsoft/onnxruntime) — 跨平台推理引擎
- [MI-GAN](https://github.com/Picsart-AI-Research/MI-GAN) — 图像修复模型（ICCV 2023）
- [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR) — PP-OCRv3 检测/识别模型
