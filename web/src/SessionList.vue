<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { api, Unauthorized, type SessionRow, type MachineRow } from './api'
import { ago, agent, folder, shortOs, splitAction, stateOf } from './format'
import { usePoll, ticker } from './useNow'
import Icon from './Icon.vue'

const emit = defineEmits<{ unauth: [] }>()
const rows = ref<SessionRow[]>([])
const now = ticker()
const seen = ref<Record<number, string | null>>({})
const osOf = ref<Record<number, string | null>>({})
const offline = (id: number) => id in seen.value && (!seen.value[id] || now.value.getTime() - new Date(seen.value[id]!).getTime() > 120000)
usePoll(async () => {
  try { rows.value = await api<SessionRow[]>('/api/sessions') }
  catch (e) { if (e instanceof Unauthorized) emit('unauth'); throw e }
  try {
    const ms = await api<MachineRow[]>('/api/machines')
    seen.value = Object.fromEntries(ms.map(m => [m.id, m.lastSeen]))
    osOf.value = Object.fromEntries(ms.map(m => [m.id, m.os]))
  }
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
const pick = (e: Event) => { const v = (e.target as HTMLSelectElement).value; sel.value = v ? Number(v) : null }
</script>
<template>
  <div class="space-y-5">
    <div class="flex flex-wrap items-end justify-between gap-3">
      <div>
        <h1 class="text-3xl font-semibold">Sessions</h1>
        <p class="mt-1 text-sm text-slate-400">Live view of coding agent sessions across your machines. Talk to them from anywhere.</p>
      </div>
      <div class="flex flex-wrap items-center gap-4 text-xs text-slate-400">
        <select v-if="machines.length > 1" :value="eff ?? ''" class="rounded-md border border-slate-600 bg-slate-900 px-3 py-2 text-sm text-slate-200" @change="pick">
          <option value="">All Machines</option>
          <option v-for="[id, name] in machines" :key="id" :value="id">{{ name }}{{ offline(id) ? ' (offline)' : '' }}</option>
        </select>
        <span class="flex items-center gap-2"><span class="h-2 w-2 rounded-full bg-emerald-400"></span>Auto-refreshes every 5 seconds</span>
      </div>
    </div>
    <p v-if="!rows.length" class="text-slate-400">No sessions yet.</p>
    <section v-for="g in groups" :key="g.s" class="overflow-hidden rounded-xl border border-slate-700/60 bg-slate-900/70">
      <h2 class="flex items-center gap-3 border-l-4 px-4 py-3" :class="[stateOf(g.s).head, stateOf(g.s).border]">
        <span class="flex h-6 w-6 items-center justify-center rounded-full bg-current/20" :class="stateOf(g.s).text"><Icon :name="stateOf(g.s).icon" class="h-4 w-4" /></span>
        <span class="text-lg font-semibold" :class="stateOf(g.s).text">{{ stateOf(g.s).label }}</span>
        <span class="rounded-full bg-slate-700/70 px-2 text-xs text-slate-200">{{ g.items.length }}</span>
        <span class="ml-auto hidden text-xs font-normal text-slate-400 sm:block">{{ stateOf(g.s).hint }}</span>
      </h2>
      <a v-for="r in g.items" :key="`${r.machineId}/${r.sessionId}`" :href="`#/s/${r.machineId}/${encodeURIComponent(r.sessionId)}`"
        class="grid grid-cols-[1.5rem_minmax(0,1fr)_1rem] items-center gap-x-3 gap-y-2 border-l-4 border-t border-t-slate-700/50 px-4 py-3 hover:bg-slate-800/60 md:grid-cols-[1.5rem_minmax(0,2fr)_5.5rem_minmax(0,1.2fr)_minmax(0,1.2fr)_6.5rem_minmax(0,2fr)_1rem]"
        :class="stateOf(g.s).border">
        <span class="text-slate-400"><Icon name="chat" class="h-5 w-5" /></span>
        <span class="min-w-0">
          <span class="flex items-center gap-2 font-semibold">
            <span v-if="r.pendingCommands" class="inline-block h-2 w-2 shrink-0 animate-spin rounded-full border-2 border-sky-400 border-t-transparent"></span>
            <span class="truncate">{{ r.title ?? folder(r.cwd) }}</span>
          </span>
          <span class="flex items-center gap-1 text-xs text-slate-500"><Icon name="folder" class="h-3 w-3" /><span class="truncate">{{ r.cwd }}</span></span>
        </span>
        <span class="col-start-3 row-start-1 text-slate-500 md:col-start-auto md:row-start-auto md:order-last"><Icon name="chevron" /></span>
        <span class="col-start-2 col-span-2 flex flex-wrap items-center gap-x-4 gap-y-1 text-xs md:contents">
          <span class="text-slate-300 md:text-sm">{{ ago(r.lastActivity, now) }}<span class="hidden text-xs text-slate-500 md:block">last active</span></span>
          <span class="min-w-0 text-slate-300 md:text-sm"><span class="md:block md:truncate">{{ r.machine }}</span><span v-if="offline(r.machineId)" class="ml-1 rounded bg-slate-700 px-1 text-xs text-slate-400 md:ml-0">offline</span>
            <span class="text-slate-500 md:block md:text-xs"><span class="md:hidden"> · </span>{{ shortOs(osOf[r.machineId]) }}</span></span>
          <span class="flex items-center gap-2 text-slate-300 md:text-sm"><span class="flex h-5 w-5 shrink-0 items-center justify-center rounded bg-slate-700 text-[11px] font-semibold">{{ agent(r.runtime).badge }}</span>{{ agent(r.runtime).name }}</span>
          <span class="justify-self-start rounded-md border px-2 py-0.5 text-xs" :class="stateOf(r.state).pill">{{ stateOf(r.state).label }}</span>
        </span>
        <span v-if="r.lastAction || r.pendingCommands" class="col-start-2 col-span-2 flex min-w-0 flex-wrap items-center gap-x-3 gap-y-1 text-xs md:col-start-auto md:col-span-1">
          <span v-if="r.lastAction" :title="r.lastAction" class="flex min-w-0 items-center gap-2">
            <span v-if="splitAction(r.lastAction).verb" class="text-slate-400">{{ splitAction(r.lastAction).verb }}</span>
            <code class="truncate rounded bg-slate-800 px-1.5 py-0.5 font-mono text-slate-200">{{ splitAction(r.lastAction).arg }}</code>
          </span>
          <span v-if="r.pendingCommands" class="flex items-center gap-1 text-sky-300"><Icon name="chat" class="h-4 w-4" />{{ r.pendingCommands }} waiting</span>
        </span>
        <span v-else class="hidden md:block"></span>
      </a>
    </section>
  </div>
</template>
