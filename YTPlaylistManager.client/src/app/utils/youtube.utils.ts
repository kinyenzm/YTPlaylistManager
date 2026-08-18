/** Miniatura por defecto de una canción. Es una URL estática: no gasta cuota. */
export function thumbUrl(videoId: string): string {
  return `https://i.ytimg.com/vi/${videoId}/default.jpg`;
}

/**
 * Distingue un videoId de un nombre de canción: los ids son alfanuméricos con
 * guiones y nunca llevan espacios. Decide contra qué campo se busca.
 */
export function looksLikeVideoId(text: string): boolean {
  return /^[A-Za-z0-9_-]{8,}$/.test(text) && !text.includes(' ');
}
