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
