import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import type { DataRequestDetail } from './api';
import { ErasurePanel } from './ErasurePanel';
import type { RequestCommand } from './useRequestCommand';

// A completed erasure says what the law keeps and, apart from it, that the audit trail is kept permanently: the
// request's dates describe only what the law keeps, so "nothing had to be kept" would hide the access log.

const completed: DataRequestDetail = {
  id: '0192a000-0000-7000-8000-000000000a01',
  requestNumber: 'DPR-2026-000012',
  type: 'Erasure',
  status: 'Completed',
  channel: 'Portal',
  patientId: '0192a000-0000-7000-8000-000000000b01',
  uhid: 'NH-000123',
  principalName: 'Asha Verma',
  contactPhone: null,
  contactEmail: null,
  requestedBy: 'Self',
  requesterName: null,
  details: 'Stop all messages.',
  corrections: [],
  receivedAt: '2026-11-02T05:00:00Z',
  dueAt: '2026-12-02T05:00:00Z',
  isOverdue: false,
  identityCheck: null,
  identityNote: null,
  identityVerifiedAt: null,
  identityVerifiedBy: null,
  assignedToUserId: null,
  assignedToName: null,
  response: 'Done.',
  closedAt: '2026-11-06T05:00:00Z',
  closedBy: 'Privacy officer',
  erasedItems: ['messages'],
  retainedUntil: null,
  retainedPermanently: false,
  hasSummary: true,
  events: [],
  version: 3,
};

function show(request: DataRequestDetail) {
  render(
    <FluentProvider theme={webLightTheme}>
      <QueryClientProvider client={new QueryClient()}>
        <ErasurePanel
          request={request}
          canManage
          command={{} as RequestCommand}
          outcome={null}
          onErased={() => undefined}
          onAnswer={() => undefined}
        />
      </QueryClientProvider>
    </FluentProvider>,
  );
}

const AUDIT_TRAIL =
  'The audit trail (the record access log) is never erased by a request: the hospital keeps it permanently and never edits it.';

describe('a completed erasure', () => {
  it('does not say nothing was kept when the law keeps nothing: the audit trail is', () => {
    show(completed);
    expect(screen.getByText('Nothing had to be kept as the law requires.')).toBeInTheDocument();
    expect(screen.getByText(AUDIT_TRAIL)).toBeInTheDocument();
  });

  it('says until when the law keeps the rest, and the audit trail apart', () => {
    show({ ...completed, retainedUntil: '2034-10-01' });
    expect(screen.getByText(/Until 01 Oct 2034, as the law requires\./)).toBeInTheDocument();
    expect(screen.getByText(AUDIT_TRAIL)).toBeInTheDocument();
  });
});
