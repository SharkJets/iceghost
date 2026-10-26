<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { visibleCommands } from './outbox'
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
const seen = ref<string | null | undefined>(undefined)
const os = ref<string | null>(null)
const url = `/api/sessions/${props.machineId}/${encodeURIComponent(props.sessionId)}`
usePoll(async () => {
  try { d.value = await api<SessionDetail>(url); loadErr.value = false
    const m = (await api<MachineRow[]>('/api/machines').catch(() => null))?.find(m => String(m.id) === props.machineId)
    seen.value = m?.lastSeen; os.value = m?.os ?? null }
  catch (e) { if (e instanceof Unauthorized) emit('unauth'); else loadErr.value = true; throw e }
}, 3000)

const pending = (c: { status: string }) => c.status === 'queued' || c.status === 'delivered' || c.status === 'running'
const offline = computed(() => seen.value !== undefined && isOffline(seen.value, now.value))
// pending, or failed newer than the newest transcript entry
const cmds = computed(() => visibleCommands(d.value?.commands ?? [], d.value?.tail.at(-1)?.at))
const tail = computed(() => [...(d.value?.tail ?? [])].reverse())
// announce only when the newest entry changes (not on first load)
const announce = ref('')
let base: string | undefined
watch(() => { const e = tail.value[0]; return e ? `${e.at}|${e.role}|${e.text.length}|${e.text.slice(0, 40)}` : '' }, k => {
  if (base === undefined) { base = k; return }
  announce.value = k ? `New message from ${tail.value[0].role === 'user' ? 'You' : 'Agent'}` : ''
})
const copy = (t: string) => navigator.clipboard?.writeText(t)
const tones = { normal: 'text-muted', busy: 'text-link', warn: 'text-stall', bad: 'text-bad' }
const label = (c: SessionDetail['commands'][number]) => {
  const earlier = d.value!.commands.some(o => o.id < c.id && pending(o))
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
  try { await api(url + '/messages', 'POST', { text: t, image: img.value?.data, imageType: img.value?.type }); text.value = ''; clearImg(); d.value = await api<SessionDetail>(url); window.scrollTo({ top: 0, behavior: 'smooth' }); ta.value?.focus() }
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
  <a href="#/" class="inline-flex min-h-11 items-center gap-1 text-link hover:underline"><Icon name="back" />All sessions</a>
  <p v-if="!d && loadErr" role="alert" class="text-bad">Could not load session.</p>
  <div v-if="d" class="space-y-4">
    <div class="space-y-2">
      <h1 class="break-words text-2xl font-bold leading-tight">{{ d.session.title ?? folder(d.session.cwd) }}</h1>
      <p class="flex flex-wrap items-center gap-x-2 gap-y-1 text-sm text-muted">
        <span class="inline-flex items-center gap-1.5 font-bold" :class="stateOf(d.session.state).text"><span aria-hidden="true" :class="stateOf(d.session.state).dot"></span>{{ stateOf(d.session.state).label }}</span>
        <span aria-hidden="true">·</span><span>{{ d.session.machine }}</span><span v-if="offline" class="rounded border border-edge px-1.5 text-xs text-stall">offline</span>
        <span aria-hidden="true">·</span><span>{{ agent(d.session.runtime).name }}</span>
        <span aria-hidden="true">·</span><span>active {{ ago(d.session.lastActivity, now) }}</span>
      </p>
    </div>
    <div class="flex flex-wrap items-start gap-5">
      <section aria-labelledby="h-conv" class="flex min-w-0 flex-[999_1_560px] flex-col gap-4">
        <h2 id="h-conv" class="sr-only">Conversation, newest first</h2>
        <div class="sticky top-0 [@media(max-height:500px)]:static z-10 bg-night py-2">
          <p v-if="offline" role="status" class="mb-2 rounded bg-panel p-2 text-sm text-stall">This PC is offline (last seen {{ seen ? ago(seen, now) : 'never' }}). Messages can't be delivered.</p>
          <label for="msg" class="mb-2 block text-sm font-semibold">Message the agent</label>
          <textarea id="msg" ref="ta" v-model="text" rows="2" aria-describedby="msg-hint" class="w-full resize-y rounded-xl border border-edge bg-panel p-3 text-ink outline-none"
            @keydown.enter.ctrl.prevent="send" @keydown.enter.meta.prevent="send" @paste="onPaste"></textarea>
          <span id="msg-hint" class="sr-only sm:not-sr-only sm:block sm:text-xs sm:text-muted">Ctrl+Enter to send</span>
          <div v-if="img" class="my-2 flex items-end gap-3">
            <img :src="img.url" alt="Attached screenshot" class="max-h-24 rounded-md border border-line" />
            <button type="button" class="inline-flex min-h-11 items-center px-2 text-sm text-link" @click="clearImg">Remove</button>
          </div>
          <div class="mt-2 flex items-center justify-end gap-2">
            <input ref="pick" type="file" accept="image/*" class="hidden" @change="onPick" />
            <button type="button" class="flex min-h-12 shrink-0 items-center gap-2 rounded-xl border border-edge px-3 text-sm text-ink" title="Attach or paste a screenshot" aria-label="Attach or paste a screenshot" @click="pick?.click()"><Icon name="image" /><span class="hidden min-[360px]:inline">Screenshot</span></button>
            <button type="button" class="flex min-h-12 shrink-0 items-center gap-2 rounded-xl bg-brand px-5 font-semibold text-white disabled:opacity-50" :disabled="sending || offline" @click="send"><Icon name="send" />Send</button>
          </div>
          <p v-if="err" role="alert" class="mt-1 text-sm text-bad">{{ err }}</p>
        </div>
        <p class="sr-only" role="status" aria-live="polite">{{ announce }}</p>
        <ol class="m-0 flex list-none flex-col gap-4 p-0">
          <li v-for="c in cmds" :key="'c' + c.id" class="flex max-w-[88%] flex-col items-end gap-1.5 self-end">
            <span class="text-xs text-muted">You · {{ ago(c.createdAt, now) }}<template v-if="c.hasImage"> · screenshot</template></span>
            <p class="m-0 whitespace-pre-wrap break-words rounded-2xl rounded-br-sm bg-bubble px-3.5 py-3 text-ink">{{ c.text }}</p>
            <span class="flex flex-wrap items-center justify-end gap-x-3 text-sm" :class="tones[label(c).tone]">
              <span class="inline-flex items-center gap-1.5"><span v-if="label(c).tone === 'busy'" class="inline-block h-3 w-3 animate-spin rounded-full border-2 border-current border-t-transparent"></span>{{ label(c).text }}</span>
              <button v-if="pending(c)" type="button" class="inline-flex min-h-11 items-center px-1 font-semibold text-link" @click="dismiss(c.id)">Cancel message</button>
            </span>
            <span v-if="c.output" :title="c.output" class="line-clamp-3 whitespace-pre-wrap break-words font-mono text-xs text-muted">{{ c.output }}</span>
          </li>
          <li v-if="!tail.length && !cmds.length" class="text-sm text-muted">No messages yet.</li>
          <li v-for="e in tail" :key="`${e.at}|${e.role}|${e.text.length}|${e.text.slice(0, 40)}`" class="flex flex-col gap-1.5" :class="e.role === 'user' ? 'max-w-[88%] items-end self-end' : ''">
            <span class="text-xs text-muted">{{ e.role === 'user' ? 'You' : 'Agent' }}<template v-if="e.at"> · {{ ago(e.at, now) }}</template></span>
            <details v-if="e.role === 'tool'" class="group min-w-0 rounded-lg border border-line bg-panel">
              <summary class="flex min-h-11 cursor-pointer list-none items-center gap-1.5 px-3 font-mono text-xs [&::-webkit-details-marker]:hidden"><span class="shrink-0 transition-transform group-open:rotate-90"><Icon name="chevron" /></span><span class="min-w-0 [overflow-wrap:anywhere]">{{ e.text.split('\n')[0] }}</span></summary>
              <pre tabindex="0" class="m-0 max-h-80 overflow-auto whitespace-pre-wrap break-words border-t border-line p-3 font-mono text-xs text-ink">{{ e.text }}</pre>
            </details>
            <p v-else-if="e.role === 'user'" class="m-0 whitespace-pre-wrap break-words rounded-2xl rounded-br-sm bg-bubble px-3.5 py-3 text-ink">{{ e.text }}</p>
            <p v-else class="m-0 whitespace-pre-wrap break-words text-ink">{{ e.text }}</p>
          </li>
        </ol>
      </section>
      <aside aria-labelledby="h-info" class="min-w-0 flex-[1_1_260px] space-y-3 rounded-2xl border border-line bg-panel p-4">
        <h2 id="h-info" class="text-sm font-bold uppercase tracking-wide text-muted">Session info</h2>
        <dl class="grid grid-cols-[auto_minmax(0,1fr)] items-center gap-x-4 gap-y-2 text-sm">
          <dt class="text-muted">Machine</dt><dd class="break-words">{{ d.session.machine }}<template v-if="os"> · {{ shortOs(os) }}</template></dd>
          <dt class="text-muted">Agent</dt><dd>{{ agent(d.session.runtime).name }}</dd>
          <dt class="text-muted">Project</dt><dd class="break-all font-mono text-xs">{{ d.session.cwd }}</dd>
          <dt class="text-muted">Session ID</dt>
          <dd class="flex items-center gap-1"><span class="truncate font-mono text-xs" :title="d.session.sessionId">{{ d.session.sessionId }}</span>
            <button type="button" class="inline-flex min-h-11 min-w-11 shrink-0 items-center justify-center text-link" title="Copy session ID" aria-label="Copy session ID" @click="copy(d.session.sessionId)"><Icon name="copy" /></button></dd>
          <dt class="text-muted">Status</dt>
          <dd class="inline-flex items-center gap-1.5" :class="stateOf(d.session.state).text"><span aria-hidden="true" :class="stateOf(d.session.state).dot"></span>{{ stateOf(d.session.state).label }}</dd>
        </dl>
      </aside>
    </div>
  </div>
</template>
