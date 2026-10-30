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
| 默认本地开发构建 | 用 `git describe --tags --match 'v[0-9]*' --abbrev=0` 找当前提交可达的最近版本标签，补丁号加一，再加独立构建序号；输出 `build/X.Y.Z.N/`，显示 `X.Y.Z.N-dev`。 |
| `-Version X.Y.Z` 或 `-Version vX.Y.Z` | 构建指定三位版本，不附加开发序号或 `-dev`；本地默认输出 `build/X.Y.Z/`。这只生成本地文件，不创建 Git 标签或 Release。 |
| CI 标签构建 | 使用 `GITHUB_REF_NAME` 对应的三位版本，不附加 `-dev`。 |
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

## 实现与恢复约束

### 接口发现和连接

[AppClient.cs](../src/AppClient.cs) 通过本机 CDP 连接桌宠的 Tauri/WebView2 主窗口。[discover.js](../src/Bridge/discover.js) 动态识别模块和导出能力；[Capabilities.cs](../src/Capabilities.cs) 在连接时校验，运行中每 60 秒检查版本，补数或领取前强制检查。版本变化后仅在完整能力校验成功时更新映射；失败时保留原映射并等待重试，不能把旧映射作为新版已兼容的证据。

启动时等待主窗口就绪；首次普通连接失败也进入重试。启动诊断记录保存失败会抛出独立的 `StartupConfigurationException`，停止后台并保留待确认记录，不进入连接重试。断线后只尝试重连现有桌宠，不自动重启桌宠。状态独立保留断线起点和失败类别：调试入口不存在、主窗口未就绪、接口不兼容及其他连接错误。前 60 秒显示等待；超过 60 秒且调试入口仍不存在时，显示停止后台、正常退出桌宠、重新启动后台的指引。明确的能力契约失败才显示不兼容，资源加载失败或未就绪不作为不兼容证据。连接恢复清除连接提示，保留操作核对与手动暂停状态。启动调试会话与后续只读重连是不同路径，不能用重连去反复修改启动配置。

### 查询与超时

- 正常宝箱时间每 30 秒同步，面板每秒更新显示；超过 90 秒的宝箱数据不再用于有效倒计时显示。
- 补数前可复用 5 秒内同账号的服务器状态；领取前及结果核验读取新状态，因此实际查询频率不等于严格每 30 秒一次。
- 页面共享同账号的并发查询，查询开始至少间隔 2 秒。共享查询本身及观察者均有 8 秒等待上限；超时后允许退避重查。当前实现不保证取消底层网络请求，迟到的旧响应不作为已超时查询的核验结果。
- Worker 按 5、10、20、40、60 秒退避并加入随机间隔；页面查询和事务也维护各自退避。遵守 `Retry-After`，明确收到 HTTP 403/429 时至少等 60 秒。等待期间不执行新补数或领取，恢复不取消用户的手动暂停。
- 面板等待重试时显示“立即重试”，独立发送 `retry` 命令。Worker 与页面查询分别保留不可提前的等待截止时间；手动重试仅跳过普通退避，不清零失败次数，不解除手动暂停或待确认记录，也不跳过事务自身退避。命令完成以 `retryCommandId` 确认，处理中禁用按钮。
- 版本读取、资源发现及 CDP 调用各有预算。领取观察调用使用 12 秒预算，事务在桌宠页面异步执行，不能把观察超时当成请求未执行。
- 正式版本检查在面板启动后及每 6 小时执行，失败后 15 分钟重试；网络请求超时 8 秒，只提示打开发布页，不自动替换程序。

### 领取与补数

[Worker.cs](../src/Worker.cs) 在执行前写入待确认记录，操作使用独立编号和账号摘要。账号摘要由环境与账号标识经 SHA-256 计算；不要将原始账号标识写入新增诊断日志。

[claim.js](../src/Bridge/claim.js) 调用动态发现的桌宠 `openChest` 接口，参数为槽位与累计次数，不依赖领取按钮或动画 DOM。两个宝箱串行处理：请求前读取背包基线，验证消费回执后，保留奖励的物品 ID、名称和类型，复用原 store 的 `applyChestClickConsume(1000)` 和原计数保存方法。必须同时核对服务器扣除 1,000 次、目标槽位进入下一轮、另一槽位未变，以及奖励物品的背包数量增加，才记录领取确认；缺失奖励回执或超过 60 秒仍未观察到背包增量时显示奖励待确认，不重发领取。

确认后通过 `updateUserInfo` 同步宝箱时间，避免覆盖其他计数；识别到原程序支持 `cat-panel-action / ready` 时，请求原面板刷新。该入口会按面板当前可见标签刷新内容，不代表奖励动画或私有冷却缓存已同步；没有导出的安全接口时，不修改原程序的私有计时变量。`uiRefreshRequested` 仅表示刷新事件提交成功。

页面事务保留请求是否已发送、回执、本地扣数和保存阶段。查询、核验及可安全重复的保存步骤可恢复；已提交但结果未知的领取绝不重发。明确的 `70004`、`70005`、`70006` 拒绝，仍须确认计数和槽位未变化，才允许结束本次记录并等待下一次尝试。请求长时间未返回时显示结果未知，不据此重发。

[boost.js](../src/Bridge/boost.js) 按已校验的能力选择计数路径，不直接调用服务器补数接口。旧版通过原生 `cat-panel-action` 事件发送一次 `boost-local-clicks`，观察原 store action 回执及旧上报覆盖，最多观察 180 轮，每轮等待 250ms。正常调度下约 45 秒；计时器延迟或电脑休眠可能延长观察时间。

