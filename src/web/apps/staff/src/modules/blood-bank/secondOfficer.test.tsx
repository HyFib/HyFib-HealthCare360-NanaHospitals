import type * as Core from '@hc360/core';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, openScreen, SCREEN_TESTS, SLOW, STAFF_USER, stubBrowserApis } from '@/test/appScreen';
import type { BloodRequestDetail, DonationDetail, TtiPanel } from './api';

// The blood bank's second officer, on the donation and request screens with the shell, against a fake server. Results that
// clear a donation wait for another officer: the officer who entered them is offered the correction, never the Verify, and
// another officer verifies once, with the version shown. Uncross-matched blood is released by the treating doctor from
// their own sign-in: the doctor's view of an emergency request offers it, the blood bank's does not.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

const DONATION_ID = '0192a000-0000-7000-8000-00000000d001';
const DONATION = `/api/v1/blood-bank/donations/${DONATION_ID}`;
const REQUEST_ID = '0192a000-0000-7000-8000-00000000d101';
const REQUEST = `/api/v1/blood-bank/requests/${REQUEST_ID}`;
const PATIENT_ID = '0192a000-0000-7000-8000-00000000d201';
const OTHER_OFFICER = '0192a000-0000-7000-8000-00000000d301';
const NOW = new Date().toISOString();

const CLEAR: TtiPanel = {
  hiv: 'NonReactive',
  hbsAg: 'NonReactive',
  hcv: 'NonReactive',
  syphilis: 'NonReactive',
  malaria: 'NonReactive',
};

const donation = (more: Partial<DonationDetail> = {}): DonationDetail => ({
  id: DONATION_ID,
  bagNumber: 'NHBB26000412',
  donorId: '0192a000-0000-7000-8000-00000000d401',
  donorNumber: 'BD26-000311',
  donorName: 'Rahul Deshmukh',
  bagType: 'Triple',
  volumeMl: 450,
  type: 'Voluntary',
  replacementForPatientId: null,
  collectedAt: NOW,
  collectedByName: 'Sr. Anita Pawar',
  status: 'Separated',
  bloodGroup: 'BPositive',
  tti: CLEAR,
  labResultsAt: NOW,
  labResultsByUserId: OTHER_OFFICER,
  labResultsByName: 'Santosh Gaikwad',
  awaitingVerification: true,
  verifiedAt: null,
  verifiedByName: null,
  verifiedByEnterer: false,
  separateVerifier: true,
  separatedAt: NOW,
  donorReaction: null,
  notes: null,
  components: [],
  version: 7,
  ...more,
});

const request = (more: Partial<BloodRequestDetail> = {}): BloodRequestDetail => ({
  id: REQUEST_ID,
  requestNumber: 'BR26-00231',
  patientId: PATIENT_ID,
  uhid: 'NH26000123',
  patientName: 'Asha Devi',
  admissionId: null,
  visitId: null,
  orderItemId: null,
  componentType: 'PRBC',
  unitsRequested: 2,
  unitsReserved: 0,
  unitsIssued: 0,
  urgency: 'Emergency',
  indication: 'Post-partum haemorrhage, Hb 6.1 g/dL',
  requiredBy: null,
  requestedByName: 'Dr. Meera Iyer',
  haemoglobinGdl: 6.1,
  plateletCount: null,
  patientBloodGroup: 'Unknown',
  groupedAt: null,
  groupedByUserId: null,
  groupedByName: null,
  earlierGroup: null,
  firstSampleGroup: null,
  regroupedAt: null,
  groupingHeld: false,
  emergencyReleaseAuthorisedBy: null,
  emergencyReleaseByUserId: null,
  emergencyReleaseAt: null,
  emergencyReleaseReason: null,
  separateVerifier: true,
  status: 'Pending',
  cancelReason: null,
  crossMatches: [],
  issues: [],
  version: 2,
  ...more,
});

// What the blood bank officer on duty holds: no practitioner record, so no doctor.
const OFFICER: Core.AuthUser = {
  ...STAFF_USER,
  id: '0192a000-0000-7000-8000-00000000d302',
  displayName: 'Priya Nair',
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
};

let server: ReturnType<typeof fakeServer>;

beforeEach(() => {
  stubBrowserApis();
});

afterEach(() => {
  expect(server.unanswered).toEqual([]);
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe("a donation's screening results and the second officer", SCREEN_TESTS, () => {
  it('offers Verify to another officer, says the units wait, and verifies once with the version shown', async () => {
    server = fakeServer({ [`GET ${DONATION}`]: donation(), [`POST ${DONATION}/lab-results/verify`]: null });
    const user = userEvent.setup();
    openScreen(`/blood-bank/donations/${DONATION_ID}`, OFFICER);

    expect(await screen.findByText('Awaiting verification', {}, SLOW)).toBeInTheDocument();
    expect(
      screen.getByText(/Entered by Santosh Gaikwad\. Check the group and every marker against the worksheet/),
    ).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Correct results' })).not.toBeInTheDocument();
    await expectNoAxeViolations();

    await user.click(screen.getByRole('button', { name: 'Verify results' }));
    const dialog = await screen.findByRole('alertdialog', { name: 'Verify the results of bag NHBB26000412?' });
    expect(within(dialog).getByText(/as entered by Santosh Gaikwad/)).toBeInTheDocument();
    await expectNoAxeViolations();
    await user.click(within(dialog).getByRole('button', { name: 'Verify and release' }));

    await vi.waitFor(() =>
      expect(server.requests.filter((r) => r.path === `${DONATION}/lab-results/verify`)).toHaveLength(1),
    );
    expect(server.requests.find((r) => r.path === `${DONATION}/lab-results/verify`)?.body).toEqual({ version: 7 });
    await waitFor(() => expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument());
  });

  it('does not offer Verify to the officer who entered the results, only the correction', async () => {
    server = fakeServer({
      [`GET ${DONATION}`]: donation({ labResultsByUserId: OFFICER.id, labResultsByName: 'Priya Nair' }),
    });
    openScreen(`/blood-bank/donations/${DONATION_ID}`, OFFICER);

    expect(
      await screen.findByText(
        /You entered these results\. Another blood bank officer checks them against the worksheet/,
        {},
        SLOW,
      ),
    ).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Verify/ })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Correct results' })).toBeInTheDocument();
    await expectNoAxeViolations();
  });

  it('lets the one officer on duty verify their own results where the facility has no second officer, saying it is reviewed', async () => {
    server = fakeServer({
      [`GET ${DONATION}`]: donation({
        labResultsByUserId: OFFICER.id,
        labResultsByName: 'Priya Nair',
        separateVerifier: false,
      }),
    });
    openScreen(`/blood-bank/donations/${DONATION_ID}`, OFFICER);

    expect(
      await screen.findByRole('button', { name: 'Verify my results (no second officer)' }, SLOW),
    ).toBeInTheDocument();
    expect(screen.getByText(/the release is listed for the medical officer’s review/)).toBeInTheDocument();
  });
});

