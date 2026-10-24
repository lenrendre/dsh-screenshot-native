/**
 * dsh-screenshot — host half.
 *
 * Owns everything the client half cannot do:
 *   * Windows-native window enumeration / show / hide (scripts/win.ps1)
 *   * screen, window and region capture (scripts/shot.ps1)
 *   * image cropping (scripts/crop.ps1)
 *   * clipboard and file output
 *
 * The client half is a normal compiled client module and talks to this half over
 * plain HTTP routes registered on the host web server, which is the transport a
 * persistent bundle uses (host.call belongs to dynamic Cordis packages).
 *
 * @module dsh-screenshot
 */

import { spawn } from 'node:child_process'
import { appendFileSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

export const name = 'dsh-screenshot'

// `tools` is declared so Cordis waits for the tools service before calling apply(): resolving it
// lazily does not work here, because the service is provided by another plugin that initialises
// later, so ctx.tools was still undefined when the tools tried to register (observed 2026-10-19).
export const inject = ['webServer', 'tools']

/** Absolute directory holding the PowerShell helpers and this module. */
const HERE = dirname(fileURLToPath(import.meta.url))
const PKG_ROOT = dirname(HERE)
const SCRIPTS = join(PKG_ROOT, 'scripts')

/** Screenshot scratch space; every intermediate PNG lands here. */
// Must live inside the plugin's own package directory. The native engine is spawned by the host
// (a plain user token, not a sandboxed tool child), and the DSH sandbox denies write for that
// token under DSH_HOME and under %TEMP% (verified 2026-10-19: both "access denied" while the
// workspace stayed writable). Each file is deleted right after it is read, so nothing is kept.
const WORK_DIR = join(PKG_ROOT, 'bin', '.shots')

/** Route prefix served to the client half. */
const PREFIX = '/dsh-screenshot-native'

/**
 * PowerShell installations to probe, most capable first.
 *
 * Absolute paths only — see `powershell()` for why a bare command name is not used.
 * The list covers PowerShell 7 (machine and per-user installs) and the Windows
 * PowerShell that ships with Windows itself.
 */
const PWSH_CANDIDATES = [
  'C:\\Program Files\\PowerShell\\7\\pwsh.exe',
  'C:\\Program Files\\PowerShell\\7-preview\\pwsh.exe',
  join(process.env.LOCALAPPDATA ?? '', 'Microsoft', 'PowerShell', 'pwsh.exe'),
  'C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe',
].filter((p) => p !== '')

let resolvedShell = null

/**
 * Locate a PowerShell to run the helper scripts with.
 *
 * Only absolute paths are probed. Returning a bare command name (`pwsh.exe`) would
 * let the plugin load on a machine without PowerShell and then fail later with a raw
 * `spawn pwsh.exe ENOENT` — an error that says nothing about what is actually wrong.
 * A missing shell is a hard requirement, so it is reported as one.
 *
 * @returns {string} absolute path to a usable shell.
 * @throws {Error} when none of the candidates exists.
 */
function powershell() {
  if (resolvedShell !== null) return resolvedShell
  for (const candidate of PWSH_CANDIDATES) {
    if (existsSync(candidate)) {
      resolvedShell = candidate
      return resolvedShell
    }
  }
  throw new Error(
    'dsh-plugin-screenshot needs PowerShell, but none was found on this machine. '
    + `Looked for: ${PWSH_CANDIDATES.join(', ')}. `
    + 'This plugin is Windows-only; install PowerShell 7 (https://aka.ms/powershell) '
    + 'or Windows PowerShell, or set config.shellPath to the interpreter to use.',
  )
}

/**
 * Validate an explicitly configured shell and remember it.
 *
 * Called from apply() so a bad `shellPath` is reported at load time with the config
 * key named, rather than surfacing later as a spawn failure mid-capture.
 *
 * @param {string} path
 * @throws {Error} when the path is not an existing file.
 */
function useConfiguredShell(path) {
  if (!existsSync(path)) {
    throw new Error(`dsh-plugin-screenshot: config.shellPath points at "${path}", which does not exist.`)
  }
  resolvedShell = path
}

/**
 * Run one PowerShell script and parse its single-line JSON reply.
 * @param {string} script - file name inside scripts/.
 * @param {string[]} args - script arguments.
 * @returns {Promise<object>} the parsed payload.
 */
function runScript(script, args = []) {
  return new Promise((resolve, reject) => {
    const file = join(SCRIPTS, script)
    if (!existsSync(file)) {
      reject(new Error(`screenshot: helper not found: ${file}`))
      return
    }
    // Resolve the shell first: a missing PowerShell is a configuration problem the
    // caller should see stated plainly, not an ENOENT from deep inside spawn.
    let shell = null
    try {
      shell = powershell()
    } catch (error) {
      reject(error)
      return
    }
    const child = spawn(
      shell,
      ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', file, ...args],
      { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] },
    )
    let out = ''
    let err = ''
    child.stdout.setEncoding('utf8')
    child.stderr.setEncoding('utf8')
    child.stdout.on('data', (chunk) => { out += chunk })
    child.stderr.on('data', (chunk) => { err += chunk })
    child.on('error', (error) => {
      // ENOENT here means the resolved shell vanished between the existence check and
      // the spawn; anything else is passed through unchanged.
      if (error.code === 'ENOENT') {
        reject(new Error(`screenshot: cannot run PowerShell at ${shell} (ENOENT). The plugin is Windows-only.`))
        return
      }
      reject(error)
    })
    child.on('close', (code) => {
      const line = out.trim().split(/\r?\n/).filter((l) => l.trim().startsWith('{')).pop()
      if (line === undefined) {
        reject(new Error(`screenshot: ${script} produced no JSON (exit ${String(code)})${err ? `: ${err.trim().slice(0, 300)}` : ''}`))
        return
      }
      let parsed
      try { parsed = JSON.parse(line) } catch (error) {
        reject(new Error(`screenshot: ${script} returned unparsable JSON: ${error.message}`))
        return
      }
      if (parsed.status !== 'ok') {
        reject(new Error(`screenshot: ${script} failed: ${String(parsed.error ?? 'unknown')}`))
        return
      }
      resolve(parsed.data ?? {})
    })
  })
}

