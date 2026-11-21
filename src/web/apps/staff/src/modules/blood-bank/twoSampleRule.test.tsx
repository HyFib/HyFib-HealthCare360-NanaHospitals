import type * as Core from '@hc360/core';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fromIstInput, toIstInput } from '@/modules/ot/shared/datetime';
import { paged } from '@/test/fakeApi';
import { fakeServer, openScreen, SCREEN_TESTS, SLOW, STAFF_USER, stubBrowserApis } from '@/test/appScreen';
import type { BloodRequestDetail, BloodRequestListItem } from './api';
import { oneSampleNote } from './print';

// The two-sample rule on the request screen, with the shell, against a fake server. A patient never grouped before waits
// for a second sample: the screen says so, and records when and by whom each sample was drawn. The officer who grouped
// the first sample is told another officer groups the second; another officer groups it, drawn separately (not the
// first sample's draw time), as a new sample. Group-specific blood on the one sample is the treating doctor's emergency
// override, with a clinical reason, offered on emergency requests only. A sample that disagrees, a later one of the
// request's own or one grouped on another request of the patient, holds the request, its draw shown. The requests list
// shows which requests wait for a second sample.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

const REQUEST_ID = '0192a000-0000-7000-8000-00000000e101';
const REQUEST = `/api/v1/blood-bank/requests/${REQUEST_ID}`;
const PATIENT_ID = '0192a000-0000-7000-8000-00000000e201';
const SANTOSH = '0192a000-0000-7000-8000-00000000e301';
const MINUTE = 60_000;
const minuteAgo = (minutes: number) => new Date(Math.floor(Date.now() / MINUTE) * MINUTE - minutes * MINUTE);
const FIRST_DRAWN = minuteAgo(45).toISOString();
const GROUPED = minuteAgo(20).toISOString();

const request = (more: Partial<BloodRequestDetail> = {}): BloodRequestDetail => ({
  id: REQUEST_ID,
  requestNumber: 'BR26-00412',
  patientId: PATIENT_ID,
  uhid: 'NH26000456',
  patientName: 'Lakshmi Ammal',
  admissionId: null,
  visitId: null,
  orderItemId: null,
  componentType: 'PRBC',
  unitsRequested: 2,
  unitsReserved: 0,
  unitsIssued: 0,
  urgency: 'Urgent',
  indication: 'Upper GI bleed, Hb 5.8 g/dL',
  requiredBy: null,
  requestedByName: 'Dr. Meera Iyer',
  haemoglobinGdl: 5.8,
  plateletCount: null,
  patientBloodGroup: 'APositive',
  groupedAt: GROUPED,
  groupedByUserId: SANTOSH,
  groupedByName: 'Santosh Gaikwad',
  earlierGroup: null,
  firstSampleGroup: null,
  regroupedAt: null,
  groupingHeld: true,
  sampleDrawnAt: FIRST_DRAWN,
  sampleDrawnBy: 'Sr. Kavya Menon',
  sampleReceivedAt: GROUPED,
  firstSampleDrawnAt: null,
  firstSampleDrawnBy: null,
  secondSampleRequired: true,
  awaitingSecondSample: true,
  disagreeingSampleGroup: null,
  disagreeingSampleAt: null,
  disagreeingSampleDrawnAt: null,
  disagreeingSampleDrawnBy: null,
  disagreeingSampleRequestNumber: null,
  singleSampleOverrideBy: null,
  singleSampleOverrideByUserId: null,
  singleSampleOverrideAt: null,
  singleSampleOverrideReason: null,
  emergencyReleaseAuthorisedBy: null,
  emergencyReleaseByUserId: null,
  emergencyReleaseAt: null,
  emergencyReleaseReason: null,
  separateVerifier: true,
  status: 'InProcess',
  cancelReason: null,
  crossMatches: [],
  issues: [],
  version: 3,
  ...more,
});

/** A blood bank officer: no practitioner record, so no doctor. */
const officer = (id: string, displayName: string): Core.AuthUser => ({
  ...STAFF_USER,
  id,
  displayName,
  roles: ['BloodBankOfficer'],
  practitionerId: null,
  permissions: [
    'bloodbank.read',
    'bloodbank.donors.manage',
    'bloodbank.inventory.manage',
    'bloodbank.requests.create',
    'bloodbank.crossmatch.perform',
    'bloodbank.units.issue',
    'bloodbank.reactions.report',
    'bloodbank.reactions.investigate',
  ],
});

