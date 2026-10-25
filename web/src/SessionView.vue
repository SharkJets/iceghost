<script setup lang="ts">
import { ref, watch, nextTick } from 'vue'
import { api, Unauthorized, type SessionDetail } from './api'
import { ago, folder, nearBottom } from './format'
import { usePoll, ticker } from './useNow'
import { guard } from './route'

const props = defineProps<{ machineId: string; sessionId: string }>()
const emit = defineEmits<{ unauth: [] }>()
const d = ref<SessionDetail | null>(null)
const text = ref('')
const err = ref('')
const sending = ref(false)
const loadErr = ref(false)
const now = ticker()
const pinned = ref(false)
const bottom = (behavior: ScrollBehavior) => window.scrollTo({ top: document.documentElement.scrollHeight, behavior })
watch(d, async (n, o) => {
  const stick = pinned.value || nearBottom(window.scrollY, window.innerHeight, document.documentElement.scrollHeight)
  await nextTick()
  if (!o) bottom('instant')
  else if (stick) bottom('smooth')
  const last = n?.commands.reduce((a, c) => (a && a.createdAt > c.createdAt ? a : c), null as SessionDetail['commands'][number] | null)
  if (last && (last.status === 'done' || last.status === 'failed')) pinned.value = false
})
const url = `/api/sessions/${props.machineId}/${encodeURIComponent(props.sessionId)}`
usePoll(async () => {
  try { d.value = await api<SessionDetail>(url); loadErr.value = false }
  catch (e) { if (e instanceof Unauthorized) emit('unauth'); else loadErr.value = true; throw e }
}, 3000)

const send = guard(async () => {
  const t = text.value.trim()
  if (!t) return
  err.value = ''
  sending.value = true
  try { await api(url + '/messages', 'POST', { text: t }); text.value = ''; pinned.value = true; d.value = await api<SessionDetail>(url) }
  catch (e) { if (e instanceof Unauthorized) emit('unauth'); else err.value = 'Send failed (1 to 20000 chars)' }
  finally { sending.value = false }
})
</script>
<template>
  <div class="space-y-4">
    <a href="#/" class="text-sm text-sky-400">&larr; Sessions</a>
    <p v-if="!d && loadErr" class="text-red-400">Could not load session.</p>
  </div>
  <div v-if="d" class="space-y-4">
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
        <button class="rounded bg-sky-600 px-4 py-1" :disabled="sending" @click="send">Send</button>
      </div>
    </div>
  </div>
</template>
