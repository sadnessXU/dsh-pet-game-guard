# dsh-pet-game-guard

Hides your [dsh-pet](https://github.com/PC2005-cloud/dsh-pet) windows while you are playing a game, and puts them back when you are done.

Windows only. Requires dsh-pet to be installed and visible (any `display` value: `desktop`, `web` or `both`).

- While a game or a fullscreen app is in the foreground, the pet's windows are hidden with `ShowWindow`.
- When you return to the desktop, the same windows are shown again, still on top and without stealing focus.
- Nothing in dsh-pet's own configuration is modified, and the pet process keeps running: it just is not visible.
- A tray icon ("宠物游戏守卫" / Pet Game Guard) lets you pause the guard, show the pet immediately, reload the config or quit.

## Install

```sh
dsh plugin --profile web add dsh-pet-game-guard
```

Use your own profile name if the host is not `web`:

```sh
dsh plugin --profile desktop add dsh-pet-game-guard
```

Restart the host once. Nothing else is needed: the plugin unpacks its helper binary and starts it with DSH, and stops it when DSH exits.

Local development:

```sh
dsh plugin --profile web add link:/absolute/path/to/dsh-pet-game-guard
```

## What it decides from

A tick every 350 ms, three signals, with a debounce of 2 consecutive identical ticks:

1. **Fullscreen window** — the foreground window covers the whole monitor and has no caption (`WS_CAPTION`). Maximised windows are excluded, because they keep the caption or leave the taskbar gap.
2. **Game process** — the foreground executable lives in a game directory. Steam libraries are read from the registry plus `libraryfolders.vdf`, `steamapps\common` is added, and the usual Epic / Xbox / GOG / Riot / WeGame folders are appended. `extra_game_dirs` and `extra_game_exes` extend this by hand.
3. **Ignored processes** — `ignore_processes` is checked first, so a fullscreen DeepSeek Harness window never hides the pet.

Hiding is done by window title prefix (`dsh-pet`) and class (`Chrome_WidgetWin_1`), so it works for every pet in `display: desktop` and `display: both` without knowing their ids.

## Files

The helper is installed to `%LOCALAPPDATA%\PetGuard\`:

| File | Purpose |
| --- | --- |
| `PetGuard.exe` | the helper; replaced on plugin update |
| `config.ini` | settings; created once, then never overwritten |
| `petguard.log` | written only when `debug_log=1`, rotated at 512 KiB |
| `selftest.txt` | output of the command line modes below |

The helper must live outside the DSH workspace: a process started from a directory with a low integrity label has no permission to call `ShowWindow` on the pet's windows and fails with `err=5`.

## config.ini

| Key | Default | Meaning |
| --- | --- | --- |
| `enabled` | `1` | `0` disables the guard entirely (pet always visible) |
| `poll_ms` | `350` | detection interval in milliseconds |
| `hide_after` | `2` | consecutive "in game" ticks before hiding |
| `show_after` | `2` | consecutive "not in game" ticks before showing |
| `hide_when_fullscreen` | `1` | hide when a fullscreen window is in front |
| `hide_when_game_process` | `1` | hide when the foreground process is a game |
| `scan_steam_libraries` | `1` | scan Steam libraries automatically |
| `rescan_minutes` | `5` | how often the game directory list is rebuilt |
| `debug_log` | `1` | write `petguard.log` |
| `ignore_processes` | `explorer.exe,DeepSeek Harness.exe` | never hide when these are in front (comma separated, exe names) |
| `extra_game_dirs` | *(empty)* | extra game directories, whole subtree (comma separated) |
| `extra_game_exes` | *(empty)* | extra game executable names (comma separated) |

After editing, right-click the tray icon and choose 重新加载配置 (Reload config).

## Command line

```sh
PetGuard.exe --once     # print one detection result to selftest.txt
PetGuard.exe --dirs     # print the scanned game directories and extra exe names
PetGuard.exe --list     # list the pet windows it can see
PetGuard.exe --hide     # hide the pet now
PetGuard.exe --restore  # show the pet now
PetGuard.exe --status   # print the current status
PetGuard.exe --quit     # ask a running instance to exit (restores the pet first)
```

## Limitations

- Windows only; the helper is a .NET Framework 4 executable compiled from `assets/PetGuard.cs`.
- It hides windows, it does not stop the pet: dsh-pet's own automatic whispering keeps calling the model on its usual schedule. Turn `whisperEnabled` off in the pet's config if that matters to you.
- A borderless fullscreen video (for example YouTube in fullscreen) also counts as fullscreen and hides the pet. Raise `hide_when_fullscreen=0` if you only want games.
- The guard runs while DSH runs. When DSH exits, it exits too.

## Changelog

- **0.1.2** — Fix the tray icon silently disappearing: listen for the `TaskbarCreated` broadcast (re-register after Explorer restarts) and re-register once 20 s after startup to survive a boot race with Explorer.
- **0.1.1** — Recover when the guard is killed externally; no longer kills a user-run instance on uninstall or host exit.
- **0.1.0** — First release.

## License

MIT
