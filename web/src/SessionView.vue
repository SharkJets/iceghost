<script setup lang="ts">
import { computed, ref } from 'vue'
import { api, Unauthorized, type SessionDetail, type MachineRow } from './api'
import { ago, commandLabel, folder, isOffline } from './format'
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
const ta = ref<HTMLTextAreaElement | null>(null)
const seen = ref<string | null | undefined>(undefined)
const url = `/api/sessions/${props.machineId}/${encodeURIComponent(props.sessionId)}`
usePoll(async () => {
  try { d.value = await api<SessionDetail>(url); loadErr.value = false
    seen.value = (await api<MachineRow[]>('/api/machines').catch(() => null))?.find(m => String(m.id) === props.machineId)?.lastSeen }
  catch (e) { if (e instanceof Unauthorized) emit('unauth'); else loadErr.value = true; throw e }
}, 3000)

const offline = computed(() => seen.value !== undefined && isOffline(seen.value, now.value))
const cmds = computed(() => [...(d.value?.commands ?? [])].sort((a, b) => b.id - a.id))
const tail = computed(() => [...(d.value?.tail ?? [])].reverse())
const tones = { normal: 'text-slate-400', busy: 'text-sky-400', warn: 'text-amber-400', bad: 'text-red-400' }
const label = (c: SessionDetail['commands'][number]) => {
  const earlier = d.value!.commands.some(o => o.id < c.id && (o.status === 'queued' || o.status === 'delivered' || o.status === 'running'))
  return commandLabel(c, d.value!.session.lastActivity, now.value, earlier)
}

const send = guard(async () => {
  const t = text.value.trim()
  if (!t) return
  err.value = ''
  sending.value = true
  try { await api(url + '/messages', 'POST', { text: t }); text.value = ''; d.value = await api<SessionDetail>(url); window.scrollTo({ top: 0, behavior: 'smooth' }); ta.value?.focus() }
  catch (e) { if (e instanceof Unauthorized) emit('unauth'); else err.value = e instanceof Error && !/^\d+$/.test(e.message) ? e.message : 'Send failed (1 to 20000 chars)' }
  finally { sending.value = false }
})

const dismiss = guard(async (id: number) => {
  err.value = ''
  try { await api(`${url}/commands/${id}/dismiss`, 'POST'); d.value = await api<SessionDetail>(url) }
  catch (e) { if (e instanceof Unauthorized) emit('unauth'); else err.value = e instanceof Error ? e.message : 'Dismiss failed' }
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
    <p v-if="offline" class="rounded bg-amber-900 p-2 text-sm text-amber-200">This PC is offline (last seen {{ seen ? ago(seen, now) : 'never' }}). Messages can't be delivered.</p>
    <div>
      <textarea ref="ta" v-model="text" rows="3" placeholder="Message (Ctrl/Cmd+Enter to send)" class="w-full rounded bg-slate-800 p-2"
        @keydown.enter.ctrl.prevent="send" @keydown.enter.meta.prevent="send"></textarea>
      <div class="flex items-center justify-between">
        <span class="text-sm text-red-400">{{ err }}</span>
        <button class="rounded bg-sky-600 px-4 py-1" :disabled="sending || offline" @click="send">Send</button>
      </div>
    </div>
    <div v-if="cmds.length">
      <h2 class="mb-1 text-xs uppercase tracking-wide text-slate-400">Messages sent</h2>
      <div v-for="c in cmds" :key="c.id" class="mb-1 rounded bg-slate-800 p-2 text-sm">
        <div class="flex justify-between"><span class="whitespace-pre-wrap break-words">{{ c.text }}</span>
          <span class="ml-2 shrink-0 text-xs" :class="tones[label(c).tone]">
            <span v-if="label(c).tone === 'busy'" class="mr-1 inline-block h-3 w-3 animate-spin rounded-full border-2 border-sky-400 border-t-transparent align-middle"></span>{{ label(c).text }} · {{ ago(c.createdAt, now) }}
            <button v-if="c.status === 'queued' || c.status === 'delivered' || c.status === 'running'" class="ml-2 text-xs text-slate-400 hover:text-red-400" @click="dismiss(c.id)">Dismiss</button></span></div>
        <div v-if="c.output" class="mt-1 whitespace-pre-wrap break-words font-mono text-xs text-slate-400">{{ c.output }}</div>
      </div>
    </div>
    <div class="space-y-2">
      <div v-for="(e, i) in tail" :key="i" class="max-w-[90%] whitespace-pre-wrap break-words rounded-lg p-2 text-sm"
        :class="e.role === 'user' ? 'ml-auto bg-sky-800' : e.role === 'tool' ? 'bg-slate-800 font-mono text-xs text-slate-400' : 'bg-slate-700'">{{ e.text }}</div>
    </div>
  </div>
</template>
