# 开发与本地维护

本文面向开发者和 Agent，以当前本地源码和脚本为依据。使用方法见 [README](../README.md)，测试命令与验证边界见 [TESTING](TESTING.md)，正式版本变化只记录在 [CHANGELOG](../CHANGELOG.md)。当前源码可能包含尚未发布的改动，不应据此改写旧版更新记录。

## 构建环境与命令

在 Windows 上使用系统自带的 .NET Framework C# 编译器。脚本使用 `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`，目标为 .NET Framework 4.8、x64，界面使用 WPF。CI 使用 Windows Server 2022 和 `pwsh`；本地应使用能执行脚本的 PowerShell 环境。程序用户无需安装 PowerShell 7、Node.js 或 Python。

以下命令均从仓库根目录执行：

```powershell
.\scripts\Build.ps1
```

默认输出 `WuwaTappoHelper.exe`、`WuwaTappoHelper.exe.config` 和用于编译的 `Version.cs`。需要同时编译并运行全部原生检查时使用 `-Test`。日常通过 `./scripts/Test.ps1 -Suite Worker` 等入口按功能检查，完整命令见 [TESTING](TESTING.md)。桥接脚本测试使用 Node.js；README 动态预览额外需要 FFmpeg，二者均不是助手运行依赖。

## 版本与输出目录

[Build.ps1](../scripts/Build.ps1) 的规则如下：

| 情况 | 程序版本与输出 |
| --- | --- |
| 默认本地开发构建 | 找当前提交可达的最近正式版本标签（排除预发布标签），补丁号加一，再加独立构建序号；输出 `build/X.Y.Z.N/`，显示 `X.Y.Z.N-dev`。 |
| `-Version X.Y.Z` 或 `-Version vX.Y.Z` | 构建指定三位版本，不附加开发序号或 `-dev`；本地默认输出 `build/X.Y.Z/`。这只生成本地文件，不创建 Git 标签或 Release。 |
| 测试版构建 | `-Version X.Y.Z-beta.N` 保留预发布标识，程序集使用三位基础版本。 |
| CI 标签构建 | 使用 `GITHUB_REF_NAME` 对应的正式或测试版本，不附加 `-dev`。 |
| CI 非标签构建 | 最近可达标签的下一补丁版本加 `-dev`，不附加本地序号。 |
| `-OutputDirectory` | 覆盖输出位置，不改变上述版本计算规则。CI 默认输出 `build/`，供后续打包使用。 |

例如最近标签为 `v0.1.4`，本地依次生成 `build/0.1.5.1/`、`build/0.1.5.2/`。末位取 `build/` 中现有同一前三位版本目录的最大序号加一，不推进正式发布版本号；删除所有相应目录后序号可能重新从 1 开始。版本标签不可用时，基础值回退为 `0.0.0`，再按开发构建规则计算。

```powershell
# 显式构建一个三位版本，不发布
.\scripts\Build.ps1 -Version 0.1.5

# 需要隔离输出时，仍使用数字版本目录；序号应按现有目录选择
.\scripts\Build.ps1 -OutputDirectory .\build\0.1.5.2
```

四位本地版本写入程序集版本，完整的 `-dev` 标识写入信息版本；[更新检查](../src/Updates.cs)以程序集前三位比较正式版本。不要把本地末位序号当作新的 Release 版本。

清理旧构建前检查运行中的 exe、配置及 `runtime/`。不要覆盖正在运行的程序，不要把账号操作记录当作构建垃圾删除；保留正在运行的版本，另建数字目录验证。构建脚本本身不负责清理旧版。

## 代码入口

