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

/**
 * What a prescription calls a medicine, as the hospital prints it (Indian Medical Council Regulations 2002, reg. 1.5): its
 * generic name in capitals with the strength ("PARACETAMOL 650 mg"), then the product the doctor chose with its form
 * ("Dolo 650 · Tablet"), or the form alone when the product is named by its generic. A line with no generic name, signed
 * before one was asked for, is named by the product, strength and form, as it was.
 */
export function medicineNames(
  line: Pick<PortalPrescriptionLine, 'drugName' | 'genericName' | 'strength' | 'dosageForm'>,
): { name: string; detail: string | null } {
  const generic = line.genericName?.trim();
  if (!generic) {
    return {
      name: [line.drugName, line.strength, line.dosageForm].filter((part) => part && part.trim()).join(' '),
      detail: null,
    };
  }
  const strength = line.strength?.trim();
  const product = namedByItsGeneric(line.drugName, generic) ? null : line.drugName.trim();
  const detail = [product, line.dosageForm?.trim()].filter(Boolean).join(' · ');
  return { name: [generic.toUpperCase(), strength].filter(Boolean).join(' '), detail: detail || null };
}

/**
 * Whether a product is named by its generic: the shorter name's words begin the longer's, whatever their case
 * ("Paracetamol 650 mg Tablet" for paracetamol), but a brand is not ("Pan" for pantoprazole).
 */
export function namedByItsGeneric(product: string, generic: string): boolean {
  const words = (name: string) => name.trim().toLowerCase().split(/\s+/).filter(Boolean);
  const [a, b] = [words(product), words(generic)];
  const [shorter, longer] = a.length <= b.length ? [a, b] : [b, a];
  return shorter.length > 0 && shorter.every((word, i) => word === longer[i]);
}
