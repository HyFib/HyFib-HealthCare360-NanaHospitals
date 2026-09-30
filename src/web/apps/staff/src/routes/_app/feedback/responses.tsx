import { Badge, Button, Caption1, Checkbox, Field, Select, Text, tokens, Tooltip } from '@fluentui/react-components';
import {
  CommentRegular,
  LockClosedRegular,
  PersonFeedbackRegular,
  SendRegular,
  TextBulletListLtrRegular,
} from '@fluentui/react-icons';
import { formatDateTime, todayIso } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { toOptions, useDepartments } from '@/lib/lookups';
import { usePermission } from '@/lib/permissions';
import { feedbackQueries } from '@/modules/feedback/api';
import { NpsAnswer, SentimentBadge, StarsValue } from '@/modules/feedback/badges';
import {
  CHANNELS,
  channelLabel,
  FEEDBACK_CAPTURERS,
  FEEDBACK_PERMISSIONS as F,
  SENTIMENTS,
  type FeedbackChannel,
  type Sentiment,
} from '@/modules/feedback/labels';
import { PeriodFields } from '@/modules/feedback/PeriodFields';
import { ResponseDrawer } from '@/modules/feedback/ResponseDrawer';
import { PERIOD_PRESETS, resolvePeriod, type PeriodPreset } from '@/modules/feedback/rules';
import { SendFeedbackLinkDialog } from '@/modules/feedback/SendFeedbackLinkDialog';
import { StaffFeedbackDialog } from '@/modules/feedback/StaffFeedbackDialog';
import { isIsoDate } from '@/modules/hr/shared/dates';
import { FilterPad } from '@/modules/hr/shared/layout';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';

interface ResponsesSearch {
  preset?: PeriodPreset;
  from?: string;
  to?: string;
  formId?: string;
  departmentId?: string;
  channel?: FeedbackChannel;
  sentiment?: Sentiment;
  lowOnly?: boolean;
  page?: number;
}

