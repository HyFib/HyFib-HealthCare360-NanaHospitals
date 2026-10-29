import { FluentProvider } from '@fluentui/react-components';
import type * as Ui from '@hc360/ui';
import { hc360LightTheme } from '@hc360/ui';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { EmergencyCase, Mlc } from './api';
import { SexualAssaultExamination } from './SexualAssaultExamination';

// A sexual-assault victim's examination on the ER case (BNSS 2023, s.184; for a child, the POCSO Act 2012, s.27): what
// the form asks for a child and for an adult, and what it shows once the report has gone to the investigating officer.

const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));
vi.mock('@/lib/lookups', () => ({ searchPractitioners: vi.fn(() => Promise.resolve([])) }));

const MLC: Mlc = {
  id: '0192a000-0000-7000-8000-00000000e001',
  number: 'NHMLC2600011',
  identifiedAt: '2026-10-29T05:00:00Z',
  broughtBy: 'Relative',
  broughtByName: 'Mother, Selvi',
  cause: 'SexualAssault',
  incidentAt: '2026-10-29T03:00:00Z',
  incidentPlace: 'Near the school, Velachery',
  injuries: 'Abrasions over both forearms',
  consciousOnArrival: true,
  smellOfAlcohol: false,
  doctorName: 'Dr. Meera Iyer',
  doctorRegistrationNumber: 'TNMC 45678',
  policeStation: 'Velachery',
  intimatedAt: '2026-10-29T05:20:00Z',
  intimationMode: 'Phone',
  receivedBy: 'SI Meena',
  policeEntryNumber: 'GD 12/2026',
  intimationOverdue: false,
  version: 3,
  isChild: true,
  isPocso: true,
  reportedTo: 'SpecialJuvenilePoliceUnit',
  examination: null,
  investigatingOfficer: null,
  reportForwardedAt: null,
  reportDueAt: null,
  reportOverdue: false,
};

const CASE: EmergencyCase = {
  id: '0192a000-0000-7000-8000-00000000c003',
  visitId: '0192a000-0000-7000-8000-00000000v003',
  patientId: '0192a000-0000-7000-8000-00000000p003',
  patientName: 'Kavya S',
  uhid: 'NH26000103',
  gender: 'Female',
  ageYears: 12,
  arrivedAt: '2026-10-29T04:40:00Z',
  arrivalMode: 'WalkIn',
  level: 2,
  triagedAt: '2026-10-29T04:45:00Z',
  doctorSeenAt: '2026-10-29T04:55:00Z',
  doctorName: 'Dr. Meera Iyer',
  disposition: null,
  disposedAt: null,
  disposedByName: null,
  dispositionNotes: null,
  referredTo: null,
  triages: [],
  mlc: MLC,
  version: 5,
};

const ADULT: EmergencyCase = {
  ...CASE,
  ageYears: 27,
  mlc: { ...MLC, isChild: false, isPocso: false, reportedTo: null },
};

const SENT: EmergencyCase = {
  ...CASE,
  mlc: {
    ...MLC,
    examination: {
      examinerId: '0192a000-0000-7000-8000-0000000000d7',
      examinerName: 'Dr. Lakshmi Iyer',
      examinerRegistration: 'TNMC 45678',
      startedAt: '2026-10-29T06:00:00Z',
      endedAt: '2026-10-29T06:40:00Z',
      consentGivenBy: 'Her mother, Selvi',
      attendantKind: 'Parent',
      attendantName: 'Mother, Selvi',
      missingSafeguards: [],
    },
    investigatingOfficer: 'SI Meena, AWPS Velachery',
    reportForwardedAt: '2026-10-29T09:00:00Z',
    reportDueAt: '2026-11-05T06:40:00Z',
  },
};

// Entered as an adult's and examined by a man with nobody present; then found to be a girl child's.
const FOUND_A_CHILD: EmergencyCase = {
  ...CASE,
  mlc: {
    ...MLC,
    examination: {
      examinerId: '0192a000-0000-7000-8000-0000000000d8',
      examinerName: 'Dr. Ravi Kumar',
      examinerRegistration: 'TNMC 45679',
      startedAt: '2026-10-29T06:00:00Z',
      endedAt: '2026-10-29T06:40:00Z',
      consentGivenBy: 'The patient',
      attendantKind: null,
      attendantName: null,
      missingSafeguards: ['WomanDoctor', 'PersonPresent'],
    },
    reportDueAt: '2026-11-05T06:40:00Z',
  },
};

function show(c: EmergencyCase) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <FluentProvider theme={hc360LightTheme}>
      <QueryClientProvider client={client}>
        <main>
          <h1>Emergency case</h1>
          <SexualAssaultExamination c={c} canMlc />
        </main>
      </QueryClientProvider>
    </FluentProvider>,
  );
}

describe('SexualAssaultExamination', () => {
  it("asks for a girl child's examination a woman doctor and who was present, with no accessibility problems", async () => {
    show(CASE);

    expect(screen.getByText('A child: the POCSO Act 2012 applies')).toBeInTheDocument();
    expect(screen.getByText(/A girl is examined by a woman doctor/)).toBeInTheDocument();
    expect(screen.getByRole('combobox', { name: /Who was present/ })).toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: /Their name/ })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Record the examination' })).toBeDisabled();
    await expectNoAxeViolations();
  });

  it("asks an adult's examination for nobody present", () => {
    show(ADULT);

    expect(screen.queryByText('A child: the POCSO Act 2012 applies')).not.toBeInTheDocument();
    expect(screen.queryByRole('combobox', { name: /Who was present/ })).not.toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: /Consent given by/ })).toBeInTheDocument();
  });

  it("says what a girl child's examination recorded before the case was known to be a child's lacks", async () => {
    show(FOUND_A_CHILD);

    expect(screen.getByText("Examined before the case was known to be a child's")).toBeInTheDocument();
    expect(
      screen.getByText(/a girl child is examined by a woman doctor \(s.27\(2\)\); and a parent/),
    ).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Correct the examination' })).toBeInTheDocument();
    await expectNoAxeViolations();
  });

  it('shows the examination and its report once sent, with nothing left to fill', async () => {
    show(SENT);

    expect(screen.getByText(/Examined by Dr. Lakshmi Iyer \(Reg. TNMC 45678\)/)).toBeInTheDocument();
    expect(screen.getByText(/present: Mother, Selvi \(parent\)/)).toBeInTheDocument();
    expect(screen.getByText(/Report sent to SI Meena, AWPS Velachery/)).toBeInTheDocument();
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
    await expectNoAxeViolations();
  });
});
