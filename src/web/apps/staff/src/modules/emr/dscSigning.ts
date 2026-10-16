import { bridge, BridgeError, useSession } from '@hc360/core';
import {
  canIssueFinalCopy,
  prescriptionsApi,
  type PrescriptionDigitalSignatureDto,
  type PrescriptionDto,
} from './prescriptionsApi';

/**
 * Signing a prescription with the doctor's Digital Signature Certificate (ERX-04). The server hands out the
 * prescription's canonical bytes, HC360 Desktop signs them with a DSC the doctor picks in the Windows certificate dialog
 * (the token asks for its PIN), and the server checks the detached CMS signature before keeping it.
 */
export async function signWithDsc(prescriptionId: string): Promise<PrescriptionDigitalSignatureDto> {
  const toSign = await prescriptionsApi.signingContent(prescriptionId);
  const signed = await bridge.signPrescription(toSign.content);
  return prescriptionsApi.attachSignature(prescriptionId, signed.signature);
}

/** Whether the signed-in user is the prescription's prescriber: the only one who may sign it with a DSC. */
export function useIsPrescriber(prescription: Pick<PrescriptionDto, 'practitionerId'>): boolean {
  const practitionerId = useSession((state) => state.user?.practitionerId ?? null);
  return practitionerId !== null && practitionerId === prescription.practitionerId;
}

/** What to tell the doctor when signing stopped on the desktop's side; null for a server refusal (shown as such). */
export function desktopSigningProblem(
  error: unknown,
): { intent: 'info' | 'warning'; title: string; body: string } | null {
  if (!(error instanceof BridgeError)) {
    return null;
  }
  switch (error.code) {
    case 'cancelled':
      return { intent: 'info', title: 'Not signed', body: 'No certificate was chosen.' };
    case 'method_not_found':
      return {
        intent: 'warning',
        title: 'Update HC360 Desktop',
        body: 'This version of HC360 Desktop cannot sign with a DSC. Ask IT to update it.',
      };
    default:
      return { intent: 'warning', title: 'The DSC could not sign', body: error.message };
  }
}

/** The badge next to the prescription's status: nothing until the hospital uses DSC signing. */
export function digitalSignatureBadge(
  signature: Pick<PrescriptionDigitalSignatureDto, 'state' | 'policy'> | undefined,
): { label: string; color: 'success' | 'danger' | 'warning' | 'informative' } | null {
  if (!signature) {
    return null;
  }
  switch (signature.state) {
    case 'Valid':
      return { label: 'DSC signed', color: 'success' };
    case 'Invalid':
      return { label: 'DSC signature invalid', color: 'danger' };
    case 'Untrusted':
      return { label: 'DSC issuer not verified', color: 'warning' };
    default:
      if (signature.policy === 'Off') {
        return null;
      }
      return signature.policy === 'Required'
        ? { label: 'DSC signature required', color: 'warning' }
        : { label: 'Not DSC signed', color: 'informative' };
  }
}

/**
 * What to do once a refill is issued (ERX-05): print it at once — unless the hospital requires the doctor's DSC (ERX-04).
 * A refill is a new prescription in the original prescriber's name, so then only that doctor can make it printable, by
 * signing it with their DSC: they are taken to it; anyone else (a pharmacist, a colleague) is told who must.
 */
export function afterRefill(
  refill: PrescriptionDto,
  myPractitionerId: string | null,
): { print: boolean; openToSign: boolean; title: string; body: string } {
  if (canIssueFinalCopy(refill)) {
    return {
      print: true,
      openToSign: false,
      title: `Refill ${refill.prescriptionNumber} issued`,
      body: 'It is now in the pharmacy queue.',
    };
  }
  if (myPractitionerId !== null && myPractitionerId === refill.practitionerId) {
    return {
      print: false,
      openToSign: true,
      title: `Refill ${refill.prescriptionNumber} issued: sign it with your DSC`,
      body: 'It is in the pharmacy queue. This hospital prints and shares prescriptions only once the doctor has signed them with their DSC.',
    };
  }
  return {
    print: false,
    openToSign: false,
    title: `Refill ${refill.prescriptionNumber} issued`,
    body:
      'It is in the pharmacy queue. This hospital prints and shares prescriptions only once the doctor has signed them with ' +
      `their DSC: ${refill.practitionerName ?? 'the prescribing doctor'} must sign it before it can be printed or given to the patient.`,
  };
}
