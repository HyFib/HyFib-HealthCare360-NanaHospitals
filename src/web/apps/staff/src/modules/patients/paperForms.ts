import { escapeHtml, humanize, printDocument, printHtml } from '@hc360/core';
import { GUARDIAN_RELATIONS, PATIENT_CATEGORIES } from './api';

/**
 * The paper forms of a downtime kit (NABH IMS, continuity when HC360 is down): blank A4 forms that follow HC360's own
 * screens field by field, so what is written on paper during a downtime is entered afterwards in the same order. Each
 * carries the downtime UHID, the date and time, and a box for who entered it in HC360 and when.
 */

export type PaperFormKind = 'registration' | 'consultation' | 'requisition' | 'mar' | 'tally';

export interface PaperFormInfo {
  kind: PaperFormKind;
  title: string;
  /** Who fills it in, and what it is for. */
  description: string;
}

export const PAPER_FORMS: readonly PaperFormInfo[] = [
  {
    kind: 'registration',
    title: 'Patient registration',
    description: 'Registration desk: the fields of Register patient, under the kit’s next downtime UHID.',
  },
  {
    kind: 'consultation',
    title: 'OP consultation and prescription',
    description: 'Doctors: vitals, findings, diagnosis, medicines and investigations advised.',
  },
  {
    kind: 'requisition',
    title: 'Laboratory and imaging requisition',
    description: 'Wards and OPD: tests ordered, with the sample collection details.',
  },
  {
    kind: 'mar',
    title: 'Medication administration record',
    description: 'Nurses: each dose given, when and by whom.',
  },
  {
    kind: 'tally',
    title: 'Back-entry tally',
    description: 'Each area: how many paper records were kept, entered and checked, for the downtime sign-off.',
  },
];

const box = (label: string, wide = false) =>
  `<div class="box${wide ? ' wide' : ''}"><span class="label">${escapeHtml(label)}</span></div>`;

const choices = (label: string, options: readonly string[]) =>
  `<div class="box wide"><span class="label">${escapeHtml(label)}</span><span class="choices">${options
    .map((o) => `<span class="choice">☐ ${escapeHtml(o)}</span>`)
    .join('')}</span></div>`;

const section = (title: string, content: string) => `<h2>${escapeHtml(title)}</h2><div class="grid">${content}</div>`;

/** Blank rows of a table with the given column headings. */
const table = (columns: readonly string[], rows: number) =>
  `<table class="lines"><thead><tr>${columns.map((c) => `<th>${escapeHtml(c)}</th>`).join('')}</tr></thead><tbody>${Array.from(
    { length: rows },
    () => `<tr>${columns.map(() => '<td></td>').join('')}</tr>`,
  ).join('')}</tbody></table>`;

/** The patient and downtime boxes every clinical form starts with. */
const patientHeader = () =>
  section(
    'Patient',
    [
      box('Downtime UHID / UHID'),
      box('Date and time'),
      box('Patient name', true),
      box('Age / sex'),
      box('Ward / OPD / bed'),
    ].join(''),
  );

/** Who entered the form in HC360 after the downtime, and when: filled in at back-entry. */
const enteredFooter = () =>
  `<div class="entered">${box('Entered in HC360 by (name and signature)', true)}${box('Entered on (date and time)')}${box(
    'Checked by',
  )}</div>`;

function registration(): string {
  return [
    section(
      'Downtime',
      [
        box('Downtime UHID (from the kit’s sheet)'),
        box('Date and time registered'),
        box('Registration desk / staff'),
      ].join(''),
    ),
    section(
      'Name',
      [
        box('Title'),
        box('First name'),
        box('Middle name'),
        box('Last name / surname'),
        choices('Gender', ['Male', 'Female', 'Other']),
      ].join(''),
    ),
    section(
      'Date of birth or age',
      [
        box('Date of birth (DD/MM/YYYY)'),
        box('or age: years'),
        box('months'),
        choices('Blood group', ['A+', 'A−', 'B+', 'B−', 'AB+', 'AB−', 'O+', 'O−']),
      ].join(''),
    ),
    section(
      'Contact',
      [box('Mobile number'), box('Alternate phone'), box('Email', true), box('Preferred language')].join(''),
    ),
    section(
      'Address',
      [
        box('House / street', true),
        box('Area / locality', true),
        box('City / town / village'),
        box('District'),
        box('State'),
        box('PIN code'),
      ].join(''),
    ),
    section(
      'Family and emergency contact',
      [
        box('Father / husband / guardian name', true),
        choices('Relation', GUARDIAN_RELATIONS.map(humanize)),
        box('Emergency contact name'),
        box('Phone'),
        box('Relationship'),
      ].join(''),
    ),
    section(
      'Other details',
      [
        choices('Marital status', ['Single', 'Married', 'Divorced', 'Widowed', 'Separated']),
        box('Occupation'),
        box('Religion'),
        box('Nationality'),
        choices('Patient category', PATIENT_CATEGORIES.map(humanize)),
        box('ABHA number'),
        box('ABHA address'),
      ].join(''),
    ),
    section(
      'Consent',
      [
        choices('', ['Patient was given the privacy notice']),
        choices('', ['Patient agrees to WhatsApp reminders and updates on this number']),
        box('Patient’s signature or thumb impression', true),
      ].join(''),
    ),
    '<p class="note">Do not write the Aadhaar number on paper: enter it in HC360 from the card at the next visit.</p>',
    enteredFooter(),
  ].join('');
}

