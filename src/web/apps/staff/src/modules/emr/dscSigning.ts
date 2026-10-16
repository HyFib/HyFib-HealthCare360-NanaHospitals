import { bridge, BridgeError } from '@hc360/core';
import { prescriptionsApi, type PrescriptionDigitalSignatureDto } from './prescriptionsApi';

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
    default:
      if (signature.policy === 'Off') {
        return null;
      }
      return signature.policy === 'Required'
        ? { label: 'DSC signature required', color: 'warning' }
        : { label: 'Not DSC signed', color: 'informative' };
  }
}
