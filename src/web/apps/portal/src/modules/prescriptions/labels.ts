import type { Tone } from '@/components/ListRow';
import type { Language, MessageKey } from '@/i18n';
import type { PortalPrescriptionLine, PrescriptionStatus, RefillStatus } from '@/lib/types';

export const RX_STATUS: Record<PrescriptionStatus, { key: MessageKey; tone: Tone }> = {
  Signed: { key: 'rx.status.Signed', tone: 'success' },
  Cancelled: { key: 'rx.status.Cancelled', tone: 'danger' },
  Draft: { key: 'rx.status.Draft', tone: 'neutral' },
};

export const REFILL_STATUS: Record<RefillStatus, { key: MessageKey; tone: Tone }> = {
  Requested: { key: 'rx.refill.status.Requested', tone: 'warning' },
  Approved: { key: 'rx.refill.status.Approved', tone: 'success' },
  Rejected: { key: 'rx.refill.status.Rejected', tone: 'danger' },
  Cancelled: { key: 'rx.refill.status.Cancelled', tone: 'neutral' },
};

/**
 * "Twice a day, after food" in the patient's language (the server sends English, Hindi and Tamil), with the written pattern
 * ("1-0-1") alongside when it adds something.
 */
export function frequencyText(
  line: Pick<PortalPrescriptionLine, 'frequency' | 'frequencyEnglish' | 'frequencyHindi' | 'frequencyTamil'>,
  language: Language,
): string {
  const translated = { en: line.frequencyEnglish, hi: line.frequencyHindi, ta: line.frequencyTamil }[language];
  const words = translated || line.frequencyEnglish || line.frequency;
  const pattern = line.frequency?.trim();
  return pattern && words && !words.toLowerCase().includes(pattern.toLowerCase())
    ? `${words} (${pattern})`
    : words || pattern || '';
}

/** "Paracetamol 500 mg tablet" from the parts the doctor chose. */
export function medicineName(line: Pick<PortalPrescriptionLine, 'drugName' | 'strength' | 'dosageForm'>): string {
  return [line.drugName, line.strength, line.dosageForm].filter((part) => part && part.trim()).join(' ');
}
