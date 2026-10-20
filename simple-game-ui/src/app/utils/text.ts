export function stripDiacritics(value: string): string {
  return value.normalize('NFD').replace(/[\u0300-\u036f]/g, '');
}

export function normalizeWordInput(value: string, maxLength: number): string {
  return stripDiacritics(value)
    .replace(/[^A-Za-z]/g, '')
    .toUpperCase()
    .slice(0, maxLength);
}

export function normalizeLetter(value: string): string {
  return stripDiacritics(value).toUpperCase();
}
