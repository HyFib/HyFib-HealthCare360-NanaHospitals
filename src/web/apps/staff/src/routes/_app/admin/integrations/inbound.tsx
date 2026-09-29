import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  makeStyles,
  Select,
  Text,
  tokens,
} from '@fluentui/react-components';
import { formatDateTime, humanize } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { IntegrationsTabs } from '@/modules/admin/IntegrationsTabs';
import {
  INBOX_PAGE_SIZE,
  integrationQueries,
  keywordActionDisplay,
  pageFromUrl,
  WHATSAPP_KEYWORDS,
  type InboundWhatsAppMessageDto,
  type WhatsAppKeyword,
} from '@/modules/admin/integrationsApi';
import { NoAccess } from '@/modules/admin/NoAccess';

interface InboundSearch {
  keyword?: WhatsAppKeyword;
  page?: number;
}

export const Route = createFileRoute('/_app/admin/integrations/inbound')({
  validateSearch: (s: Record<string, unknown>): InboundSearch => ({
    keyword: WHATSAPP_KEYWORDS.includes(s.keyword as WhatsAppKeyword) ? (s.keyword as WhatsAppKeyword) : undefined,
    page: pageFromUrl(s.page),
  }),
  component: WhatsAppInboxPage,
});

const useStyles = makeStyles({
  toolbar: { padding: '0 16px 12px' },
  muted: { color: tokens.colorNeutralForeground3 },
  preview: { display: 'block', maxWidth: '420px', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  detail: { display: 'grid', gridTemplateColumns: 'auto 1fr', gap: '6px 16px', alignItems: 'baseline' },
  block: {
    gridColumn: '1 / -1',
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-word',
    backgroundColor: tokens.colorNeutralBackground3,
    padding: '8px 12px',
    borderRadius: tokens.borderRadiusMedium,
    margin: 0,
  },
});

function KeywordBadge({ keyword }: { keyword: WhatsAppKeyword }) {
  if (keyword === 'OptOut') {
    return <StatusBadge status="OptOut" tone="danger" label="Opted out" />;
  }
  if (keyword === 'OptIn') {
    return <StatusBadge status="OptIn" tone="success" label="Opted in" />;
  }
  return null;
}

/** What HC360 did about a STOP or START. */
function ResultBadge({ row }: { row: InboundWhatsAppMessageDto }) {
  const result = keywordActionDisplay(row.keyword, row.keywordAction);
  return result ? (
    <StatusBadge status={row.keywordAction ?? 'Waiting'} tone={result.tone} label={result.label} />
  ) : null;
}

/** Messages patients sent to the hospital's WhatsApp number (sender masked), with STOP/START opt-outs and opt-ins. */
function WhatsAppInboxPage() {
  const styles = useStyles();
  const allowed = usePermission('admin.integrations.manage');
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const [open, setOpen] = useState<InboundWhatsAppMessageDto | null>(null);
  const page = search.page ?? 1;
  const messages = useQuery({ ...integrationQueries.inbound({ keyword: search.keyword, page }), enabled: allowed });

  if (!allowed) {
    return (
      <Page>
        <PageHeader title="Integrations" />
        <NoAccess what="the WhatsApp inbox" />
      </Page>
    );
  }

  return (
    <Page>
      <PageHeader
        title="Integrations"
        subtitle="Messages patients sent to the hospital’s WhatsApp number. A STOP reply stops WhatsApp messages to that number, except sign-in codes; START resumes them."
      />
      <IntegrationsTabs selected="inbound" />
      <Section flush>
        <div className={styles.toolbar}>
          <FilterBar>
            <Field label="Show">
              <Select
                value={search.keyword ?? ''}
                onChange={(_, data) =>
                  void navigate({
                    search: { keyword: WHATSAPP_KEYWORDS.includes(data.value as WhatsAppKeyword) ? (data.value as WhatsAppKeyword) : undefined },
                  })
                }
              >
                <option value="">All messages</option>
                <option value="OptOut">Opt-outs (STOP)</option>
                <option value="OptIn">Opt-ins (START)</option>
                <option value="None">Other messages</option>
              </Select>
            </Field>
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="WhatsApp messages from patients"
          rows={messages.data?.items}
          loading={messages.isLoading}
          error={messages.error}
          onRetry={() => void messages.refetch()}
          getRowId={(row) => row.id}
          onRowClick={setOpen}
          size="small"
          columns={[
            { key: 'when', header: 'Received', width: 180, render: (row) => formatDateTime(row.receivedAt) },
            { key: 'from', header: 'From', width: 150, render: (row) => row.fromPhoneMasked },
            { key: 'name', header: 'WhatsApp name', priority: 'low', render: (row) => row.profileName ?? '—' },
            {
              key: 'text',
              header: 'Message',
              render: (row) => <span className={styles.preview}>{row.text ?? `(${humanize(row.messageType).toLowerCase()})`}</span>,
            },
            { key: 'keyword', header: 'Choice', width: 120, render: (row) => <KeywordBadge keyword={row.keyword} /> },
            { key: 'result', header: 'Result', width: 160, render: (row) => <ResultBadge row={row} /> },
          ]}
          emptyTitle="No messages from patients"
          emptyDescription="Patients’ replies appear here once the WhatsApp webhook is set up in Meta and WhatsApp is configured."
          page={page}
          pageSize={INBOX_PAGE_SIZE}
          totalCount={messages.data?.totalCount}
          onPageChange={(next) => void navigate({ search: (prev) => ({ ...prev, page: next > 1 ? next : undefined }) })}
        />
      </Section>

      <Dialog open={open !== null} onOpenChange={(_, data) => !data.open && setOpen(null)}>
        <DialogSurface style={{ maxWidth: 640 }}>
          <DialogBody>
            <DialogTitle>WhatsApp message</DialogTitle>
            <DialogContent className={styles.detail}>
              {open && (
                <>
                  <Caption1 className={styles.muted}>Received</Caption1>
                  <Text>{formatDateTime(open.receivedAt)}</Text>
                  <Caption1 className={styles.muted}>From</Caption1>
                  <Text>
                    {open.fromPhoneMasked}
                    {open.profileName ? ` (${open.profileName})` : ''}
                  </Text>
                  <Caption1 className={styles.muted}>Type</Caption1>
                  <Text>{humanize(open.messageType)}</Text>
                  {open.keyword !== 'None' && (
                    <>
                      <Caption1 className={styles.muted}>Choice</Caption1>
                      <span>
                        <KeywordBadge keyword={open.keyword} />
                      </span>
                      <Caption1 className={styles.muted}>Result</Caption1>
                      <Text>{keywordActionDisplay(open.keyword, open.keywordAction)?.detail}</Text>
                    </>
                  )}
                  <pre className={styles.block}>{open.text ?? 'This message has no text (for example a photo or a location).'}</pre>
                </>
              )}
            </DialogContent>
            <DialogActions>
              <Button onClick={() => setOpen(null)}>Close</Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </Page>
  );
}
