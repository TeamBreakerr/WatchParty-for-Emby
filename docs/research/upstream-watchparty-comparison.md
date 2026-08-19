# 上游 WatchParty-for-Emby 与当前实现对照

研究日期：2026-08-18  
上游仓库：[`jeremiahbeasley/WatchParty-for-Emby`](https://github.com/jeremiahbeasley/WatchParty-for-Emby)  
上游提交：[`23af2c65e98e4293b45701daeead6021aaaba866`](https://github.com/jeremiahbeasley/WatchParty-for-Emby/tree/23af2c65e98e4293b45701daeead6021aaaba866)  
当前基线：`WatchParty-for-Emby` `53dbe12cb2da03072fbbcece6fe26c6fc5c4c52e`

## 结论先说

上游插件并不是通过客户端 WebSocket 或专用 seek 事件来同步，而是完全依赖 Emby Server 的三类能力：

1. `ISessionManager.PlaybackStart`、`PlaybackProgress`、`PlaybackStopped` 事件；
2. `ISessionManager.SendPlaystateCommand` 向客户端发送 `Pause`、`Unpause`、`Seek`；
3. 一个默认每 5 秒运行的服务器定时器，读取各 Session 的 `PlayState.PositionTicks`，超过默认 10 秒才纠偏。

因此，在“每个用户只有一个播放会话、播放客户端正常上报、网络稳定、只需要数秒级纠偏”的场景中，它的效果可以很好；但它不能可靠区分“用户刚刚拖动进度条”和“播放器正常的周期进度上报”。对于 Emby Web 重建 video element、iOS 后台重连、VidHub/同账号多会话、Xiaoya/115 慢流媒体等场景，上游设计本身没有足够的身份、顺序和来源信息。

这正是本次遇到的两类现象的根源：

- 上游能把进度最终纠回去，但 seek 不是即时、明确的一次性动作，而要等下一次进度事件或定时器；
- 上游能“调用成功”地发送命令，却没有检查目标客户端是否声明支持 Remote Control，也没有等待客户端执行回声，因此日志里的“发送成功”不等同于播放器真的执行了。

当前 fork 的改动不是改变上游的基本协议，而是补上了上游在现代 Emby 客户端和异常时序下缺少的身份、事件来源、顺序化和回声抑制。

## 1. 上游实际如何工作

### 1.1 房间、Master 与 Participant

创建房间时，外部页面把 `MasterUserId` 写入 `WatchPartyItem`；上游创建请求见 [`ExternalWebServer.cs` L1061-L1103](https://github.com/jeremiahbeasley/WatchParty-for-Emby/blob/23af2c65e98e4293b45701daeead6021aaaba866/ExternalWebServer.cs#L1061-L1103)，配置对象的字段和默认同步参数见 [`PluginConfiguration.cs` L123-L183](https://github.com/jeremiahbeasley/WatchParty-for-Emby/blob/23af2c65e98e4293b45701daeead6021aaaba866/PluginConfiguration.cs#L123-L183)。配置页面也强制选择 master 用户：[`Configuration/configPage.js` L1087-L1095](https://github.com/jeremiahbeasley/WatchParty-for-Emby/blob/23af2c65e98e4293b45701daeead6021aaaba866/Configuration/configPage.js#L1087-L1095)。

播放开始时，上游按媒体项找到房间、按 `UserId` 注册参与者，随后用 `UserId == party.MasterUserId` 判断是否为 master：

- `GetOrCreateParticipant` 使用 `Dictionary<string, PartyParticipant>`，键是 `session.UserId`，并把当时的 `session.Id` 存入值：[`ServerEntryPoint.cs` L158-L178](https://github.com/jeremiahbeasley/WatchParty-for-Emby/blob/23af2c65e98e4293b45701daeead6021aaaba866/ServerEntryPoint.cs#L158-L178)；
- `OnPlaybackStart` 注册参与者、记录 `SessionId` 到 `_partySyncedSessions`，并以用户 ID 判断 master：[`ServerEntryPoint.cs` L919-L1021](https://github.com/jeremiahbeasley/WatchParty-for-Emby/blob/23af2c65e98e4293b45701daeead6021aaaba866/ServerEntryPoint.cs#L919-L1021)，特别是 `L953`、`L957`、`L999`；
- `PartyParticipant` 本身同时只有一个 `SessionId` 字段：[`PluginConfiguration.cs` L103-L121](https://github.com/jeremiahbeasley/WatchParty-for-Emby/blob/23af2c65e98e4293b45701daeead6021aaaba866/PluginConfiguration.cs#L103-L121)。

这在“一个账号一个设备”时没有问题。但同一账号同时开官方 iOS、VidHub、浏览器，后加入的会话不会新建一个 participant，而是复用同一个 `UserId` 条目；其活动位置和 `SessionId` 会相互覆盖。上游的 `_partySyncedSessions` 虽然按 `SessionId` 记录初次同步，却无法弥补 participant 主记录按用户覆盖的问题。

### 1.2 PlaybackStart：进入房间时的初次校准

`OnPlaybackStart` 做了几件事：

- 检查用户是否在允许列表和人数上限内，不满足则向该 Session 发送 `Stop`（L927-L936）；
- 用户首次带有 Emby resume position 时，将其跳到 `party.CurrentPositionTicks`（L963-L969）；
- waiting room 中先向客户端发送 `Pause`，等待 ready（L971-L988）；
- 从暂停恢复时先把该 Session 跳到房间位置（L991-L996）；
- 非 master 首次加入时跳到 master 的房间位置，master 自己不跳（L999-L1016）。

所以，上游确实支持“受控端刚开始播放后跟 master”，但它只在 `PlaybackStart` 事件路径上主动做一次。若客户端丢失 `PlaybackStart`、后台回来只发进度、或 Emby 认为还是同一个播放实例，初次校准就可能不执行。

### 1.3 PlaybackProgress：上游把所有进度都当作同一种输入

上游在 [`ServerEntryPoint.cs` L1289-L1348](https://github.com/jeremiahbeasley/WatchParty-for-Emby/blob/23af2c65e98e4293b45701daeead6021aaaba866/ServerEntryPoint.cs#L1289-L1348) 处理 `PlaybackProgress`：

1. 用 `e.Session.Id` 保存上一次暂停状态；
2. 记录参与者的 `e.PlaybackPositionTicks` 和 `e.IsPaused`；
3. 暂停状态从播放变暂停时调用 `HandlePauseAttempt`；
4. 从暂停变播放时调用 `HandleUnpauseAttempt`；
5. 非 master 的进度会进入 `HandleSeekRestrictions`；
6. master 的每一条 progress 都直接写入 `party.CurrentPositionTicks`，并更新 `party.IsPlaying`（L1330-L1340）。

这里没有 `Seek` 字段、用户输入类型、generation/epoch、playback instance、请求序号或“这是旧 video element 的报告”等信息。上游只能根据“当前位置和预期时钟相差很大”去推测 seek；因此 Web 拖动后出现 `1s -> 19.9s -> 1s -> 29.9s` 时，服务端无法仅凭接口区分它们是用户操作、旧心跳、重建播放器的暂态位置还是缓冲恢复。

上游的非 master seek 限制也依赖同一类 progress 数值：[`ServerEntryPoint.cs` L526-L569](https://github.com/jeremiahbeasley/WatchParty-for-Emby/blob/23af2c65e98e4293b45701daeead6021aaaba866/ServerEntryPoint.cs#L526-L569)。它可以把观众手动跳远检测出来，但不能提供 master seek 的即时通知。

### 1.4 Pause/Unpause 与“Remote Control”

上游的控制调用是 Emby Server 内部的 `ISessionManager.SendPlaystateCommand`，不是插件自己维护的客户端 WebSocket：

- `PauseControl == "Anyone"` 时暂停发起者以外的用户：[`ServerEntryPoint.cs` L347-L404](https://github.com/jeremiahbeasley/WatchParty-for-Emby/blob/23af2c65e98e4293b45701daeead6021aaaba866/ServerEntryPoint.cs#L347-L404)；
- `PauseAllUsers` 遍历 `_sessionManager.Sessions`，对播放相同房间媒体的 Session 发送 `PlaystateCommand.Pause`：[`ServerEntryPoint.cs` L406-L444](https://github.com/jeremiahbeasley/WatchParty-for-Emby/blob/23af2c65e98e4293b45701daeead6021aaaba866/ServerEntryPoint.cs#L406-L444)；
- 恢复路径同样发送 `PlaystateCommand.Unpause`：[`ServerEntryPoint.cs` L446-L524](https://github.com/jeremiahbeasley/WatchParty-for-Emby/blob/23af2c65e98e4293b45701daeead6021aaaba866/ServerEntryPoint.cs#L446-L524)。

上游没有检查 `SessionInfo.SupportsRemoteControl` 或 `PlayableMediaTypes`，也没有按返回结果确认播放器执行了命令；发送任务没有抛异常时就会继续记录成功。因此：

- 官方 iOS、官方 Web 等支持 Emby playstate remote command 的客户端，通常能执行；
- VidHub 可以上报 PlaybackProgress，因而可以作为 master，但如果它不声明/不实现 remote control，就不能可靠接收服务端发来的 pause/seek；
- “服务器接受了 SendPlaystateCommand”不等于“VidHub 或某个 WebKit 播放器已跳转”。

这解释了论坛中“效果不错”和本次“VidHub 做 master、官方 iOS 做受控端”并不矛盾：上游最擅长的是官方客户端能够接收命令且上报规则稳定的组合。

### 1.5 Seek：不是专用事件，而是服务器推送的 Playstate Seek

上游没有 master seek API。它的 seek 命令只会在两条路径产生：

1. 非 master 触发 seek 限制时，把该用户跳回房间位置；
2. 定时同步发现某个客户端偏离超过阈值时，调用 `SyncUserToPosition`。

`SyncUserToPosition` 给目标 Session 发送 `PlaystateCommand.Seek`，位置是房间位置加配置的 `SyncOffsetMilliseconds`：[`ServerEntryPoint.cs` L1125-L1152](https://github.com/jeremiahbeasley/WatchParty-for-Emby/blob/23af2c65e98e4293b45701daeead6021aaaba866/ServerEntryPoint.cs#L1125-L1152)。注意：这里的 `Seek` 是服务端发给受控端的命令，不是 master 客户端向插件报告“用户刚拖动了进度条”。

### 1.6 周期同步：5 秒采样、10 秒才纠偏、并发发送

默认配置是 `SyncIntervalSeconds = 5`、`SyncOffsetMilliseconds = 1000`：[`PluginConfiguration.cs` L186-L205](https://github.com/jeremiahbeasley/WatchParty-for-Emby/blob/23af2c65e98e4293b45701daeead6021aaaba866/PluginConfiguration.cs#L186-L205)。

`CheckAndSyncUsers` 每隔该间隔读取 Session 的 `PlayState.PositionTicks`，计算它与 `party.CurrentPositionTicks` 的差值；大于 `SyncToleranceSeconds`（默认 10 秒）才调用 `SyncUserToPosition`：[`ServerEntryPoint.cs` L1034-L1094](https://github.com/jeremiahbeasley/WatchParty-for-Emby/blob/23af2c65e98e4293b45701daeead6021aaaba866/ServerEntryPoint.cs#L1034-L1094)。发送使用 `Task.Run`（L1079-L1083），没有按目标 Session 的串行队列，也没有“最新 seek 覆盖旧 seek”的策略。

因此它的设计目标是“漂移超过阈值后粗粒度纠偏”，不是“master 每次拖动都立即让受控端跳转，并持续按主时钟校准”。对于慢速 115/Xiaoya 流，旧命令还可能在新命令之前完成，客户端自己产生的旧进度又会被下一次服务端采样读到，形成来回跳动。

### 1.7 PlaybackStopped：旧会话竞态

上游停止处理一开始就按 `UserId` 删除 participant：[`ServerEntryPoint.cs` L1350-L1364](https://github.com/jeremiahbeasley/WatchParty-for-Emby/blob/23af2c65e98e4293b45701daeead6021aaaba866/ServerEntryPoint.cs#L1350-L1364)。对 master，随后清理同步/暂停状态并从剩余 participant 中按字典首项挑选新 host（L1366-L1399）；对非 master，再按 `SessionId` 清理集合（L1401-L1417）。

这带来两个具体竞态：

- 同一账号的新播放会话加入后，旧会话迟到的 Stop 仍按用户 ID 删除整个 participant；
- iOS 切后台、重新连接、快速重播时，Stop 不一定表示用户永久离开房间，却会被上游当作离开处理。

这不是 Emby 官方说“Stop 必然等同离开房间”，而是上游插件选择了这样的语义。

## 2. 上游外部 API 是否是 WebSocket

不是。上游 `Api/WatchPartyService.cs` 暴露的是普通 HTTP ServiceStack 路由：

- `GET /WatchParty/Sync`、`GET /WatchParty/Info`、`GET /WatchParty/List`：[`Api/WatchPartyService.cs` L13-L75](https://github.com/jeremiahbeasley/WatchParty-for-Emby/blob/23af2c65e98e4293b45701daeead6021aaaba866/Api/WatchPartyService.cs#L13-L75)；
- 参与者、ready、start 等 GET/POST：[`Api/WatchPartyService.cs` L77-L121](https://github.com/jeremiahbeasley/WatchParty-for-Emby/blob/23af2c65e98e4293b45701daeead6021aaaba866/Api/WatchPartyService.cs#L77-L121)。

上游的 `ExternalWebServer` 也主要是独立 dashboard 的 HTTP Listener、登录、配置、Emby API 代理和聊天接口；源码中没有 WebSocket upgrade/连接管理实现（可见其请求分发集中在 [`ExternalWebServer.cs` L567-L733](https://github.com/jeremiahbeasley/WatchParty-for-Emby/blob/23af2c65e98e4293b45701daeead6021aaaba866/ExternalWebServer.cs#L567-L733)）。播放控制链路是“Emby 客户端 ↔ Emby Server SessionManager 事件/命令”，不是“插件 dashboard ↔ 客户端 WebSocket”。

## 3. 当前 fork 补了什么

### 3.1 从 UserId participant 改为 SessionId participant，同时保留 UserId 作为用户属性

当前 `GetOrCreateParticipant` 通过 `PartySessionRegistry.TryUpsertSession` 注册 `(partyId, session.Id)`，并保留 `UserId`；见 [`ServerEntryPoint.cs` L202-L253](/Users/teambreaker/Projects/WatchParty-for-Emby/ServerEntryPoint.cs:202) 与 [`PartySessionRegistry.cs` L1-L190](/Users/teambreaker/Projects/WatchParty-for-Emby/PartySessionRegistry.cs:1)。同一账号多个设备不再互相覆盖。

当前还保存 `PlaySessionId`，对同一个 Emby `SessionId` 的旧播放实例做退休和迟到进度过滤：[`PartySessionRegistry.cs` L280-L391](/Users/teambreaker/Projects/WatchParty-for-Emby/PartySessionRegistry.cs:280)。

### 3.2 只对声明可接收控制的客户端发送远程播放命令

当前 `SupportsRemoteControlledPlayback` 同时检查 `session.SupportsRemoteControl` 和可播放媒体类型中的 `Video`：[`ServerEntryPoint.cs` L3191-L3197](/Users/teambreaker/Projects/WatchParty-for-Emby/ServerEntryPoint.cs:3191)、[`PlaybackControlCapabilities.cs` L6-L32](/Users/teambreaker/Projects/WatchParty-for-Emby/PlaybackControlCapabilities.cs:6)。

所以 VidHub 作为 master 仍可通过进度事件驱动房间时钟；但“受控端能否收到 pause/seek”取决于受控端是否真实声明并实现 Remote Control。这是能力边界，不是把 VidHub 误报成“命令已执行”。

### 3.3 所有播放事件和出站命令按会话顺序化

当前 PlaybackStart/Progress/Stopped 先进入每个 `SessionId` 的事件队列；出站 Seek 使用每会话的 latest-only 合并，Pause/Unpause 保持串行：[`ServerEntryPoint.cs` L1611-L1660](/Users/teambreaker/Projects/WatchParty-for-Emby/ServerEntryPoint.cs:1611)、[`ServerEntryPoint.cs` L1108-L1153](/Users/teambreaker/Projects/WatchParty-for-Emby/ServerEntryPoint.cs:1108)、[`KeyedAsyncSerialQueue.cs`](/Users/teambreaker/Projects/WatchParty-for-Emby/KeyedAsyncSerialQueue.cs)。这解决了上游 `Task.Run` 多个 seek 并发乱序的问题，但不会让一个本身不支持 Remote Control 的播放器凭空获得执行能力。

### 3.4 Web master seek 有专用通知

当前部署的 Emby Web patch 在 `PlaybackManager.seek` 前后调用 `POST /WatchParty/Playback/Seek`，请求携带 `PositionTicks`、`ItemId`、`DeviceId`；实现脚本见 [`tools/patch_emby_watchparty_progress.py` L1-L73](/Users/teambreaker/Projects/WatchParty-for-Emby/tools/patch_emby_watchparty_progress.py:1)。

服务端用用户、设备、媒体和已注册 master session 解析唯一来源，确认后将 explicit seek 写入主时钟并同步参与者：[`ServerEntryPoint.cs` L1412-L1487](/Users/teambreaker/Projects/WatchParty-for-Emby/ServerEntryPoint.cs:1412)、[`ServerEntryPoint.cs` L1489-L1609](/Users/teambreaker/Projects/WatchParty-for-Emby/ServerEntryPoint.cs:1489)。

这使服务端终于能区分：

- explicit seek：用户明确拖动，立即成为权威位置；
- regular progress：普通心跳，只用于时钟推进和兼容旧入口；
- participant echo：受控端收到服务端命令后的旧位置/暂停回声，不重新升级为房间控制。

### 3.5 generation/settle/echo 与 Web reload 兼容

当前 `PlaybackSyncCoordinator` 保存每会话 pending seek、目标、过期时间、确认状态和暂停回声；`TryBeginSeek` 会抑制重复/过时校准，`ApplyExplicitMasterSeek` 直接提升主时钟，`UpdateMasterPositionGuardingReloadArtifact` 过滤 Web 重建播放器产生的 near-zero/旧单调心跳：[`PlaybackSyncCoordinator.cs` L52-L166](/Users/teambreaker/Projects/WatchParty-for-Emby/PlaybackSyncCoordinator.cs:52)、[`PlaybackSyncCoordinator.cs` L193-L230](/Users/teambreaker/Projects/WatchParty-for-Emby/PlaybackSyncCoordinator.cs:193)、[`PlaybackSyncCoordinator.cs` L599-L710](/Users/teambreaker/Projects/WatchParty-for-Emby/PlaybackSyncCoordinator.cs:599)。

这里的 generation/播放实例不是给浏览器加新协议，而是服务端用 `PlaySessionId`、pending target 和事件顺序，把“属于旧播放实例/旧命令”的迟到事件从当前房间状态中隔离出来。

### 3.6 Stop grace 与旧 Stop 防护

当前 Stop 处理先检查 `PlaySessionId` 是否仍是该 Session 的当前实例，非 master 的短暂 Stop 进入 grace；重新出现的进度可恢复 participant，而不是立即拆除房间状态：[`ServerEntryPoint.cs` L3335-L3403](/Users/teambreaker/Projects/WatchParty-for-Emby/ServerEntryPoint.cs:3335)、[`ParticipantStopGraceTracker.cs`](/Users/teambreaker/Projects/WatchParty-for-Emby/ParticipantStopGraceTracker.cs)。

## 4. 为什么别人说“上游效果不错”仍然可信

从上游代码能推导出几个容易成功的典型条件：

1. 房间里每个账号只有一个设备/Session，所以 UserId 键不会碰撞；
2. master 使用官方客户端，`PlaybackProgress` 稳定且不会在 seek 后长时间保留旧 video element 的心跳；
3. 受控端是能执行 Emby `SendPlaystateCommand` 的官方客户端；
4. 网络延迟不高，5 秒采样和 10 秒容忍区间足以掩盖普通漂移；
5. 测试只验证播放/暂停和最终追上，而不是要求拖动后立即跳转、慢流重缓冲期间严格不反跳、后台重连后保留会话。

在这些条件下，上游“progress 推进主时钟 + 5 秒轮询纠偏 + SendPlaystateCommand”是简单且足够有效的实现。论坛反馈说明这种工作区间真实存在，但不能证明它覆盖了本次 Xiaoya/115、VidHub、Web reload、iOS background、多会话组合。

## 5. 对当前使用方式的准确结论

| 场景 | 上游行为 | 当前 fork 行为 |
|---|---|---|
| VidHub 做 master | 只要能发 `PlaybackProgress`，可推进房间位置；不会因为“不支持接收命令”而不能当 master | 保持该能力，但只有显式声明支持 Remote Control 的目标才接收服务端控制 |
| 官方 iOS 做受控端 | 服务器调用 SendPlaystateCommand；是否执行取决于客户端能力和连接 | 先检查能力，再按 Session 串行发送，并过滤命令回声/旧进度 |
| Web 拖动进度条 | 上游只能等普通 PlaybackProgress 推测，可能延迟、误判、反跳 | patched Web 发送专用 `/WatchParty/Playback/Seek`，服务端立即同步参与者 |
| 受控端慢速缓冲 | 上游定时器可能在旧位置上重复发 Seek，且并发乱序 | pending seek/settle 窗口、latest-only 队列和位置回声抑制 |
| 同账号多设备 | UserId participant 覆盖 SessionId | SessionId + PlaySessionId 分离 |
| iOS 后台/快速重播 | Stop 直接按 UserId 移除 | 检查当前播放实例，短暂 Stop grace，回来后恢复 |

上游并非“写错了却碰巧能用”，而是一个较早、较简单的服务器侧同步器；当前 fork 为了满足“master 拖动后受控端立即跟随、目标设备严格按房间状态、慢流和后台重连不乱跳”的要求，增加了它原本没有的事件来源和时序模型。

## 6. 研究边界

本研究核对了上游仓库提交 `23af2c6` 的源码和当前仓库基线源码。论坛中的个别成功案例没有作为代码事实来源；“哪些客户端具体实现了 Remote Control”仍以该客户端上报的 Emby `SessionInfo.SupportsRemoteControl`、`PlayableMediaTypes` 以及实际命令回声为准，而不能仅凭客户端名称推断。
