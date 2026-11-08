/**
 * Whether saving clears or unlinks the stay's medico-legal flag (maker-checker, slice 7, as the server decides it):
 * marking a stay medico-legal, or giving it its MLC number, is saved at once; clearing it, or changing or removing the
 * number it is linked by, waits for someone who keeps the MLC register.
 */
export function mlcChangeWaits(
  now: { isMlc: boolean; mlcNumber: string | null },
  asked: { isMlc: boolean; mlcNumber: string },
): boolean {
  if (!now.isMlc) {
    return false;
  }
  const number = (now.mlcNumber ?? '').trim();
  return !asked.isMlc || (number !== '' && number.toLowerCase() !== asked.mlcNumber.trim().toLowerCase());
}
