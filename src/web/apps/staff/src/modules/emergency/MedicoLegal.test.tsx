import { FluentProvider } from '@fluentui/react-components';
import type * as Ui from '@hc360/ui';
import { hc360LightTheme } from '@hc360/ui';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { EmergencyCase, ErBays, WoundCertificate } from './api';
import { BayControl } from './BayControl';
import { WoundCertificateSection } from './WoundCertificateSection';

// The ER case's bay and the MLC's wound certificate: a free bay is offered and taken, the injuries' nature under BNS 2023
// is asked as the server will check it, a signed certificate no longer changes, and the court report is asked for.

const get = vi.hoisted(() => vi.fn());
const post = vi.hoisted(() => vi.fn());
const put = vi.hoisted(() => vi.fn());
vi.mock('@/lib/api', () => ({ api: { get, post, put } }));
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));

const CASE: EmergencyCase = {
  id: 'c1',
  visitId: 'v1',
  patientId: 'p1',
  patientName: 'Senthil Kumar',
  uhid: 'NH26000201',
  gender: 'Male',
  ageYears: 34,
  arrivedAt: '2026-11-08T04:40:00Z',
  arrivalMode: 'Police',
  level: 3,
  triagedAt: '2026-11-08T04:45:00Z',
  doctorSeenAt: null,
  doctorName: null,
  disposition: null,
  disposedAt: null,
  disposedByName: null,
  dispositionNotes: null,
  referredTo: null,
  triages: [],
  mlc: null,
  version: 4,
  bayId: null,
  bayCode: null,
};

const BAYS: ErBays = {
  bays: [
    {
      id: 'b1',
      code: 'R1',
      area: 'Resuscitation',
      displayOrder: 1,
      isActive: true,
      caseId: 'other',
      patientName: 'Another',
      uhid: 'NH1',
      level: 1,
      since: null,
      version: 1,
    },
    {
      id: 'b2',
      code: 'A1',
      area: 'Acute',
      displayOrder: 2,
      isActive: true,
      caseId: null,
      patientName: null,
      uhid: null,
      level: null,
      since: null,
      version: 1,
    },
    {
      id: 'b3',
      code: 'A2',
      area: 'Acute',
      displayOrder: 3,
      isActive: false,
      caseId: null,
      patientName: null,
      uhid: null,
      level: null,
      since: null,
      version: 1,
    },
  ],
  free: 1,
  occupied: 1,
};

const SIGNED: WoundCertificate = {
  id: 'w1',
  examinedAt: '2026-11-08T05:00:00Z',
  identificationMarks: 'A mole below the right ear',
  history: 'Assaulted with a stick',
  injuries: [
    {
      number: 1,
      kind: 'Fracture',
      site: 'Left forearm, middle third',
      size: null,
      description: 'X-ray: fracture of the ulna',
      weapon: 'Blunt',
      age: 'Fresh, within 6 hours',
      nature: 'Grievous',
      clause: 'FractureOrDislocation',
      reservedFor: null,
    },
  ],
  remarks: null,
  isSigned: true,
  signedAt: '2026-11-08T06:00:00Z',
  doctorName: 'Dr. Meera Iyer',
  doctorRegistration: 'TNMC 45678',
  version: 2,
};

function show(node: ReactNode) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <FluentProvider theme={hc360LightTheme}>
      <QueryClientProvider client={client}>
        <main>
          <h1>Emergency case</h1>
          {node}
        </main>
      </QueryClientProvider>
    </FluentProvider>,
  );
}

beforeEach(() => {
  get.mockReset();
  post.mockReset();
  put.mockReset();
});

describe('BayControl', () => {
  it('offers only the free bays in use and puts the patient in one, with no accessibility problems', async () => {
    get.mockResolvedValue(BAYS);
    put.mockResolvedValue({ ...CASE, bayId: 'b2', bayCode: 'A1' });
    show(<BayControl c={CASE} canMove />);

    expect(screen.getByText('Not in a bay.')).toBeInTheDocument();
    const select = screen.getByRole('combobox', { name: 'Put in bay' });
    await waitFor(() => expect(screen.getByRole('option', { name: 'A1 · Acute care' })).toBeInTheDocument());
    expect(screen.queryByRole('option', { name: /R1/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('option', { name: /A2/ })).not.toBeInTheDocument();
    await expectNoAxeViolations();

    await userEvent.selectOptions(select, 'b2');
    await userEvent.click(screen.getByRole('button', { name: 'Put in bay' }));
    await waitFor(() =>
      expect(put).toHaveBeenCalledWith('/api/v1/emergency/cases/c1/bay', { bayId: 'b2', version: 4 }),
    );
  });

  it('moves or frees the bay of a patient in one, and shows only the bay to a reader', async () => {
    get.mockResolvedValue(BAYS);
    show(<BayControl c={{ ...CASE, bayId: 'b1', bayCode: 'R1' }} canMove />);
    expect(screen.getByText(/In bay R1/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Free the bay' })).toBeInTheDocument();
    expect(screen.getByRole('combobox', { name: 'Move to bay' })).toBeInTheDocument();
  });
});

describe('WoundCertificateSection', () => {
  it('asks a grievous hurt for its clause and refuses a simple fracture before it is sent, with no accessibility problems', async () => {
    show(<WoundCertificateSection c={{ ...CASE, mlc: { id: 'm1' } as EmergencyCase['mlc'] }} canMlc canReport />);

    await userEvent.type(screen.getByRole('textbox', { name: /Marks of identification/ }), 'A scar on the left shin');
    await userEvent.type(screen.getByRole('textbox', { name: /Site/ }), 'Left forearm');
    await userEvent.type(screen.getByRole('textbox', { name: /Description/ }), 'Swelling, deformity');
    await userEvent.type(screen.getByRole('textbox', { name: /Age of injury/ }), 'Fresh');
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Injury' }), 'Fracture');
    expect(screen.getByText(/a fracture or dislocation is grievous hurt/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save the certificate' })).toBeDisabled();

    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Nature' }), 'Grievous');
    expect(screen.getByText(/say which kind of grievous hurt/)).toBeInTheDocument();
    await userEvent.selectOptions(
      screen.getByRole('combobox', { name: /Kind of grievous hurt/ }),
      'FractureOrDislocation',
    );
    expect(screen.getByRole('button', { name: 'Save the certificate' })).toBeEnabled();
    await expectNoAxeViolations();
  });

  it('shows a signed certificate fixed, with print and the court report, and nothing to edit', async () => {
    show(
      <WoundCertificateSection
        c={{ ...CASE, mlc: { id: 'm1' } as EmergencyCase['mlc'], woundCertificate: SIGNED }}
        canMlc
        canReport
      />,
    );

    expect(screen.getByText(/Signed by Dr. Meera Iyer \(Reg. TNMC 45678\)/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Save the certificate' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Sign as the examining doctor/ })).not.toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: /Site/ })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Print the wound certificate' })).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: /Medico-legal report for a court/ }));
    expect(await screen.findByRole('dialog', { name: 'Medico-legal report for a court' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Make and print' })).toBeDisabled();
    await expectNoAxeViolations();
  });
});
