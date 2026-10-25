# dsh-pet-game-guard

打游戏时把 [dsh-pet](https://github.com/PC2005-cloud/dsh-pet) 的桌宠收起来，退出游戏再放回来。

仅支持 Windows。需要先装好 dsh-pet，并且它处于可见状态（`display` 为 `desktop`、`web` 或 `both` 都行）。

- 前台是全屏应用或游戏程序时，用 `ShowWindow` 把桌宠的窗口隐藏起来。
- 回到桌面后把同一批窗口显示回来，仍然置顶，且不抢焦点。
- 不改 dsh-pet 的任何配置，桌宠进程继续运行，只是看不见。
- 托盘图标「宠物游戏守卫」可以暂停监视、立即显示宠物、重新加载配置、退出。

## 安装

```sh
dsh plugin --profile web add dsh-pet-game-guard
```

宿主不是 `web` profile 时换成自己的名字：

```sh
dsh plugin --profile desktop add dsh-pet-game-guard
```

重启一次宿主即可。插件会自己解开随包分发的辅助程序，跟着 DSH 一起起停，不需要另外装别的东西。

本地开发安装：

```sh
dsh plugin --profile web add link:/绝对路径/dsh-pet-game-guard
```

## 判定依据

每 350 ms 一拍，三种信号，连续 2 拍一致才动手（防闪）：

1. **全屏窗口** —— 前台窗口铺满整块显示器且没有标题栏（`WS_CAPTION`）。最大化窗口不算，它要么带标题栏，要么留着任务栏的缝。
2. **游戏进程** —— 前台进程的可执行文件在游戏目录里。Steam 库从注册表与 `libraryfolders.vdf` 读取，拼上 `steamapps\common`，再自动补上 Epic / Xbox / GOG / Riot / WeGame 的常见目录；`extra_game_dirs` 与 `extra_game_exes` 可以手工补充。
3. **忽略名单** —— 先查 `ignore_processes`，所以全屏的 DeepSeek Harness 窗口不会把桌宠藏起来。

隐藏靠窗口标题前缀（`dsh-pet`）与窗口类（`Chrome_WidgetWin_1`）定位，因此 `display: desktop` 与 `display: both` 下的每一只宠物都能处理，不需要知道它们的 id。

## 文件位置

辅助程序装在 `%LOCALAPPDATA%\PetGuard\`：

| 文件 | 说明 |
| --- | --- |
| `PetGuard.exe` | 守卫本体，插件升级时自动替换 |
| `config.ini` | 配置；只在第一次写入，之后不会覆盖 |
| `petguard.log` | 仅在 `debug_log=1` 时写入，超过 512 KiB 自动重建 |
| `selftest.txt` | 下面这些命令行模式的输出 |

必须放在 DSH 工作区之外：从带低完整性标记的目录启动的进程没有权限对桌宠窗口调 `ShowWindow`，会一直报 `err=5`。

## config.ini

| 键 | 默认 | 含义 |
| --- | --- | --- |
| `enabled` | `1` | `0` 完全停用（宠物一直显示） |
| `poll_ms` | `350` | 检测间隔（毫秒） |
| `hide_after` | `2` | 连续几拍「游戏中」才隐藏 |
| `show_after` | `2` | 连续几拍「不在游戏」才显示 |
| `hide_when_fullscreen` | `1` | 前台是全屏窗口就隐藏 |
| `hide_when_game_process` | `1` | 前台是游戏程序就隐藏 |
| `scan_steam_libraries` | `1` | 自动扫描 Steam 库 |
| `rescan_minutes` | `5` | 多久重建一次游戏目录表（分钟） |
| `debug_log` | `1` | 是否写 `petguard.log` |
| `ignore_processes` | `explorer.exe,DeepSeek Harness.exe` | 这些进程在前台时一律不隐藏（逗号分隔，写 exe 名） |
| `extra_game_dirs` | 空 | 额外的游戏目录，整棵子树都算（逗号分隔） |
| `extra_game_exes` | 空 | 额外的游戏进程名（逗号分隔） |

改完保存，右键托盘图标选「重新加载配置」即生效。

## 命令行

```sh
PetGuard.exe --once     # 跑一次判定，结果写进 selftest.txt
PetGuard.exe --dirs     # 打印扫描到的游戏目录与额外 exe 名
PetGuard.exe --list     # 列出它能看到的桌宠窗口
PetGuard.exe --hide     # 立即隐藏
PetGuard.exe --restore  # 立即显示
PetGuard.exe --status   # 打印当前状态
PetGuard.exe --quit     # 让正在跑的那一份退出（会先把宠物放出来）
```

## 限制

- 只支持 Windows；守卫是 `assets/PetGuard.cs` 用 .NET Framework 4 编译出来的。
- 它只隐藏窗口，不会让宠物停下：dsh-pet 自己的自动碎碎念仍按原周期调用模型。在意 token 就把宠物配置里的 `whisperEnabled` 关掉。
- 无边框全屏的视频（比如浏览器里全屏看视频）同样算全屏，也会隐藏桌宠；只想针对游戏就把 `hide_when_fullscreen` 设为 `0`。
- 守卫跟着 DSH 一起运行，DSH 退出它就退出。

## 许可

MIT
