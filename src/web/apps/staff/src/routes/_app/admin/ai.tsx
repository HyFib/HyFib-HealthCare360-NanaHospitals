import {
  Caption1,
  Combobox,
  Field,
  Input,
  makeStyles,
  Option,
  Select,
  Tab,
  TabList,
  Text,
  tokens,
} from '@fluentui/react-components';
import { formatDateTime, formatNumber, useDebouncedValue } from '@hc360/core';
import { DataTable, ErrorState, FilterBar, LoadingState, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQueries, useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useMemo, useState, type ReactNode } from 'react';
import { aiQueries } from '@/lib/ai';
import { usePermission } from '@/lib/permissions';
import {
  AI_PURPOSES,
  AI_USAGE_PAGE_SIZE,
  aiProviderLabel,
  aiUsageQueries,
  isAiPurpose,
  purposeLabel,
  type AiPurpose,
} from '@/modules/admin/aiApi';
import { adminQueries } from '@/modules/admin/api';
import { formatDuration, isIsoDay, pageFromUrl } from '@/modules/admin/integrationsApi';
import { NoAccess } from '@/modules/admin/NoAccess';
import { SettingsForm } from '@/modules/admin/SettingsForm';
import { AI_SETTINGS } from '@/modules/admin/settingsForms';

interface AiSearch {
  tab?: 'settings';
  purpose?: AiPurpose;
  userId?: string;
  from?: string;
  to?: string;
  page?: number;
}

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export const Route = createFileRoute('/_app/admin/ai')({
  validateSearch: (s: Record<string, unknown>): AiSearch => ({
    tab: s.tab === 'settings' ? 'settings' : undefined,
    purpose: isAiPurpose(s.purpose) ? s.purpose : undefined,
    userId: typeof s.userId === 'string' && UUID.test(s.userId) ? s.userId : undefined,
    from: isIsoDay(s.from) ? s.from : undefined,
    to: isIsoDay(s.to) ? s.to : undefined,
    page: pageFromUrl(s.page),
  }),
  component: AiAdminPage,
});

const useStyles = makeStyles({
  cards: {
    display: 'grid',
    gap: '16px',
    gridTemplateColumns: 'repeat(auto-fill, minmax(320px, 1fr))',
    alignItems: 'start',
  },
  rows: { display: 'grid', gridTemplateColumns: 'minmax(110px, auto) 1fr', gap: '6px 16px', alignItems: 'baseline' },
  muted: { color: tokens.colorNeutralForeground3 },
  error: { color: tokens.colorPaletteRedForeground1, display: 'block' },
  toolbar: { padding: '0 16px 12px' },
});

function Row({ label, children }: { label: string; children: ReactNode }) {
  const styles = useStyles();
  return (
    <>
      <Caption1 className={styles.muted}>{label}</Caption1>
      <Text>{children}</Text>
    </>
  );
}

/** AI assistant administration (AIV-01..06, SEC-12): whether AI and browser speech work, who used AI, and the settings. */
function AiAdminPage() {
  const allowed = usePermission('ai.settings.manage');
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });

  if (!allowed) {
    return (
      <Page>
        <PageHeader title="AI assistant" />
        <NoAccess what="AI administration" />
      </Page>
    );
  }

  return (
    <Page>
      <PageHeader
        title="AI assistant"
        subtitle="Voice and typed commands, dictation and AI usage. AI is off until you turn it on."
      />
      <AiStatus />
      <TabList
        selectedValue={search.tab ?? 'usage'}
        onTabSelect={(_, data) => void navigate({ search: data.value === 'settings' ? { tab: 'settings' } : {} })}
        aria-label="AI administration"
      >
        <Tab value="usage">Usage</Tab>
        <Tab value="settings">Settings</Tab>
      </TabList>
      {search.tab === 'settings' ? (
        <Section title="Settings" description={AI_SETTINGS.intro}>
          <SettingsForm spec={AI_SETTINGS} />
        </Section>
      ) : (
        <AiUsage />
      )}
    </Page>
  );
}

