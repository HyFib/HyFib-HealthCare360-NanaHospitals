import {
  Button,
  Caption1,
  Checkbox,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Radio,
  RadioGroup,
  Select,
  Spinner,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { PeopleRegular, SaveRegular, ShieldCheckmarkRegular } from '@fluentui/react-icons';
import { isApiError, useDebouncedValue, useHotkey, type Gender } from '@hc360/core';
import { DataTable, sameValues, Section, useLeaveGuard, useNotify } from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { useDepartments } from '@/lib/lookups';
import { usePermission } from '@/lib/permissions';
import { MainAside, Stack } from '@/modules/hr/shared/layout';
import { crmApi, crmKeys, crmQueries, type CampaignDetail, type SegmentInput } from './api';
import { CAMPAIGN_TEMPLATE_KEY, CRM_PERMISSIONS, type CampaignChannel } from './labels';
import { smsLength, SMS_CAMPAIGN_MAX } from './rules';

const useStyles = makeStyles({
  counter: { display: 'flex', gap: '12px', flexWrap: 'wrap', color: tokens.colorNeutralForeground3 },
  over: { color: tokens.colorPaletteRedForeground1, fontWeight: tokens.fontWeightSemibold },
  departments: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fill, minmax(170px, 1fr))',
    maxHeight: '180px',
    overflowY: 'auto',
    padding: '4px 8px',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
  },
  count: {
    fontSize: tokens.fontSizeHero800,
    fontWeight: tokens.fontWeightSemibold,
    lineHeight: tokens.lineHeightHero800,
  },
  row: { display: 'flex', gap: '12px', flexWrap: 'wrap' },
  preview: {
    whiteSpace: 'pre-wrap',
    padding: '10px 12px',
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground3,
    fontSize: tokens.fontSizeBase200,
  },
});

interface Draft {
  name: string;
  channel: CampaignChannel;
  message: string;
  minAge: string;
  maxAge: string;
  gender: Gender | '';
  city: string;
  lastVisitFrom: string;
  lastVisitTo: string;
  departmentIds: string[];
}

function toDraft(campaign: CampaignDetail | null): Draft {
  const s = campaign?.segment;
  return {
    name: campaign?.name ?? '',
    channel: campaign?.channel ?? 'Sms',
    message: campaign?.message ?? '',
    minAge: s?.minAge === null || s?.minAge === undefined ? '' : String(s.minAge),
    maxAge: s?.maxAge === null || s?.maxAge === undefined ? '' : String(s.maxAge),
    gender: s?.gender && s.gender !== 'Unknown' ? s.gender : '',
    city: s?.city ?? '',
    lastVisitFrom: s?.lastVisitFrom ?? '',
    lastVisitTo: s?.lastVisitTo ?? '',
    departmentIds: s?.departmentIds ?? [],
  };
}

function toSegment(d: Draft): SegmentInput {
  return {
    minAge: d.minAge === '' ? null : Number(d.minAge),
    maxAge: d.maxAge === '' ? null : Number(d.maxAge),
    gender: d.gender || null,
    city: d.city.trim() || null,
    lastVisitFrom: d.lastVisitFrom || null,
    lastVisitTo: d.lastVisitTo || null,
    departmentIds: d.departmentIds.length ? d.departmentIds : null,
  };
}

function problems(d: Draft): Record<string, string> {
  const p: Record<string, string> = {};
  if (!d.name.trim()) p.name = 'Give the campaign a name.';
  if (!d.message.trim()) p.message = 'Write the message.';
  if (d.channel === 'Sms' && d.message.length > SMS_CAMPAIGN_MAX)
    p.message = `Keep SMS text within ${SMS_CAMPAIGN_MAX} characters (3 parts).`;
  if (d.message.length > 1000) p.message = 'Keep the message within 1,000 characters.';
  const min = d.minAge === '' ? null : Number(d.minAge);
  const max = d.maxAge === '' ? null : Number(d.maxAge);
  if (min !== null && (min < 0 || min > 120)) p.minAge = 'Ages are 0 to 120.';
  if (max !== null && (max < 0 || max > 120)) p.maxAge = 'Ages are 0 to 120.';
  if (min !== null && max !== null && min > max) p.maxAge = 'Maximum age is below the minimum age.';
  if (d.lastVisitFrom && d.lastVisitTo && d.lastVisitFrom > d.lastVisitTo)
    p.lastVisitTo = 'The visit range ends before it starts.';
  return p;
}

/**
 * Campaign draft: the message and the audience. Only patients with an active marketing consent are ever counted or
 * messaged (checked again at send time), and every message carries a personal opt-out link.
 */
