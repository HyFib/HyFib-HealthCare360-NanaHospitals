import { hasPermission, useSession } from '@hc360/core';
import { api } from '@/lib/api';
import type { WitnessConfirmed } from './api';

/** Who signed the form for a patient leaving against medical advice, or that nobody would. */
export type AgainstAdviceSignatory = 'Patient' | 'Relative' | 'RefusedToSign';

export const AGAINST_ADVICE_SIGNATORY_LABEL: Record<AgainstAdviceSignatory, string> = {
  Patient: 'The patient signed',
  Relative: 'A relative signed',
  RefusedToSign: 'Refused to sign',
};

/**
 * A patient leaving against medical advice, as the server takes it (maker-checker, slice 8): who signed (a relative named,
 * with the relationship); the doctor who explained the risks, as a confirmation given at this screen when the doctor is not
 * the one signed in; the witness's confirmation; and the scanned form, a file of the document store.
 */
export interface AgainstAdviceRequest {
  signedBy: AgainstAdviceSignatory;
  signatoryName: string | null;
  relationship: string | null;
  doctorConfirmationId: string | null;
  witnessConfirmationId: string;
  signedFormFileId: string | null;
}

/** What the screen holds while the declaration is filled in. */
export interface AgainstAdviceState {
  signedBy: AgainstAdviceSignatory;
  signatoryName: string;
  relationship: string;
  /** The doctor's confirmation, when the doctor is not the one signed in. */
  doctor: WitnessConfirmed | null;
  witness: WitnessConfirmed | null;
  signedFormFileId: string | null;
  signedFormName: string | null;
}

export const EMPTY_AGAINST_ADVICE: AgainstAdviceState = {
  signedBy: 'Patient',
  signatoryName: '',
  relationship: '',
  doctor: null,
  witness: null,
  signedFormFileId: null,
  signedFormName: null,
};

/**
 * What is still missing before the declaration can be sent, in words, or null when it is complete: a relative named with
 * the relationship, the doctor (unless the signed-in user is the doctor) and a witness who is not the doctor.
 */
export function againstAdviceMissing(state: AgainstAdviceState, doctorSignedIn: boolean): string | null {
  if (state.signedBy === 'Relative' && (!state.signatoryName.trim() || !state.relationship.trim())) {
    return 'Name the relative who signed, and their relationship to the patient.';
  }
  if (!doctorSignedIn && !state.doctor) {
    return 'The doctor who explained the risks confirms with their own user name and password.';
  }
  if (!state.witness) {
    return 'A witness confirms with their own user name and password.';
  }
  if (state.doctor && state.witness.userId === state.doctor.userId) {
    return 'The witness is someone other than the doctor who explained the risks.';
  }
  return null;
}

/** The request the server takes, once the declaration is complete (see `againstAdviceMissing`). */
export function againstAdviceRequest(state: AgainstAdviceState): AgainstAdviceRequest {
  const relative = state.signedBy === 'Relative';
  return {
    signedBy: state.signedBy,
    signatoryName: relative ? state.signatoryName.trim() : null,
    relationship: relative ? state.relationship.trim() : null,
    doctorConfirmationId: state.doctor?.confirmationId ?? null,
    witnessConfirmationId: state.witness!.confirmationId,
    signedFormFileId: state.signedFormFileId,
  };
}

/** Stores the scanned signed form, encrypted, for this patient (`files.write`); the server checks what it is from its content. */
export function uploadSignedForm(patientId: string, file: File) {
  const data = new FormData();
  data.append('file', file);
  data.append('category', 'consent');
  data.append('patientId', patientId);
  return api.post<{ id: string }>('/api/v1/files', data, { timeoutMs: 120_000 });
}

/** Whether the signed-in user is a doctor who explains the risks themselves: they sign encounters and have a practitioner record. */
export function useDoctorSignedIn(): boolean {
  const user = useSession((state) => state.user);
  return !!user?.practitionerId && hasPermission(user, 'clinical.encounters.sign');
}