function AiStatus() {
  const styles = useStyles();
  const status = useQuery(aiQueries.status());
  if (status.isLoading) {
    return <LoadingState label="Checking AI…" />;
  }
  if (status.error || !status.data) {
    return <ErrorState error={status.error} onRetry={() => void status.refetch()} title="Could not check AI" />;
  }
  const ai = status.data;
  return (
    <div className={styles.cards}>
      <Section title="AI help">
        <div className={styles.rows}>
          <Row label="Status">
            <StatusBadge
              status={ai.enabled ? 'Active' : 'Off'}
              tone={ai.enabled ? 'success' : 'subtle'}
              label={ai.enabled ? 'On' : 'Off'}
            />
          </Row>
          <Row label="Provider">{aiProviderLabel(ai.provider)}</Row>
          <Row label="Model">{ai.model ?? '—'}</Row>
          {ai.reason && <Row label="Why it is off">{ai.reason}</Row>}
        </div>
      </Section>
      <Section title="Voice in browsers">
        <div className={styles.rows}>
          <Row label="Status">
            <StatusBadge
              status={ai.serverSpeechToText ? 'Active' : 'Off'}
              tone={ai.serverSpeechToText ? 'success' : 'subtle'}
              label={ai.serverSpeechToText ? 'On' : 'Off'}
            />
          </Row>
          <Row label="Speech server">{aiProviderLabel(ai.speechProvider)}</Row>
          {ai.speechReason && <Row label="Note">{ai.speechReason}</Row>}
        </div>
        <Caption1 className={styles.muted}>HC360 Desktop always dictates with offline Windows speech.</Caption1>
      </Section>
    </div>
  );
}

