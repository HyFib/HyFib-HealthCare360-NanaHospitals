import type * as Ui from '@hc360/ui';
import { Hc360ThemeProvider } from '@hc360/ui';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { stubBrowserApis } from '@/test/appScreen';
import { mastersApi, type PractitionerDto, type PractitionerHprCheck } from './api';
import { describeHprCheck, HprCheckPanel } from './HprCheckPanel';

const notify = { success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() };
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));

const check = (overrides: Partial<PractitionerHprCheck> = {}): PractitionerHprCheck => ({
  result: 'Verified',
  mismatches: [],
  checkedAt: '2026-11-08T05:30:00Z',
  checkedByName: 'Hospital Admin',
  source: 'simulator',
  registryName: 'Asha Menon',
  registryCouncil: 'Tamil Nadu Medical Council',
  registryNumber: '12345',
  current: true,
  ...overrides,
});

const doctor = (hprCheck: PractitionerHprCheck | null, hprId: string | null = '91-1234-5678-9012'): PractitionerDto =>
  ({ id: 'p1', displayName: 'Dr. Asha Menon', hprId, version: 4, hprCheck }) as PractitionerDto;

function renderPanel(practitioner: PractitionerDto, dirty = false) {
  const client = new QueryClient({ defaultOptions: { mutations: { retry: false } } });
  return render(
    <Hc360ThemeProvider>
      <QueryClientProvider client={client}>
        <HprCheckPanel practitioner={practitioner} dirty={dirty} />
      </QueryClientProvider>
    </Hc360ThemeProvider>,
  );
}

beforeEach(() => {
  stubBrowserApis();
});

describe('describeHprCheck', () => {
  it('says what matched, what differs and where the answer came from', () => {
    expect(describeHprCheck(check())).toMatchObject({ intent: 'success', title: 'HPR ID verified' });
    expect(describeHprCheck(check()).body).toContain('against the HPR simulator (not the live registry)');
    expect(describeHprCheck(check({ source: 'abdm' })).body).not.toContain('simulator');
    const mismatch = describeHprCheck(check({ result: 'Mismatch', mismatches: ['council', 'registrationNumber'] }));
    expect(mismatch.intent).toBe('error');
    expect(mismatch.body).toContain('Differs: the medical council, the registration number.');
    expect(describeHprCheck(check({ result: 'NotFound' })).title).toBe('Not in the registry');
    expect(describeHprCheck(check({ current: false }))).toMatchObject({
      intent: 'warning',
      title: 'Changed since it was checked',
    });
  });
});

describe('HprCheckPanel', () => {
  it('checks the saved HPR ID with the registry, as last read', async () => {
    const checkHpr = vi.spyOn(mastersApi, 'checkHpr').mockResolvedValue(check());
    const { baseElement } = renderPanel(doctor(check({ result: 'Mismatch', mismatches: ['name'] })));
    expect(screen.getByText('Does not match the registry')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Check with the HPR registry' }));
    await waitFor(() => expect(checkHpr).toHaveBeenCalledWith('p1', 4));
    await expectNoAxeViolations(baseElement);
  });

  it('checks nothing while the form has unsaved changes or there is no HPR ID', async () => {
    const { unmount } = renderPanel(doctor(null), true);
    expect(screen.getByRole('button', { name: 'Check with the HPR registry' })).toBeDisabled();
    expect(screen.getByText('The HPR ID has not been checked with the registry.')).toBeInTheDocument();
    unmount();
    const { baseElement } = renderPanel(doctor(null, null));
    expect(screen.getByRole('button', { name: 'Check with the HPR registry' })).toBeDisabled();
    await expectNoAxeViolations(baseElement);
  });
});
