<script setup lang="ts">
import { ref } from 'vue'
import { api } from './api'
import Icon from './Icon.vue'

const emit = defineEmits<{ ok: [] }>()
const password = ref('')
const err = ref('')
async function go() {
  try { await api('/api/login', 'POST', { password: password.value }); emit('ok') }
  catch { err.value = 'Login failed' }
}
</script>
<template>
  <form class="mx-auto mt-16 flex w-full max-w-sm flex-col items-center gap-4 rounded-2xl border border-slate-700/60 bg-slate-900/80 p-6 text-center sm:mt-24" @submit.prevent="go">
    <Icon name="snow" class="h-12 w-12 text-sky-300" />
    <h1 class="text-2xl font-semibold">IceGhost</h1>
    <p class="text-sm text-slate-400">Monitor coding agent sessions across your machines.</p>
    <label class="flex w-full items-center gap-2 rounded-md border border-slate-600 bg-slate-950/60 px-3 text-slate-400 focus-within:border-sky-500">
      <Icon name="lock" />
      <input v-model="password" type="password" autocomplete="current-password" placeholder="Admin password"
        class="min-w-0 flex-1 bg-transparent py-2 text-slate-100 outline-none" />
    </label>
    <button class="w-full rounded-md bg-sky-600 py-2 font-medium hover:bg-sky-500">Sign In</button>
    <p v-if="err" class="text-sm text-red-400">{{ err }}</p>
    <p class="text-xs text-slate-500">Uses a single admin password. Login lasts about a month and survives server restarts.</p>
  </form>
</template>