export const Route = createFileRoute('/_app/feedback/responses')({
  validateSearch: (s: Record<string, unknown>): ResponsesSearch => ({
    preset: PERIOD_PRESETS.includes(s.preset as PeriodPreset) ? (s.preset as PeriodPreset) : undefined,
    from: isIsoDate(s.from) ? s.from : undefined,
    to: isIsoDate(s.to) ? s.to : undefined,
    formId: typeof s.formId === 'string' && s.formId ? s.formId : undefined,
    departmentId: typeof s.departmentId === 'string' && s.departmentId ? s.departmentId : undefined,
    channel: CHANNELS.includes(s.channel as FeedbackChannel) ? (s.channel as FeedbackChannel) : undefined,
    sentiment: SENTIMENTS.includes(s.sentiment as Sentiment) ? (s.sentiment as Sentiment) : undefined,
    lowOnly: s.lowOnly === true ? true : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: () => (
    <RequirePermission permission={F.read} what="feedback responses">
      <ResponsesPage />
    </RequirePermission>
  ),
});

function ResponsesPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const departments = useDepartments();
  const forms = useQuery(feedbackQueries.forms());
  const canCapture = usePermission(FEEDBACK_CAPTURERS);
  const [openId, setOpenId] = useState<string | null>(null);
  const [capturing, setCapturing] = useState(false);
  const [sending, setSending] = useState(false);

  const preset = search.preset ?? 'last3Months';
  const period = resolvePeriod(preset, search.from, search.to, todayIso());
  const responses = useQuery(
    feedbackQueries.responses({
      ...period,
      formId: search.formId,
      departmentId: search.departmentId,
      channel: search.channel,
      sentiment: search.sentiment,
      lowOnly: search.lowOnly,
      page: search.page,
    }),
  );
  const departmentName = new Map((departments.data ?? []).map((d) => [d.id, d.name]));
  const filtered = Boolean(
    search.formId || search.departmentId || search.channel || search.sentiment || search.lowOnly,
  );

  const set = (patch: Partial<ResponsesSearch>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });

  return (
    <Page>
      <PageHeader
        title="Feedback responses"
        subtitle="Every answer from QR posters, kiosks, SMS links, the portal and staff. Low scores raise a complaint automatically."
        icon={<TextBulletListLtrRegular />}
        breadcrumbs={[{ label: 'Feedback analytics', onClick: () => void navigate({ to: '/feedback' }) }]}
        actions={
          canCapture && (
            <>
              <Button icon={<SendRegular />} onClick={() => setSending(true)}>
                Send feedback link
              </Button>
              <Button appearance="primary" icon={<PersonFeedbackRegular />} onClick={() => setCapturing(true)}>
                Record feedback
              </Button>
            </>
          )
        }
      />
      <Section flush>
        <FilterPad>
          <FilterBar>
            <PeriodFields preset={preset} from={period.from} to={period.to} onChange={(patch) => set(patch)} />
            <Field label="Form">
              <Select value={search.formId ?? ''} onChange={(_, d) => set({ formId: d.value || undefined })}>
                <option value="">All forms</option>
                {(forms.data ?? []).map((f) => (
                  <option key={f.id} value={f.id}>
                    {f.title}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Department">
              <Select
                value={search.departmentId ?? ''}
                onChange={(_, d) => set({ departmentId: d.value || undefined })}
              >
                <option value="">All</option>
                {toOptions(departments.data).map((o) => (
                  <option key={o.value} value={o.value}>
                    {o.label}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Came through">
              <Select
                value={search.channel ?? ''}
                onChange={(_, d) => set({ channel: (d.value || undefined) as FeedbackChannel | undefined })}
              >
                <option value="">Any channel</option>
                {CHANNELS.map((c) => (
                  <option key={c} value={c}>
                    {channelLabel[c]}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Sentiment">
              <Select
                value={search.sentiment ?? ''}
                onChange={(_, d) => set({ sentiment: (d.value || undefined) as Sentiment | undefined })}
              >
                <option value="">Any</option>
                {SENTIMENTS.map((s) => (
                  <option key={s} value={s}>
                    {s}
                  </option>
                ))}
              </Select>
            </Field>
            <Checkbox
              style={{ alignSelf: 'flex-end' }}
              checked={Boolean(search.lowOnly)}
              onChange={(_, d) => set({ lowOnly: d.checked ? true : undefined })}
              label="Low scores only"
            />
          </FilterBar>
        </FilterPad>
        <DataTable
          ariaLabel="Feedback responses"
          rows={responses.data?.items}
          loading={responses.isLoading}
          error={responses.error}
          onRetry={() => void responses.refetch()}
          getRowId={(r) => r.id}
          onRowClick={(r) => setOpenId(r.id)}
          columns={[
            { key: 'submittedAt', header: 'Received', width: 150, render: (r) => formatDateTime(r.submittedAt) },
            {
              key: 'form',
              header: 'Form',
              render: (r) => (
                <span>
                  <Text weight="semibold">{r.formTitle}</Text>
                  <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                    {channelLabel[r.channel]}
                    {r.departmentId ? ` · ${departmentName.get(r.departmentId) ?? 'Other department'}` : ''}
                  </Caption1>
                </span>
              ),
            },
            { key: 'rating', header: 'Rating', render: (r) => <StarsValue value={r.averageRating} /> },
            { key: 'nps', header: 'Recommend', align: 'center', render: (r) => <NpsAnswer value={r.nps} /> },
            {
              key: 'sentiment',
              header: 'Sentiment',
              priority: 'low',
              render: (r) => <SentimentBadge sentiment={r.sentiment} />,
            },
            {
              key: 'flags',
              header: 'Notes',
              render: (r) => (
                <span style={{ display: 'inline-flex', gap: 6, alignItems: 'center', flexWrap: 'wrap' }}>
                  {r.isLowScore && (
                    <Badge color="danger" appearance="tint" shape="rounded">
                      {r.complaintNumber ? `Complaint ${r.complaintNumber}` : 'Low score'}
                    </Badge>
                  )}
                  {r.hasComment && (
                    <Tooltip content="Has a comment" relationship="label">
                      <CommentRegular />
                    </Tooltip>
                  )}
                  {r.isAnonymous && (
                    <Tooltip content="Anonymous (QR poster or kiosk)" relationship="label">
                      <LockClosedRegular />
                    </Tooltip>
                  )}
                </span>
              ),
            },
          ]}
          emptyTitle={filtered ? 'No responses match' : 'No feedback in this period'}
          emptyDescription={
            filtered
              ? 'Clear a filter or choose a longer period.'
              : 'Answers from QR posters, kiosks, SMS links and staff appear here as they arrive.'
          }
          page={search.page ?? 1}
          pageSize={25}
          totalCount={responses.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
        />
      </Section>

      <ResponseDrawer responseId={openId} onClose={() => setOpenId(null)} />
      <StaffFeedbackDialog open={capturing} onClose={() => setCapturing(false)} />
      <SendFeedbackLinkDialog open={sending} onClose={() => setSending(false)} />
    </Page>
  );
}
