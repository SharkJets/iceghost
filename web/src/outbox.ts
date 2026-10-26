type Cmd = { id: number; status: string; createdAt: string }

const isPending = (s: string) => s === 'queued' || s === 'delivered' || s === 'running'

// pending always; failed only if newer than the newest transcript entry; newest first
export function visibleCommands<T extends Cmd>(commands: T[], newestTailAt?: string | null): T[] {
  const cut = newestTailAt ? Date.parse(newestTailAt) : NaN
  return commands
    .filter(c => isPending(c.status) || (c.status === 'failed' && !(Date.parse(c.createdAt) <= cut)))
    .sort((a, b) => b.id - a.id)
}
