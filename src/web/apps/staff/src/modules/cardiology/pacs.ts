import type { DeviceFileDto } from './api';

/**
 * A cardiology study's DICOM files and the PACS (INT-PACS): the technician sends them (STOW-RS), filed under the
 * patient's UHID and the study, and they open in the PACS viewer. A PACS stores DICOM only, so PDFs, pictures and
 * SCP-ECG files stay in HC360.
 */

export const DICOM_CONTENT_TYPE = 'application/dicom';

export interface PacsState {
  /** DICOM files attached to the study. */
  dicom: number;
  /** Those the PACS holds. */
  stored: number;
  /** Those still to send: never sent, or not stored the last time. */
  toSend: number;
  /** Whether a send was tried before and left some out. */
  retry: boolean;
}

export function pacsState(files: readonly DeviceFileDto[]): PacsState {
  const dicom = files.filter((file) => file.contentType === DICOM_CONTENT_TYPE);
  const stored = dicom.filter((file) => file.pacs?.status === 'Stored').length;
  return {
    dicom: dicom.length,
    stored,
    toSend: dicom.length - stored,
    retry: dicom.some((file) => file.pacs !== null && file.pacs.status !== 'Stored'),
  };
}

/** What the PACS did with the file, under its name; null for a file never sent. */
export function pacsFileStatus(file: DeviceFileDto): { text: string; stored: boolean } | null {
  const pacs = file.pacs;
  if (!pacs) {
    return null;
  }
  if (pacs.status === 'Stored') {
    return { text: pacs.message ? `In the PACS. ${pacs.message}` : 'In the PACS', stored: true };
  }
  const reason =
    pacs.message ??
    (pacs.status === 'NotDicom' ? 'It is not a DICOM file a PACS can store.' : 'The PACS did not store it.');
  return { text: `Not in the PACS. ${reason}`, stored: false };
}

/** The send button's label: "Send again" once a send left files out, so it is clear they are tried again. */
export function sendLabel(state: PacsState): string {
  return state.retry ? 'Send again to PACS' : 'Send to PACS';
}