function consultation(): string {
  return [
    patientHeader(),
    section('Consultation', [box('Department'), box('Doctor'), box('Token / visit')].join('')),
    section('Vitals', ['BP', 'Pulse', 'Temp', 'SpO₂', 'RR', 'Weight', 'Height'].map((v) => box(v)).join('')),
    section(
      'Notes',
      [
        box('Complaints', true),
        box('Findings', true),
        box('Diagnosis (and ICD-10 if known)', true),
        box('Allergies', true),
      ].join(''),
    ),
    '<h2>Medicines</h2>',
    table(['Medicine and strength', 'Dose', 'Route', 'Frequency', 'Duration', 'Instructions'], 8),
    section('Advice', [box('Investigations advised', true), box('Follow-up', true)].join('')),
    `<div class="grid">${box('Doctor’s signature', true)}${box('Registration number')}</div>`,
    enteredFooter(),
  ].join('');
}

function requisition(): string {
  return [
    patientHeader(),
    section(
      'Order',
      [
        box('Ordered by (doctor)'),
        choices('Priority', ['Routine', 'Urgent', 'STAT']),
        box('Clinical details / provisional diagnosis', true),
      ].join(''),
    ),
    '<h2>Tests</h2>',
    table(['Laboratory test or imaging study', 'Sample / body part', 'Remarks'], 10),
    section(
      'Sample',
      [
        box('Collected on (date and time)'),
        box('Collected by'),
        box('Received in lab / imaging at'),
        box('Received by'),
      ].join(''),
    ),
    '<p class="note">Critical results are phoned to the ward or doctor and written down with the time and who took them.</p>',
    enteredFooter(),
  ].join('');
}

function mar(): string {
  return [
    patientHeader(),
    section('Allergies and weight', [box('Allergies', true), box('Weight')].join('')),
    '<h2>Doses given</h2>',
    table(['Medicine', 'Dose', 'Route', 'Frequency', 'Time', 'Given by', 'Time', 'Given by', 'Time', 'Given by'], 12),
    '<p class="note">Write a dose not given with the reason (refused, nil by mouth, not available). Continue on a new sheet; number the sheets.</p>',
    enteredFooter(),
  ].join('');
}

function tally(): string {
  return [
    section(
      'Downtime',
      [
        box('Downtime number (from the log)'),
        box('Area / department'),
        box('From (date and time)'),
        box('To (date and time)'),
      ].join(''),
    ),
    '<h2>Paper records</h2>',
    table(['Form', 'Kept on paper', 'Entered in HC360', 'Checked (second person)', 'Remarks'], 8),
    section('Sign-off', [box('Area in-charge (name and signature)', true), box('Date')].join('')),
    '<p class="note">Send this sheet to the quality team with the area’s count; the downtime is closed when every area’s records are entered and checked.</p>',
  ].join('');
}

const BUILDERS: Record<PaperFormKind, () => string> = { registration, consultation, requisition, mar, tally };

/** The body of a paper form, headed with the facility and the form's title. */
export function paperFormBody(kind: PaperFormKind, facilityName: string): string {
  const info = PAPER_FORMS.find((f) => f.kind === kind)!;
  return `<header><div class="facility">${escapeHtml(facilityName)}</div><h1>${escapeHtml(info.title)}</h1>
<div class="muted">Downtime form: fill in while HC360 cannot be used, and enter it in HC360 when it is back.</div></header>${BUILDERS[kind]()}`;
}

const STYLES = `
  header { border-bottom: 2px solid #333; margin-bottom: 6px; padding-bottom: 4px; }
  .facility { font-weight: 600; font-size: 11pt; color: #045E80; }
  h1 { font-size: 14pt; }
  h2 { font-size: 10pt; margin: 8px 0 3px; text-transform: uppercase; letter-spacing: 0.5px; color: #333; }
  .grid { display: flex; flex-wrap: wrap; gap: 4px; }
  .box { flex: 1 1 30%; min-height: 34px; border: 1px solid #888; padding: 2px 4px; }
  .box.wide { flex-basis: 100%; }
  .label { font-size: 8pt; color: #444; display: block; }
  .choices { display: flex; flex-wrap: wrap; gap: 4px 12px; padding-top: 2px; }
  .choice { white-space: nowrap; }
  table.lines th, table.lines td { border: 1px solid #888; height: 24px; font-size: 8pt; }
  table.lines th { background: #eee; }
  .note { font-size: 8pt; margin: 6px 0; }
  .entered { display: flex; flex-wrap: wrap; gap: 4px; margin-top: 10px; border-top: 1px dashed #888; padding-top: 6px; }
  .entered .box { background: #f6f6f6; }`;

/** Prints one blank form on A4, for copying into the kits. */
export async function printPaperForm(kind: PaperFormKind, facilityName: string): Promise<void> {
  const info = PAPER_FORMS.find((f) => f.kind === kind)!;
  await printHtml(
    printDocument(paperFormBody(kind, facilityName), { title: info.title, pageSize: 'A4', styles: STYLES }),
    {
      pageSize: 'A4',
    },
  );
}
