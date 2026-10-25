<script setup lang="ts">
import { computed, onUnmounted, ref } from 'vue'
import { api } from './api'
import Login from './Login.vue'
import SessionList from './SessionList.vue'
import SessionView from './SessionView.vue'
import Machines from './Machines.vue'

const hash = ref(location.hash || '#/')
const onHash = () => (hash.value = location.hash || '#/')
addEventListener('hashchange', onHash)
onUnmounted(() => removeEventListener('hashchange', onHash))

const authed = ref(true) // optimistic; the first 401 flips it
const gen = ref(0) // bump to remount views after login
const m = computed(() => hash.value.match(/^#\/s\/([^/]+)\/(.+)$/))
const isMachines = computed(() => hash.value === '#/machines')

async function logout() { await api('/api/logout', 'POST').catch(() => {}); authed.value = false }
function loggedIn() { authed.value = true; gen.value++ }
</script>
<template>
  <Login v-if="!authed" @ok="loggedIn" />
  <div v-else class="mx-auto max-w-2xl p-3">
    <header class="mb-4 flex items-center justify-between">
      <a href="#/" class="text-lg font-semibold">iceghost</a>
      <nav class="flex gap-4 text-sm text-slate-400">
        <a href="#/machines">Machines</a><button @click="logout">Logout</button>
      </nav>
    </header>
    <SessionView v-if="m" :key="gen + hash" :machine-id="m[1]" :session-id="decodeURIComponent(m[2])" @unauth="authed = false" />
    <Machines v-else-if="isMachines" :key="gen" @unauth="authed = false" />
    <SessionList v-else :key="gen" @unauth="authed = false" />
  </div>
</template>
