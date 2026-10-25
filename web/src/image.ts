const MAX_SIDE = 2400
const MAX_BYTES = 2.5 * 1024 * 1024
const SERVER_MAX = 3 * 1024 * 1024
const OK_TYPES = ['image/png', 'image/jpeg', 'image/gif', 'image/webp']

export function fitSize(w: number, h: number, max = MAX_SIDE): { w: number; h: number } {
  const s = Math.min(1, max / Math.max(w, h))
  return { w: Math.round(w * s), h: Math.round(h * s) }
}

export const needsReencode = (bytes: number, w: number, h: number) => bytes > MAX_BYTES || Math.max(w, h) > MAX_SIDE

const toBase64 = (b: Blob) => new Promise<string>((res, rej) => {
  const r = new FileReader()
  r.onload = () => res(String(r.result).split(',')[1] ?? '')
  r.onerror = () => rej(r.error)
  r.readAsDataURL(b)
})

/** Base64 (no data: prefix) + mime for the upload, and an object URL for the preview. Big or odd-format images become JPEG. */
export async function prepareImage(file: Blob): Promise<{ data: string; type: string; url: string }> {
  if (!file.type.startsWith('image/')) throw new Error('Not an image')
  const bmp = await createImageBitmap(file)
  let out = file
  if (needsReencode(file.size, bmp.width, bmp.height) || !OK_TYPES.includes(file.type)) {
    const { w, h } = fitSize(bmp.width, bmp.height)
    const cv = document.createElement('canvas')
    cv.width = w; cv.height = h
    cv.getContext('2d')!.drawImage(bmp, 0, 0, w, h)
    out = await new Promise<Blob>((res, rej) => cv.toBlob(b => (b ? res(b) : rej(new Error('Could not read image'))), 'image/jpeg', 0.9))
  }
  bmp.close()
  if (out.size > SERVER_MAX) throw new Error('Image too large (max 3 MB)')
  return { data: await toBase64(out), type: out.type, url: URL.createObjectURL(out) }
}
