# 安装链文件说明

这里同时存在两套安装代码，改名/删除前请先看清楚谁还在用。

## 现行：`Restoria-setup.exe`（Inno Setup 6）

由 `make_package.ps1` 调用 ISCC 编译：

- `Restoria.iss` — 安装包定义（安装位置、向导、快捷方式、载荷来源）
- `EULA.txt` — 许可协议页内容
- `ChineseSimplified.isl` — 官方简中语言文件（保存在仓库内，构建不依赖外网）

产物走标准卸载入口：`unins000.exe` + 注册表卸载登记。`verify_install.ps1`、
`verify_release.ps1`、`smoke_e2e.ps1` 校验的都是这一套。

构建需要本机有 Inno Setup 6 的 `ISCC.exe`。`make_package.ps1` 会先读环境变量
`INNO_SETUP_ISCC`，再找 Inno Setup 6 的默认安装路径。

## 仍在用：便携包 `Restoria-portable.zip`

便携包继续使用自研的 `setup-boot.exe` 引导器（它不出现在 Inno Setup 的安装包里）。
因此下列文件**仍然参与构建**，不能当死代码删：

- `build_setup.ps1` — 编译 `setup-boot.exe` / `uninst.exe`
- `AssemblySetup.cs`、`AssemblyUninst.cs`、`SetupInfo.cs` — 版本信息与程序集属性
- `SetupGui.cs`、`UninstallGui.cs`、`EulaForm.cs`、`EulaText.cs`、`FlatButton.cs`、`install.ps1`、`setup.cmd` — 引导器界面与逻辑

版本号由 `tools\bump_version.ps1` 统一同步到这里的文件。

## 历史：`tools\pack_payload.ps1`

旧的「把载荷塞进 exe 尾部」脚本，只服务于自研单文件安装器。发行链改用 Inno Setup 后
不再调用，按仓库约定保留作参考。它涉及的 `PTBOOT01` / `PTPK0001` 私有索引也已从
打包与验收脚本中移除。