export function CampaignEditor({
  campaign,
  onSaved,
}: {
  campaign: CampaignDetail | null;
  onSaved: (id: string) => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const departments = useDepartments('Clinical');
  const canManage = usePermission(CRM_PERMISSIONS.campaignsManage);
  const [draft, setDraft] = useState<Draft>(() => toDraft(campaign));
  // The campaign as the editor was filled, then as this user saved it: its version goes with the save. The page reads
  // it again in the background, and a version read so must never be sent with this draft (REL-04).
  const [loaded, setLoaded] = useState(campaign);
  const [touched, setTouched] = useState(false);
  const [busy, setBusy] = useState(false);
  const errors = touched ? problems(draft) : {};
  const length = smsLength(draft.message);
  const segmentKey = useDebouncedValue(JSON.stringify(toSegment(draft)), 500);
  const segment = useMemo(() => JSON.parse(segmentKey) as SegmentInput, [segmentKey]);
  const preview = useQuery({
    queryKey: ['crm', 'campaigns', 'preview', segment, draft.channel],
    queryFn: () => crmApi.previewAudience(segment, draft.channel),
    enabled: canManage && Object.keys(problems({ ...draft, name: 'x', message: 'x' })).length === 0,
    staleTime: 30_000,
    retry: false,
  });

  const set = <K extends keyof Draft>(key: K, value: Draft[K]) => setDraft((prev) => ({ ...prev, [key]: value }));

  /** The save answers with nothing: the editor is filled again from the campaign read after it, version included. */
  const refill = async (id: string) => {
    try {
      const saved = await queryClient.fetchQuery({ ...crmQueries.campaign(id), staleTime: 0 });
      setLoaded(saved);
      setDraft(toDraft(saved));
    } catch {
      // Not read: the editor keeps the version it had, so its next save is refused rather than saved over a change.
    }
  };

  const save = async () => {
    setTouched(true);
    if (Object.keys(problems(draft)).length > 0) {
      return;
    }
    setBusy(true);
    const request = {
      name: draft.name.trim(),
      channel: draft.channel,
      templateKey: loaded?.templateKey ?? CAMPAIGN_TEMPLATE_KEY,
      message: draft.message.trim(),
      segment: toSegment(draft),
      version: loaded?.version ?? null,
    };
    try {
      if (loaded) {
        await crmApi.updateCampaign(loaded.id, request);
        notify.success('Draft saved');
        await refill(loaded.id);
        void queryClient.invalidateQueries({ queryKey: crmKeys.campaigns });
        onSaved(loaded.id);
      } else {
        const id = await crmApi.createCampaign(request);
        notify.success('Campaign saved as a draft', 'Schedule it when the audience and message are right.');
        void queryClient.invalidateQueries({ queryKey: crmKeys.campaigns });
        onSaved(String(id));
      }
    } catch (error) {
      if (isApiError(error) && error.code === 'concurrency') {
        notify.error(error, 'Someone else changed this campaign');
        void queryClient.invalidateQueries({ queryKey: crmKeys.campaigns });
      } else {
        notify.error(error, 'Could not save the campaign');
      }
    } finally {
      setBusy(false);
    }
  };

  useHotkey('mod+s', () => void save(), { allowInInputs: true, enabled: !busy && canManage });

  // Leaving the screen or closing the window with unsaved changes asks first (SRS UX-08).
  useLeaveGuard({ dirty: !sameValues(draft, toDraft(loaded)), busy });

  return (
    <MainAside>
      <Stack gap={16}>
        <Section title="Message">
          <Stack gap={12}>
            <Field
              label="Campaign name"
              required
              validationMessage={errors.name}
              validationState={errors.name ? 'error' : 'none'}
              hint="Only staff see this."
            >
              <Input
                value={draft.name}
                onChange={(_, d) => set('name', d.value)}
                maxLength={150}
                autoFocus={!campaign}
                placeholder="For example: Diabetes camp, October"
              />
            </Field>
            <Field label="Send by">
              <RadioGroup
                layout="horizontal"
                value={draft.channel}
                onChange={(_, d) => set('channel', d.value as CampaignChannel)}
              >
                <Radio value="Sms" label="SMS" />
                <Radio value="WhatsApp" label="WhatsApp" />
              </RadioGroup>
            </Field>
            <Field
              label="Message"
              required
              validationMessage={errors.message}
              validationState={errors.message ? 'error' : 'none'}
            >
              <Textarea
                value={draft.message}
                onChange={(_, d) => set('message', d.value)}
                rows={6}
                maxLength={1000}
                resize="vertical"
              />
            </Field>
            <div className={styles.counter} aria-live="polite">
              <span
                className={draft.channel === 'Sms' && draft.message.length > SMS_CAMPAIGN_MAX ? styles.over : undefined}
              >
                {draft.message.length} characters{draft.channel === 'Sms' ? ` of ${SMS_CAMPAIGN_MAX}` : ''}
              </span>
              {draft.channel === 'Sms' && length.parts > 0 && (
                <span>
                  {length.parts} SMS part{length.parts === 1 ? '' : 's'}
                  {length.unicode ? ' (Indian-language text uses more parts)' : ''}
                </span>
              )}
            </div>
            <Caption1>
              The hospital name and a personal opt-out link are added to every message by the approved template.
            </Caption1>
          </Stack>
        </Section>
        <Section title="Who receives it" description="Leave a filter empty to include everyone.">
          <Stack gap={12}>
            <div className={styles.row}>
              <Field
                label="Age from"
                validationMessage={errors.minAge}
                validationState={errors.minAge ? 'error' : 'none'}
              >
                <Input
                  type="number"
                  min={0}
                  max={120}
                  value={draft.minAge}
                  onChange={(_, d) => set('minAge', d.value)}
                  style={{ width: 110 }}
                />
              </Field>
              <Field
                label="Age to"
                validationMessage={errors.maxAge}
                validationState={errors.maxAge ? 'error' : 'none'}
              >
                <Input
                  type="number"
                  min={0}
                  max={120}
                  value={draft.maxAge}
                  onChange={(_, d) => set('maxAge', d.value)}
                  style={{ width: 110 }}
                />
              </Field>
              <Field label="Gender">
                <Select value={draft.gender} onChange={(_, d) => set('gender', d.value as Gender | '')}>
                  <option value="">Everyone</option>
                  <option value="Female">Women</option>
                  <option value="Male">Men</option>
                  <option value="Other">Other</option>
                </Select>
              </Field>
              <Field label="City">
                <Input value={draft.city} onChange={(_, d) => set('city', d.value)} maxLength={100} placeholder="Any" />
              </Field>
            </div>
            <div className={styles.row}>
              <Field label="Last visit from">
                <Input type="date" value={draft.lastVisitFrom} onChange={(_, d) => set('lastVisitFrom', d.value)} />
              </Field>
              <Field
                label="Last visit to"
                validationMessage={errors.lastVisitTo}
                validationState={errors.lastVisitTo ? 'error' : 'none'}
              >
                <Input type="date" value={draft.lastVisitTo} onChange={(_, d) => set('lastVisitTo', d.value)} />
              </Field>
            </div>
            <Field
              label={`Visited these departments${draft.departmentIds.length ? ` (${draft.departmentIds.length} chosen)` : ''}`}
              hint="Any visit to a chosen department counts."
            >
              <div className={styles.departments}>
                {(departments.data ?? []).map((dept) => (
                  <Checkbox
                    key={dept.id}
                    label={dept.name}
                    checked={draft.departmentIds.includes(dept.id)}
                    onChange={(_, d) =>
                      set(
                        'departmentIds',
                        d.checked
                          ? [...draft.departmentIds, dept.id].slice(0, 50)
                          : draft.departmentIds.filter((id) => id !== dept.id),
                      )
                    }
                  />
                ))}
              </div>
            </Field>
          </Stack>
        </Section>
        {canManage && (
          <div>
            <Button
              appearance="primary"
              size="large"
              icon={busy ? <Spinner size="tiny" aria-hidden /> : <SaveRegular />}
              disabled={busy}
              onClick={() => void save()}
            >
              Save draft (Ctrl+S)
            </Button>
          </div>
        )}
      </Stack>
      <Stack gap={16}>
        <Section title="Audience">
          <Stack gap={8}>
            {preview.isFetching && !preview.data ? (
              <Spinner size="tiny" label="Counting…" labelPosition="after" />
            ) : preview.error ? (
              <MessageBar intent="error">
                <MessageBarBody>
                  {isApiError(preview.error) ? preview.error.userMessage : 'Could not count the audience.'}
                </MessageBarBody>
              </MessageBar>
            ) : preview.data ? (
              <>
                <span className={styles.count}>
                  <PeopleRegular aria-hidden /> {preview.data.count.toLocaleString('en-IN')}
                </span>
                <Text>patients would receive it today{preview.isFetching ? ' (updating…)' : ''}.</Text>
              </>
            ) : (
              <Caption1>Fix the filters to see the count.</Caption1>
            )}
            <MessageBar intent="info" icon={<ShieldCheckmarkRegular />} layout="multiline">
              <MessageBarBody>
                Only patients who agreed to marketing messages, have a mobile number and are active are counted (DPDP
                Act). On WhatsApp, the patient’s number must also have opted in to WhatsApp and not replied STOP.
                Consent is checked again when each message is sent.
              </MessageBarBody>
            </MessageBar>
          </Stack>
        </Section>
        {preview.data && preview.data.sample.length > 0 && (
          <Section title="A few of them" description="Names are masked" flush>
            <DataTable
              ariaLabel="Sample of the audience"
              rows={preview.data.sample}
              getRowId={(s) => s.uhid}
              columns={[
                { key: 'uhid', header: 'UHID', render: (s) => <Text font="monospace">{s.uhid}</Text> },
                { key: 'name', header: 'Name', render: (s) => s.maskedName },
                { key: 'age', header: 'Age', render: (s) => s.age ?? '—' },
                { key: 'city', header: 'City', priority: 'low', render: (s) => s.city ?? '—' },
              ]}
              size="small"
            />
          </Section>
        )}
        {draft.message.trim() && (
          <Section title="Preview">
            <div className={styles.preview}>{draft.message.trim()}</div>
          </Section>
        )}
      </Stack>
    </MainAside>
  );
}
