<script setup lang="ts">
import { computed, ref } from 'vue'
import Icon from './Icon.vue'
import { api, Unauthorized, type SessionDetail, type MachineRow } from './api'
import { ago, agent, commandLabel, folder, isOffline, shortOs, stateOf } from './format'
import { usePoll, ticker } from './useNow'
import { guard } from './route'
import { prepareImage } from './image'

const props = defineProps<{ machineId: string; sessionId: string }>()
const emit = defineEmits<{ unauth: [] }>()
const d = ref<SessionDetail | null>(null)
const text = ref('')
const err = ref('')
const sending = ref(false)
const img = ref<{ data: string; type: string; url: string } | null>(null)
const pick = ref<HTMLInputElement | null>(null)
const loadErr = ref(false)
const now = ticker()
const ta = ref<HTMLTextAreaElement | null>(null)
const body = ref<HTMLElement | null>(null)
const seen = ref<string | null | undefined>(undefined)
const os = ref<string | null>(null)
const url = `/api/sessions/${props.machineId}/${encodeURIComponent(props.sessionId)}`
usePoll(async () => {
  try { d.value = await api<SessionDetail>(url); loadErr.value = false
    const m = (await api<MachineRow[]>('/api/machines').catch(() => null))?.find(m => String(m.id) === props.machineId)
    seen.value = m?.lastSeen; os.value = m?.os ?? null }
  catch (e) { if (e instanceof Unauthorized) emit('unauth'); else loadErr.value = true; throw e }
}, 3000)

const offline = computed(() => seen.value !== undefined && isOffline(seen.value, now.value))
const cmds = computed(() => [...(d.value?.commands ?? [])].sort((a, b) => b.id - a.id))
const tail = computed(() => [...(d.value?.tail ?? [])].reverse())
const pending = (c: { status: string }) => c.status === 'queued' || c.status === 'delivered' || c.status === 'running'
const copy = (t: string) => navigator.clipboard?.writeText(t)
const tones = { normal: 'text-slate-400', busy: 'text-sky-400', warn: 'text-amber-400', bad: 'text-red-400' }
const label = (c: SessionDetail['commands'][number]) => {
  const earlier = d.value!.commands.some(o => o.id < c.id && (o.status === 'queued' || o.status === 'delivered' || o.status === 'running'))
  return commandLabel(c, d.value!.session.lastActivity, now.value, earlier)
}

const clearImg = () => { if (img.value) URL.revokeObjectURL(img.value.url); img.value = null }
const attach = async (f?: Blob | null) => {
  if (!f) return
  err.value = ''
  try { const p = await prepareImage(f); clearImg(); img.value = p }
  catch (e) { err.value = e instanceof Error ? e.message : 'Could not read image' }
}
const onPick = (e: Event) => { const i = e.target as HTMLInputElement; attach(i.files?.[0]); i.value = '' }
const onPaste = (e: ClipboardEvent) => {
  const f = [...(e.clipboardData?.items ?? [])].find(i => i.type.startsWith('image/'))?.getAsFile()
  if (f) { e.preventDefault(); attach(f) } // text paste falls through
}

const send = guard(async () => {
  const t = text.value.trim()
  if (!t && !img.value) return
  err.value = ''
  sending.value = true
  try { await api(url + '/messages', 'POST', { text: t, image: img.value?.data, imageType: img.value?.type }); text.value = ''; clearImg(); d.value = await api<SessionDetail>(url); window.scrollTo({ top: 0, behavior: 'smooth' }); body.value?.scrollTo({ top: 0, behavior: 'smooth' }); ta.value?.focus() }
  catch (e) { if (e instanceof Unauthorized) emit('unauth'); else err.value = e instanceof Error && !/^\d+$/.test(e.message) ? e.message : 'Send failed (text up to 20000 chars)' }
  finally { sending.value = false }
})

const dismiss = guard(async (id: number) => {
  err.value = ''
  try { await api(`${url}/commands/${id}/dismiss`, 'POST'); d.value = await api<SessionDetail>(url) }
  catch (e) { if (e instanceof Unauthorized) emit('unauth'); else err.value = e instanceof Error ? e.message : 'Dismiss failed' }
})
</script>

