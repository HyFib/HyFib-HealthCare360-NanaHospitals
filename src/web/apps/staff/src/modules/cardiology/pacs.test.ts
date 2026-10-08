import { describe, expect, it } from 'vitest';
import type { DeviceFileDto } from './api';
import { DICOM_CONTENT_TYPE, pacsFileStatus, pacsState, sendLabel } from './pacs';

const file = (id: string, contentType: string, pacs: DeviceFileDto['pacs'] = null): DeviceFileDto => ({
  id,
  fileName: `${id}.bin`,
  contentType,
  sizeBytes: 2048,
  pacs,
});

const stored = { status: 'Stored', message: null, sentAt: '2026-10-08T04:30:00Z' } as const;
const refused = {
  status: 'Refused',
  message: 'The PACS refused the object: it is out of storage space or other resources.',
  sentAt: '2026-10-08T04:30:00Z',
} as const;

describe('pacsState', () => {
  it('counts only the DICOM files, and those still to send', () => {
    const files = [
      file('loop-1', DICOM_CONTENT_TYPE, stored),
      file('loop-2', DICOM_CONTENT_TYPE),
      file('report', 'application/pdf'),
      file('ecg', 'application/x-scp-ecg'),
    ];

    expect(pacsState(files)).toEqual({ dicom: 2, stored: 1, toSend: 1, retry: false });
  });

  it('says a file left out last time will be sent again', () => {
    const state = pacsState([file('loop-1', DICOM_CONTENT_TYPE, stored), file('loop-2', DICOM_CONTENT_TYPE, refused)]);

    expect(state).toEqual({ dicom: 2, stored: 1, toSend: 1, retry: true });
    expect(sendLabel(state)).toBe('Send again to PACS');
    expect(sendLabel(pacsState([file('loop-1', DICOM_CONTENT_TYPE)]))).toBe('Send to PACS');
  });

  it('has nothing to send for a study without DICOM files', () => {
    expect(pacsState([file('report', 'application/pdf')])).toEqual({ dicom: 0, stored: 0, toSend: 0, retry: false });
  });
});

describe('pacsFileStatus', () => {
  it('says nothing for a file never sent', () => {
    expect(pacsFileStatus(file('loop', DICOM_CONTENT_TYPE))).toBeNull();
  });

  it('says a file is in the PACS, with any warning', () => {
    expect(pacsFileStatus(file('loop', DICOM_CONTENT_TYPE, stored))).toEqual({ text: 'In the PACS', stored: true });
    expect(
      pacsFileStatus(
        file('loop', DICOM_CONTENT_TYPE, {
          ...stored,
          message: 'The PACS stored the object, and changed some of its values.',
        }),
      ),
    ).toEqual({ text: 'In the PACS. The PACS stored the object, and changed some of its values.', stored: true });
  });

  it('gives the reason a file is not in the PACS', () => {
    expect(pacsFileStatus(file('loop', DICOM_CONTENT_TYPE, refused))).toEqual({
      text: 'Not in the PACS. The PACS refused the object: it is out of storage space or other resources.',
      stored: false,
    });
    expect(
      pacsFileStatus(file('loop', DICOM_CONTENT_TYPE, { status: 'NotDicom', message: null, sentAt: stored.sentAt })),
    ).toEqual({ text: 'Not in the PACS. It is not a DICOM file a PACS can store.', stored: false });
  });
});
