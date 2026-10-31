import type * as Core from '@hc360/core';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent, { type UserEvent } from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { BillDetail, ReceiptDto } from '@/modules/billing/api';
import type { AdmissionCreated, AvailableBedDto } from '@/modules/inpatient/api';
import type { VisitDetail } from '@/modules/patients/api';
import type { PharmacySaleCreated } from '@/modules/pharmacy/api';
import type { AppointmentDetail } from '@/modules/scheduling/api';
import { ASHA as BILLED, BILLING_ANSWERS, DRAFT } from './accessibility/fixtures/billing';
import {
  AVAILABILITY,
  CALENDAR_ANSWERS,
  DOCTORS,
  FRONT_OFFICE_ANSWERS,
  OPD,
  PATIENTS,
} from './accessibility/fixtures/frontOffice';
import { ENTRY, ENTRY_ID, PHARMACY_ANSWERS } from './accessibility/fixtures/pharmacy';
import { fakeServer, openScreen, paged, SCREEN_TESTS, SLOW, stubBrowserApis, type FakeRequest } from './appScreen';
import { watchScreens } from './screens';

// SRS UX-06: the common journeys take at most three screens. Each test drives the real routes and screens, with the
// server faked, from the screen the journey starts on to the end of the task, and counts the distinct screens (routes)
// it passed through on the way. Dialogs and drawers open on their screen and are not counted.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

/** At most this many screens per journey (UX-06). */
const MAX_SCREENS = 3;

const ASHA = PATIENTS[0]!;
const ANIL = DOCTORS[1]!;