/** Monotonic suffix so concurrent captures never share a file name. */
let shotSeq = 0
function nextShotPath(tag) {
  shotSeq += 1
  return join(WORK_DIR, `${tag}-${Date.now()}-${String(shotSeq)}.png`)
}

/**
 * Capture the screen, one window, or one region to a PNG file.
 * @param {{mode?: string, hwnd?: number, x?: number, y?: number, width?: number, height?: number}} options
 */
async function capture(options = {}) {
  mkdirSync(WORK_DIR, { recursive: true })
  const mode = options.mode ?? 'fullscreen'
  const out = nextShotPath(mode)
  const args = ['-Mode', mode, '-OutFile', out]
  if (mode === 'window') {
    if (!Number.isFinite(options.hwnd)) throw new Error('screenshot: window mode needs hwnd')
    args.push('-Hwnd', String(options.hwnd))
  } else if (mode === 'region') {
    const x = Math.round(options.x ?? 0)
    const y = Math.round(options.y ?? 0)
    const w = Math.round(options.width ?? 0)
    const h = Math.round(options.height ?? 0)
    if (w <= 0 || h <= 0) throw new Error('screenshot: region needs a positive size')
    args.push('-X', String(x), '-Y', String(y), '-W', String(w), '-H', String(h))
  }
  const result = await runScript('shot.ps1', args)
  // Trust the path generated here, not the one the script echoed back. If that string were mangled
  // on its way out of PowerShell (a non-ASCII path once arrived as "??"), the returned path would
  // point at a file that does not exist.
  return Object.assign({}, result, { file: out })
}

/**
 * List top-level windows with their real screen rectangles.
 * @returns {Promise<Array<object>>}
 */
async function listWindows() {
  const data = await runScript('win.ps1', ['-Action', 'list'])
  return Array.isArray(data.windows) ? data.windows : []
}