<template>
  <div class="mb-4"><a href="#/" class="inline-flex min-h-9 items-center gap-1 text-sm text-sky-400 hover:text-sky-300 lg:min-h-0"><Icon name="back" />Back to Sessions</a>
    <p v-if="!d && loadErr" class="mt-2 text-red-400">Could not load session.</p>
  </div>
  <div v-if="d" class="space-y-4">
    <div class="flex flex-wrap items-start justify-between gap-x-4 gap-y-2">
      <div class="min-w-0">
        <h1 class="break-words text-2xl font-semibold">{{ d.session.title ?? folder(d.session.cwd) }}</h1>
        <div class="mt-2 flex flex-wrap items-center gap-x-4 gap-y-2 text-sm text-slate-300">
          <span class="flex items-center gap-1.5"><Icon name="monitor" />{{ d.session.machine }}</span>
          <span v-if="os" class="text-slate-400">{{ shortOs(os) }}</span>
          <span class="flex items-center gap-2"><span class="flex h-5 w-5 items-center justify-center rounded bg-slate-700 text-[11px] font-semibold">{{ agent(d.session.runtime).badge }}</span>{{ agent(d.session.runtime).name }}</span>
          <span class="rounded-md border px-2 py-0.5 text-xs" :class="stateOf(d.session.state).pill">{{ stateOf(d.session.state).label }}</span>
          <span class="flex min-w-0 items-center gap-1.5 text-slate-400"><Icon name="folder" /><span class="truncate">{{ d.session.cwd }}</span></span>
        </div>
      </div>
      <span class="text-xs text-slate-400">Last active {{ ago(d.session.lastActivity, now) }}</span>
    </div>
    <div class="grid gap-4 lg:grid-cols-3">
      <section class="flex min-w-0 flex-col rounded-xl lg:overflow-hidden border border-slate-700/60 bg-slate-900/70 lg:col-span-2">
        <div ref="body" class="min-h-48 space-y-4 p-4 lg:max-h-[60vh] lg:overflow-y-auto">
          <p v-if="!tail.length" class="text-sm text-slate-500">No messages yet.</p>
          <div v-for="e in tail" :key="`${e.at}|${e.role}|${e.text.length}|${e.text.slice(0, 40)}`" class="flex gap-3">
            <span class="flex h-7 w-7 shrink-0 items-center justify-center rounded-full bg-slate-700 text-xs font-semibold text-slate-200">
              <Icon v-if="e.role === 'user'" name="user" /><Icon v-else-if="e.role === 'tool'" name="chevron" /><template v-else>{{ agent(d.session.runtime).badge }}</template>
            </span>
            <div class="min-w-0 flex-1">
              <div class="mb-1 flex items-baseline gap-2 text-xs"><span class="font-semibold text-slate-200">{{ e.role === 'user' ? 'You' : e.role === 'tool' ? 'Tool' : 'Agent' }}</span><span class="text-slate-500">{{ e.at ? ago(e.at, now) : '' }}</span></div>
              <details v-if="e.role === 'tool'" class="group">
                <summary class="flex min-h-9 cursor-pointer list-none items-center gap-1.5 font-mono text-xs text-slate-400 [&::-webkit-details-marker]:hidden"><span class="shrink-0 transition-transform group-open:rotate-90"><Icon name="chevron" /></span><span class="truncate">{{ e.text.split('\n')[0] }}</span></summary>
                <div class="whitespace-pre-wrap break-words rounded-md border border-slate-700 bg-slate-950/60 p-2 font-mono text-xs text-slate-300">{{ e.text }}</div>
              </details>
              <p v-else class="whitespace-pre-wrap break-words text-sm text-slate-200">{{ e.text }}</p>
            </div>
          </div>
        </div>
        <div class="sticky top-0 [@media(max-height:500px)]:static z-10 order-first rounded-t-xl border-b border-slate-700/60 bg-slate-900 p-3 lg:static lg:order-none lg:rounded-none lg:border-b-0 lg:border-t lg:bg-transparent">
          <p v-if="offline" class="mb-2 rounded bg-amber-900 p-2 text-sm text-amber-200">This PC is offline (last seen {{ seen ? ago(seen, now) : 'never' }}). Messages can't be delivered.</p>
          <div class="rounded-lg border border-slate-600 bg-slate-950/60 p-2">
            <textarea ref="ta" v-model="text" rows="3" placeholder="Type a message to the agent..." class="w-full resize-none bg-transparent text-sm outline-none"
              @keydown.enter.ctrl.prevent="send" @keydown.enter.meta.prevent="send" @paste="onPaste"></textarea>
            <div v-if="img" class="mb-2 flex items-end gap-3">
              <img :src="img.url" alt="Attached screenshot" class="max-h-24 rounded-md border border-slate-700" />
              <button class="inline-flex min-h-9 items-center text-xs text-slate-400 hover:text-red-400 lg:min-h-0" @click="clearImg">Remove</button>
            </div>
            <div class="flex flex-wrap items-center justify-between gap-2">
              <span class="hidden text-xs text-slate-500 sm:inline">Ctrl+Enter to send</span>
              <div class="ml-auto flex shrink-0 items-center gap-2">
                <input ref="pick" type="file" accept="image/*" class="hidden" @change="onPick" />
                <button class="flex shrink-0 items-center gap-2 rounded-md border border-slate-600 px-3 py-1.5 text-sm text-slate-300 hover:text-slate-100" title="Attach or paste a screenshot" aria-label="Attach or paste a screenshot" @click="pick?.click()"><Icon name="image" /><span class="hidden min-[360px]:inline">Screenshot</span></button>
                <button class="flex shrink-0 items-center gap-2 rounded-md bg-sky-600 px-4 py-1.5 text-sm font-medium hover:bg-sky-500 disabled:opacity-50" :disabled="sending || offline" @click="send"><Icon name="send" />Send</button>
              </div>
            </div>
          </div>
          <p v-if="err" class="mt-1 text-sm text-red-400">{{ err }}</p>
        </div>
      </section>
      <div class="min-w-0 space-y-4">
        <section class="rounded-xl border border-slate-700/60 bg-slate-900/70 p-4">
          <h2 class="font-semibold">Message Status</h2>
          <p class="mb-3 text-xs text-slate-500">Shows the status of messages you've sent</p>
          <p v-if="!cmds.length" class="text-sm text-slate-500">No messages sent.</p>
          <div v-for="c in cmds" :key="c.id" class="border-t border-slate-700/50 py-2 text-sm first:border-t-0">
            <div class="flex items-start justify-between gap-2">
              <span class="min-w-0 break-words">{{ c.text.length > 80 ? c.text.slice(0, 80) + '...' : c.text }}</span>
              <button v-if="pending(c)" class="inline-flex min-h-9 shrink-0 items-center rounded border border-slate-600 px-2 py-0.5 text-xs text-slate-300 hover:text-red-400 lg:min-h-0" @click="dismiss(c.id)">Dismiss</button>
            </div>
            <div class="mt-1 flex flex-wrap items-center gap-x-2 text-xs">
              <span class="flex items-center gap-1" :class="tones[label(c).tone]">
                <span v-if="label(c).tone === 'busy'" class="inline-block h-3 w-3 animate-spin rounded-full border-2 border-sky-400 border-t-transparent"></span>{{ label(c).text }}</span>
              <span class="text-slate-500">{{ ago(c.createdAt, now) }}</span>
              <span v-if="c.hasImage" class="text-slate-500">· screenshot</span>
            </div>
            <div v-if="c.output" :title="c.output" class="mt-1 line-clamp-3 whitespace-pre-wrap break-words font-mono text-xs text-slate-400">{{ c.output }}</div>
          </div>
        </section>
        <section class="rounded-xl border border-slate-700/60 bg-slate-900/70 p-4">
          <h2 class="mb-3 font-semibold">Session Info</h2>
          <dl class="grid grid-cols-[5rem_minmax(0,1fr)] items-center gap-x-3 gap-y-2 text-sm">
            <dt class="text-slate-500">Project</dt><dd class="truncate" :title="d.session.cwd">{{ d.session.cwd }}</dd>
            <dt class="text-slate-500">Machine</dt><dd class="truncate">{{ d.session.machine }}<template v-if="os"> ({{ shortOs(os) }})</template></dd>
            <dt class="text-slate-500">Agent</dt><dd>{{ agent(d.session.runtime).name }}</dd>
            <dt class="text-slate-500">Session ID</dt>
            <dd class="flex items-center gap-2"><span class="truncate font-mono text-xs" :title="d.session.sessionId">{{ d.session.sessionId }}</span>
              <button class="inline-flex min-h-9 min-w-9 shrink-0 items-center justify-center text-slate-400 hover:text-slate-200 lg:min-h-0 lg:min-w-0" title="Copy session id" aria-label="Copy session ID" @click="copy(d.session.sessionId)"><Icon name="copy" /></button></dd>
            <dt class="text-slate-500">Status</dt><dd :class="stateOf(d.session.state).text">{{ stateOf(d.session.state).label }}</dd>
          </dl>
        </section>
      </div>
    </div>
  </div>
</template>
