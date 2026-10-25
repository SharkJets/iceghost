export class Unauthorized extends Error {}
export const auth = { onUnauthorized: () => {} }

export async function api<T = unknown>(path: string, method = 'GET', body?: unknown): Promise<T> {
  const r = await fetch(path, {
    method,
    headers: body === undefined ? {} : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  })
  if (r.status === 401 && path !== '/api/login') { auth.onUnauthorized(); throw new Unauthorized() }
  if (!r.ok) {
    const j = await r.json().catch(() => null)
    throw new Error(typeof j?.error === 'string' ? j.error : `${r.status}`)
  }
  const text = await r.text()
  return (text ? JSON.parse(text) : undefined) as T
}

export type SessionRow = {
  machineId: number; machine: string; runtime: string; sessionId: string; cwd: string
  title: string | null; state: string; lastActivity: string; pendingCommands: number; lastAction?: string | null
}
export type Entry = { role: string; text: string; at: string }
export type CommandRow = { id: number; text: string; status: string; output: string | null; createdAt: string; startedAt?: string | null; heartbeatAt?: string | null; hasImage?: boolean }
export type SessionDetail = { session: SessionRow; tail: Entry[]; commands: CommandRow[] }
export type MachineRow = { id: number; name: string; hostname: string | null; os: string | null; lastSeen: string | null }
