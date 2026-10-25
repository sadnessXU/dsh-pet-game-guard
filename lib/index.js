/**
 * dsh-pet-game-guard —— 宿主半侧
 *
 * 职责：把随包分发的 PetGuard.exe 装到用户目录、跟着 DSH 一起起停，并在它自己跑挂时拉起。
 * 所有「判定 / 隐藏窗口」的逻辑都在 PetGuard.exe 里（C# 源码见 assets/PetGuard.cs），
 * 这里只做进程生命周期，不碰 dsh-pet 的任何配置。
 */
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { spawn, spawnSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'

// lib/index.js -> 包根目录
const PACKAGE_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const ASSETS = path.join(PACKAGE_ROOT, 'assets')
const EXE_NAME = 'PetGuard.exe'

/**
 * 必须装在用户目录：DSH 工作区/沙箱目录可能带低完整性标记，
 * 低完整性进程没有权限对桌宠窗口调 ShowWindow（会 err=5），守卫在那里等于瞎的。
 */
function installDir() {
  const localAppData = process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local')
  return path.join(localAppData, 'PetGuard')
}

function readIfExists(file) {
  try {
    return fs.readFileSync(file)
  } catch {
    return null
  }
}

function sameBytes(a, b) {
  return a !== null && b !== null && a.length === b.length && a.equals(b)
}

/** 请正在运行的那一份自己退出（命名事件，不是硬杀）；没有实例时静默返回。 */
function requestQuit(exe, timeoutMs = 3000) {
  try {
    spawnSync(exe, ['--quit'], { timeout: timeoutMs, windowsHide: true, stdio: 'ignore' })
  } catch {
    /* 没有在跑的实例属于正常情况 */
  }
}

/**
 * 现在机器上到底有没有守卫进程。
 * 用来区分两种 exit code 0：一种是「另一份已经在跑」（别再拉起），
 * 一种是「它自己退了」（该拉回来）。Windows 自带 tasklist，不需要额外依赖。
 */
function isGuardRunning() {
  try {
    const out = spawnSync('tasklist', ['/FI', 'IMAGENAME eq PetGuard.exe', '/NH'], {
      encoding: 'utf8',
      windowsHide: true,
      timeout: 4000,
    })
    return /PetGuard\.exe/i.test(out?.stdout ?? '')
  } catch {
    // 查不出来就当作在跑：宁可少拉一次，也不要拉起第二份互相打架
    return true
  }
}

/** 把 exe 与默认配置放到用户目录；已存在且内容相同的文件不动（保住用户改过的 config.ini）。 */
function ensureInstalled(logger) {
  const dir = installDir()
  fs.mkdirSync(dir, { recursive: true })

  const exeDst = path.join(dir, EXE_NAME)
  const srcBytes = readIfExists(path.join(ASSETS, EXE_NAME))
  if (!srcBytes) throw new Error('包内缺少 assets/PetGuard.exe')

  if (!sameBytes(srcBytes, readIfExists(exeDst))) {
    // 覆盖前先停旧进程，否则 Windows 会以「文件正被使用」拒绝写入
    requestQuit(exeDst)
    fs.writeFileSync(exeDst, srcBytes)
    logger?.info?.(`[pet-game-guard] 已更新 ${exeDst}`)
  }

  const cfgDst = path.join(dir, 'config.ini')
  if (!fs.existsSync(cfgDst)) {
    fs.copyFileSync(path.join(ASSETS, 'config.ini'), cfgDst)
    logger?.info?.(`[pet-game-guard] 已写入默认配置 ${cfgDst}`)
  }

  return { dir, exe: exeDst, config: cfgDst }
}

export default {
  name: 'pet-game-guard',

  apply(ctx) {
    const logger = ctx?.logger ?? console
    if (process.platform !== 'win32') {
      logger.info?.('[pet-game-guard] 只支持 Windows，已跳过')
      return
    }

    let stopped = false
    let child = null
    let restartTimer = null
    let restartDelay = 5000
    /** 当前在跑的那一份是不是我们拉起来的；不是就别去发全局退出信号。 */
    let owned = false
    /** exit code 0 又查不到进程的连击次数，超过阈值就认为配置停用了，不再重试。 */
    let zeroExits = 0

    const exePath = () => path.join(installDir(), EXE_NAME)

    const scheduleRestart = () => {
      if (stopped || restartTimer) return
      restartTimer = setTimeout(() => {
        restartTimer = null
        const delay = restartDelay
        restartDelay = Math.min(restartDelay * 2, 60000)
        logger.info?.(`[pet-game-guard] ${delay}ms 后重启守卫`)
        start()
      }, restartDelay)
      restartTimer.unref?.()
    }

    function start() {
      if (stopped) return

      let installed
      try {
        installed = ensureInstalled(logger)
      } catch (err) {
        logger.error?.(`[pet-game-guard] 安装失败：${err?.message ?? err}`)
        scheduleRestart()
        return
      }

      let proc
      try {
        proc = spawn(installed.exe, [], {
          cwd: installed.dir,
          windowsHide: true,
          stdio: 'ignore',
          // 宿主被强杀时守卫能自己发现并退出，不留孤立托盘进程
          env: { ...process.env, PETGUARD_PARENT_PID: String(process.pid) },
        })
      } catch (err) {
        logger.error?.(`[pet-game-guard] 启动失败：${err?.message ?? err}`)
        scheduleRestart()
        return
      }

      child = proc
      owned = true
      proc.on('error', (err) => {
        logger.error?.(`[pet-game-guard] 进程错误：${err?.message ?? err}`)
      })
      proc.on('exit', (code, signal) => {
        if (child === proc) child = null
        owned = false
        if (stopped) return
        if (code === 0) {
          // code 0 = 互斥体被占用（另一份在跑）或配置里停用了。
          // 先看机器上还有没有 PetGuard.exe：有就别管，没有就当它自己退了、拉回来。
          if (isGuardRunning()) {
            zeroExits = 0
            logger.info?.('[pet-game-guard] 守卫退出：已有实例在运行，交给那一份')
            return
          }
          zeroExits += 1
          if (zeroExits > 3) {
            logger.info?.('[pet-game-guard] 守卫连续正常退出且没有实例，判定为配置停用，不再重启')
            return
          }
          logger.warn?.('[pet-game-guard] 守卫退出但没有实例在跑，准备重启')
          scheduleRestart()
          return
        }
        zeroExits = 0
        logger.warn?.(`[pet-game-guard] 守卫异常退出（code=${code} signal=${signal}），准备重启`)
        scheduleRestart()
      })

      logger.info?.(`[pet-game-guard] 已启动：${installed.exe}`)
    }

    const stop = () => {
      stopped = true
      if (restartTimer) {
        clearTimeout(restartTimer)
        restartTimer = null
      }
      // 只在「这份守卫是我们拉起来的」时才发全局退出信号：
      // 否则别的宿主（例如另一个 profile）正在用的那一份会被我们顺手停掉。
      if (owned) requestQuit(exePath())
      if (child) {
        try {
          child.kill()
        } catch {
          /* 忽略 */
        }
        child = null
      }
      owned = false
    }

    start()

    if (typeof ctx.effect === 'function') ctx.effect(() => stop)
    else if (typeof ctx.on === 'function') ctx.on('dispose', () => stop())
    else process.on('exit', () => stop())
  },
}
