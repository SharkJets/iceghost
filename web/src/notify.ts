import type { SessionRow } from './api'
import { agent, sessionLabel } from './format'

export const key = (r: SessionRow) => `${r.machineId}/${encodeURIComponent(r.sessionId)}`
export const supported = () => typeof window !== 'undefined' && 'Notification' in window && isSecureContext
export const store = {
  get: (k: string) => { try { return localStorage.getItem(k) } catch { return null } },
  set: (k: string, v: string) => { try { localStorage.setItem(k, v) } catch { /* private mode */ } },
}

/** Rows newly needing input since `seen` (key -> lastActivity). Returned seen only keeps rows still needing input. */
export function pickNew(rows: SessionRow[], seen: Record<string, string>) {
  const next: Record<string, string> = {}, fire: SessionRow[] = []
  for (const r of rows) {
    if (r.state !== 'needsInput') continue
    const k = key(r)
    next[k] = r.lastActivity
    if (seen[k] !== r.lastActivity) fire.push(r)
  }
  return { fire, seen: next }
}

export function loadSeen(): Record<string, string> {
  try { const v = JSON.parse(store.get('ig.notify.seen') ?? '{}'); return v && typeof v === 'object' ? v : {} } catch { return {} }
}

const open = (title: string, o: NotificationOptions, hash: string) => {
  const n = new Notification(title, { ...o, icon: '/iceghost-logo-500.png' })
  n.onclick = () => { window.focus(); location.hash = hash; n.close() }
}
export function show(fire: SessionRow[]) {
  if (fire.length > 3) return open(`${fire.length} sessions need you`, { tag: 'ig-needs' }, '#/')
  for (const r of fire) open(sessionLabel(r), { body: `${r.machine} · ${agent(r.runtime).name} needs your input`, tag: key(r) }, `#/s/${key(r)}`)
}