describe('uncross-matched release on the doctor’s own sign-in', SCREEN_TESTS, () => {
  const answers = (detail: BloodRequestDetail) => ({
    [`GET ${REQUEST}`]: detail,
    [`GET /api/v1/patients/${PATIENT_ID}`]: { id: PATIENT_ID, uhid: 'NH26000123', fullName: 'Asha Devi' },
    [`GET /api/v1/patients/${PATIENT_ID}/payer`]: { current: null },
    [`GET /api/v1/emr/patients/${PATIENT_ID}/flags`]: { allergyStatus: 'NotRecorded', allergies: [], alerts: [] },
    'GET /api/v1/print/letterhead': { name: 'Nana Hospitals' },
  });

  it("offers the doctor's view of an emergency request the authorisation, and sends the reason alone", async () => {
    server = fakeServer({ ...answers(request()), [`POST ${REQUEST}/emergency-release`]: null });
    const user = userEvent.setup();
    openScreen(`/blood-bank/requests/${REQUEST_ID}`);

    await user.click(await screen.findByRole('button', { name: 'Authorise uncrossmatched release' }, SLOW));
    const dialog = await screen.findByRole('alertdialog', { name: 'Authorise uncross-matched blood' });
    // No name is typed: the release is the signed-in doctor's.
    expect(within(dialog).queryByRole('textbox', { name: /Doctor/ })).not.toBeInTheDocument();
    const authorise = within(dialog).getByRole('button', { name: 'Authorise release' });
    await user.type(within(dialog).getByRole('textbox', { name: /Clinical reason/ }), 'Haemorrhagic shock, BP 70/40');
    await user.click(within(dialog).getByRole('checkbox', { name: /I, Dr\. Meera Iyer, am treating this patient/ }));
    await expectNoAxeViolations();
    await user.click(authorise);

    await vi.waitFor(() =>
      expect(server.requests.filter((r) => r.path === `${REQUEST}/emergency-release`)).toHaveLength(1),
    );
    expect(server.requests.find((r) => r.path === `${REQUEST}/emergency-release`)?.body).toEqual({
      reason: 'Haemorrhagic shock, BP 70/40',
    });
    await waitFor(() => expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument());
  });

  it('does not offer the blood bank officer the authorisation', async () => {
    server = fakeServer(answers(request()));
    openScreen(`/blood-bank/requests/${REQUEST_ID}`, OFFICER);

    expect(
      await screen.findByText(/Ask the treating doctor to authorise it on this request/, {}, SLOW),
    ).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Authorise uncrossmatched release' })).not.toBeInTheDocument();
    await expectNoAxeViolations();
  });

  it('shows who authorised the release, and who grouped and cross-matched', async () => {
    server = fakeServer({
      ...answers(
        request({
          emergencyReleaseAuthorisedBy: 'Dr. Meera Iyer',
          emergencyReleaseByUserId: STAFF_USER.id,
          emergencyReleaseAt: NOW,
          emergencyReleaseReason: 'Haemorrhagic shock, BP 70/40.',
          patientBloodGroup: 'BPositive',
          groupedAt: NOW,
          groupedByUserId: OTHER_OFFICER,
          groupedByName: 'Santosh Gaikwad',
          status: 'InProcess',
          crossMatches: [
            {
              id: '0192a000-0000-7000-8000-00000000d501',
              componentId: '0192a000-0000-7000-8000-00000000d601',
              unitNumber: 'NHBB26000377-PR',
              method: 'GelCard',
              result: 'Compatible',
              performedAt: NOW,
              performedByUserId: OFFICER.id,
              performedByName: 'Priya Nair',
              remarks: null,
              isValid: true,
              chargeStatus: 'Posted',
            },
          ],
        }),
      ),
      [`GET ${REQUEST}/compatible-units`]: [],
    });
    openScreen(`/blood-bank/requests/${REQUEST_ID}`, OFFICER);

    expect(
      await screen.findByText(/Uncross-matched release authorised by Dr\. Meera Iyer/, {}, SLOW),
    ).toBeInTheDocument();
    expect(screen.getByText(/Grouped by Santosh Gaikwad/)).toBeInTheDocument();
    expect(within(screen.getByRole('table', { name: 'Cross-matches' })).getByText(/Priya Nair/)).toBeInTheDocument();
    await expectNoAxeViolations();
  });
});
