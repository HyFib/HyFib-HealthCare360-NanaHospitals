import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { beforeAll, describe, expect, it, vi } from 'vitest';
import type { WardRoundPatient } from '@/modules/inpatient/api';
import type { WorklistAppointment } from './api';
import { WardRoundList } from './WardRoundList';
import { WorklistAppointments } from './WorklistAppointments';

const NOW = new Date('2026-10-02T05:00:00Z'); // 10:30 IST

// Fluent's MessageBar measures itself; jsdom has no ResizeObserver.
beforeAll(() => {
  vi.stubGlobal(
    'ResizeObserver',
    class {
      observe() {}
      unobserve() {}
      disconnect() {}
    },
  );
});

function wrap(children: ReactNode) {
  return render(
    <FluentProvider theme={webLightTheme}>
      <QueryClientProvider client={new QueryClient()}>{children}</QueryClientProvider>
    </FluentProvider>,
  );
}

const appointment = (patch: Partial<WorklistAppointment>): WorklistAppointment => ({
  id: crypto.randomUUID(),
  number: 'APT-1',
  startsAt: '2026-10-02T04:30:00Z',
  endsAt: '2026-10-02T04:45:00Z',
  status: 'Booked',
  visitType: 'New',
  isTelehealth: false,
  reason: null,
  patientId: crypto.randomUUID(),
  uhid: 'NH26000001',
  patientName: 'Patient',
  gender: 'Female',
  age: '40 y',
  practitionerId: 'd1',
  practitionerName: 'Dr Rao',
  visitId: null,
  checkedInAt: null,
  tokenLabel: null,
  encounterId: null,
  encounterStatus: null,
  hasActiveAllergies: false,
  hasCriticalAlerts: false,
  isRestricted: false,
  ...patch,
});

const inpatient = (patch: Partial<WardRoundPatient>): WardRoundPatient => ({
  admissionId: crypto.randomUUID(),
  admissionNumber: 'IP-1',
  patientId: crypto.randomUUID(),
  uhid: 'NH26000002',
  patientName: 'Patient',
  gender: 'Male',
  age: '60 y',
  wardId: 'w1',
  wardName: 'Male general ward',
  bedLabel: 'GM-01',
  isCriticalCare: false,
  status: 'Admitted',
  admittedAt: '2026-09-30T04:30:00Z',
  dayOfStay: 3,
  expectedDischargeDate: null,
  provisionalDiagnosis: null,
  isMlc: false,
  isRestricted: false,
  consultantId: 'd1',
  consultantName: 'Dr Rao',
  isPrimaryConsultant: true,
  hasActiveAllergies: false,
  hasCriticalAlerts: false,
  lastVisitAt: null,
  seenToday: false,
  lastNoteAt: null,
  resultsToReview: 0,
  hasCriticalResults: false,
  vitalsAt: null,
  vitalsSeverity: null,
  vitalsFlags: [],
  ...patch,
});

