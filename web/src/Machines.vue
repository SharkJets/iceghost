<script setup lang="ts">
import { ref } from 'vue'
import { api, Unauthorized, type MachineRow } from './api'
import { ago, isOffline, shortOs } from './format'
import Icon from './Icon.vue'
import { usePoll, ticker } from './useNow'

const emit = defineEmits<{ unauth: [] }>()
const rows = ref<MachineRow[]>([])
const name = ref('')
const token = ref<string | null>(null)
const err = ref('')
const adding = ref(false)
const now = ticker()
const cmd = () => `iceghost-runner run --server ${location.origin} --token ${token.value}`

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
        <p class="mt-1 max-w-xl text-sm text-slate-400">Computers enrolled with IceGhost. Each machine runs a small runner that reads agent sessions and reports to this server.</p>
      </div>
      <button class="flex items-center gap-2 rounded-md bg-sky-600 px-4 py-2 text-sm font-medium hover:bg-sky-500" @click="adding = !adding"><Icon name="plus" />Enroll Machine</button>
    </div>
    <form v-if="adding" class="flex gap-2" @submit.prevent="enroll">
      <input v-model="name" placeholder="New machine name" class="min-w-0 flex-1 rounded-md border border-slate-600 bg-slate-950/60 p-2" />
      <button class="rounded-md bg-sky-600 px-4 hover:bg-sky-500">Enroll</button>
    </form>
    <p v-if="err" class="text-sm text-red-400">{{ err }}</p>
    <div v-if="token" class="rounded-xl border border-slate-700/60 bg-slate-900/70 p-3 text-sm">
      <p class="mb-1 text-amber-300">Shown once. Copy it now.</p>
      <code class="block break-all font-mono text-xs">{{ cmd() }}</code>
      <button class="mt-2 rounded bg-slate-700 px-3 py-1" @click="copy">Copy</button>
    </div>
    <div class="overflow-hidden rounded-xl border border-slate-700/60 bg-slate-900/70 text-sm">
      <div class="hidden grid-cols-[minmax(0,2fr)_minmax(0,1fr)_minmax(0,1fr)_minmax(0,1fr)_5rem] gap-3 border-b border-slate-700/60 px-4 py-2 text-xs text-slate-400 md:grid">
        <span>Hostname</span><span>OS</span><span>Status</span><span>Last Seen</span><span class="text-right">Actions</span>
      </div>
      <p v-if="!rows.length" class="p-4 text-slate-500">No machines enrolled.</p>
      <div v-for="m in rows" :key="m.id" class="grid grid-cols-2 items-center gap-x-3 gap-y-1 border-t border-slate-700/50 px-4 py-3 first:border-t-0 md:grid-cols-[minmax(0,2fr)_minmax(0,1fr)_minmax(0,1fr)_minmax(0,1fr)_5rem]">
        <div class="col-span-2 min-w-0 md:col-span-1"><div class="flex items-center gap-2 font-medium"><Icon name="monitor" class="text-slate-400" /><span class="truncate">{{ m.name }}</span></div>
          <div class="truncate text-xs text-slate-500">{{ m.hostname ?? '-' }}</div></div>
        <span class="text-slate-300">{{ shortOs(m.os) }}</span>
        <span class="flex items-center gap-2"><span class="h-2 w-2 rounded-full" :class="isOffline(m.lastSeen, now) ? 'bg-red-500' : 'bg-emerald-400'"></span>{{ isOffline(m.lastSeen, now) ? 'Offline' : 'Online' }}</span>
        <span :class="isOffline(m.lastSeen, now) ? 'text-red-400' : 'text-slate-300'">{{ m.lastSeen ? ago(m.lastSeen, now) : 'never seen' }}</span>
        <span class="text-right"><button class="rounded border border-slate-600 px-2 py-0.5 text-xs text-red-400 hover:bg-slate-800" @click="remove(m)">Delete</button></span>
      </div>
    </div>
  </div>
</template>