function AiUsage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canReadUsers = usePermission('admin.users.read');
  const page = search.page ?? 1;
  const usage = useQuery(
    aiUsageQueries.usage({ purpose: search.purpose, userId: search.userId, from: search.from, to: search.to, page }),
  );

  // Usage rows carry user ids only; show names for the people on this page.
  const userIds = useMemo(
    () => [...new Set((usage.data?.items ?? []).map((row) => row.userId).filter((id): id is string => Boolean(id)))],
    [usage.data],
  );
  const users = useQueries({
    queries: userIds.map((id) => ({ ...adminQueries.user(id), staleTime: 10 * 60_000, enabled: canReadUsers })),
  });
  const names = new Map(users.flatMap((user) => (user.data ? [[user.data.id, user.data.displayName] as const] : [])));
  const person = (id: string | null) => (id ? (names.get(id) ?? `Staff member ${id.slice(0, 8)}`) : 'System');

  const filter = (patch: Partial<AiSearch>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });

  return (
    <Section flush>
      <div className={styles.toolbar}>
        <FilterBar>
          <Field label="What for">
            <Select
              value={search.purpose ?? ''}
              onChange={(_, data) => filter({ purpose: isAiPurpose(data.value) ? data.value : undefined })}
            >
              <option value="">Everything</option>
              {AI_PURPOSES.map((purpose) => (
                <option key={purpose} value={purpose}>
                  {purposeLabel(purpose)}
                </option>
              ))}
            </Select>
          </Field>
          {canReadUsers && <UserFilter userId={search.userId} onChange={(userId) => filter({ userId })} />}
          <Field label="From">
            <Input
              type="date"
              value={search.from ?? ''}
              max={search.to}
              onChange={(_, data) => filter({ from: isIsoDay(data.value) ? data.value : undefined })}
            />
          </Field>
          <Field label="To">
            <Input
              type="date"
              value={search.to ?? ''}
              min={search.from}
              onChange={(_, data) => filter({ to: isIsoDay(data.value) ? data.value : undefined })}
            />
          </Field>
        </FilterBar>
      </div>
      <DataTable
        ariaLabel="AI usage"
        rows={usage.data?.items}
        loading={usage.isLoading}
        error={usage.error}
        onRetry={() => void usage.refetch()}
        getRowId={(row) => row.id}
        size="small"
        columns={[
          { key: 'when', header: 'When', width: 180, render: (row) => formatDateTime(row.createdAt) },
          { key: 'who', header: 'Person', render: (row) => person(row.userId) },
          { key: 'purpose', header: 'What for', render: (row) => purposeLabel(row.purpose) },
          {
            key: 'model',
            header: 'Service',
            priority: 'low',
            render: (row) => row.model ?? aiProviderLabel(row.provider),
          },
          {
            key: 'tokens',
            header: 'Tokens in / out',
            priority: 'low',
            align: 'end',
            render: (row) =>
              row.inputTokens || row.outputTokens
                ? `${formatNumber(row.inputTokens)} / ${formatNumber(row.outputTokens)}`
                : '—',
          },
          {
            key: 'latency',
            header: 'Took',
            width: 90,
            align: 'end',
            priority: 'low',
            render: (row) => formatDuration(row.latencyMs),
          },
          {
            key: 'patient',
            header: 'For a patient',
            width: 110,
            priority: 'low',
            render: (row) => (row.patientId ? 'Yes' : '—'),
          },
          {
            key: 'result',
            header: 'Result',
            render: (row) => (
              <span>
                <StatusBadge
                  status={row.success ? 'Succeeded' : 'Failed'}
                  label={row.success ? 'Succeeded' : 'Failed'}
                />
                {row.error && <Caption1 className={styles.error}>{row.error}</Caption1>}
              </span>
            ),
          },
        ]}
        emptyTitle="No AI use recorded for these filters"
        emptyDescription="Every AI call is recorded here: who, when, what for, tokens and time. Everyday commands answered by the built-in rules are not AI calls."
        page={page}
        pageSize={AI_USAGE_PAGE_SIZE}
        totalCount={usage.data?.totalCount}
        onPageChange={(next) => void navigate({ search: (prev) => ({ ...prev, page: next > 1 ? next : undefined }) })}
      />
      <div className={styles.toolbar}>
        <Caption1 className={styles.muted}>
          What was said or written is never stored: only who, when, what for, tokens and time.
        </Caption1>
      </div>
    </Section>
  );
}

/** Pick a person to filter by (search by name or user name). */
function UserFilter({
  userId,
  onChange,
}: {
  userId: string | undefined;
  onChange: (userId: string | undefined) => void;
}) {
  const [term, setTerm] = useState('');
  const debounced = useDebouncedValue(term.trim(), 250);
  const matches = useQuery({ ...adminQueries.users({ search: debounced, page: 1 }), enabled: debounced.length >= 2 });
  const selected = useQuery({ ...adminQueries.user(userId ?? ''), enabled: Boolean(userId), staleTime: 10 * 60_000 });
  const options = debounced.length >= 2 ? (matches.data?.items ?? []) : [];

  return (
    <Field label="Person" style={{ minWidth: 220 }}>
      <Combobox
        placeholder="Anyone"
        value={term || (userId ? (selected.data?.displayName ?? '') : '')}
        selectedOptions={userId ? [userId] : []}
        onChange={(event) => setTerm(event.target.value)}
        onOptionSelect={(_, data) => {
          onChange(data.optionValue || undefined);
          setTerm('');
        }}
        clearable
      >
        {options.length === 0 ? (
          <Option value="" text="" disabled>
            {debounced.length >= 2
              ? matches.isFetching
                ? 'Searching…'
                : 'No one matches'
              : 'Type at least 2 letters of a name'}
          </Option>
        ) : (
          options.map((user) => (
            <Option key={user.id} value={user.id} text={user.displayName}>
              {`${user.displayName} (${user.userName})`}
            </Option>
          ))
        )}
      </Combobox>
    </Field>
  );
}
