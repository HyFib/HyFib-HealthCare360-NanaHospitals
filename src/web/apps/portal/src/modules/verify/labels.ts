import type { MessageKey } from '@/i18n';
import type { DiagnosticKind } from '@/lib/types';
import type { VerifyKind } from './verification';

export const VERIFY_KIND_TEXT: Record<VerifyKind, { title: MessageKey; hint: MessageKey }> = {
  prescription: { title: 'verify.kind.prescription', hint: 'verify.kind.prescription.hint' },
  lab: { title: 'verify.kind.lab', hint: 'verify.kind.lab.hint' },
  radiology: { title: 'verify.kind.radiology', hint: 'verify.kind.radiology.hint' },
  cardiology: { title: 'verify.kind.cardiology', hint: 'verify.kind.cardiology.hint' },
};

/** The kind of document a report answer names (the server's diagnostic kind). */
export const REPORT_KIND_TEXT: Record<DiagnosticKind, MessageKey> = {
  Laboratory: 'verify.kind.lab',
  Radiology: 'verify.kind.radiology',
  Cardiology: 'verify.kind.cardiology',
};
