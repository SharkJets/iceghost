<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { api, Unauthorized, type SessionRow, type MachineRow } from './api'
import { ago, agent, folder, matches, splitAction, stateOf, summary } from './format'
import { guard } from './route'
import { usePoll, ticker } from './useNow'
import Icon from './Icon.vue'

const emit = defineEmits<{ unauth: [] }>()
const rows = ref<SessionRow[]>([])
const now = ticker()
const seen = ref<Record<number, string | null>>({})
const offline = (id: number) => id in seen.value && (!seen.value[id] || now.value.getTime() - new Date(seen.value[id]!).getTime() > 120000)
usePoll(async () => {
  try { rows.value = await api<SessionRow[]>('/api/sessions') }
  catch (e) { if (e instanceof Unauthorized) emit('unauth'); throw e }
  try {
    const ms = await api<MachineRow[]>('/api/machines')
    seen.value = Object.fromEntries(ms.map(m => [m.id, m.lastSeen]))
  }
  catch (e) { if (e instanceof Unauthorized) emit('unauth') }
}, 5000)

const stored = localStorage.getItem('ig.machine')
const sel = ref<number | null>(stored ? Number(stored) : null)
watch(sel, v => v === null ? localStorage.removeItem('ig.machine') : localStorage.setItem('ig.machine', String(v)))
const machines = computed(() => [...new Map(rows.value.map(r => [r.machineId, r.machine])).entries()].sort((a, b) => a[1].localeCompare(b[1])))
const runtimes = computed(() => [...new Set(rows.value.map(r => r.runtime))].sort())
const eff = computed(() => machines.value.some(m => m[0] === sel.value) ? sel.value : null)
const q = ref('')
const rt = ref('')
const filtered = computed(() => rows.value.filter(r => (eff.value === null || r.machineId === eff.value) && (!rt.value || r.runtime === rt.value) && matches(r, q.value)))
const needs = computed(() => filtered.value.filter(r => r.state === 'needsInput'))
const active = computed(() => filtered.value.filter(r => r.state !== 'needsInput' && r.state !== 'done').sort((a, b) => (a.state === 'stalled' ? 0 : 1) - (b.state === 'stalled' ? 0 : 1)))
const done = computed(() => filtered.value.filter(r => r.state === 'done'))
const counts = computed(() => summary(rows.value))
const pick = (e: Event) => { const v = (e.target as HTMLSelectElement).value; sel.value = v ? Number(v) : null }
const key = (r: SessionRow) => `${r.machineId}/${encodeURIComponent(r.sessionId)}`
const href = (r: SessionRow) => `#/s/${key(r)}`
const showDone = ref(false)

