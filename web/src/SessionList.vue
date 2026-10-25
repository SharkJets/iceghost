<script setup lang="ts">
import { computed, ref } from 'vue'
import { api, Unauthorized, type SessionRow } from './api'
import { ago, folder } from './format'
import { usePoll, ticker } from './useNow'

const emit = defineEmits<{ unauth: [] }>()
const rows = ref<SessionRow[]>([])
const now = ticker()
usePoll(async () => {
  try { rows.value = await api<SessionRow[]>('/api/sessions') }
  catch (e) { if (e instanceof Unauthorized) emit('unauth'); throw e }
}, 5000)

const order = ['needsInput', 'stalled', 'working', 'done']
const groups = computed(() => order.map(s => ({ s, items: rows.value.filter(r => r.state === s) })).filter(g => g.items.length))
</script>
<template>
  <div class="space-y-4">
    <p v-if="!rows.length" class="text-slate-400">No sessions yet.</p>
    <section v-for="g in groups" :key="g.s">
      <h2 class="mb-1 text-xs uppercase tracking-wide text-slate-400">{{ g.s }}</h2>
      <a v-for="r in g.items" :key="`${r.machineId}/${r.sessionId}`" :href="`#/s/${r.machineId}/${encodeURIComponent(r.sessionId)}`"
        class="mb-1 block rounded bg-slate-800 p-3">
        <div class="flex justify-between text-sm">
          <span class="font-medium">{{ folder(r.cwd) }}</span>
          <span class="text-slate-400">{{ ago(r.lastActivity, now) }}</span>
        </div>
        <div class="truncate text-sm text-slate-300">{{ r.title ?? '(untitled)' }}</div>
        <div class="text-xs text-slate-500">{{ r.machine }} · {{ r.runtime }}<span v-if="r.pendingCommands"> · {{ r.pendingCommands }} queued</span></div>
      </a>
    </section>
  </div>
</template>
