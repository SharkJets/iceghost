<script setup lang="ts">
import { computed, onUnmounted, ref } from 'vue'
import { api, auth } from './api'
import Icon from './Icon.vue'
import Logo from './Logo.vue'
import Login from './Login.vue'
import SessionList from './SessionList.vue'
import SessionView from './SessionView.vue'
import Machines from './Machines.vue'
import { parseRoute } from './route'

const mainEl = ref<HTMLElement | null>(null)
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
const tab = (on: boolean) => `flex min-h-11 items-center gap-2 border-b-2 px-3 text-sm ${on ? 'border-link bg-raised text-ink' : 'border-transparent text-muted hover:text-ink'}`
</script>
<template>
  <Login v-if="!authed" @ok="loggedIn" />
  <template v-else>
    <a href="#main" @click.prevent="mainEl?.focus(); mainEl?.scrollIntoView()" class="sr-only focus:not-sr-only focus:absolute focus:left-2 focus:top-2 focus:z-50 focus:rounded-md focus:bg-brand focus:px-3 focus:py-2 focus:text-white">Skip to content</a>
    <header class="border-b border-line bg-panel">
      <div class="mx-auto flex min-h-14 max-w-7xl flex-wrap items-center gap-x-6 gap-y-2 px-4 sm:px-6">
        <a href="#/" class="flex min-w-0 items-center gap-3 py-2">
          <Logo class="h-8 w-auto" />
          <span class="text-lg font-semibold leading-tight">IceGhost <span class="text-xs font-normal text-muted">{{ version }}</span></span>
        </a>
        <nav class="flex gap-1 md:mx-auto">
          <a href="#/" :class="tab(route.name !== 'machines')" :aria-current="route.name !== 'machines' ? 'page' : undefined"><Icon name="monitor" />Sessions</a>
          <a href="#/machines" :class="tab(route.name === 'machines')" :aria-current="route.name === 'machines' ? 'page' : undefined"><Icon name="folder" />Machines</a>
        </nav>
        <div class="ml-auto flex items-center gap-3 text-sm text-ink">
          <span class="hidden items-center gap-1.5 sm:flex"><Icon name="user" />admin</span>
          <button class="flex min-h-11 items-center gap-1.5 rounded-md border border-edge px-3 hover:bg-raised" @click="logout"><Icon name="logout" />Log out</button>
        </div>
      </div>
    </header>
    <main id="main" ref="mainEl" tabindex="-1" class="outline-none mx-auto max-w-7xl px-4 pt-5 pb-[calc(5rem+env(safe-area-inset-bottom))] sm:px-6">
      <SessionView v-if="route.name === 'session'" :key="gen + hash" :machine-id="String(route.machineId)" :session-id="route.sessionId" @unauth="authed = false" />
      <Machines v-else-if="route.name === 'machines'" :key="gen" @unauth="authed = false" />
      <SessionList v-else :key="gen" @unauth="authed = false" />
    </main>
    <footer class="fixed inset-x-0 bottom-0 z-20 border-t border-line bg-panel pb-[env(safe-area-inset-bottom)] text-sm text-muted">
      <div class="mx-auto flex max-w-7xl flex-wrap items-center justify-between gap-x-4 px-4 py-2 sm:px-6">
        <div><a class="inline-flex min-h-9 items-center hover:text-ink" href="https://github.com/SharkJets/iceghost">https://github.com/SharkJets/iceghost</a></div>
        <div><a class="inline-flex min-h-9 items-center hover:text-ink" href="https://sharkjets.com">SharkJets.com</a></div>
      </div>
    </footer>
  </template>
</template>
