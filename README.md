# MiaoWu（喵悟）

MiaoWu 是一个本地优先的 Windows 桌面 AI 智能体工作台：把工作区文件、会话与模型调用放进同一个窗口，让智能体在你的目录里读文件、改代码、执行命令，每一步都可审批、可回溯。

当前版本：**v0.8.0**（功能高速开发中，尚不具备使用条件）。变更见 [CHANGELOG.md](CHANGELOG.md)。

## 下载

发布包见 [GitHub Releases](https://github.com/Twilightspark/MiaoWu/releases)，v0.8.0 提供两种：

- `MiaoWu-0.8.0-setup.exe`：管理员安装到 `%ProgramFiles%\MiaoWu`，双击后按提示提权；运行期不需要管理员。
- `MiaoWu-0.8.0-portable.zip`：解压即用，运行其中的 `MiaoWu.exe`。

两者都只需要 .NET Framework 4.8（Windows 7 SP1 及以上）。安装包默认**未签名**，SmartScreen 可能提示“未知发布者”。应用内「设置 → 关于 → 检查更新」读取的就是这里的 latest release，只做版本比较，不自动下载。

## 特性

- **工作区为中心**：左栏管理多个工作区与会话，中栏与智能体对话，右栏预览 Markdown、文本、代码、图片与 diff；启动时右栏默认收起，把问答放到最大（`Ctrl+J` 或问答头部按钮展开）。
- **新会话欢迎态**：品牌图标 + 标题与输入卡作为一组居中，输入卡最宽 720；发出第一条消息后整组滑回栏底。没有工作区时输入卡原地挡住并提示先在左栏选择，版式不跳动。
- **左栏可直接打理**：工作区置顶/重命名/清除会话/删除，会话重命名/置顶/归档/删除，文件重命名与删除（进回收站）；改动即时写回本地，右栏标签同步。
- **多模型接入**：支持 OpenAI-compatible 与 Anthropic Messages 两种协议，第三方预置与自定义供应商并存；模型可在设置中增删与热切换，也可按 Base URL 从 `/models` 一键获取，应用启动与首次引导完成后还会在后台自动补齐新模型；输入框右下的胶囊是「模型 / 推理等级」两级入口，胶囊上直接显示当前模型与等级，等级（Default / Low / Medium / High）只在模型支持思考时作为 `reasoning_effort` 随请求发送；API Key 使用 Windows DPAPI 加密保存在本机。
- **可读的对话**：用户消息是右对齐气泡，助手正文左对齐；工具调用与思考过程按轮折进一行「执行过程」，收起后显示「已完成，用时 N秒」（运行中「运行中，用时 N秒」），点开才看每一步；工具行用中文动作名、参数摘要与状态点，不再摊 JSON；过程行是纯文本行，悬停只把左侧图标换成展开箭头，没有灰底卡片。时间、模型与用量收在每条回答的尾部，会话标题旁显示累计运行时长，生成中还有本轮计时。消息流只在你看到底部时跟随新内容，往上翻不会被拉回；整列内容与输入卡同宽（最宽 720），正文、气泡与过程组的左右边缘都对齐输入卡，滚动条覆盖在内容之上、出现时不挤窄正文，滚到底与输入卡之间留 12 的呼吸区。
- **输入框即命令台**：键入 `/` 调命令（`/compact` 压缩较早对话，`/plan` / `/work` 切计划模式），`@` 按路径引用工作区文件与目录；文件和图片在输入框里显示为 chip，图片随消息一起发送；工具行不再常驻计划 chip，计划模式只由这两条命令切换；输入卡下方三条读数（会话统计 / Token 用量 / 上下文已用）随时可查。
- **设置与关闭行为**：设置收敛为通用 / 模型 / 关于三页；通用页提供外观、语言（预留）、关闭行为与「流程审批（每次询问 / 从不询问）」「任务目标（关闭 / 自动续跑）」，关闭可选“每次询问 / 退出应用 / 后台运行”；关于页显示当前版本，「检查更新」直接读取 GitHub Releases 的最新发布并比较版本（只读，不下载不替换），旁边可打开发布页；托盘不可用（受限权限）时降级为最小化到任务栏并给出提示。
- **工具调用与审批**：读写文件、搜索、执行命令都经过路径沙箱与风险分级；审批以右下角通知出现（标题带风险级别，可就地展开 diff），允许一次 / 本会话允许 / 拒绝，不打断消息流；写文件自动备份，可撤销，缺父目录会自动创建；工具失败会附一句可执行的下一步建议（超时改用抓取、路径不存在先 `glob`、被拒不要原样重试），同一调用连续失败第 2 次起明确提示停手。文件读写只认严格 UTF-8（读取时剥离 BOM、写入不写 BOM），命令输出按 UTF-8 解码，抓取按响应头 charset 解码。
- **自主推进**：模型可以把一段任务写成会话目标并自动续跑，开启计划模式后先出计划再执行；需要选择时用右下角提问（选项即动作、可跳过），也能创建定时任务、跑后台命令、搜索与抓取网页。
- **定时任务**：左栏时钟图标把中/右栏换成任务页——左侧按已开启 / 已暂停 / 已结束 / 全部筛选与搜索，右侧用分组卡片编辑名称、内容、执行位置、关联工作区、执行模型、推理等级、重复（仅一次 / 每天 / 每周 / 每月 / 自定义）、间隔、星期、时间与只读时区，并查看每次投递的运行记录；已有任务的立即执行、暂停 / 恢复与删除收进 `⋯` 菜单。
- **能力市场**：左栏底部入口打开整页技能管理——工作区技能与继承的用户级技能同列并标来源，可搜索、导入技能文件夹、按当前工作区启用 / 停用；详情里能看简介、使用方式、来源路径、包含文件与诊断，也能只读浏览 `SKILL.md` 正文。停用只影响模型的下一次请求，`skill` 工具与页面共用同一份目录。
- **后台任务看得见**：会话标题行在「⋯」左侧显示「N 个任务」（有任务在跑时只报运行数并带呼吸点），点开是 340 宽下拉，分「运行中 / 已结束」两组，能看到命令、状态与耗时，直接读实时输出、停止任务，或清空已结束记录。
- **首次启动引导**：主窗口内两栏完成第一个工作区与可选模型供应商；完成前不进入三栏。
- **本地数据**：配置、会话与审计日志保存在本机（`用户目录\.miaowu`），不依赖云端服务。
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

`scripts\publish.ps1` 执行 Release 构建；检测到 Inno Setup 6 时会一并生成安装包与便携包：

- `.artifacts\installer\MiaoWu-0.8.0-setup.exe`：**管理员安装**（`%ProgramFiles%\MiaoWu`），
  **双击即弹 UAC**（`publish.ps1` 编译后用 `scripts\patch-setup-manifest.ps1` 把 SetupLdr 清单改成
  `requireAdministrator`，Windows 在进程创建时就提权）；.NET Framework 4.8 检测，开始菜单 /
  可选公共桌面与开机启动，卸载时询问是否保留 `%USERPROFILE%\.miaowu`；装完以原用户身份启动，
  运行期不需要管理员。
- `.artifacts\installer\MiaoWu-0.8.0-portable.zip`：不经过安装器，解压即用，供临时目录不可写时兜底。
- `.artifacts\installer\MiaoWu-0.8.0-setup-offline.exe`：**全量包**，内嵌 .NET Framework 4.8 离线安装程序
  （约 105 MB）。目标机缺 4.8 时，安装器在「准备安装」阶段静默装好 4.8 再继续；已有 4.8 的机器只多背体积，
  不会重复安装。生成方式：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\fetch-dotnet-redist.ps1   # 下到 .artifacts\redist\
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -Configuration Release -BundleDotNet
```

`fetch-dotnet-redist.ps1` 只接受微软签名的 `ndp48-x86-x64-allos-enu.exe`（也可以手动下载后放到 `redist\`
或 `.artifacts\redist\`，或用 `-DotNetRedist <路径>` 指定）。全量包里的 4.8 安装程序不做二次压缩（本身已压缩，
再套 lzma2 只多花编译时间），所以体积≈小包 + 105 MB。

⚠️ Windows 7 自带 .NET 4.8 的前提是系统已装 SHA-2 代码签名支持更新（`KB4474419`、`KB4490628`）；
没装的话 4.8 安装程序会直接失败，安装器会把退出码和日志路径（`%TEMP%\ndp48-install.log`）显示出来。

Inno Setup 装在非默认目录时，先设 `INNO_SETUP_DIR` 指向安装目录或 `ISCC.exe`，例如
`$env:INNO_SETUP_DIR="D:\Software\tools\Inno Setup 6"`。

安装器启动时报 `Setup was unable to create the directory "...\Temp\is-*.tmp"`（Error 5）是老版本的
症状：SetupLdr 要先把安装器副本解到 `%TEMP%`，低完整性进程（受限终端、沙箱、部分安全软件）写不了
Medium 标记的 Temp。现在 `setup.exe` 的清单是 `requireAdministrator`，双击会在进程创建时先弹 UAC，
解包发生在高完整性进程里；万一某个启动入口仍然绕过提权，用
`Start-Process -FilePath <setup.exe> -Verb RunAs` 手动提权启动。
从旧版**每用户安装**升级到这版之前，先在「应用和功能」里卸载旧版（旧版装在 `%LOCALAPPDATA%\Programs\MiaoWu`）。

## 签名

构建产物默认**不签名**：`scripts\publish.ps1` 不调用签名工具，`MiaoWu.exe`、安装包与卸载程序都保持未签名
（UAC 与 SmartScreen 会显示“未知发布者”）。需要签名时手工执行：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\sign.ps1 -Path src\MiaoWu.App\bin\Release\net48\MiaoWu.exe
powershell -ExecutionPolicy Bypass -File scripts\sign.ps1 -Path .artifacts\installer\MiaoWu-0.8.0-setup.exe
```

- **开发自签名**：`powershell -File scripts\sign.ps1 -CreateDevCertificate` 生成
  `.artifacts\signing\miaowu-dev.cer` 与 `miaowu-dev.key.json`。私钥用 `RSAParameters` 读文件、
  `CopyWithPrivateKey` 组合，不写用户证书存储，所以在受限终端里也能签。
- **让签名变 Valid**：`powershell -File scripts\trust-dev-cert.ps1` 把证书装进“当前用户”的
  **受信任的根证书颁发机构**与**受信任的发布者**（不需要管理员）。不装的话签名状态是
  `UnknownError`（根证书不受信任），UAC 仍显示“未知发布者”。
- **正式发布**：用 `-PfxPath` / `-PfxPassword`（或环境变量 `MIAOWU_SIGN_PFX` /
  `MIAOWU_SIGN_PFX_PASSWORD`）指向 CA 签发的代码签名证书。**自签名证书不能解除 SmartScreen 的
  “未知发布者”**，对外分发必须换成 OV/EV 证书。
- **卸载程序**：默认不签。要给 `unins000.exe` 也签名，需要在 `installer/MiaoWu.iss` 里自行启用
  `SignTool` 与 `SignedUninstaller`，并在编译时传 `/DSIGN`。

## 权限模型

MiaoWu 运行时**不需要管理员**：安装目录只读，所有可写内容都在当前用户自己的目录里。

| 目录 | 谁写 | 需要的权限 | 谁创建 |
|---|---|---|---|
| `%ProgramFiles%\MiaoWu` | 安装器（提权） | 用户只读 + 执行即可 | 安装器 |
| `%USERPROFILE%\.miaowu`（含 `logs` / `cache` / `backups` / `exports` / `skills` / `attachments`） | 应用 | 当前用户修改 | 首次启动时应用 |
| `<工作区>\.miaowu`（含 `sessions` / `attachments` / `cache` / `skills`，默认隐藏） | 应用 | 当前用户修改 | 首次打开工作区时应用 |
| `%TEMP%` | 安装器（解包）、命令工具（临时脚本） | 当前用户修改 | 系统 |

- 应用在创建全局 `.miaowu` 与工作区 `.miaowu` 时逐个目录做写探针；不可写时先尝试给当前账户补一条可继承的
  完全控制 ACE，仍失败就以“当前账户无法写入目录：…”失败退出，不再等到写日志 / 写会话时才报错。
- 判断权限问题先跑目录自检（普通桌面、非提权终端）：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\check-folders.ps1 -Workspace D:\work\demo
```

输出 `FOLDER_OK` / `FOLDER_DENIED` 与当前进程完整性级别（`Low` 表示已被降权，此时的 DENIED
不代表目录真的没权限）。

## 许可

本项目采用 Apache License 2.0，详见 [LICENSE](LICENSE)。
