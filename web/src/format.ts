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

export function shortOs(os: string | null | undefined): string {
  if (!os) return '-'
  if (/darwin|macos/i.test(os)) return 'macOS'
  if (/windows/i.test(os)) return 'Windows'
  const u = os.match(/ubuntu\s+(\d+\.\d+)/i)
  if (u) return `Ubuntu ${u[1]}`
  return /linux|ubuntu/i.test(os) ? 'Linux' : os
}

const agents: Record<string, [string, string]> = { claude: ['C', 'Claude Code'], codex: ['X', 'Codex'], pi: ['π', 'pi'] }
export const agent = (runtime: string) => { const a = agents[runtime]; return { badge: a?.[0] ?? runtime.slice(0, 1).toUpperCase(), name: a?.[1] ?? runtime } }

/** "Bash: dotnet test" -> verb "Bash", arg "dotnet test". */
export function splitAction(a: string): { verb: string; arg: string } {
  const i = a.indexOf(': ')
  return i < 0 ? { verb: '', arg: a } : { verb: a.slice(0, i), arg: a.slice(i + 2) }
}

export const states: Record<string, { label: string; hint: string; icon: string; pill: string; head: string; text: string; border: string }> = {
  needsInput: { label: 'Needs Input', hint: 'Sessions waiting for your input', icon: 'alert', pill: 'border-red-500/60 bg-red-500/15 text-red-300', head: 'bg-red-950/60', text: 'text-red-400', border: 'border-l-red-500' },
  stalled: { label: 'Stalled', hint: "Sessions haven't made progress recently", icon: 'clock', pill: 'border-amber-500/60 bg-amber-500/15 text-amber-300', head: 'bg-amber-950/50', text: 'text-amber-400', border: 'border-l-amber-500' },
  working: { label: 'Working', hint: 'Sessions actively making progress', icon: 'play', pill: 'border-sky-500/60 bg-sky-500/15 text-sky-300', head: 'bg-sky-950/60', text: 'text-sky-400', border: 'border-l-sky-500' },
  done: { label: 'Done', hint: 'Recently completed sessions', icon: 'check', pill: 'border-emerald-500/60 bg-emerald-500/15 text-emerald-300', head: 'bg-emerald-950/50', text: 'text-emerald-400', border: 'border-l-emerald-500' },
}
export const stateOf = (s: string) => states[s] ?? { ...states.done, label: s, hint: '' }
