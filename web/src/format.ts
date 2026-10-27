export function ago(iso: string, now: Date): string {
  const s = Math.max(0, (now.getTime() - new Date(iso).getTime()) / 1000)
  if (s < 60) return 'just now'
  if (s < 3600) return `${Math.floor(s / 60)}m ago`
  if (s < 86400) return `${Math.floor(s / 3600)}h ago`
  return `${Math.floor(s / 86400)}d ago`
}

export const folder = (cwd: string) => cwd.split(/[\\/]/).filter(Boolean).pop() ?? cwd


export type Tone = 'normal' | 'busy' | 'warn' | 'bad'
export function commandLabel(
  c: { status: string; startedAt?: string | null; heartbeatAt?: string | null },
  sessionLastActivity: string, now: Date, earlierPendingInSameSession: boolean,
): { text: string; tone: Tone } {
  if (c.status === 'queued') return { text: 'waiting for runner', tone: 'normal' }
  if (c.status === 'delivered')
    return { text: earlierPendingInSameSession ? 'queued behind previous message' : 'waiting for a free slot', tone: 'normal' }
  if (c.status !== 'running') return { text: c.status, tone: c.status === 'failed' ? 'bad' : 'normal' }
  const t = now.getTime()
  const secs = (iso: string) => Math.max(0, Math.floor((t - new Date(iso).getTime()) / 1000))
  if (c.heartbeatAt && secs(c.heartbeatAt) > 60) return { text: 'runner not responding', tone: 'bad' }
  const started = c.startedAt ?? sessionLastActivity
  const last = new Date(sessionLastActivity) > new Date(started) ? sessionLastActivity : started
  const quiet = secs(last)
  if (quiet > 300) return { text: `quiet for ${Math.floor(quiet / 60)}m, may be stuck`, tone: 'warn' }
  return { text: `running ${Math.floor(secs(started) / 60)}m · last activity ${quiet}s ago`, tone: 'busy' }
}

export const isOffline = (lastSeen: string | null | undefined, now: Date) =>
  !lastSeen || now.getTime() - new Date(lastSeen).getTime() > 120000

/** "v0.1.16", "0.1.16", "v0.1.16-2-gabc1234" -> [0,1,16]; dev/null/garbage -> null. */
export function parseVer(v: string | null | undefined): [number, number, number] | null {
  const m = v?.match(/^v?(\d+)\.(\d+)\.(\d+)(?:[-+]|$)/)
  return m ? [+m[1], +m[2], +m[3]] : null
}

/** Runner older than the server. Dev server or dev runner: never; old runner (no version, junk): yes. */
export function runnerOutdated(runner: string | null | undefined, server: string): boolean {
  const s = parseVer(server)
  if (!s || runner === 'dev') return false
  const r = parseVer(runner)
  if (!r) return true
  for (let i = 0; i < 3; i++) if (r[i] !== s[i]) return r[i] < s[i]
  return false
}

export function shortOs(os: string | null | undefined): string {
  if (!os) return '-'
  if (/darwin|macos/i.test(os)) return 'macOS'
  if (/windows/i.test(os)) return 'Windows'
  const u = os.match(/ubuntu\s+(\d+\.\d+)/i)
  if (u) return `Ubuntu ${u[1]}`
  return /linux|ubuntu/i.test(os) ? 'Linux' : os
}

export type RunnerOs = 'windows' | 'macos' | 'linux'
export function guessOs(ua: string, platform = ''): RunnerOs {
  const s = `${platform} ${ua}`
  if (/^win/i.test(platform) || /windows/i.test(ua)) return 'windows'
  return /mac|iphone|ipad|darwin/i.test(s) ? 'macos' : 'linux'
}
export function enrollCommand(os: RunnerOs, server: string, token: string): string {
  const args = `run --server ${server} --token ${token}`
  if (os === 'windows') return `.\\iceghost-runner.exe ${args}`
  const run = `chmod +x iceghost-runner && ./iceghost-runner ${args}`
  return os === 'macos' ? `xattr -d com.apple.quarantine iceghost-runner; ${run}` : run
}

const agents: Record<string, [string, string]> = { claude: ['C', 'Claude Code'], codex: ['X', 'Codex'], pi: ['π', 'pi'] }
export const agent = (runtime: string) => { const a = agents[runtime]; return { badge: a?.[0] ?? runtime.slice(0, 1).toUpperCase(), name: a?.[1] ?? runtime } }

/** "Bash: dotnet test" -> verb "Bash", arg "dotnet test". */
export function splitAction(a: string): { verb: string; arg: string } {
  const i = a.indexOf(': ')
  return i < 0 ? { verb: '', arg: a } : { verb: a.slice(0, i), arg: a.slice(i + 2) }
}

export const states: Record<string, { label: string; hint: string; icon: string; text: string; dot: string }> = {
  needsInput: { label: 'Needs you', hint: 'Sessions waiting for your input', icon: 'alert', text: 'text-needs', dot: 'h-2.5 w-2.5 rotate-45 bg-needs' },
  stalled: { label: 'Stalled', hint: "Sessions haven't made progress recently", icon: 'clock', text: 'text-stall', dot: 'h-2.5 w-2.5 bg-stall' },
  working: { label: 'Working', hint: 'Sessions actively making progress', icon: 'play', text: 'text-link', dot: 'h-2.5 w-2.5 rounded-full bg-link' },
  done: { label: 'Done', hint: 'Recently completed sessions', icon: 'check', text: 'text-ok', dot: 'h-2.5 w-2.5 rounded-full border-2 border-ok' },
}
export const stateOf = (s: string) => states[s] ?? { ...states.done, label: s, hint: '' }

type Row = { title: string | null; cwd: string; machine: string; runtime: string; state: string }
export const matches = (r: Row, q: string) => {
  const n = q.trim().toLowerCase()
  return !n || [r.title, r.cwd, r.machine, agent(r.runtime).name].some(f => f?.toLowerCase().includes(n))
}
export const summary = (rows: { state: string }[]) => {
  const c: Record<string, number> = { needsInput: 0, stalled: 0, working: 0, done: 0 }
  for (const r of rows) c[r.state] = (c[r.state] ?? 0) + 1
  return c
}

export const sessionLabel = (r: { title?: string | null; cwd: string }) => r.title ? `${folder(r.cwd)} · ${r.title}` : folder(r.cwd)
