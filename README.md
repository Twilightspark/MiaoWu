# MiaoWu（喵悟）

MiaoWu 是一个本地优先的 Windows 桌面 AI 智能体工作台：把工作区文件、会话与模型调用放进同一个窗口，让智能体在你的目录里读文件、改代码、执行命令，每一步都可审批、可回溯。

当前版本：**v0.1.0**

## 特性

- **工作区为中心**：左栏管理多个工作区与会话，中栏预览 Markdown、文本、代码、图片与 diff，右栏与智能体对话。
- **多模型接入**：支持 OpenAI-compatible 与 Anthropic Messages 两种协议，模型可在设置中增删与热切换；API Key 使用 Windows DPAPI 加密保存在本机。
- **工具调用与审批**：读写文件、搜索、执行命令都经过路径沙箱与风险分级，执行前可批准或拒绝并查看 diff；写文件自动备份，可撤销。
- **本地数据**：配置、会话与审计日志保存在本机（`%LocalAppData%\MiaoWu`），不依赖云端服务。
- **Windows 7 友好**：基于 .NET Framework 4.8，无需额外运行时。

## 技术栈

- WPF + .NET Framework 4.8，运行时仅依赖 BCL，不引入运行时 NuGet 依赖
- C# 7.3，MVVM
- 测试：xUnit

## 环境要求

- Windows 7 SP1 及以上（x64 / x86）
- .NET Framework 4.8
- 构建：.NET SDK 8/9 或 Visual Studio 2022
- 打包：Inno Setup 6（可选）

## 构建与运行

```powershell
dotnet build MiaoWu.sln -c Debug -m:1
```

产物位于 `src\MiaoWu.App\bin\Debug\net48\MiaoWu.exe`。

也可以直接用脚本：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build.ps1
powershell -ExecutionPolicy Bypass -File scripts\test.ps1
```

`-m:1` 用于受限环境：多节点 MSBuild 依赖命名管道，在被限制的沙箱或 CI 中可能静默失败。

## 目录结构

```text
src/MiaoWu.App             # WPF 界面、视图模型与平台服务
src/MiaoWu.Core            # 领域模型与智能体运行时
src/MiaoWu.Infrastructure  # 存储、模型接入、工具、日志
tests/MiaoWu.Tests         # xUnit 测试
scripts/                   # 构建、测试与发布脚本
installer/                 # Inno Setup 安装脚本
```

## 安装包

`scripts\publish.ps1` 执行 Release 构建；检测到 Inno Setup 6 时会一并生成安装包。

## 许可

本项目采用 Apache License 2.0，详见 [LICENSE](LICENSE)。
