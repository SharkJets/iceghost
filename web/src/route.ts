export type Route =
  | { name: 'list' }
  | { name: 'machines' }
  | { name: 'session'; machineId: number; sessionId: string }

export function parseRoute(hash: string): Route {
  if (hash === '#/machines') return { name: 'machines' }
  const m = hash.match(/^#\/s\/([^/]+)\/(.+)$/)
  if (m) {
    try { return { name: 'session', machineId: Number(m[1]), sessionId: decodeURIComponent(m[2]) } }
    catch { /* malformed escape: fall through to list */ }
  }
  return { name: 'list' }
}

/** Ignore calls while a previous call's promise is still pending. */
export function guard<A extends unknown[]>(fn: (...a: A) => Promise<unknown>) {
  let busy = false
  return async (...a: A) => {
    if (busy) return
    busy = true
    try { await fn(...a) } finally { busy = false }
  }
}
