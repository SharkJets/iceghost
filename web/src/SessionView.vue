<script setup lang="ts">
import { ref } from 'vue'
import { api, Unauthorized, type SessionDetail } from './api'
import { ago, folder } from './format'
import { usePoll, ticker } from './useNow'

const props = defineProps<{ machineId: string; sessionId: string }>()
const emit = defineEmits<{ unauth: [] }>()
const d = ref<SessionDetail | null>(null)
const text = ref('')
const err = ref('')
const now = ticker()
const url = `/api/sessions/${props.machineId}/${encodeURIComponent(props.sessionId)}`
usePoll(async () => {
  try { d.value = await api<SessionDetail>(url) }
  catch (e) { if (e instanceof Unauthorized) emit('unauth'); throw e }
}, 3000)

async function send() {
  const t = text.value.trim()
  if (!t) return
  err.value = ''
  try { await api(url + '/messages', 'POST', { text: t }); text.value = ''; d.value = await api<SessionDetail>(url) }
  catch (e) { if (e instanceof Unauthorized) emit('unauth'); else err.value = 'Send failed (1 to 20000 chars)' }
}
</script>
<template>
  <div v-if="d" class="space-y-4">
    <a href="#/" class="text-sm text-sky-400">&larr; Sessions</a>
    <div>
      <h1 class="font-semibold">{{ d.session.title ?? folder(d.session.cwd) }}</h1>
      <div class="text-xs text-slate-500">{{ d.session.machine }} · {{ d.session.runtime }} · {{ d.session.state }} · {{ d.session.cwd }}</div>
    </div>
    <div class="space-y-2">
      <div v-for="(e, i) in d.tail" :key="i" class="max-w-[90%] whitespace-pre-wrap break-words rounded-lg p-2 text-sm"
        :class="e.role === 'user' ? 'ml-auto bg-sky-800' : e.role === 'tool' ? 'bg-slate-800 font-mono text-xs text-slate-400' : 'bg-slate-700'">{{ e.text }}</div>
    </div>
    <div v-if="d.commands.length">
      <h2 class="mb-1 text-xs uppercase tracking-wide text-slate-400">Messages sent</h2>
      <div v-for="c in d.commands" :key="c.id" class="mb-1 rounded bg-slate-800 p-2 text-sm">
        <div class="flex justify-between"><span class="whitespace-pre-wrap break-words">{{ c.text }}</span>
          <span class="ml-2 shrink-0 text-xs" :class="c.status === 'failed' ? 'text-red-400' : 'text-slate-400'">{{ c.status }} · {{ ago(c.createdAt, now) }}</span></div>
        <div v-if="c.output" class="mt-1 whitespace-pre-wrap break-words font-mono text-xs text-slate-400">{{ c.output }}</div>
      </div>
    </div>
    <div class="sticky bottom-0 bg-slate-900 pb-2 pt-1">
      <textarea v-model="text" rows="3" placeholder="Message (Ctrl/Cmd+Enter to send)" class="w-full rounded bg-slate-800 p-2"
        @keydown.enter.ctrl.prevent="send" @keydown.enter.meta.prevent="send"></textarea>
      <div class="flex items-center justify-between">
        <span class="text-sm text-red-400">{{ err }}</span>
        <button class="rounded bg-sky-600 px-4 py-1" @click="send">Send</button>
      </div>
    </div>
  </div>
</template>
