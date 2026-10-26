<script setup lang="ts">
import { useId } from 'vue'
const id = useId()
</script>
<template>
  <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 104 182" aria-hidden="true"><defs><linearGradient :id="`${id}b`" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#3A8DF0"/><stop offset="1" stop-color="#0353A4"/></linearGradient><linearGradient :id="`${id}f`" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#5FD8F2"/><stop offset="1" stop-color="#22A2DE"/></linearGradient><radialGradient :id="`${id}g`"><stop offset="0" stop-color="#0466C8" stop-opacity=".8"/><stop offset="1" stop-color="#0466C8" stop-opacity="0"/></radialGradient></defs><ellipse cx="52" cy="168" rx="52" ry="14" :fill="`url(#${id}g)`"/><ellipse cx="52" cy="167" rx="28" ry="6" fill="#0466C8"/><ellipse cx="50" cy="165" rx="18" ry="2" fill="#7FB2E5" opacity=".6"/><path d="M7 2h90a5 5 0 0 1 5 5v119Q52 98 2 126V7a5 5 0 0 1 5-5z" :fill="`url(#${id}b)`" stroke="#023E7D" stroke-width="2.5" stroke-linejoin="round"/><path d="M15 12h74a3 3 0 0 1 3 3v93Q52 84 12 108V15a3 3 0 0 1 3-3z" :fill="`url(#${id}f)`" stroke="#0353A4" stroke-width="2.5" stroke-linejoin="round"/><path d="M32 25l8-1-2 36h-8z M64 24l8 1 2 35h-8z" fill="#0466C8" stroke="#023E7D" stroke-width="2" stroke-linejoin="round"/><path d="M8 6h86" stroke="#fff" stroke-opacity=".35" stroke-width="2" stroke-linecap="round"/></svg>
</template>
