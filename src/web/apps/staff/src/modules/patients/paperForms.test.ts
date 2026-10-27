import { describe, expect, it } from 'vitest';
import { PAPER_FORMS, paperFormBody } from './paperForms';

// The downtime kit's paper forms: each is headed with the facility and its title, carries the boxes that tie it to
// its patient and its back-entry, and the registration form asks for what Register patient asks for, so a form is
// entered field by field.

const text = (html: string) => new DOMParser().parseFromString(html, 'text/html').body.textContent ?? '';

describe('Paper forms', () => {
  it('heads every form with the facility (escaped) and the form', () => {
    for (const form of PAPER_FORMS) {
      const body = paperFormBody(form.kind, 'Nana <Hospitals> & Co');
      expect(body).toContain('Nana &lt;Hospitals&gt; &amp; Co');
      expect(text(body)).toContain(form.title);
    }
  });

  it('asks on the registration form for every field of Register patient', () => {
    const registration = text(paperFormBody('registration', 'Nana Hospitals'));
    for (const label of [
      'Downtime UHID',
      'Date and time registered',
      'Title',
      'First name',
      'Middle name',
      'Last name / surname',
      'Gender',
      'Date of birth',
      'Blood group',
      'Mobile number',
      'Alternate phone',
      'Email',
      'Preferred language',
      'House / street',
      'Area / locality',
      'City / town / village',
      'District',
      'State',
      'PIN code',
      'Father / husband / guardian name',
      'Relation',
      'Emergency contact name',
      'Relationship',
      'Marital status',
      'Occupation',
      'Religion',
      'Nationality',
      'Patient category',
      'ABHA number',
      'ABHA address',
      'Restricted record (VIP, staff or high-profile patient)',
      'Notes for the front desk',
      'Patient was given the privacy notice',
      'Patient agrees to WhatsApp reminders and updates on this number',
    ]) {
      expect(registration, label).toContain(label);
    }
    // The choices are the ones HC360 offers.
    expect(registration).toContain('Son of');
    expect(registration).toContain('Below poverty line');
    expect(registration).toContain('Separated');
    // Aadhaar is not written on paper.
    expect(registration).toContain('Do not write the Aadhaar number on paper');
  });

  it('ties each clinical form to its patient and to its back-entry', () => {
    for (const kind of ['registration', 'consultation', 'requisition', 'mar'] as const) {
      const body = text(paperFormBody(kind, 'Nana Hospitals'));
      expect(body).toMatch(/Downtime UHID/);
      expect(body).toContain('Entered in HC360 by');
    }

    const tally = text(paperFormBody('tally', 'Nana Hospitals'));
    expect(tally).toContain('Downtime number (from the log)');
    expect(tally).toContain('Checked (second person)');
  });
});