beforeEach(() => {
  stubBrowserApis();
  localStorage.clear();
  // jsdom does no layout; the slot strip keeps the chosen day in view.
  Element.prototype.scrollIntoView = () => undefined;
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

/** A patient found by typing part of the name into the patient picker, then chosen. */
async function pickPatient(user: UserEvent, name: string) {
  const picker = await screen.findByRole('combobox', { name: /^Patient/ }, SLOW);
  await user.type(picker, name);
  await user.click(await screen.findByRole('option', { name: new RegExp(name) }, SLOW));
}

/** Reports the journey's screens, and checks it within the limit. */
function expectWithinLimit(journey: string, screens: string[]) {
  process.stdout.write(`UX-06 ${journey}: ${screens.length} screen(s): ${screens.join(' → ')}\n`);
  expect(screens.length, `${journey} passes through ${screens.join(', ')}`).toBeLessThanOrEqual(MAX_SCREENS);
}

describe('common journeys take at most three screens (UX-06)', SCREEN_TESTS, () => {
  it('register a new patient, then check in with a queue token', async () => {
    const visit: VisitDetail = {
      id: '0192a000-0000-7000-8000-000000000c01',
      visitNumber: 'NHOV260000211',
      patientId: '0192a000-0000-7000-8000-000000000009',
      type: 'OutPatient',
      status: 'Open',
      departmentId: OPD.id,
      practitionerId: null,
      appointmentId: null,
      visitedAt: new Date().toISOString(),
      completedAt: null,
      isFollowUp: false,
      isFreeFollowUp: false,
      previousVisitId: null,
      payerType: 'Self',
      payerId: null,
      referralSource: 'Self',
      referredBy: null,
      chiefComplaint: null,
      isMlc: false,
      mlcNumber: null,
      notes: null,
      cancelReason: null,
      token: {
        id: '0192a000-0000-7000-8000-000000000c11',
        visitId: '0192a000-0000-7000-8000-000000000c01',
        patientId: '0192a000-0000-7000-8000-000000000009',
        departmentId: OPD.id,
        practitionerId: null,
        tokenDate: new Date().toISOString().slice(0, 10),
        sequence: 14,
        label: 'GM-014',
        status: 'Waiting',
        priority: 'Normal',
        issuedAt: new Date().toISOString(),
        calledAt: null,
        serviceStartedAt: null,
        completedAt: null,
        room: null,
        callCount: 0,
      },
      version: 1,
    };
    fakeServer({
      ...FRONT_OFFICE_ANSWERS,
      'POST /api/v1/patients/duplicates': [],
      'POST /api/v1/patients': { id: visit.patientId, uhid: 'NH26000301' },
      'POST /api/v1/visits': visit,
    });
    const user = userEvent.setup();
    const { router } = openScreen('/patients');
    const journey = watchScreens(router);

    const search = await screen.findByRole('main', {}, SLOW);
    await user.click((await within(search).findAllByRole('button', { name: 'Register patient' }, SLOW))[0]!);
    await user.type(await screen.findByRole('textbox', { name: /^First name/ }, SLOW), 'Sunita');
    await user.type(screen.getByRole('textbox', { name: /^Last name/ }), 'Kulkarni');
    await user.click(screen.getByRole('radio', { name: 'Female' }));
    await user.click(screen.getByRole('radio', { name: 'Age only' }));
    await user.type(await screen.findByLabelText(/^Years/), '46');
    await user.type(screen.getByRole('textbox', { name: /^Mobile number/ }), '9876543210');
    await user.click(screen.getByRole('button', { name: 'Register and issue UHID' }));

    const registered = await screen.findByRole('dialog', { name: 'Patient registered' }, SLOW);
    await user.click(within(registered).getByRole('button', { name: 'Check in now' }));
    const checkIn = await screen.findByRole('dialog', { name: /^Check in Sunita Kulkarni/ }, SLOW);
    const department = within(checkIn).getByRole('combobox', { name: /^Department/ });
    await waitFor(() => expect(department).toHaveTextContent(OPD.name));
    await user.selectOptions(department, OPD.id);
    await user.click(within(checkIn).getByRole('button', { name: 'Check in and issue token' }));
    expect(await screen.findByText('GM-014', {}, SLOW)).toBeInTheDocument();

    expectWithinLimit('Register → token', journey.screens());
    expect(journey.screens()).toEqual(['/_app/patients/', '/_app/patients/register']);
  });

  it('book an appointment', async () => {
    const doctor = AVAILABILITY[1]!;
    const server = fakeServer({
      ...CALENDAR_ANSWERS,
      'POST /api/v1/scheduling/appointments': (request: FakeRequest) => {
        const body = request.body as { startsAt: string; practitionerId: string; visitType: 'New'; channel: 'WalkIn' };
        const booked: AppointmentDetail = {
          id: '0192a000-0000-7000-8000-000000000a51',
          number: 'AP2600140',
          facilityId: doctor.slots[0]!.facilityId,
          patientId: ASHA.id,
          uhid: ASHA.uhid,
          patientName: ASHA.fullName,
          gender: ASHA.gender,
          age: ASHA.age,
          phone: null,
          practitionerId: body.practitionerId,
          practitionerName: doctor.practitionerName,
          departmentId: OPD.id,
          departmentName: OPD.name,
          scheduleId: null,
          startsAt: body.startsAt,
          endsAt: body.startsAt,
          seatNumber: 1,
          status: 'Booked',
          channel: body.channel,
          visitType: body.visitType,
          isTelehealth: false,
          room: 'Room 5',
          reason: null,
          notes: null,
          bookedAt: new Date().toISOString(),
          confirmedAt: null,
          feeAmount: 500,
          paymentStatus: 'PayAtHospital',
          paymentProvider: null,
          gatewayOrderId: null,
          gatewayPaymentId: null,
          amountPaid: null,
          paidAt: null,
          visitId: null,
          checkedInAt: null,
          completedAt: null,
          noShowAt: null,
          cancelReasonCode: null,
          cancelReason: null,
          cancelledBy: null,
          cancelledAt: null,
          rescheduledFromId: null,
          rescheduledToId: null,
          waitlistEntryId: null,
          reminder24hSentAt: null,
          reminder2hSentAt: null,
          history: [],
          version: 1,
        };
        return booked;
      },
    });
    const user = userEvent.setup();
    const { router } = openScreen('/scheduling');
    const journey = watchScreens(router);

    const appointments = await screen.findByRole('main', {}, SLOW);
    await user.click((await within(appointments).findAllByRole('button', { name: /^Book appointment/ }, SLOW))[0]!);
    await pickPatient(user, 'Asha');
    await user.selectOptions(await screen.findByRole('combobox', { name: 'Department' }, SLOW), OPD.id);
    await user.click(await screen.findByRole('radio', { name: new RegExp(ANIL.name) }, SLOW));
    const free = await waitFor(() => {
      const chips = screen
        .getAllByRole('button', { pressed: false })
        .filter(
          (chip) => !chip.hasAttribute('disabled') && /^\d{1,2}:\d{2}/.test(chip.getAttribute('aria-label') ?? ''),
        );
      expect(chips.length).toBeGreaterThan(0);
      return chips[0]!;
    }, SLOW);
    await user.click(free);
    const book = screen
      .getAllByRole('button', { name: 'Book appointment' })
      .find((b) => b.getAttribute('type') === 'submit')!;
    await waitFor(() => expect(book).toBeEnabled());
    await user.click(book);

    const booked = await screen.findByRole('dialog', { name: 'Appointment booked' }, SLOW);
    expect(within(booked).getByText('AP2600140')).toBeInTheDocument();
    await user.click(within(booked).getByRole('button', { name: 'Done' }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/scheduling'));
    expect(server.requests.filter((r) => r.method === 'POST')).toHaveLength(1);

    expectWithinLimit('Book appointment', journey.screens());
    expect(journey.screens()).toEqual(['/_app/scheduling/', '/_app/scheduling/book']);
  });

  it('make an OP bill and take the payment', async () => {
    let bill: BillDetail = DRAFT;
    const receipt: ReceiptDto = {
      id: '0192a000-0000-7000-8000-000000000b61',
      receiptNumber: 'NHRC260000401',
      purpose: 'BillPayment',
      patientId: BILLED.id,
      uhid: BILLED.uhid,
      patientName: BILLED.fullName,
      billId: DRAFT.id,
      billNumber: 'NHOB260000211',
      ipAccountId: null,
      amount: 850,
      source: 'Counter',
      counter: 'OP counter 1',
      receivedByName: 'Dr. Meera Iyer',
      receivedAt: new Date().toISOString(),
      notes: null,
      payments: [
        {
          mode: 'Cash',
          amount: 850,
          reference: null,
          cardLast4: null,
          cardNetwork: null,
          chequeNumber: null,
          chequeBank: null,
          chequeDate: null,
        },
      ],
    };
    const server = fakeServer({
      ...BILLING_ANSWERS,
      'GET /api/v1/patients': paged([BILLED]),
      // No draft yet: the cashier starts the bill from the visit's unbilled services.
      'GET /api/v1/billing/bills': paged([]),
      'POST /api/v1/billing/bills': () => bill,
      [`GET /api/v1/billing/bills/${DRAFT.id}`]: () => bill,
      [`POST /api/v1/billing/bills/${DRAFT.id}/finalize`]: () => {
        bill = {
          ...bill,
          status: 'Finalized',
          billNumber: 'NHOB260000211',
          finalizedAt: new Date().toISOString(),
          version: 3,
        };
        return bill;
      },
      'POST /api/v1/billing/receipts': () => {
        bill = { ...bill, status: 'Paid', paidAmount: 850, dueAmount: 0, receipts: [], version: 4 };
        return receipt;
      },
    });
    // The counter does not print in this test.
    localStorage.setItem('hc360:billing:counter', JSON.stringify({ autoPrint: false }));
    const user = userEvent.setup();
    const { router } = openScreen('/billing/op');
    const journey = watchScreens(router);

    await pickPatient(user, 'Asha');
    // Today's visit, with its unbilled services, comes first.
    await user.click((await screen.findAllByRole('button', { name: 'Start bill' }, SLOW))[0]!);
    await user.click(await screen.findByRole('button', { name: /^Finalise and collect/ }, SLOW));
    const finalise = await screen.findByRole('alertdialog', { name: 'Finalise this bill?' }, SLOW);
    await user.click(within(finalise).getByRole('button', { name: 'Finalise' }));
    const collect = await screen.findByRole('dialog', { name: 'Collect payment' }, SLOW);
    await user.click(await within(collect).findByRole('button', { name: /^Collect ₹850\.00/ }, SLOW));
    expect(await screen.findByRole('dialog', { name: 'Payment recorded' }, SLOW)).toBeInTheDocument();
    expect(server.requests.find((r) => r.path === '/api/v1/billing/receipts')?.body).toMatchObject({
      billId: DRAFT.id,
      payments: [expect.objectContaining({ mode: 'Cash', amount: 850 })],
    });

    expectWithinLimit('OP bill + payment', journey.screens());
    expect(journey.screens()).toEqual(['/_app/billing/op']);
  });

  it('dispense a prescription', async () => {
    const sale: PharmacySaleCreated = {
      id: '0192a000-0000-7000-8000-000000000e71',
      invoiceNumber: 'NHPS260000512',
      netAmount: 206.1,
      paidAmount: 206.1,
      dueAmount: 0,
      status: 'Paid',
    };
    const server = fakeServer({
      ...PHARMACY_ANSWERS,
      'GET /api/v1/print/letterhead': { name: 'Nana Hospitals, Pune' },
      'GET /api/v1/emr/patients/*': { allergyStatus: 'NoKnownAllergies', allergies: [], alerts: [] },
      'GET /api/v1/patients/*': (request: FakeRequest) =>
        request.path.endsWith('/visits') ? paged([]) : request.path.endsWith('/payer') ? { current: null } : undefined,
      [`POST /api/v1/pharmacy/queue/${ENTRY_ID}/dispense`]: sale,
    });
    // The counter does not print in this test.
    localStorage.setItem('hc360:pharmacy-counter', JSON.stringify({ autoPrintReceipt: false, autoPrintLabels: false }));
    const user = userEvent.setup();
    const { router } = openScreen('/pharmacy/queue');
    const journey = watchScreens(router);

    await user.click(await screen.findByText(ENTRY.prescriptionNumber, {}, SLOW));
    expect(await screen.findByText(`Prescription ${ENTRY.prescriptionNumber}`, {}, SLOW)).toBeInTheDocument();
    // One of the medicines is controlled: the pharmacist confirms the checks before dispensing.
    await user.click(
      await screen.findByRole(
        'checkbox',
        { name: "I have checked the prescription, the patient's identity and the quantity" },
        SLOW,
      ),
    );
    const dispense = await screen.findByRole('button', { name: /^Dispense and bill/ }, SLOW);
    await waitFor(() => expect(dispense).toBeEnabled(), SLOW);
    await user.click(dispense);
    const done = await screen.findByRole('dialog', { name: 'Dispensed' }, SLOW);
    expect(within(done).getByText(`Invoice ${sale.invoiceNumber}`)).toBeInTheDocument();
    expect(server.requests.filter((r) => r.path.endsWith('/dispense'))).toHaveLength(1);

    expectWithinLimit('Dispense prescription', journey.screens());
    expect(journey.screens()).toEqual(['/_app/pharmacy/queue/', '/_app/pharmacy/queue/$entryId']);
  });

  it('admit a patient to a bed', async () => {
    const bed: AvailableBedDto = {
      bedId: '0192a000-0000-7000-8000-000000000d71',
      label: 'GW-12',
      wardId: '0192a000-0000-7000-8000-000000000d61',
      wardName: 'General ward (female)',
      wardType: 'General',
      roomNumber: '204',
      status: 'Vacant',
      hasOxygen: true,
      hasVentilator: false,
      hasMonitor: false,
      isEligible: true,
      ineligibleReason: null,
    };
    const created: AdmissionCreated = {
      id: '0192a000-0000-7000-8000-000000000d81',
      admissionNumber: 'NHIP260000077',
      visitId: '0192a000-0000-7000-8000-000000000d82',
      visitNumber: 'NHIV260000077',
      bedId: bed.bedId,
      bedLabel: bed.label,
    };
    // She was seen in OPD an hour ago: the stay is linked to that visit (in the production-style run it was not).
    const opd = {
      id: '0192a000-0000-7000-8000-000000000d91',
      visitNumber: 'NHV260000076',
      type: 'OutPatient',
      status: 'InProgress',
      visitedAt: new Date(Date.now() - 3_600_000).toISOString(),
      departmentName: 'General Medicine',
      practitionerName: 'Dr. Anil Kumar',
    };
    const server = fakeServer({
      ...FRONT_OFFICE_ANSWERS,
      'GET /api/v1/beds/available': [bed],
      [`GET /api/v1/patients/${ASHA.id}/admissions`]: [],
      [`GET /api/v1/patients/${ASHA.id}/visits`]: { items: [opd], page: 1, pageSize: 10, totalCount: 1 },
      'POST /api/v1/inpatient/admissions': created,
    });
    // The admission desk does not print in this test.
    localStorage.setItem('hc360:inpatient:admit-autoprint', '0');
    const user = userEvent.setup();
    const { router } = openScreen('/inpatient/admit');
    const journey = watchScreens(router);

    await pickPatient(user, 'Asha');
    await user.click(await screen.findByRole('radio', { name: /^Bed GW-12/ }, SLOW));
    await user.click(
      screen.getAllByRole('button', { name: 'Admit patient' }).find((b) => b.getAttribute('type') === 'submit')!,
    );
    const admitted = await screen.findByRole('dialog', { name: 'Patient admitted' }, SLOW);
    expect(within(admitted).getByText('NHIP260000077')).toBeInTheDocument();
    expect(
      server.requests.find((r) => r.path === '/api/v1/inpatient/admissions' && r.method === 'POST')?.body,
    ).toMatchObject({
      patientId: ASHA.id,
      bedId: bed.bedId,
      source: 'OutPatient',
      sourceVisitId: opd.id,
    });

    expectWithinLimit('Admit to bed', journey.screens());
    expect(journey.screens()).toEqual(['/_app/inpatient/admit']);
  });
});
