<script setup lang="ts">
import { ref } from 'vue'
import { api } from './api'
import Icon from './Icon.vue'
import Logo from './Logo.vue'

const emit = defineEmits<{ ok: [] }>()
const password = ref('')
const err = ref('')
async function go() {
  try { await api('/api/login', 'POST', { password: password.value }); emit('ok') }
  catch { err.value = 'Login failed' }
}
</script>
<template>
  <form class="mx-auto mt-16 flex w-full max-w-sm flex-col items-center gap-4 rounded-2xl border border-line bg-panel p-6 text-center sm:mt-24" @submit.prevent="go">
    <Logo class="h-16 w-auto" />
    <h1 class="text-2xl font-semibold">IceGhost</h1>
    <p class="text-sm text-muted">Monitor coding agent sessions across your machines.</p>
    <label class="flex min-h-12 w-full items-center gap-2 rounded-md border border-edge bg-night px-3 text-muted focus-within:border-link">
      <Icon name="lock" />
      <span class="sr-only">Admin password</span>
      <input v-model="password" type="password" autocomplete="current-password" placeholder="Admin password"
        class="min-h-12 min-w-0 flex-1 bg-transparent text-ink outline-none placeholder:text-edge" />
    </label>
    <button class="min-h-12 w-full rounded-md bg-brand font-medium text-white hover:brightness-110">Sign In</button>
    <p v-if="err" role="alert" class="text-sm text-bad">{{ err }}</p>
    <p class="text-xs text-muted">Uses a single admin password. Login lasts about a month and survives server restarts.</p>
  </form>
</template>
