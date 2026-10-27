<script setup lang="ts">
import { ref } from 'vue'
import { api, Unauthorized, type MachineRow } from './api'
import { ago, enrollCommand, guessOs, isOffline, runnerOutdated, shortOs, type RunnerOs } from './format'
import Icon from './Icon.vue'
import { usePoll, ticker } from './useNow'

const emit = defineEmits<{ unauth: [] }>()
const rows = ref<MachineRow[]>([])
const name = ref('')
const token = ref<string | null>(null)
const err = ref('')
const adding = ref(false)
const now = ticker()
const version = import.meta.env.VITE_VERSION || 'dev'
const oses: [RunnerOs, string][] = [['windows', 'Windows'], ['macos', 'macOS'], ['linux', 'Linux']]
const sel = ref<RunnerOs>(guessOs(navigator.userAgent, navigator.platform))
const cmd = () => enrollCommand(sel.value, location.origin, token.value ?? '')

async function load() {
  try { rows.value = await api<MachineRow[]>('/api/machines') }
  catch (e) { if (e instanceof Unauthorized) emit('unauth'); throw e }
}
usePoll(load, 10000)

function fail(e: unknown, msg: string) { if (e instanceof Unauthorized) emit('unauth'); else err.value = msg }

async function enroll() {
  if (!name.value.trim()) return
  err.value = ''
  try {
    const r = await api<{ id: number; token: string }>('/api/machines', 'POST', { name: name.value.trim() })
    token.value = r.token; name.value = ''; await load()
  } catch (e) { fail(e, 'Enroll failed') }
}
async function remove(m: MachineRow) {
  if (!confirm(`Delete machine ${m.name}? Its sessions and queue are removed.`)) return
  err.value = ''
  try { await api(`/api/machines/${m.id}`, 'DELETE'); await load() }
  catch (e) { fail(e, 'Delete failed') }
}
const copy = () => navigator.clipboard?.writeText(cmd())
</script>
<template>
  <div class="space-y-5">
    <div class="flex flex-wrap items-start justify-between gap-3">
      <div>
        <h1 class="text-3xl font-semibold">Machines</h1>
        <p class="mt-1 max-w-xl text-sm text-muted">Computers enrolled with IceGhost. Each machine runs a small runner that reads agent sessions and reports to this server.</p>
      </div>
      <button class="flex items-center gap-2 rounded-md bg-brand text-white px-4 min-h-11 py-2 text-sm font-medium hover:brightness-110" @click="adding = !adding"><Icon name="plus" />Enroll Machine</button>
    </div>
    <form v-if="adding" class="flex gap-2" @submit.prevent="enroll">
      <input v-model="name" placeholder="New machine name" class="min-w-0 flex-1 rounded-md border border-edge bg-night min-h-11 px-2 placeholder:text-edge" />
      <button class="min-h-11 rounded-md bg-brand text-white px-4 hover:brightness-110">Enroll</button>
    </form>
    <p v-if="err" class="text-sm text-bad">{{ err }}</p>
    <div v-if="token" class="rounded-xl border border-line bg-panel p-3 text-sm">
      <p class="mb-1 text-needs">Shown once. Copy it now.</p>
      <div class="mb-2 flex flex-wrap gap-2">
        <button v-for="[id, label] in oses" :key="id" :aria-pressed="sel === id" class="min-h-11 rounded px-3" :class="sel === id ? 'bg-brand text-white' : 'bg-raised'" @click="sel = id">{{ label }}</button>
      </div>
      <code class="block break-all font-mono text-xs">{{ cmd() }}</code>
      <button class="mt-2 min-h-11 rounded bg-raised px-3" @click="copy">Copy</button>
      <p class="mt-2 text-xs text-muted">Download the runner for your OS from the <a href="https://github.com/SharkJets/iceghost/releases/latest" target="_blank" rel="noopener" class="text-link underline">latest release</a>. The token is only needed on the first run; it is saved, so later just run <code class="font-mono">iceghost-runner{{ sel === 'windows' ? '.exe' : '' }} run</code> (add <code class="font-mono">--headless</code> for no window, see the README).</p>
    </div>
    <div class="overflow-hidden rounded-xl border border-line bg-panel text-sm">
      <div class="hidden grid-cols-[minmax(0,2fr)_minmax(0,1fr)_minmax(0,1.2fr)_minmax(0,1fr)_minmax(0,1fr)_5rem] gap-3 border-b border-line px-4 py-2 text-xs text-muted md:grid">
        <span>Hostname</span><span>OS</span><span>Runner</span><span>Status</span><span>Last Seen</span><span class="text-right">Actions</span>
      </div>
      <p v-if="!rows.length" class="p-4 text-muted">No machines enrolled.</p>
      <div v-for="m in rows" :key="m.id" class="grid grid-cols-2 items-center gap-x-3 gap-y-1 border-t border-line px-4 py-3 first:border-t-0 md:grid-cols-[minmax(0,2fr)_minmax(0,1fr)_minmax(0,1.2fr)_minmax(0,1fr)_minmax(0,1fr)_5rem]">
        <div class="col-span-2 min-w-0 md:col-span-1"><div class="flex items-center gap-2 font-medium"><Icon name="monitor" class="text-muted" /><span class="truncate">{{ m.name }}</span></div>
          <div class="truncate text-xs text-muted">{{ m.hostname ?? '-' }}</div></div>
        <span class="text-ink">{{ shortOs(m.os) }}</span>
        <span class="text-ink">{{ m.runnerVersion ?? 'unknown' }}<span v-if="m.lastSeen && runnerOutdated(m.runnerVersion, version)" class="ml-1 rounded bg-raised px-1 text-xs text-needs">outdated</span></span>
        <span class="flex items-center gap-2"><span class="h-2 w-2 rounded-full" :class="isOffline(m.lastSeen, now) ? 'bg-bad' : 'bg-ok'"></span><span :class="isOffline(m.lastSeen, now) ? 'text-bad' : 'text-ok'">{{ isOffline(m.lastSeen, now) ? 'Offline' : 'Online' }}</span></span>
        <span :class="isOffline(m.lastSeen, now) ? 'text-bad' : 'text-ink'">{{ m.lastSeen ? ago(m.lastSeen, now) : 'never seen' }}</span>
        <span class="text-right"><button :aria-label="`Delete machine ${m.name}`" class="inline-flex min-h-11 items-center rounded border border-edge px-2 py-0.5 text-xs text-bad hover:bg-raised" @click="remove(m)">Delete</button></span>
      </div>
    </div>
  </div>
</template>