const SANTOSH_USER = officer(SANTOSH, 'Santosh Gaikwad');
const PRIYA_USER = officer('0192a000-0000-7000-8000-00000000e302', 'Priya Nair');

const answers = (detail: BloodRequestDetail) => ({
  [`GET ${REQUEST}`]: detail,
  [`GET /api/v1/patients/${PATIENT_ID}`]: { id: PATIENT_ID, uhid: 'NH26000456', fullName: 'Lakshmi Ammal' },
  [`GET /api/v1/patients/${PATIENT_ID}/payer`]: { current: null },
  [`GET /api/v1/emr/patients/${PATIENT_ID}/flags`]: { allergyStatus: 'NotRecorded', allergies: [], alerts: [] },
  'GET /api/v1/print/letterhead': { name: 'Nana Hospitals' },
});

let server: ReturnType<typeof fakeServer>;

beforeEach(() => {
  stubBrowserApis();
});

afterEach(() => {
  expect(server.unanswered).toEqual([]);
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('a patient never grouped before waits for a second sample', SCREEN_TESTS, () => {
  it('says so, with the first sample’s draw, and tells the officer who grouped it that another officer groups the second', async () => {
    server = fakeServer(answers(request()));
    openScreen(`/blood-bank/requests/${REQUEST_ID}`, SANTOSH_USER);

    expect(await screen.findByText('A second sample is needed', {}, SLOW)).toBeInTheDocument();
    expect(screen.getByText(/Sample drawn .* by Sr\. Kavya Menon · received/)).toBeInTheDocument();
    expect(
      screen.getByText('You grouped the first sample: another blood bank officer groups the second.'),
    ).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Group the second sample' })).not.toBeInTheDocument();
    expect(
      screen.getByText(/Compatible units appear once a second, separately drawn sample confirms the group/),
    ).toBeInTheDocument();
    // Group-specific blood on one sample is the doctor's: the blood bank is not offered it.
    expect(screen.queryByRole('button', { name: 'Authorise blood on one sample' })).not.toBeInTheDocument();
    await expectNoAxeViolations();
  });

  it('lets the officer who grouped the first sample record a second that disagrees', async () => {
    server = fakeServer({
      ...answers(request()),
      [`POST ${REQUEST}/grouping`]: new Response(
        JSON.stringify({
          title: 'Cannot complete this action',
          status: 422,
          code: 'bloodbank.grouping_discrepancy',
          detail: 'This sample groups as BPositive but the first grouped as APositive: possible wrong blood in tube.',
        }),
        { status: 422, headers: { 'content-type': 'application/problem+json' } },
      ),
    });
    const user = userEvent.setup();
    openScreen(`/blood-bank/requests/${REQUEST_ID}`, SANTOSH_USER);

    await user.click(await screen.findByRole('button', { name: 'Record a sample that disagrees' }, SLOW));
    const dialog = await screen.findByRole('dialog', { name: /Record a sample that disagrees/ });
    expect(within(dialog).getByText('You grouped the first sample (A+)')).toBeInTheDocument();
    expect(within(dialog).getByText(/one that groups otherwise is recorded whoever groups it/)).toBeInTheDocument();
    await user.selectOptions(within(dialog).getByLabelText(/group of the second sample/), 'BPositive');
    await user.type(within(dialog).getByLabelText(/Drawn by/), 'Sr. Leela Thomas');
    fireEvent.change(within(dialog).getByLabelText(/Drawn at/), { target: { value: toIstInput(minuteAgo(10)) } });
    await expectNoAxeViolations();
    await user.click(within(dialog).getByRole('button', { name: 'Save group' }));

    // Refused as the discrepancy it is, and recorded by the server: the dialog says so.
    expect(await within(dialog).findByText(/Grouping does not match/)).toBeInTheDocument();
    expect(server.requests.find((r) => r.path === `${REQUEST}/grouping`)?.body).toMatchObject({
      bloodGroup: 'BPositive',
      sampleDrawnBy: 'Sr. Leela Thomas',
      newSample: true,
    });
  });

  it('lets another officer group the second sample, drawn separately, as a new sample with its draw', async () => {
    server = fakeServer({ ...answers(request()), [`POST ${REQUEST}/grouping`]: null });
    const user = userEvent.setup();
    openScreen(`/blood-bank/requests/${REQUEST_ID}`, PRIYA_USER);

    await user.click(await screen.findByRole('button', { name: 'Group the second sample' }, SLOW));
    const dialog = await screen.findByRole('dialog', { name: /Group the second sample/ });
    const save = within(dialog).getByRole('button', { name: 'Save group' });
    await user.selectOptions(within(dialog).getByLabelText(/group of the second sample/), 'APositive');
    await user.type(within(dialog).getByLabelText(/Drawn by/), 'Sr. Leela Thomas');

    // The first tube again (its draw time) is not a second sample.
    fireEvent.change(within(dialog).getByLabelText(/Drawn at/), { target: { value: toIstInput(FIRST_DRAWN) } });
    expect(within(dialog).getByText(/This is the first sample’s draw time/)).toBeInTheDocument();
    expect(save).toBeDisabled();

    const drawn = toIstInput(minuteAgo(10));
    fireEvent.change(within(dialog).getByLabelText(/Drawn at/), { target: { value: drawn } });
    expect(within(dialog).queryByText(/This is the first sample’s draw time/)).not.toBeInTheDocument();
    await expectNoAxeViolations();
    await user.click(save);

    await vi.waitFor(() => expect(server.requests.filter((r) => r.path === `${REQUEST}/grouping`)).toHaveLength(1));
    expect(server.requests.find((r) => r.path === `${REQUEST}/grouping`)?.body).toEqual({
      bloodGroup: 'APositive',
      sampleDrawnAt: fromIstInput(drawn),
      sampleDrawnBy: 'Sr. Leela Thomas',
      newSample: true,
    });
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  });

  it('lets a second sample that groups differently be recorded even with the first sample’s draw minute, and warns', async () => {
    server = fakeServer({ ...answers(request()), [`POST ${REQUEST}/grouping`]: null });
    const user = userEvent.setup();
    openScreen(`/blood-bank/requests/${REQUEST_ID}`, PRIYA_USER);

    await user.click(await screen.findByRole('button', { name: 'Group the second sample' }, SLOW));
    const dialog = await screen.findByRole('dialog', { name: /Group the second sample/ });
    const save = within(dialog).getByRole('button', { name: 'Save group' });
    await user.type(within(dialog).getByLabelText(/Drawn by/), 'Sr. Leela Thomas');
    fireEvent.change(within(dialog).getByLabelText(/Drawn at/), { target: { value: toIstInput(FIRST_DRAWN) } });

    // The same group from the same draw confirms nothing: Save stays off.
    await user.selectOptions(within(dialog).getByLabelText(/group of the second sample/), 'APositive');
    expect(within(dialog).getByText(/This is the first sample’s draw time/)).toBeInTheDocument();
    expect(save).toBeDisabled();

    // A different group is the disagreement the server records whatever the draw: warned, and savable.
    await user.selectOptions(within(dialog).getByLabelText(/group of the second sample/), 'BPositive');
    expect(within(dialog).getByText(/recorded all the same: possible wrong blood in tube/)).toBeInTheDocument();
    expect(save).toBeEnabled();
    await user.click(save);

    await vi.waitFor(() => expect(server.requests.filter((r) => r.path === `${REQUEST}/grouping`)).toHaveLength(1));
    expect(server.requests.find((r) => r.path === `${REQUEST}/grouping`)?.body).toMatchObject({
      bloodGroup: 'BPositive',
      sampleDrawnAt: fromIstInput(toIstInput(FIRST_DRAWN)),
      newSample: true,
    });
  });

  it('prints the issue slip’s group as resting on one sample, with the doctor who authorised the blood', () => {
    const one = request({ awaitingSecondSample: true, singleSampleOverrideBy: 'Dr. Rao <b>' });
    expect(oneSampleNote(one)).toBe(
      '<div class="alert">on one sample only (not confirmed), blood authorised by Dr. Rao &lt;b&gt;</div>',
    );
    expect(oneSampleNote(request({ awaitingSecondSample: true, singleSampleOverrideBy: null }))).toBe(
      '<div class="alert">on one sample only (not confirmed)</div>',
    );
    expect(oneSampleNote(request({ awaitingSecondSample: false, singleSampleOverrideBy: 'Dr. Rao' }))).toBe('');
  });

  it('offers the treating doctor group-specific blood on the one sample of an emergency request, and sends the reason alone', async () => {
    server = fakeServer({
      ...answers(request({ urgency: 'Emergency' })),
      [`POST ${REQUEST}/single-sample-override`]: null,
    });
    const user = userEvent.setup();
    openScreen(`/blood-bank/requests/${REQUEST_ID}`);

    await user.click(await screen.findByRole('button', { name: 'Authorise blood on one sample' }, SLOW));
    const dialog = await screen.findByRole('alertdialog', { name: 'Authorise blood on one sample' });
    expect(within(dialog).getByText(/this request’s one sample grouped A\+/)).toBeInTheDocument();
    const authorise = within(dialog).getByRole('button', { name: 'Authorise' });
    await user.type(within(dialog).getByRole('textbox', { name: /Clinical reason/ }), 'Bleeding');
    await user.click(within(dialog).getByRole('checkbox', { name: /I, Dr\. Meera Iyer, am treating this patient/ }));
    expect(authorise).toBeDisabled();
    await user.type(within(dialog).getByRole('textbox', { name: /Clinical reason/ }), ', O-negative used up');
    await expectNoAxeViolations();
    await user.click(authorise);

    await vi.waitFor(() =>
      expect(server.requests.filter((r) => r.path === `${REQUEST}/single-sample-override`)).toHaveLength(1),
    );
    expect(server.requests.find((r) => r.path === `${REQUEST}/single-sample-override`)?.body).toEqual({
      reason: 'Bleeding, O-negative used up',
    });
    await waitFor(() => expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument());
  });

  it('does not offer blood on one sample on a request that is not an emergency: the blood bank waits for the second', async () => {
    server = fakeServer(answers(request({ urgency: 'Urgent' })));
    openScreen(`/blood-bank/requests/${REQUEST_ID}`);

    expect(await screen.findByText('A second sample is needed', {}, SLOW)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Authorise blood on one sample' })).not.toBeInTheDocument();
    expect(screen.queryByText('Blood needed before the second sample?')).not.toBeInTheDocument();
  });

  it('shows the doctor’s override, and offers the units it allows, while the second sample is still awaited', async () => {
    server = fakeServer({
      ...answers(
        request({
          urgency: 'Emergency',
          groupingHeld: false,
          singleSampleOverrideBy: 'Dr. Ramesh Rao',
          singleSampleOverrideByUserId: STAFF_USER.id,
          singleSampleOverrideAt: GROUPED,
          singleSampleOverrideReason: 'Ongoing haemorrhage, O-negative used up.',
        }),
      ),
      [`GET ${REQUEST}/compatible-units`]: [],
    });
    openScreen(`/blood-bank/requests/${REQUEST_ID}`, PRIYA_USER);

    expect(
      await screen.findByText(/Group-specific blood on one sample authorised by Dr\. Ramesh Rao/, {}, SLOW),
    ).toBeInTheDocument();
    expect(screen.getByText(/Ongoing haemorrhage, O-negative used up\./)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Group the second sample' })).toBeInTheDocument();
    await waitFor(() => expect(server.requests.some((r) => r.path === `${REQUEST}/compatible-units`)).toBe(true));
    await expectNoAxeViolations();
  });

  it('holds the request once a later sample disagrees, the doctor’s authorisation no longer standing', async () => {
    const disputed = request({
      urgency: 'Emergency',
      groupingHeld: true,
      disagreeingSampleGroup: 'BPositive',
      disagreeingSampleAt: GROUPED,
      disagreeingSampleDrawnAt: minuteAgo(25).toISOString(),
      disagreeingSampleDrawnBy: 'Sr. Leela Thomas',
      singleSampleOverrideBy: 'Dr. Ramesh Rao',
      singleSampleOverrideByUserId: STAFF_USER.id,
      singleSampleOverrideAt: GROUPED,
      singleSampleOverrideReason: 'Ongoing haemorrhage, O-negative used up.',
    });
    server = fakeServer(answers(disputed));
    openScreen(`/blood-bank/requests/${REQUEST_ID}`, PRIYA_USER);

    expect(await screen.findByText('The samples disagree: possible wrong blood in tube', {}, SLOW)).toBeInTheDocument();
    expect(
      screen.getByText(/A later sample grouped B\+, the first A\+\..*Dr\. Ramesh Rao authorised no longer goes/),
    ).toBeInTheDocument();
    // The disagreeing tube's draw, for the investigation.
    expect(screen.getByText(/The later sample was drawn .* by Sr\. Leela Thomas\./)).toBeInTheDocument();
    expect(screen.queryByText(/Group-specific blood on one sample authorised by/)).not.toBeInTheDocument();
    expect(screen.queryByText('A second sample is needed')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Group a fresh sample' })).toBeInTheDocument();
    expect(screen.getByText(/two samples of this patient disagree/)).toBeInTheDocument();
    await expectNoAxeViolations();
  });

  it('holds a request resting on one sample that a sample grouped on another request of the patient disagrees with', async () => {
    server = fakeServer(
      answers(
        request({
          urgency: 'Emergency',
          disagreeingSampleGroup: 'OPositive',
          disagreeingSampleAt: GROUPED,
          disagreeingSampleDrawnAt: minuteAgo(25).toISOString(),
          disagreeingSampleDrawnBy: 'Sr. Leela Thomas',
          disagreeingSampleRequestNumber: 'BR26-00413',
        }),
      ),
    );
    const user = userEvent.setup();
    openScreen(`/blood-bank/requests/${REQUEST_ID}`, PRIYA_USER);

    expect(await screen.findByText('The samples disagree: possible wrong blood in tube', {}, SLOW)).toBeInTheDocument();
    expect(
      screen.getByText(
        /A sample grouped on the patient’s request BR26-00413 came out O\+, this request’s one sample A\+\. That sample was drawn .* by Sr\. Leela Thomas\./,
      ),
    ).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Authorise blood on one sample' })).not.toBeInTheDocument();

    // The fresh sample that decides is drawn at another time than both tubes.
    await user.click(screen.getByRole('button', { name: 'Group a fresh sample' }));
    const dialog = await screen.findByRole('dialog', { name: /Group a fresh sample/ });
    expect(
      within(dialog).getByText(/one grouped on request BR26-00413 O\+ \(drawn .* by Sr\. Leela Thomas\)/),
    ).toBeInTheDocument();
    fireEvent.change(within(dialog).getByLabelText(/Drawn at/), {
      target: { value: toIstInput(minuteAgo(25).toISOString()) },
    });
    expect(within(dialog).getByText(/draw time of the sample that disagreed/)).toBeInTheDocument();
    await expectNoAxeViolations();
  });

  it('does not offer the doctor blood on either of two samples that disagree', async () => {
    server = fakeServer(
      answers(request({ urgency: 'Emergency', disagreeingSampleGroup: 'BPositive', disagreeingSampleAt: GROUPED })),
    );
    openScreen(`/blood-bank/requests/${REQUEST_ID}`);

    expect(await screen.findByText('The samples disagree: possible wrong blood in tube', {}, SLOW)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Authorise blood on one sample' })).not.toBeInTheDocument();
  });

  it('shows the group confirmed on two samples, with both draws', async () => {
    server = fakeServer({
      ...answers(
        request({
          groupingHeld: false,
          awaitingSecondSample: false,
          regroupedAt: GROUPED,
          firstSampleGroup: 'APositive',
          firstSampleDrawnAt: FIRST_DRAWN,
          firstSampleDrawnBy: 'Sr. Kavya Menon',
          sampleDrawnAt: minuteAgo(10).toISOString(),
          sampleDrawnBy: 'Sr. Leela Thomas',
          groupedByUserId: PRIYA_USER.id,
          groupedByName: 'Priya Nair',
        }),
      ),
      [`GET ${REQUEST}/compatible-units`]: [],
    });
    openScreen(`/blood-bank/requests/${REQUEST_ID}`, SANTOSH_USER);

    expect(await screen.findByText(/Group confirmed on two samples/, {}, SLOW)).toHaveTextContent(
      /by Sr\. Kavya Menon, the second .* by Sr\. Leela Thomas/,
    );
    expect(screen.getByText(/Second sample grouped by/)).toBeInTheDocument();
    expect(screen.queryByText('A second sample is needed')).not.toBeInTheDocument();
  });

  it('marks the requests that wait for a second sample on the requests list', async () => {
    const item = (id: string, requestNumber: string, awaitingSecondSample: boolean): BloodRequestListItem => ({
      id,
      requestNumber,
      patientId: PATIENT_ID,
      uhid: 'NH26000456',
      patientName: 'Lakshmi Ammal',
      componentType: 'PRBC',
      unitsRequested: 2,
      unitsReserved: 0,
      unitsIssued: 0,
      urgency: 'Urgent',
      status: 'InProcess',
      patientBloodGroup: 'APositive',
      createdAt: GROUPED,
      requiredBy: null,
      awaitingSecondSample,
    });
    server = fakeServer({
      'GET /api/v1/blood-bank/requests': paged([
        item(REQUEST_ID, 'BR26-00412', true),
        item('0192a000-0000-7000-8000-00000000e102', 'BR26-00413', false),
      ]),
    });
    openScreen('/blood-bank/requests', SANTOSH_USER);

    const waiting = (await screen.findByText(/BR26-00412/, {}, SLOW)).closest('tr');
    expect(within(waiting!).getByText('Second sample needed')).toBeInTheDocument();
    expect(
      within(screen.getByText(/BR26-00413/).closest('tr')!).queryByText('Second sample needed'),
    ).not.toBeInTheDocument();
  });
});
