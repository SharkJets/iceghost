<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { api, Unauthorized, type SessionRow, type MachineRow } from './api'
import { ago, folder } from './format'
import { usePoll, ticker } from './useNow'

const emit = defineEmits<{ unauth: [] }>()
const rows = ref<SessionRow[]>([])
const now = ticker()
const seen = ref<Record<number, string | null>>({})
const offline = (id: number) => id in seen.value && (!seen.value[id] || now.value.getTime() - new Date(seen.value[id]!).getTime() > 120000)
usePoll(async () => {
  try { rows.value = await api<SessionRow[]>('/api/sessions') }
  catch (e) { if (e instanceof Unauthorized) emit('unauth'); throw e }
  try { seen.value = Object.fromEntries((await api<MachineRow[]>('/api/machines')).map(m => [m.id, m.lastSeen])) }
  catch (e) { if (e instanceof Unauthorized) emit('unauth') }
}, 5000)

const order = ['needsInput', 'stalled', 'working', 'done']
const stored = localStorage.getItem('ig.machine')
const sel = ref<number | null>(stored ? Number(stored) : null)
watch(sel, v => v === null ? localStorage.removeItem('ig.machine') : localStorage.setItem('ig.machine', String(v)))
const machines = computed(() => [...new Map(rows.value.map(r => [r.machineId, r.machine])).entries()].sort((a, b) => a[1].localeCompare(b[1])))
const eff = computed(() => machines.value.some(m => m[0] === sel.value) ? sel.value : null)
const groups = computed(() => {
  const rs = eff.value === null ? rows.value : rows.value.filter(r => r.machineId === eff.value)
  return order.map(s => ({ s, items: rs.filter(r => r.state === s) })).filter(g => g.items.length)
})
const pill = (on: boolean) => `rounded px-2 py-0.5 text-xs ${on ? 'bg-sky-600 text-white' : 'bg-slate-800 text-slate-300'}`
</script>
<template>
  <div class="space-y-4">
    <p v-if="!rows.length" class="text-slate-400">No sessions yet.</p>
    <div v-if="machines.length > 1" class="flex flex-wrap gap-1">
      <button :class="pill(eff === null)" @click="sel = null">All</button>
      <button v-for="[id, name] in machines" :key="id" :class="[pill(eff === id), offline(id) && 'opacity-50']" @click="sel = id">{{ name }}</button>
    </div>
    <section v-for="g in groups" :key="g.s">
      <h2 class="mb-1 text-xs uppercase tracking-wide text-slate-400">{{ g.s }}</h2>
      <a v-for="r in g.items" :key="`${r.machineId}/${r.sessionId}`" :href="`#/s/${r.machineId}/${encodeURIComponent(r.sessionId)}`"
        class="mb-1 block rounded bg-slate-800 p-3">
        <div class="flex justify-between text-sm">
          <span class="font-medium"><span v-if="r.pendingCommands" class="mr-1 inline-block h-2 w-2 animate-spin rounded-full border-2 border-sky-400 border-t-transparent"></span>{{ folder(r.cwd) }}</span>
          <span class="text-slate-400">{{ ago(r.lastActivity, now) }}</span>
        </div>
        <div class="truncate text-sm text-slate-300">{{ r.title ?? '(untitled)' }}</div>
        <div v-if="r.lastAction && (r.state === 'working' || r.state === 'stalled')" :title="r.lastAction" class="truncate font-mono text-xs text-slate-400">{{ r.lastAction }}</div>
        <div class="text-xs text-slate-500">{{ r.machine }}<span v-if="offline(r.machineId)" class="ml-1 rounded bg-slate-700 px-1 text-slate-400">offline</span> · {{ r.runtime }}<span v-if="r.pendingCommands"> · {{ r.pendingCommands }} queued</span></div>
      </a>
    </section>
  </div>
</template>