| 位置 | 职责 |
| --- | --- |
| [Program.cs](../src/Program.cs) | 面板、后台、探测和预览入口；传递实例标识。 |
| [Panel.cs](../src/Panel.cs)、[Panel.xaml](../src/Panel.xaml) | WPF 面板、托盘、设置和控制命令。 |
| [Instances.cs](../src/Instances.cs)、[Platform.cs](../src/Platform.cs) | 实例目录、互斥、进程及端口归属、桌宠启动与恢复。 |
| [AppClient.cs](../src/AppClient.cs)、[Capabilities.cs](../src/Capabilities.cs)、[Connection.cs](../src/Connection.cs) | CDP 连接、能力校验和连接状态。 |
| [Worker.cs](../src/Worker.cs)、[Policy.cs](../src/Policy.cs) | 调度、策略、操作确认、退避和恢复。 |
| [Data.cs](../src/Data.cs) | 状态模型、原子写入、待确认记录和日志。 |
| [Bridge](../src/Bridge) | 页面资源发现、状态查询、补数及领取事务。 |
| [Updates.cs](../src/Updates.cs) | 正式版本检查，仅提供发布页入口。 |
| [tests](../tests) | 原生与 JS 回归测试；选择方法见[测试文档](TESTING.md)。 |

调用主线：面板写入控制命令，Worker 根据状态与策略调度，经 AppClient 调用页面桥接；结果写回实例状态和日志供面板显示。不要在面板另建账号操作路径。

## 修改时必须保留的约束

- **连接与启动分开**：只有显式启动流程可以启动桌宠；断线只重连现有进程。启动诊断保存失败应停止后台，不能作为普通连接错误持续重试。
- **能力校验先于操作**：连接、版本变化及写入前检查接口能力。只有完整校验成功才能替换映射；资源未就绪与接口不兼容应分别处理。
- **未知结果不重发**：操作前持久化 `pending.json`。超时不代表请求未执行；重连只能核对原操作回执，不能重新补数或领取。保存重试也不能重复增加次数。
- **确认必须有证据**：恢复匹配操作编号、账号摘要及原 store。领取同时核对消费、目标槽位冷却、另一槽位未变及奖励背包增量；不能仅凭余额变化或页面刷新判定成功。
- **冷却拒绝不弹窗**：桌宠请求层会自动显示业务错误；助手领取使用校验过的同一接口和参数独立发出请求，仅对 `70006` 跳过提示，仍核对拒绝结果并等待。沿用原生背包预检的凭据续期，凭据仅留在页面；不改写桌宠的全局通知或手动领取入口。
- **保持等待和暂停意图**：遵守服务器限流与 `Retry-After`；“立即重试”不能绕过强制等待，不能清除手动暂停或待确认记录。底层写入未结束时不重叠提交。
- **人工确认保留记录**：“确认并继续”校验文件摘要并归档原记录，不重放原操作。页面重载、账号变化或回执丢失时，不猜测操作成功。
- **隔离实例与账号**：连接目标和运行记录不得跨实例复用；账号标识使用包含环境信息的摘要，不把原始账号标识或凭据写入诊断日志。

