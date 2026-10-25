<script setup lang="ts">
import { ref } from 'vue'
import { api } from './api'

const emit = defineEmits<{ ok: [] }>()
const password = ref('')
const err = ref('')
async function go() {
  try { await api('/api/login', 'POST', { password: password.value }); emit('ok') }
  catch { err.value = 'Login failed' }
}
</script>
<template>
  <form class="mx-auto mt-24 flex max-w-xs flex-col gap-3 p-4" @submit.prevent="go">
    <h1 class="text-xl font-semibold">iceghost</h1>
    <input v-model="password" type="password" autocomplete="current-password" placeholder="Password"
      class="rounded bg-slate-800 p-2" />
    <button class="rounded bg-sky-600 p-2">Sign in</button>
    <p v-if="err" class="text-sm text-red-400">{{ err }}</p>
  </form>
</template>
