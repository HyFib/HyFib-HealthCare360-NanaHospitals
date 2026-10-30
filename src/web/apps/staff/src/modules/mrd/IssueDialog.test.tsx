import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { ApiError } from '@hc360/core';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from '@/lib/api';
import type { RecordDocumentEntry, RecordRequestDto } from './api';
import { IssueDialog } from './IssueDialog';

// The server is outside the test: what the dialog lists, lets through and sends is what is tested.
vi.mock('@/lib/api', () => ({ api: { get: vi.fn() } }));

const report: RecordDocumentEntry = {
  kind: 'diagnostics.lab-report',
  id: 'r1',
  title: 'Lab report LR-2610-0042',
  date: '2026-10-29T06:30:00Z',
  visitId: 'v1',
  admissionId: null,
};

const prescription: RecordDocumentEntry = {
  kind: 'clinical.prescription',
  id: 'x1',
  title: 'Prescription RX-2610-0311',
  date: '2026-10-28T09:15:00Z',
  visitId: 'v1',
  admissionId: null,
};

function request(purpose: RecordRequestDto['purpose']): RecordRequestDto {
  return {
    id: 'q1',
    requestNumber: 'MRQ-2610-0007',
    patientId: 'p1',
    uhid: 'NH-000123',
    patientName: 'Lakshmi Narayanan',
    medicalRecordId: null,
    requestedByName: purpose === 'Legal' ? 'Judicial Magistrate, Saidapet' : 'Dr. Meera Iyer',
    requestingDepartment: null,
    purpose,
    details: null,
    isPatientRequest: false,
    status: 'Requested',
    requestedAt: '2026-10-29T05:00:00Z',
    issuedAt: null,
    dueBackOn: null,
    returnedAt: null,
    cancelReason: null,
    copyDueAt: null,
    copyLate: false,
    files: [],
  };
}

function renderDialog(purpose: RecordRequestDto['purpose']) {
  const onConfirm = vi.fn();
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <FluentProvider theme={webLightTheme}>
        <IssueDialog request={request(purpose)} busy={false} onConfirm={onConfirm} onCancel={() => undefined} />
      </FluentProvider>
    </QueryClientProvider>,
  );
  return onConfirm;
}

const unavailable = () => new ApiError({ title: 'Service unavailable', status: 503 });

describe('IssueDialog', () => {
  beforeEach(() => {
    vi.mocked(api.get).mockReset();
  });

  it('issues no copy while the documents cannot be listed, and lists them again when asked', async () => {
    const user = userEvent.setup();
    vi.mocked(api.get).mockRejectedValueOnce(unavailable()).mockResolvedValue([report, prescription]);
    const onConfirm = renderDialog('Legal');

    // A copy issued now would be one with no files and no certificate, for good.
    expect(await screen.findByText('The documents could not be listed.')).toBeInTheDocument();
    expect(screen.queryByText('No signed or released documents for this patient.')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Issue' })).toBeDisabled();

    await user.click(screen.getByRole('button', { name: 'Try again' }));
    // Each document is ticked by its own box or its own words.
    await user.click(await screen.findByRole('checkbox', { name: /^Lab report LR-2610-0042/ }));
    expect(screen.getByRole('checkbox', { name: 'All documents' })).toBePartiallyChecked();
    await user.click(screen.getByText(/^Prescription RX-2610-0311/));
    expect(screen.getByRole('checkbox', { name: /^Prescription RX-2610-0311/ })).toBeChecked();
    expect(screen.getByRole('checkbox', { name: 'All documents' })).toBeChecked();
    await user.click(screen.getByRole('checkbox', { name: /^Prescription RX-2610-0311/ }));
    await expectNoAxeViolations();

    await user.click(screen.getByRole('button', { name: 'Issue 1 document' }));
    expect(onConfirm).toHaveBeenCalledWith({ dueBackOn: null, documents: [{ kind: report.kind, id: report.id }] });
  });

  it('does not lend the file when a copy of documents was asked for and they cannot be listed', async () => {
    const user = userEvent.setup();
    vi.mocked(api.get).mockRejectedValue(unavailable());
    const onConfirm = renderDialog('PatientCare');

    await user.click(screen.getByRole('checkbox', { name: 'Give a copy of documents instead of lending the file' }));
    expect(await screen.findByText('The documents could not be listed.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Issue' })).toBeDisabled();

    // Lending the file itself is still a choice the clerk makes.
    await user.click(screen.getByRole('checkbox', { name: 'Give a copy of documents instead of lending the file' }));
    expect(screen.getByLabelText(/^Return by/)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Issue' }));
    expect(onConfirm).toHaveBeenCalledWith(expect.objectContaining({ documents: [] }));
    expect(onConfirm.mock.calls[0]?.[0]?.dueBackOn).not.toBeNull();
  });
});