具体批次、超时和退避参数以对应源码及回归测试为准；本文不复制全部实现分支。真实操作与自动测试的区别见[验证边界](TESTING.md#真实环境验证边界)。

## 本机数据与调试入口

程序以 exe 所在目录保存默认窗口的配置和 `runtime/`。额外实例以规范化程序路径的 SHA-256 为标识，配置及运行目录放在 `runtime/instances/<id>/`；`--instance <id>` 在面板和提权后台间传递。每个实例保留独立的文件锁、控制命令和待确认记录，新增实例不能在原窗口更换绑定路径。默认窗口有待确认操作时禁止换路径。

窗口按数据目录互斥；后台另按当前 Windows 用户和桌宠路径持有命名互斥量，阻止不同助手目录同时接管同一桌宠。推荐始终使用同一份助手，以保留连续的恢复记录。

| 文件 | 用途 |
| --- | --- |
| `config.local.json` | 桌宠路径。 |
| `runtime/preferences.json` | 所选模式。 |
| `runtime/events.jsonl` | 追加式活动日志，面板显示最近三条，暂无自动轮转。 |
| `runtime/status.json`、`runtime/control.json` | 后台状态、接口宝箱时间和面板控制命令。 |
| `runtime/pending.json`、`runtime/history/` | 未确认操作，以及手动确认后归档的原记录、摘要和处理说明。 |
| `runtime/worker.guard`、`runtime/lock.json` | 后台互斥与运行标记。 |
| `runtime/panel-lifecycle.log`、`runtime/panel-launcher-error.log`、`runtime/launcher-status.json` | 窗口生命周期及启动诊断。 |
| `runtime/debug-startup-session.json` | 调试启动方式、进程及连接就绪结果。 |

[Platform.cs](../src/Platform.cs) 使用 WebView2 支持的宿主命令行参数 `--edge-webview-switches="--remote-debugging-address=127.0.0.1 --remote-debugging-port=<port>"` 启动桌宠；每次启动分配可用端口，不修改系统注册表或助手自身环境。连接前通过进程路径、进程树和 TCP 监听进程核实端口归属；歧义或无法核实时拒绝连接。停止并恢复只等待所选桌宠退出，不受另一服的监听端口影响。参数随桌宠进程保留，使主窗口与辅助面板使用一致的 WebView2 环境选项，避免启动后撤回参数导致 `0x8007139F`。完整保留子进程继承的 `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS`，不丢弃用户的代理、GPU 等设置；宿主命令行参数由 WebView2 最后处理，覆盖冲突的调试地址和端口。普通恢复启动不添加助手的调试命令行参数，仍保留用户环境设置。仅设置环境变量的方案已在真实桌宠中观察到调试端口未启用，不再采用。

连接正常时，“停止并恢复桌宠”请求正常退出后普通启动，不强杀；断线时改为“停止后台”，仅停止助手后台。执行停止前由 Worker 再检查实际连接；“停止后台”即使遇到连接恢复，也不退出或启动桌宠。两种停止均保留待确认记录。“退出助手”只断开连接，调试入口保留至桌宠退出。不设置开机启动或定时任务。升级保留 `config.local.json` 和整个 `runtime/`，不为测试删除实际账号的待确认记录。

## CI、分发与发布

[build.yml](../.github/workflows/build.yml) 在 `main` 推送、`v*` 标签推送及手动触发时运行原生检查和四组 JS 检查，再生成便携 ZIP。

当前打包清单严格包含四个文件：exe、exe.config、README.md、config.example.json。打包阶段执行 `--check`，要求文件数为 4、总大小不超过 5 MiB。仓库与分发包不包含本机配置、运行记录、桌宠安装文件或账号数据；分发包也不附带 `docs/` 和 README 预览图片，相关文档需在仓库中查看。

通过检查的 ZIP 上传到 **Artifacts**，保留 30 天；标签推送才进一步创建 **GitHub Release**。Artifacts 不等同于 Release，手动触发工作流本身不创建 Release。

发布需单独获得授权，不由本地构建或文档整理隐含授权：

1. 在 [CHANGELOG](../CHANGELOG.md) 添加唯一、非空的 `## [X.Y.Z] - YYYY-MM-DD` 小节（测试版为 `X.Y.Z-beta.N`），不在开发文档重复该版本的更新内容。
2. 完成必要验证，并明确尚未实测的边界。
3. 经授权推送 `vX.Y.Z` 或 `vX.Y.Z-beta.N` 标签，由工作流构建、打包和发布。测试版发布为 prerelease，不标记为 latest。工作流将对应小节填入 Release notes；标签格式不符，或记录缺失、为空、重复时发布失败。

## README 动态预览

仅在需要更新预览时手动执行，正常构建和发布不会自动生成图片。Windows 开发环境需要 FFmpeg：

```powershell
.\scripts\Generate-ReadmePreview.ps1
# FFmpeg 不在 PATH 时：
.\scripts\Generate-ReadmePreview.ps1 -FFmpeg 'C:\tools\ffmpeg\bin\ffmpeg.exe'
```

[Generate-ReadmePreview.ps1](../scripts/Generate-ReadmePreview.ps1) 单独构建到 `build/readme-preview/`，通过 `--render-preview 帧输出目录` 生成 11 张模拟 PNG，再编码为 11 秒无限循环的 APNG，输出到 [docs/images/readme-motion.png](images/readme-motion.png)。中间帧位于 `build/readme-preview/frames/`。

渲染复用真实 `Panel.xaml` 和 `Panel.Render()`，不另绘近似界面。程序在创建真实运行目录前分流预览入口，跳过桌宠查找、连接、托盘及更新检查；模拟状态写入独立临时目录，结束后清理该临时目录，不读取或修改真实 `runtime/`。预览展示不是实际账号操作的测试证据。