export function apply(ctx, config = {}) {
  const snapThreshold = Number(config.snapThreshold ?? 12)
  const ignorePatterns = Array.isArray(config.ignoreWindowPatterns) ? config.ignoreWindowPatterns : []
  const longShotScrollRatio = Number(config.longShotScrollRatio ?? 0.8)
  const longShotMaxFrames = Number(config.longShotMaxFrames ?? 40)
  const defaultAction = String(config.defaultAction ?? 'clipboard')

  // Optional explicit interpreter. Probing absolute paths covers the usual installs,
  // but a portable PowerShell, a renamed folder or a locked-down machine can defeat
  // the list; this lets the deployment say exactly which shell to use.
  if (typeof config.shellPath === 'string' && config.shellPath.trim() !== '') {
    useConfiguredShell(config.shellPath.trim())
  }

  const session = { lastCapture: null }
  const disposers = []

  /** Register one exact route under the plugin prefix. */
  function route(path, handler) {
    disposers.push(ctx.webServer.register({ kind: 'exact', path: `${PREFIX}${path}`, handler }))
  }

  function sendJson(res, status, payload) {
    const body = JSON.stringify(payload)
    res.writeHead(status, {
      'Content-Type': 'application/json; charset=utf-8',
      'Cache-Control': 'no-store',
      'Content-Length': String(Buffer.byteLength(body)),
    })
    res.end(body)
  }

  // --- Native overlay ------------------------------------------------------------------
  // The in-page overlay can only show what fits inside the Harness window and cannot hide it,
  // so the interactive path runs a native WinForms tool instead: it hides the Harness window,
  // freezes the desktop, owns the whole screen and hands back one PNG.
  const NATIVE_EXE = join(PKG_ROOT, 'bin', 'WeChatShot.exe')

  /** 同一时间只允许一个原生浮层：连点两次不会开出两个窗口。 */
  let overlayBusy = false

  /** Run the native overlay once; resolves { cancelled } or { dataUrl, bytes }. */
  function runNativeOverlay() {
    return new Promise((resolve, reject) => {
      const out = join(WORK_DIR, `native-${Date.now()}-${Math.floor(Math.random() * 1e6)}.png`)
      let child
      try {
        child = spawn(NATIVE_EXE, ['--trigger', '--hide-pid', String(process.ppid), '--hide-exe', process.execPath, '--out', out], {
          cwd: dirname(NATIVE_EXE),
          windowsHide: true,
          // Piped stdio is refused by this platform's launcher (spawn EPERM); the tool keeps its
          // own log, so no stream is needed.
          stdio: 'ignore',
        })
      } catch (error) {
        reject(error)
        return
      }
      const timer = setTimeout(() => {
        try { child.kill() } catch { /* already gone */ }
        reject(new Error('the capture overlay timed out'))
      }, 5 * 60 * 1000)
      child.on('error', (error) => { clearTimeout(timer); reject(error) })
      child.on('close', (code) => {
        clearTimeout(timer)
        // One line per overlay exit, written next to the engine's own log, so the two sides can be
        // reconciled when a capture misbehaves (who saw which exit code, and was there a file).
        try {
          appendFileSync(
            join(PKG_ROOT, 'bin', 'wechatshot.log'),
            `${new Date().toISOString()}  [host] overlay closed code=${String(code)} file=${existsSync(out) ? '1' : '0'}\n`,
            'utf8',
          )
        } catch { /* logging must never break a capture */ }
        // The engine writes "<out>.error" when it refuses to capture (for example, when the Harness
        // window could not be hidden and capturing would only produce a picture of the chat itself).
        // Surface that message instead of a bare exit code.
        const errFile = out + '.error'
        const takeError = () => {
          try {
            if (!existsSync(errFile)) return null
            const message = readFileSync(errFile, 'utf8').trim()
            rmSync(errFile, { force: true })
            return message === '' ? null : message
          } catch {
            return null
          }
        }
        if (code === 2) { resolve({ cancelled: true }); return }
        if (code !== 0) {
          const reason = takeError()
          reject(new Error(reason === null ? `the capture tool exited with ${String(code)}` : reason))
          return
        }
        try {
          const bytes = readFileSync(out)
          resolve({ cancelled: false, dataUrl: 'data:image/png;base64,' + bytes.toString('base64'), bytes: bytes.length })
        } catch (error) {
          // Exit code 0 with no file means the engine believed it saved but nothing landed. Prefer
          // the engine's own explanation; otherwise say what actually happened instead of leaking a
          // bare ENOENT with a temp path into the chat.
          const reason = takeError()
          reject(new Error(reason === null
            ? `截图进程没有写出文件（${String((error && error.code) || 'unknown')}）：${out}`
            : reason))
        } finally {
          try { rmSync(out, { force: true }) } catch { /* best effort */ }
          try { rmSync(errFile, { force: true }) } catch { /* best effort */ }
        }
      })
    })
  }

  route('/native', async (req, res) => {
    if (req.method !== 'POST') { sendJson(res, 405, { ok: false, message: 'POST only' }); return }
    if (overlayBusy) {
      sendJson(res, 200, { ok: true, cancelled: true, busy: true })
      return
    }
    overlayBusy = true
    try {
      const result = await runNativeOverlay()
      sendJson(res, 200, result.cancelled ? { ok: true, cancelled: true } : { ok: true, dataUrl: result.dataUrl, bytes: result.bytes })
    } catch (error) {
      sendJson(res, 200, { ok: false, message: String(error && error.message ? error.message : error) })
    } finally {
      overlayBusy = false
    }
  })

  /** Read and parse a JSON request body, bounded. */
  function readJson(req, limit = 8 * 1024 * 1024) {
    return new Promise((resolve, reject) => {
      const chunks = []
      let total = 0
      req.on('data', (chunk) => {
        total += chunk.length
        if (total > limit) { reject(new Error('request body too large')); req.destroy(); return }
        chunks.push(chunk)
      })
      req.on('end', () => {
        const text = Buffer.concat(chunks).toString('utf8')
        if (text.trim() === '') { resolve({}); return }
        try { resolve(JSON.parse(text)) } catch (error) { reject(new Error(`invalid JSON body: ${error.message}`)) }
      })
      req.on('error', reject)
    })
  }

  /**
   * Locate the DSH window: the largest visible top-level window of this process tree.
   *
   * @param {Array<object>|undefined} windows - an enumeration the caller already has.
   *   Passing it avoids a second `win.ps1 -Action list`, which costs a full PowerShell
   *   launch (~1.5-5s depending on the machine) and is pure duplicate work when the
   *   caller has just enumerated. Omitting it keeps the previous behaviour.
   */
  async function findOwnWindow(windows) {
    const all = windows ?? await listWindows()
    const ownName = 'DeepSeek Harness'
    const mine = all
      .filter((w) => w.process === ownName || w.pid === process.pid)
      .filter((w) => w.width > 200 && w.height > 200)
    const visible = mine.filter((w) => w.visible && !w.minimized)
    const pick = (visible.length > 0 ? visible : mine)
      .sort((a, b) => (b.width * b.height) - (a.width * a.height))[0]
    return pick ?? null
  }

  // ── routes ────────────────────────────────────────────────────────────────

  // ── model-facing tools ────────────────────────────────────────────────────
  //
  // Plain definitions on purpose: this package is linked into the profile (link:), and pnpm does
  // not install peer dependencies for link: entries, so importing `@deepseek-ai/dsh-tools` fails
  // at boot ("web boot: 1 entry did not activate"). `tools.register` validates the shape itself,
  // so no helper package is needed.

  
  const tools = ctx.tools ?? (typeof ctx.get === 'function' ? ctx.get('tools') : undefined)
  if (tools && typeof tools.register === 'function') try {
  tools.register({
    name: 'screenshot_capture',
    description:
      'Capture the Windows screen to a PNG file. Use when the answer depends on what is actually on screen (UI state, an error dialog, a window the user is describing), or when the user asks you to look at their screen. Non-interactive: it does not open the camera-button overlay. Returns the file path; open it with read_image when you need to inspect the pixels.',
    parameters: {
      type: 'object',
      properties: {
        mode: {
          type: 'string',
          enum: ['screen', 'region', 'window'],
          description: 'screen = the whole virtual desktop (default); region = a rectangle; window = one top-level window',
        },
        x: { type: 'number', description: 'region only: left edge in screen pixels' },
        y: { type: 'number', description: 'region only: top edge in screen pixels' },
        width: { type: 'number', description: 'region only: width in pixels' },
        height: { type: 'number', description: 'region only: height in pixels' },
        hwnd: { type: 'number', description: 'window only: handle from list_windows' },
      },
      required: ['mode'],
    },
    output: {
      schema: {
        type: 'object',
        properties: {
          file: { type: 'string' },
          width: { type: 'number' },
          height: { type: 'number' },
          bytes: { type: 'number' },
          message: { type: 'string' },
        },
        required: ['file', 'message'],
      },
      render: (_args, value) => [{ type: 'text', text: String(value && value.message) }],
    },
    async execute(args) {
      const mode = String((args && args.mode) || 'screen')
      let result
      if (mode === 'region') {
        const width = Number((args && args.width) || 0)
        const height = Number((args && args.height) || 0)
        if (!(width > 0 && height > 0)) throw new Error('screenshot_capture: region needs positive width and height')
        result = await capture({
          mode: 'region',
          x: Number((args && args.x) || 0),
          y: Number((args && args.y) || 0),
          width,
          height,
        })
      } else if (mode === 'window') {
        const hwnd = Number((args && args.hwnd) || 0)
        if (!(hwnd > 0)) throw new Error('screenshot_capture: window mode needs a numeric hwnd from list_windows')
        result = await capture({ mode: 'window', hwnd })
      } else if (mode === 'screen') {
        result = await capture({ mode: 'fullscreen' })
      } else {
        throw new Error(`screenshot_capture: unknown mode ${mode}`)
      }
      const data = (result && result.data) || result || {}
      const file = data.file
      if (!file || !existsSync(file)) throw new Error('screenshot_capture: the capture produced no file')
      const width = Math.round(data.width || 0)
      const height = Math.round(data.height || 0)
      return {
        file,
        width,
        height,
        bytes: Math.round(data.bytes || 0),
        message: `saved ${file} (${width}x${height})`,
      }
    },
  })

  tools.register({
    name: 'list_windows',
    description:
      'List the visible top-level windows with their screen rectangles, process names and handles. Use it before screenshot_capture(mode="window"): pick a handle from here instead of guessing one.',
    parameters: { type: 'object', properties: {} },
    output: {
      schema: {
        type: 'object',
        properties: {
          count: { type: 'number' },
          windows: { type: 'array', items: { type: 'object' } },
          message: { type: 'string' },
        },
        required: ['count', 'message'],
      },
      render: (_args, value) => [{ type: 'text', text: String(value && value.message) }],
    },
    async execute() {
      const all = await listWindows()
      const windows = all
        .filter((w) => w && w.visible === true && w.minimized !== true && w.width >= 40 && w.height >= 40)
        .map((w) => ({
          hwnd: w.hwnd,
          title: w.title,
          process: w.process,
          pid: w.pid,
          rect: { x: w.x, y: w.y, width: w.width, height: w.height },
        }))
      const lines = windows.map((w) => `${w.hwnd}  ${w.process}  ${w.rect.width}x${w.rect.height}  ${w.title}`)
      return {
        count: windows.length,
        windows,
        message: lines.length > 0 ? lines.join('\n') : 'no visible top-level windows',
      }
    },
  })

  tools.register({
    name: 'annotate_image',
    description:
      'Draw marks on an existing PNG screenshot so a human can see what you mean instead of reading coordinates. Use it right before showing a screenshot: circle the broken control, arrow from a label to the area, mosaic anything private. Types: rect, ellipse, arrow (x,y -> x+width,y+height), label (filled box with text), mosaic (pixelated region). Coordinates are image pixels. Returns the annotated file path.',
    parameters: {
      type: 'object',
      properties: {
        source: { type: 'string', description: 'absolute path of the PNG to annotate' },
        marks: {
          type: 'array',
          description: 'marks painted in order',
          items: {
            type: 'object',
            properties: {
              type: { type: 'string', enum: ['rect', 'ellipse', 'arrow', 'label', 'mosaic'] },
              x: { type: 'number' },
              y: { type: 'number' },
              width: { type: 'number' },
              height: { type: 'number' },
              text: { type: 'string', description: 'label only' },
              color: { type: 'string', description: '#rrggbb, default #ff3b30' },
              thickness: { type: 'number', description: 'stroke width, default 4' },
              fontSize: { type: 'number', description: 'label only, default 24' },
            },
            required: ['type', 'x', 'y', 'width', 'height'],
          },
        },
        out: { type: 'string', description: 'optional output path; defaults to a new file in the scratch directory' },
      },
      required: ['source', 'marks'],
    },
    output: {
      schema: {
        type: 'object',
        properties: {
          file: { type: 'string' },
          marks: { type: 'number' },
          bytes: { type: 'number' },
          message: { type: 'string' },
        },
        required: ['file', 'message'],
      },
      render: (_args, value) => [{ type: 'text', text: String(value && value.message) }],
    },
    async execute(args) {
      const source = String((args && args.source) || '')
      if (!source || !existsSync(source)) throw new Error(`annotate_image: source not found: ${source}`)
      const marks = Array.isArray(args && args.marks) ? args.marks : []
      if (marks.length === 0) throw new Error('annotate_image: needs at least one mark')
      mkdirSync(WORK_DIR, { recursive: true })
      const marksFile = join(WORK_DIR, `marks-${Date.now()}.json`)
      writeFileSync(marksFile, JSON.stringify(marks), 'utf8')
      const out = args && args.out ? String(args.out) : join(WORK_DIR, `annotated-${Date.now()}.png`)
      try {
        const result = await runScript('annotate.ps1', ['-InFile', source, '-OutFile', out, '-MarksFile', marksFile])
        if (!existsSync(out)) throw new Error('annotate_image: the helper produced no file')
        const applied = Number(result && result.marks ? result.marks : 0)
        return {
          file: out,
          marks: applied,
          bytes: Number(result && result.bytes ? result.bytes : 0),
          message: `annotated ${applied} mark(s) -> ${out}`,
        }
      } finally {
        try { rmSync(marksFile, { force: true }) } catch { /* best effort */ }
      }
    },
  })

  } catch (error) {
    // A failed tool registration must not take the whole plugin (and the camera button) down.
    console.error('[screenshot] tool registration failed:', error)
  }

  return () => {
    for (const dispose of disposers.splice(0)) {
      try { dispose() } catch { /* ignore teardown failures */ }
    }
  }
}