const drafts = ref<Record<string, string>>({})
const sending = ref<Record<string, boolean>>({})
const errs = ref<Record<string, string>>({})
const sent = ref<Record<string, boolean>>({})
const reply = guard(async (r: SessionRow) => {
  const k = key(r), t = (drafts.value[k] ?? '').trim()
  if (!t) return
  errs.value[k] = ''; sent.value[k] = false; sending.value[k] = true
  try { await api(`/api/sessions/${k}/messages`, 'POST', { text: t }); drafts.value[k] = ''; sent.value[k] = true }
  catch (e) { if (e instanceof Unauthorized) emit('unauth'); else errs.value[k] = e instanceof Error && !/^\d+$/.test(e.message) ? e.message : 'Send failed (text up to 20000 chars)' }
  finally { sending.value[k] = false }
})
</script>
<template>
  <div class="space-y-6">
    <header>
      <h1 class="text-3xl font-semibold">Sessions</h1>
      <p role="status" class="mt-1 flex flex-wrap items-center gap-x-2 text-sm text-muted">
        <span v-if="counts.needsInput"><b class="font-bold text-needs">{{ counts.needsInput }} need you</b></span>
        <template v-if="counts.stalled"><span v-if="counts.needsInput" aria-hidden="true">·</span><span>{{ counts.stalled }} stalled</span></template>
        <template v-if="counts.working"><span v-if="counts.needsInput || counts.stalled" aria-hidden="true">·</span><span>{{ counts.working }} working</span></template>
        <span v-if="counts.needsInput || counts.stalled || counts.working" aria-hidden="true">·</span>
        <span class="flex items-center gap-1.5"><span class="h-2 w-2 rounded-full bg-ok" aria-hidden="true"></span>Live</span>
      </p>
    </header>
    <div role="search" class="flex flex-wrap gap-3">
      <label class="min-w-0 flex-1 basis-full sm:basis-60"><span class="sr-only">Search sessions</span>
        <input v-model="q" type="search" placeholder="Search sessions" class="min-h-12 w-full rounded-lg border border-edge bg-panel px-3 text-ink placeholder:text-muted" />
      </label>
      <label v-if="machines.length > 1" class="min-w-0 flex-1 sm:flex-none"><span class="sr-only">Machine</span>
        <select :value="eff ?? ''" class="min-h-12 w-full rounded-lg border border-edge bg-panel px-3 text-ink" @change="pick">
          <option value="">All machines</option>
          <option v-for="[id, name] in machines" :key="id" :value="id">{{ name }}{{ offline(id) ? ' (offline)' : '' }}</option>
        </select>
      </label>
      <label v-if="runtimes.length > 1" class="min-w-0 flex-1 sm:flex-none"><span class="sr-only">Agent</span>
        <select v-model="rt" class="min-h-12 w-full rounded-lg border border-edge bg-panel px-3 text-ink">
          <option value="">All agents</option>
          <option v-for="r in runtimes" :key="r" :value="r">{{ agent(r).name }}</option>
        </select>
      </label>
    </div>
    <p v-if="!filtered.length" class="text-muted">{{ rows.length ? 'No sessions match.' : 'No sessions yet.' }}</p>

    <section v-if="needs.length" aria-labelledby="h-needs" class="space-y-3">
      <h2 id="h-needs" class="text-lg font-semibold text-needs">Needs you ({{ needs.length }})</h2>
      <div class="grid grid-cols-[repeat(auto-fit,minmax(min(100%,340px),1fr))] gap-4">
        <article v-for="r in needs" :key="key(r)" class="min-w-0 space-y-3 rounded-xl border border-needs/60 bg-card p-4">
          <div>
            <h3 class="break-words font-semibold">{{ r.title ?? folder(r.cwd) }}</h3>
            <p class="text-sm text-muted">{{ r.machine }} · {{ agent(r.runtime).name }} · {{ ago(r.lastActivity, now) }}<span v-if="offline(r.machineId)" class="ml-1 rounded bg-raised px-1 text-xs">offline</span></p>
            <p class="mt-1 break-all font-mono text-xs text-muted">{{ r.cwd }}</p>
          </div>
          <blockquote v-if="r.lastAction" class="break-words border-l-2 border-needs pl-3 text-sm text-ink">{{ r.lastAction }}</blockquote>
          <form class="space-y-2" @submit.prevent="reply(r)">
            <input v-model="drafts[key(r)]" type="text" :aria-label="`Reply to ${r.title ?? folder(r.cwd)}`" @input="sent[key(r)] = false" placeholder="Reply..." class="min-h-12 w-full rounded-lg border border-edge bg-panel px-3 text-ink placeholder:text-muted" />
            <div class="flex gap-2">
              <button type="submit" :disabled="sending[key(r)] || !(drafts[key(r)] ?? '').trim() || offline(r.machineId)" class="min-h-12 flex-1 rounded-lg bg-brand px-3 font-semibold text-white disabled:opacity-50">{{ sending[key(r)] ? 'Sending...' : 'Send reply' }}</button>
              <a :href="href(r)" class="inline-flex min-h-12 flex-1 items-center justify-center rounded-lg border border-edge px-3 font-semibold text-ink hover:bg-raised">Open session</a>
            </div>
            <p role="status" aria-live="polite" class="text-sm text-ok">{{ sent[key(r)] ? 'Sent' : '' }}</p>
            <p v-if="errs[key(r)]" role="alert" class="text-sm text-bad">{{ errs[key(r)] }}</p>
          </form>
        </article>
      </div>
    </section>

    <section v-if="active.length" aria-labelledby="h-active" class="space-y-3">
      <h2 id="h-active" class="text-lg font-semibold">Active ({{ active.length }})</h2>
      <ul class="divide-y divide-line overflow-hidden rounded-xl border border-line bg-panel">
        <li v-for="r in active" :key="key(r)">
          <a :href="href(r)" class="block min-h-12 px-4 py-3 hover:bg-raised focus-visible:bg-raised md:grid md:grid-cols-[8rem_minmax(0,2fr)_minmax(0,2fr)_6rem] md:items-center md:gap-4">
            <span class="flex items-center justify-between gap-2 md:justify-start">
              <span class="flex items-center gap-2 font-medium" :class="stateOf(r.state).text"><span class="inline-block shrink-0" :class="stateOf(r.state).dot" aria-hidden="true"></span>{{ stateOf(r.state).label }}</span>
              <span class="text-sm text-muted md:hidden">{{ ago(r.lastActivity, now) }}</span>
            </span>
            <span class="block min-w-0">
              <span class="flex items-center gap-2 font-semibold">
                <span v-if="r.pendingCommands" class="inline-block h-3 w-3 shrink-0 animate-spin rounded-full border-2 border-link border-t-transparent" aria-hidden="true"></span>
                <span class="break-words">{{ r.title ?? folder(r.cwd) }}</span>
              </span>
              <span class="block break-words text-sm text-muted">{{ r.machine }} · {{ agent(r.runtime).name }} · {{ r.cwd }}<span v-if="offline(r.machineId)" class="ml-1 rounded bg-raised px-1 text-xs">offline</span></span>
            </span>
            <span class="block min-w-0 text-sm">
              <span v-if="r.lastAction" class="text-muted"><template v-if="splitAction(r.lastAction).verb">{{ splitAction(r.lastAction).verb }} </template><code class="break-all font-mono text-ink">{{ splitAction(r.lastAction).arg }}</code></span>
              <span v-if="r.pendingCommands" class="ml-2 text-link">{{ r.pendingCommands }} waiting</span>
            </span>
            <span class="hidden text-right text-sm text-muted md:block">{{ ago(r.lastActivity, now) }}</span>
          </a>
        </li>
      </ul>
    </section>

    <section v-if="done.length" class="space-y-3">
      <h2 class="text-lg font-semibold">
        <button type="button" :aria-expanded="showDone" aria-controls="done-list" class="flex min-h-12 items-center gap-2" @click="showDone = !showDone">
          <span class="inline-block transition-transform" :class="showDone ? 'rotate-90' : ''" aria-hidden="true"><Icon name="chevron" /></span>Done ({{ done.length }})
        </button>
      </h2>
      <ul v-show="showDone" id="done-list" class="divide-y divide-line overflow-hidden rounded-xl border border-line bg-panel">
        <li v-for="r in done" :key="key(r)">
          <a :href="href(r)" class="block min-h-12 px-4 py-2 hover:bg-raised focus-visible:bg-raised">
            <span class="flex items-baseline justify-between gap-2"><span class="break-words font-semibold">{{ r.title ?? folder(r.cwd) }}</span><span class="shrink-0 text-sm text-muted">{{ ago(r.lastActivity, now) }}</span></span>
            <span class="block text-sm text-muted">{{ r.machine }} · {{ agent(r.runtime).name }}</span>
            <span v-if="r.lastAction" class="block break-all font-mono text-xs text-muted">{{ r.lastAction }}</span>
          </a>
        </li>
      </ul>
    </section>
  </div>
</template>
