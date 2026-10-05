import { ApiError } from '@hc360/core';
import { describe, expect, it } from 'vitest';
import { pacsNotice } from './pacsErrors';

const refusal = (code: string, detail: string) =>
  new ApiError({ status: 422, title: 'Cannot complete this action', code, detail });

describe('pacsNotice', () => {
  it('warns with the reason when PACS holds the accession under another patient', () => {
    const notice = pacsNotice(
      refusal('pacs.patient_mismatch', 'PACS holds accession NHRD1 under patient ID NH2, not NH1.'),
      'open',
    );

    expect(notice).toEqual({
      intent: 'warning',
      title: 'Images not opened: PACS does not confirm this patient',
      message: 'PACS holds accession NHRD1 under patient ID NH2, not NH1.',
    });
  });

  it.each(['pacs.patient_id_missing', 'pacs.ambiguous_study'])('warns on %s when linking', (code) => {
    expect(pacsNotice(refusal(code, 'Reason.'), 'link')).toMatchObject({
      intent: 'warning',
      title: 'Images not linked: PACS does not confirm this patient',
    });
  });

  it('informs when the images have not reached PACS yet', () => {
    expect(pacsNotice(refusal('pacs.study_not_found', 'Not yet.'), 'open')).toEqual({
      intent: 'info',
      title: 'Images are not available',
      message: 'Not yet.',
    });
  });

  it('leaves other errors to the caller', () => {
    expect(pacsNotice(refusal('imaging.cancelled', 'Cancelled.'), 'open')).toBeNull();
    expect(pacsNotice(new Error('network'), 'link')).toBeNull();
  });
});
