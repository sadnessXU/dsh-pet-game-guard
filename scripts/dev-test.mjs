/**
 * 本地自测：import 插件的宿主半边，用假 ctx 跑一遍 apply -> 起进程 -> dispose -> 收进程。
 * 不需要启动 DSH。用法：node scripts/dev-test.mjs
 */
import { createRequire } from 'node:module'

const require = createRequire(import.meta.url)
const { execFileSync } = require('node:child_process')
const fs = require('node:fs')
const os = require('node:os')
const path = require('node:path')

const plugin = (await import('../lib/index.js')).default
console.log('plugin name =', plugin.name)

const disposers = []
const ctx = {
  logger: console,
  effect(fn) {
    disposers.push(fn())
  },
}

const installDir = path.join(process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local'), 'PetGuard')
const exe = path.join(installDir, 'PetGuard.exe')

function running() {
  try {
    const out = execFileSync('tasklist', ['/FI', 'IMAGENAME eq PetGuard.exe', '/NH'], { encoding: 'utf8' })
    return /PetGuard\.exe/i.test(out)
  } catch {
    return false
  }
}

plugin.apply(ctx)
await new Promise((r) => setTimeout(r, 2500))

const checks = []
checks.push(['exe installed', fs.existsSync(exe)])
checks.push(['config installed', fs.existsSync(path.join(installDir, 'config.ini'))])
checks.push(['guard running', running()])

for (const d of disposers) d()
await new Promise((r) => setTimeout(r, 2500))
checks.push(['guard stopped after dispose', !running()])

let failed = 0
for (const [name, ok] of checks) {
  console.log(`${ok ? 'OK  ' : 'FAIL'} ${name}`)
  if (!ok) failed++
}
console.log(failed === 0 ? 'RESULT: PASS' : 'RESULT: FAIL')
process.exit(failed === 0 ? 0 : 1)
