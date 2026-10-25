<script setup lang="ts">
import { ref } from 'vue'
import { api, Unauthorized, type MachineRow } from './api'
import { ago } from './format'
import { usePoll, ticker } from './useNow'

const emit = defineEmits<{ unauth: [] }>()
const rows = ref<MachineRow[]>([])
const name = ref('')
const token = ref<string | null>(null)
const err = ref('')
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
  <div class="space-y-4">
    <a href="#/" class="text-sm text-sky-400">&larr; Sessions</a>
    <form class="flex gap-2" @submit.prevent="enroll">
      <input v-model="name" placeholder="New machine name" class="min-w-0 flex-1 rounded bg-slate-800 p-2" />
      <button class="rounded bg-sky-600 px-4">Enroll</button>
    </form>
    <p v-if="err" class="text-sm text-red-400">{{ err }}</p>
    <div v-if="token" class="rounded bg-slate-800 p-3 text-sm">
      <p class="mb-1 text-amber-300">Shown once. Copy it now.</p>
      <code class="block break-all font-mono text-xs">{{ cmd() }}</code>
      <button class="mt-2 rounded bg-slate-700 px-3 py-1" @click="copy">Copy</button>
    </div>
    <div v-for="m in rows" :key="m.id" class="flex items-center justify-between rounded bg-slate-800 p-3 text-sm">
      <div><div class="font-medium">{{ m.name }}</div>
        <div class="text-xs text-slate-500">{{ m.hostname ?? '-' }} · {{ m.os ?? '-' }} · {{ m.lastSeen ? ago(m.lastSeen, now) : 'never seen' }}</div></div>
      <button class="text-red-400" @click="remove(m)">Delete</button>
    </div>
  </div>
</template>
