/** Under 18 today, by the date of birth on the record (DPDP Act s.2(f)). */
export function isChild(dateOfBirth: string | null, today = new Date()): boolean {
  if (!dateOfBirth) {
    return false;
  }
  const adultFrom = new Date(today);
  adultFrom.setFullYear(adultFrom.getFullYear() - 18);
  return dateOfBirth > adultFrom.toISOString().slice(0, 10);
}
