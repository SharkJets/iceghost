<script setup lang="ts">
// Small generic stroke glyphs (24x24). Each entry is a list of path `d` strings.
const paths: Record<string, string[]> = {
  monitor: ['M3 4h18v12H3z', 'M8 20h8', 'M12 16v4'],
  folder: ['M3 6a1 1 0 011-1h5l2 2h8a1 1 0 011 1v9a1 1 0 01-1 1H4a1 1 0 01-1-1z'],
  chat: ['M4 5h16v11H9l-5 4z'],
  chevron: ['M9 6l6 6-6 6'],
  clock: ['M12 3a9 9 0 100 18 9 9 0 000-18z', 'M12 7v5l3 2'],
  check: ['M5 12.5l4.5 4.5L19 7.5'],
  alert: ['M12 7v6', 'M12 16.5v.01'],
  pause: ['M9 6v12', 'M15 6v12'],
  play: ['M8 5l11 7-11 7z'],
  user: ['M12 4a4 4 0 100 8 4 4 0 000-8z', 'M4 20a8 8 0 0116 0'],
  lock: ['M6 11h12v9H6z', 'M8 11V8a4 4 0 018 0v3'],
  send: ['M21 3L10 14', 'M21 3l-7 18-4-7-7-4z'],
  copy: ['M9 9h10v11H9z', 'M5 15V4h10'],
  logout: ['M10 4H5v16h5', 'M15 8l4 4-4 4', 'M19 12H9'],
  back: ['M15 6l-6 6 6 6'],
  plus: ['M12 5v14', 'M5 12h14'],
  image: ['M3 5h18v14H3z', 'M3 16l5-5 4 4 3-3 6 6', 'M8.5 9v.01'],
}
defineProps<{ name: string }>()
</script>
<template>
  <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" width="16" height="16" class="inline-block shrink-0" aria-hidden="true">
    <path v-for="d in paths[name]" :key="d" :d="d" />
  </svg>
</template>
