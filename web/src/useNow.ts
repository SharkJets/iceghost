import { onUnmounted, ref } from 'vue'

export function usePoll(fn: () => Promise<void>, ms: number) {
  const run = () => fn().catch(() => {})
  run()
  const t = setInterval(run, ms)
  onUnmounted(() => clearInterval(t))
}

export function ticker() {
  const n = ref(new Date())
  const t = setInterval(() => (n.value = new Date()), 10000)
  onUnmounted(() => clearInterval(t))
  return n
}