describe('WorklistAppointments', () => {
  const props = {
    loading: false,
    isToday: true,
    showDoctor: false,
    canWrite: true,
    busy: false,
    now: NOW,
    onOpenChart: vi.fn(),
    onConsult: vi.fn(),
    onOpenVideo: vi.fn(),
  };

  it('marks late patients, shows the token of those who arrived, and starts their consultation', async () => {
    const onConsult = vi.fn();
    const arrived = appointment({ patientName: 'Asha Devi', status: 'CheckedIn', visitId: 'v1', tokenLabel: 'GM-012' });
    wrap(
      <WorklistAppointments
        {...props}
        onConsult={onConsult}
        appointments={[appointment({ patientName: 'Late Lata', startsAt: '2026-10-02T04:30:00Z' }), arrived]}
      />,
    );

    const lateRow = screen.getByText('Late Lata').closest('tr')!;
    expect(within(lateRow).getByText('Late')).toBeInTheDocument();
    expect(within(lateRow).queryByRole('button', { name: /Start/ })).not.toBeInTheDocument();

    expect(screen.getByText('Arrived · GM-012')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Start the consultation of Asha Devi' }));
    expect(onConsult).toHaveBeenCalledWith(arrived);
  });

  it('offers the video consultations for a telehealth booking', async () => {
    const onOpenVideo = vi.fn();
    wrap(
      <WorklistAppointments
        {...props}
        onOpenVideo={onOpenVideo}
        appointments={[appointment({ patientName: 'Ravi', isTelehealth: true, startsAt: '2026-10-02T06:00:00Z' })]}
      />,
    );

    await userEvent.click(screen.getByRole('button', { name: 'Open video consultations for Ravi' }));
    expect(onOpenVideo).toHaveBeenCalled();
  });

  it('offers no way into a call while video consultations are switched off', () => {
    wrap(
      <WorklistAppointments
        {...props}
        video={false}
        appointments={[appointment({ patientName: 'Ravi', isTelehealth: true, startsAt: '2026-10-02T06:00:00Z' })]}
      />,
    );

    expect(screen.getByText('Ravi')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Open video consultations for Ravi' })).not.toBeInTheDocument();
  });

  it('badges a restricted record, which the server has already unnamed', () => {
    wrap(
      <WorklistAppointments
        {...props}
        appointments={[
          appointment({ patientName: 'Restricted patient', isRestricted: true, startsAt: '2026-10-02T06:00:00Z' }),
        ]}
      />,
    );

    const row = screen.getByText('Restricted patient').closest('tr')!;
    expect(within(row).getByText('Restricted')).toBeInTheDocument();
  });
});

describe('WardRoundList', () => {
  const props = {
    loading: false,
    error: null,
    onRetry: vi.fn(),
    isToday: true,
    isDepartment: false,
    canRecordVisit: true,
    practitioner: null,
    onOpenChart: vi.fn(),
  };
  const round = (patients: WardRoundPatient[]) => ({
    date: '2026-10-02',
    practitionerId: 'd1',
    departmentId: null,
    patients,
  });

  it('shows who needs the doctor and can bring the most urgent first', async () => {
    wrap(
      <WardRoundList
        {...props}
        round={round([
          inpatient({
            patientName: 'Seen Suresh',
            bedLabel: 'GM-01',
            seenToday: true,
            lastVisitAt: '2026-10-02T03:00:00Z',
          }),
          inpatient({
            patientName: 'Critical Kamala',
            bedLabel: 'GM-02',
            vitalsSeverity: 'Critical',
            vitalsAt: '2026-10-02T04:00:00Z',
            vitalsFlags: ['SpO₂ 86% is critically low.'],
            resultsToReview: 1,
            hasCriticalResults: true,
          }),
          inpatient({
            patientName: 'Visiting Vijay',
            bedLabel: 'GM-03',
            isPrimaryConsultant: false,
            status: 'DischargeInitiated',
          }),
        ])}
      />,
    );

    expect(
      screen.getByText('3 in-patients: 2 not seen today, 1 needs attention now, 1 with results to review'),
    ).toBeInTheDocument();
    const critical = screen.getByText('Critical Kamala').closest('tr')!;
    expect(within(critical).getByText('Vitals critical')).toBeInTheDocument();
    expect(within(critical).getByText('Critical result')).toBeInTheDocument();
    const visiting = screen.getByText('Visiting Vijay').closest('tr')!;
    expect(within(visiting).getByText('Cross-consult')).toBeInTheDocument();
    expect(within(visiting).getByText('Discharge in progress')).toBeInTheDocument();
    expect(within(screen.getByText('Seen Suresh').closest('tr')!).getByText(/Seen at/)).toBeInTheDocument();

    const names = () =>
      screen
        .getAllByRole('row')
        .slice(1)
        .map((row) => within(row).getByText(/Suresh|Kamala|Vijay/).textContent);
    expect(names()).toEqual(['Seen Suresh', 'Critical Kamala', 'Visiting Vijay']);
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Order' }), 'attention');
    expect(names()).toEqual(['Critical Kamala', 'Visiting Vijay', 'Seen Suresh']);
  });

  it('records visits only on today’s round and for those allowed to', () => {
    const patients = round([inpatient({ patientName: 'Asha' })]);
    const { unmount } = wrap(<WardRoundList {...props} round={patients} />);
    expect(screen.getByRole('button', { name: 'Record visit to Asha' })).toBeInTheDocument();
    unmount();

    wrap(<WardRoundList {...props} round={patients} isToday={false} />);
    expect(screen.queryByRole('button', { name: 'Record visit to Asha' })).not.toBeInTheDocument();
    expect(screen.getByText(/always shows the patients in hospital now/)).toBeInTheDocument();
  });

  it('says whose patients appear when there are none', () => {
    wrap(<WardRoundList {...props} round={round([])} isDepartment />);
    expect(screen.getByText('No in-patients in this department')).toBeInTheDocument();
  });
});
