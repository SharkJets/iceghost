<script setup lang="ts">
import { computed, onUnmounted, ref } from 'vue'
import { api, auth } from './api'
import Icon from './Icon.vue'
import Login from './Login.vue'
import SessionList from './SessionList.vue'
import SessionView from './SessionView.vue'
import Machines from './Machines.vue'
import { parseRoute } from './route'

const hash = ref(location.hash || '#/')
const onHash = () => (hash.value = location.hash || '#/')
addEventListener('hashchange', onHash)
onUnmounted(() => removeEventListener('hashchange', onHash))

const authed = ref(true) // optimistic; the first 401 flips it
auth.onUnauthorized = () => { authed.value = false }
const version = import.meta.env.VITE_VERSION || 'dev'
const gen = ref(0) // bump to remount views after login
const route = computed(() => parseRoute(hash.value))

async function logout() { await api('/api/logout', 'POST').catch(() => {}); authed.value = false }
function loggedIn() { authed.value = true; gen.value++ }
const tab = (on: boolean) => `flex items-center gap-2 rounded-md border px-3 py-1.5 text-sm ${on ? 'border-sky-500/50 bg-sky-500/15 text-sky-200' : 'border-transparent text-slate-400 hover:text-slate-200'}`
</script>
<template>
  <Login v-if="!authed" @ok="loggedIn" />
  <div v-else class="mx-auto max-w-7xl p-3 sm:p-5">
    <header class="mb-5 flex flex-wrap items-center gap-x-6 gap-y-3 rounded-xl border border-slate-700/60 bg-slate-900/70 px-4 py-3">
      <a href="#/" class="flex min-w-0 items-center gap-3">
        <Icon name="snow" class="h-8 w-8 text-sky-300" />
        <span class="min-w-0">
          <span class="block text-lg font-semibold leading-tight">IceGhost <span class="text-xs font-normal text-slate-500">{{ version }}</span></span>
          <span class="hidden text-xs text-slate-400 sm:block">Monitor coding agent sessions across machines</span>
        </span>
      </a>
      <nav class="flex gap-1 md:mx-auto">
        <a href="#/" :class="tab(route.name !== 'machines')"><Icon name="monitor" />Sessions</a>
        <a href="#/machines" :class="tab(route.name === 'machines')"><Icon name="folder" />Machines</a>
      </nav>
      <div class="ml-auto flex items-center gap-3 text-sm text-slate-300">
        <span class="flex items-center gap-1.5"><Icon name="user" />admin</span>
        <button class="flex items-center gap-1.5 rounded-md border border-slate-600 px-3 py-1.5 hover:bg-slate-800" @click="logout"><Icon name="logout" />Logout</button>
      </div>
    </header>
    <SessionView v-if="route.name === 'session'" :key="gen + hash" :machine-id="String(route.machineId)" :session-id="route.sessionId" @unauth="authed = false" />
    <Machines v-else-if="route.name === 'machines'" :key="gen" @unauth="authed = false" />
    <SessionList v-else :key="gen" @unauth="authed = false" />
  </div>
</template>
