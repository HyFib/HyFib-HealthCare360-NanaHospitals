import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import type * as Ui from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from '@/lib/api';
import type { BookingDto } from './api';
import { CheckInCheckupDialog } from './CheckInCheckupDialog';

// The server and the toasts are outside the test; what the dialog says, offers and sends is what is tested.
vi.mock('@/lib/api', () => ({ api: { get: vi.fn(), post: vi.fn() } }));
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));

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

function booking(patch: Partial<BookingDto> = {}): BookingDto {
  return {
    id: 'booking-1',
    bookingNumber: 'NHPHC26000042',
    patientId: 'patient-1',
    uhid: 'NHP26000042',
    patientName: 'Ravi Kumar',
    packageId: 'package-1',
    packageCode: 'HC-EXEC',
    packageName: 'Executive Health Checkup',
    scheduledDate: '2026-10-23',
    status: 'Booked',
    visitId: null,
    orderId: null,
    corporateBatchId: null,
    corporateName: null,
    corporatePayerId: null,
    notes: null,
    checkedInAt: null,
    summary: null,
    recommendations: null,
    reviewedByPractitionerId: null,
    reviewedAt: null,
    completedAt: null,
    cancelReason: null,
    itinerary: [],
    version: 1,
    ...patch,
  };
}

/** The patient's visit of today, which the desk opened at the counter. */
const todaysVisit = {
  id: 'visit-1',
  visitNumber: 'NHPV2600123',
  status: 'Open',
  visitedAt: new Date().toISOString(),
  departmentName: 'General Medicine',
};

function renderDialog(detail: BookingDto) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <FluentProvider theme={webLightTheme}>
        <CheckInCheckupDialog booking={detail} onClose={vi.fn()} />
      </FluentProvider>
    </QueryClientProvider>,
  );
}

describe('CheckInCheckupDialog', () => {
  beforeEach(() => {
    vi.mocked(api.get)
      .mockReset()
      .mockImplementation((path: string) =>
        Promise.resolve(
          path.endsWith('/visits')
            ? { items: [todaysVisit], page: 1, pageSize: 10, totalCount: 1, totalPages: 1 }
            : { id: 'package-1', price: 2400 },
        ),
      );
    vi.mocked(api.post)
      .mockReset()
      .mockImplementation(() => Promise.resolve(booking({ status: 'CheckedIn' })));
    Object.values(notify).forEach((spy) => spy.mockClear());
  });

  it("bills a company's check-up to the company, on a visit the server opens, so there is no visit to link", async () => {
    const user = userEvent.setup();
    renderDialog(
      booking({ corporateBatchId: 'batch-1', corporateName: 'Acme Steel Ltd', corporatePayerId: 'payer-1' }),
    );

    expect(await screen.findByText('₹2,400.00')).toBeInTheDocument();
    expect(screen.getByText(/is billed to/)).toHaveTextContent(
      'The package fee ₹2,400.00 is billed to Acme Steel Ltd when you check in, on a health check-up visit opened for it. ' +
        "A co-pay in the company's terms with the hospital is the patient's to pay. The tests are covered by the package.",
    );
    expect(screen.queryByRole('combobox', { name: /today's visit/i })).not.toBeInTheDocument();
    expect(api.get).not.toHaveBeenCalledWith('/api/v1/patients/patient-1/visits', expect.anything());

    await user.click(screen.getByRole('button', { name: 'Check in' }));

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith('/api/v1/health-checkups/bookings/booking-1/check-in', { visitId: null }),
    );
    await waitFor(() =>
      expect(notify.success).toHaveBeenCalledWith(
        'Ravi Kumar checked in',
        'The package fee is billed to Acme Steel Ltd. Send the patient to the first station.',
      ),
    );
  });

  it("links a check-up the patient pays for to today's visit, whose payer the bill follows", async () => {
    const user = userEvent.setup();
    renderDialog(booking());

    const visit = await screen.findByRole('combobox', { name: /today's visit/i });
    expect(screen.getByText(/is added to the/)).toHaveTextContent("is added to the patient's bill when you check in.");
    await user.selectOptions(visit, 'visit-1');
    await user.click(screen.getByRole('button', { name: 'Check in' }));

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith('/api/v1/health-checkups/bookings/booking-1/check-in', {
        visitId: 'visit-1',
      }),
    );
  });
});