桌宠 1.0.3 已移除旧批量入口，改为在同一同步批次内调用原 store 的 `incrementLocalClickCount()`，逐次校验实际增量，每批最多 2,000 次，不触发动画。随后通过原保存方法强制保存；保存期间发生正常输入、完整上报或已有回执证明的旧上报覆盖时，最多连续三次尝试保存最新状态，不重新增加次数。保存错误或 8 秒超时后保留原计数回执，按退避仅重试保存；底层旧写入尚未结束时不启动下一次写入。账号、槽位或计数归属无法确认时保留待确认记录。

保存超时不代表底层写入已取消。原生侧首次观察可以先于页面任务结束；重连只读取原操作回执，不重复计数或发送事件，也不能只凭后来的余额增加推断成功。领取保存同样使用 8 秒观察预算，底层未结束时不重叠提交写入。

领取和补数回执保存在桌宠页面内存中，待确认记录保存在助手本地。恢复时必须匹配操作编号、账号及原 store；页面重载、回执丢失、账号变化或其他操作干扰时保留记录，不能仅凭后来的余额和冷却变化认定成功。“确认并继续”校验待确认文件摘要后归档原记录及处理说明，不重放原操作；后台未运行时不会自动启动。

核对期间面板和托盘按 `manuallyPaused` 区分用户暂停意图与待确认导致的暂停。用户可要求核对完成后保持暂停，但有待确认记录时仍禁止直接继续。补数确认统一记录 `boost-confirmed` 或 `boost-overwritten`，附带是否经恢复确认的标记；活动显示也支持已有的 `boost-recovered` 记录，并按其回执区分旧上报覆盖。

### 本机数据与调试入口

程序以 exe 所在目录保存配置和 `runtime/`。同目录后台通过文件锁串行化；这不是跨目录的全局互斥，不能从多个目录同时启动助手。

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

[Platform.cs](../src/Platform.cs) 使用 WebView2 支持的宿主命令行参数 `--edge-webview-switches="--remote-debugging-address=127.0.0.1 --remote-debugging-port=19222"` 启动桌宠；不修改系统注册表或助手自身环境。参数随桌宠进程保留，使主窗口与辅助面板使用一致的 WebView2 环境选项，避免启动后撤回参数导致 `0x8007139F`。完整保留子进程继承的 `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS`，不丢弃用户的代理、GPU 等设置；宿主命令行参数由 WebView2 最后处理，覆盖冲突的调试地址和端口。普通恢复启动不添加助手的调试命令行参数，仍保留用户环境设置。仅设置环境变量的方案已在真实桌宠中观察到调试端口未启用，不再采用。

连接正常时，“停止并恢复桌宠”请求正常退出后普通启动，不强杀；断线时改为“停止后台”，仅停止助手后台。执行停止前由 Worker 再检查实际连接；“停止后台”即使遇到连接恢复，也不退出或启动桌宠。两种停止均保留待确认记录。“退出助手”只断开连接，调试入口保留至桌宠退出。不设置开机启动或定时任务。升级保留 `config.local.json` 和整个 `runtime/`，不为测试删除实际账号的待确认记录。

## CI、分发与发布

[build.yml](../.github/workflows/build.yml) 在 `main` 推送、`v*` 标签推送及手动触发时运行原生检查和四组 JS 检查，再生成便携 ZIP。

当前打包清单严格包含四个文件：exe、exe.config、README.md、config.example.json。打包阶段执行 `--check`，要求文件数为 4、总大小不超过 5 MiB。仓库与分发包不包含本机配置、运行记录、桌宠安装文件或账号数据；分发包也不附带 `docs/` 和 README 预览图片，相关文档需在仓库中查看。

通过检查的 ZIP 上传到 **Artifacts**，保留 30 天；标签推送才进一步创建 **GitHub Release**。Artifacts 不等同于 Release，手动触发工作流本身不创建 Release。

发布需单独获得授权，不由本地构建或文档整理隐含授权：

1. 在 [CHANGELOG](../CHANGELOG.md) 添加唯一、非空的 `## [X.Y.Z] - YYYY-MM-DD` 小节，不在开发文档重复该版本的更新内容。
2. 完成必要验证，并明确尚未实测的边界。
3. 经授权推送 `vX.Y.Z` 标签，由工作流构建、打包和发布。工作流将对应小节填入 Release notes；标签格式不符，或记录缺失、为空、重复时发布失败。

## README 动态预览

仅在需要更新预览时手动执行，正常构建和发布不会自动生成图片。Windows 开发环境需要 FFmpeg：

```powershell
.\scripts\Generate-ReadmePreview.ps1
# FFmpeg 不在 PATH 时：
.\scripts\Generate-ReadmePreview.ps1 -FFmpeg 'C:\tools\ffmpeg\bin\ffmpeg.exe'
```

[Generate-ReadmePreview.ps1](../scripts/Generate-ReadmePreview.ps1) 单独构建到 `build/readme-preview/`，通过 `--render-preview 帧输出目录` 生成 11 张模拟 PNG，再编码为 11 秒无限循环的 APNG，输出到 [docs/images/readme-motion.png](images/readme-motion.png)。中间帧位于 `build/readme-preview/frames/`。

渲染复用真实 `Panel.xaml` 和 `Panel.Render()`，不另绘近似界面。程序在创建真实运行目录前分流预览入口，跳过桌宠查找、连接、托盘及更新检查；模拟状态写入独立临时目录，结束后清理该临时目录，不读取或修改真实 `runtime/`。预览展示不是实际账号操作的测试证据。
