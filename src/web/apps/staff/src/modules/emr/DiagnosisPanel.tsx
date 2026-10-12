import {
  Badge,
  Button,
  Caption1,
  Field,
  makeStyles,
  Menu,
  MenuDivider,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  Select,
  Switch,
  Text,
  tokens,
  Tooltip,
} from '@fluentui/react-components';
import { AddRegular, MoreHorizontalRegular, StarRegular } from '@fluentui/react-icons';
import { formatDate, formatDateOnly, humanize } from '@hc360/core';
import { DataTable, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { IcdCodePicker } from '@/modules/mrd/CodePicker';
import {
  DIAGNOSIS_STATUSES,
  DIAGNOSIS_TYPES,
  emrApi,
  emrKeys,
  emrQueries,
  type AddDiagnosisRequest,
  type DiagnosisDto,
  type DiagnosisStatus,
  type DiagnosisType,
  type ProblemSummary,
} from './api';
import { isConflict } from './hooks';

const useStyles = makeStyles({
  panel: { display: 'flex', flexDirection: 'column', gap: '12px' },
  addRow: {
    display: 'grid',
    gridTemplateColumns: 'minmax(260px, 1fr) auto auto auto',
    gap: '8px',
    alignItems: 'end',
    '@media (max-width: 900px)': { gridTemplateColumns: '1fr 1fr' },
  },
  list: { display: 'flex', flexDirection: 'column', gap: '6px', margin: 0, padding: 0, listStyle: 'none' },
  item: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    padding: '8px 10px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    flexWrap: 'wrap',
  },
  itemText: { display: 'flex', flexDirection: 'column', gap: '2px', flex: 1, minWidth: '200px' },
  code: {
    fontFamily: tokens.fontFamilyMonospace,
    color: tokens.colorBrandForeground1,
    fontWeight: tokens.fontWeightSemibold,
  },
  chips: { display: 'flex', gap: '6px', flexWrap: 'wrap', alignItems: 'center' },
  muted: { color: tokens.colorNeutralForeground3 },
  switchRow: { display: 'flex', alignItems: 'center', minHeight: '32px' },
});

function diagnosisTypeTone(type: DiagnosisType): 'brand' | 'warning' | 'informative' {
  return type === 'Final' ? 'brand' : type === 'Provisional' ? 'warning' : 'informative';
}

function toUpdate(d: DiagnosisDto, patch: Partial<Pick<DiagnosisDto, 'type' | 'isPrimary' | 'status'>>) {
  return {
    type: patch.type ?? d.type,
    isPrimary: patch.isPrimary ?? d.isPrimary,
    status: patch.status ?? d.status,
    onsetDate: d.onsetDate,
    notes: d.notes,
    version: d.version,
  };
}

/** The diagnoses of one encounter: ranked ICD search (Enter adds), known problems to carry forward, quick edits. */
export function EncounterDiagnoses({
  patientId,
  encounterId,
  problems,
}: {
  patientId: string;
  encounterId: string;
  problems: readonly ProblemSummary[];
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canWrite = usePermission('clinical.emr.write');
  const diagnoses = useQuery(emrQueries.diagnoses(patientId, encounterId));
  const items = diagnoses.data ?? [];
  const [type, setType] = useState<DiagnosisType>('Provisional');
  const [status, setStatus] = useState<DiagnosisStatus>('Active');
  const [primaryChoice, setPrimaryChoice] = useState<boolean | null>(null);
  const primary = primaryChoice ?? !items.some((d) => d.isPrimary);

  const refresh = () => void queryClient.invalidateQueries({ queryKey: emrKeys.patient(patientId) });

  const add = useMutation({
    mutationFn: (request: AddDiagnosisRequest) => emrApi.addDiagnosis(encounterId, request),
    onSuccess: (d) => {
      setPrimaryChoice(null);
      notify.success('Diagnosis added', d.icdCode ? `${d.icdCode} ${d.title}` : d.title);
      refresh();
    },
    onError: (error) => notify.error(error, 'Could not add the diagnosis'),
  });

  const update = useMutation({
    mutationFn: ({
      diagnosis,
      patch,
    }: {
      diagnosis: DiagnosisDto;
      patch: Partial<Pick<DiagnosisDto, 'type' | 'isPrimary' | 'status'>>;
    }) => emrApi.updateDiagnosis(diagnosis.id, toUpdate(diagnosis, patch)),
    onSuccess: refresh,
    onError: (error) => {
      notify.error(error, isConflict(error) ? 'The diagnosis was changed elsewhere' : 'Could not update the diagnosis');
      refresh();
    },
  });

  const addRequest = (code: string | null, title: string | null): AddDiagnosisRequest => ({
    icdCode: code,
    title,
    type,
    isPrimary: primary,
    onsetDate: null,
    status,
    notes: null,
  });

  const carried = problems.filter(
    (p) =>
      !items.some((d) => (p.icdCode && d.icdCode === p.icdCode) || d.title.toLowerCase() === p.title.toLowerCase()),
  );

  return (
    <div className={styles.panel}>
      {canWrite && (
        <div className={styles.addRow}>
          <IcdCodePicker
            label="Add diagnosis"
            hint="Code, words or shorthand (E11.9, dengue, T2DM, HTN). Press Enter to add."
            disabled={add.isPending}
            onPick={(code) => add.mutate(addRequest(code.code, null))}
            onFreeText={(text) => add.mutate(addRequest(null, text))}
          />
          <Field label="Type">
            <Select value={type} onChange={(_, d) => setType(d.value as DiagnosisType)}>
              {DIAGNOSIS_TYPES.map((t) => (
                <option key={t} value={t}>
                  {t}
                </option>
              ))}
            </Select>
          </Field>
          <Field label="Status">
            <Select value={status} onChange={(_, d) => setStatus(d.value as DiagnosisStatus)}>
              {DIAGNOSIS_STATUSES.map((s) => (
                <option key={s} value={s}>
                  {s}
                </option>
              ))}
            </Select>
          </Field>
          {/* The field's label names the switch; a label of the switch's own would be a second one. Its state is
              announced as on or off, so the words beside it are for the eye. */}
          <Field label="Primary">
            <div className={styles.switchRow}>
              <Switch checked={primary} onChange={(_, d) => setPrimaryChoice(d.checked)} />
              <Text aria-hidden>{primary ? 'Yes' : 'No'}</Text>
            </div>
          </Field>
        </div>
      )}

      {canWrite && carried.length > 0 && (
        <div className={styles.chips}>
          <Caption1 className={styles.muted}>Known problems:</Caption1>
          {carried.slice(0, 8).map((p) => (
            <Tooltip key={p.id} content="Add to this encounter" relationship="description">
              <Button
                size="small"
                icon={<AddRegular />}
                disabled={add.isPending}
                onClick={() =>
                  add.mutate({
                    icdCode: p.icdCode,
                    title: p.icdCode ? null : p.title,
                    type: 'Final',
                    isPrimary: primary,
                    onsetDate: p.onsetDate,
                    status: p.status,
                    notes: null,
                  })
                }
              >
                {p.icdCode ? `${p.icdCode} ` : ''}
                {p.title}
              </Button>
            </Tooltip>
          ))}
        </div>
      )}

      {items.length === 0 ? (
        <Text className={styles.muted}>
          {diagnoses.isLoading ? 'Loading…' : 'No diagnosis yet. Add at least a provisional diagnosis before signing.'}
        </Text>
      ) : (
        <ul className={styles.list} aria-label="Diagnoses of this encounter">
          {items
            .toSorted((a, b) => Number(b.isPrimary) - Number(a.isPrimary))
            .map((d) => (
              <li key={d.id} className={styles.item}>
                <span className={styles.itemText}>
                  <span>
                    {d.icdCode && <span className={styles.code}>{d.icdCode} </span>}
                    <Text weight="semibold">{d.title}</Text>
                  </span>
                  {d.notes && <Caption1 className={styles.muted}>{d.notes}</Caption1>}
                </span>
                <span className={styles.chips}>
                  {d.isPrimary && (
                    <Badge color="brand" icon={<StarRegular />}>
                      Primary
                    </Badge>
                  )}
                  <StatusBadge status={d.type} tone={diagnosisTypeTone(d.type)} />
                  {d.status !== 'Active' && (
                    <StatusBadge status={d.status} tone={d.status === 'Chronic' ? 'important' : 'success'} />
                  )}
                  {!d.icdCode && (
                    <Badge appearance="outline" color="warning">
                      No code
                    </Badge>
                  )}
                  {canWrite && (
                    <Menu>
                      <MenuTrigger disableButtonEnhancement>
                        <Button
                          size="small"
                          appearance="subtle"
                          icon={<MoreHorizontalRegular />}
                          aria-label={`Change ${d.title}`}
                          disabled={update.isPending}
                        />
                      </MenuTrigger>
                      <MenuPopover>
                        <MenuList>
                          {!d.isPrimary && (
                            <MenuItem onClick={() => update.mutate({ diagnosis: d, patch: { isPrimary: true } })}>
                              Make primary
                            </MenuItem>
                          )}
                          {DIAGNOSIS_TYPES.filter((t) => t !== d.type).map((t) => (
                            <MenuItem key={t} onClick={() => update.mutate({ diagnosis: d, patch: { type: t } })}>
                              Mark as {t.toLowerCase()}
                            </MenuItem>
                          ))}
                          <MenuDivider />
                          {DIAGNOSIS_STATUSES.filter((s) => s !== d.status).map((s) => (
                            <MenuItem key={s} onClick={() => update.mutate({ diagnosis: d, patch: { status: s } })}>
                              {s === 'Resolved'
                                ? 'Mark resolved'
                                : s === 'Chronic'
                                  ? 'Mark chronic (keep on problem list)'
                                  : 'Mark active'}
                            </MenuItem>
                          ))}
                        </MenuList>
                      </MenuPopover>
                    </Menu>
                  )}
                </span>
              </li>
            ))}
        </ul>
      )}
    </div>
  );
}

/** Chart tab: the problem list and every diagnosis ever recorded. */
export function DiagnosesTab({ patientId }: { patientId: string }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canWrite = usePermission('clinical.emr.write');
  const [problemsOnly, setProblemsOnly] = useState(true);
  const diagnoses = useQuery(emrQueries.diagnoses(patientId, null, problemsOnly));

  const update = useMutation({
    mutationFn: ({ diagnosis, status }: { diagnosis: DiagnosisDto; status: DiagnosisStatus }) =>
      emrApi.updateDiagnosis(diagnosis.id, toUpdate(diagnosis, { status })),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: emrKeys.patient(patientId) }),
    onError: (error) => {
      notify.error(error, 'Could not update the diagnosis');
      void queryClient.invalidateQueries({ queryKey: emrKeys.patient(patientId) });
    },
  });

  return (
    <>
      <div style={{ marginBottom: 12 }}>
        <Switch
          checked={problemsOnly}
          onChange={(_, d) => setProblemsOnly(d.checked)}
          label="Problem list only (active and chronic)"
        />
      </div>
      <DataTable
        ariaLabel="Diagnoses"
        rows={diagnoses.data}
        loading={diagnoses.isLoading}
        error={diagnoses.error}
        onRetry={() => void diagnoses.refetch()}
        getRowId={(d) => d.id}
        size="small"
        columns={[
          {
            key: 'code',
            header: 'ICD-10',
            width: 90,
            render: (d) => (d.icdCode ? <Text font="monospace">{d.icdCode}</Text> : '—'),
          },
          {
            key: 'title',
            header: 'Diagnosis',
            render: (d) => (
              <span>
                <Text weight={d.isPrimary ? 'semibold' : 'regular'}>{d.title}</Text>
                {d.isPrimary && <Caption1> · primary</Caption1>}
              </span>
            ),
          },
          {
            key: 'type',
            header: 'Type',
            render: (d) => <StatusBadge status={d.type} tone={diagnosisTypeTone(d.type)} />,
          },
          {
            key: 'status',
            header: 'Status',
            render: (d) => (
              <StatusBadge
                status={d.status}
                tone={d.status === 'Resolved' ? 'success' : d.status === 'Chronic' ? 'important' : 'warning'}
              />
            ),
          },
          {
            key: 'onset',
            header: 'Since',
            priority: 'low',
            render: (d) => (d.onsetDate ? formatDateOnly(d.onsetDate) : '—'),
          },
          {
            key: 'resolved',
            header: 'Resolved',
            priority: 'low',
            render: (d) => (d.resolvedOn ? formatDateOnly(d.resolvedOn) : '—'),
          },
          { key: 'recorded', header: 'Recorded', priority: 'low', render: (d) => formatDate(d.recordedAt) },
        ]}
        rowActions={
          canWrite
            ? (d) => (
                <Menu>
                  <MenuTrigger disableButtonEnhancement>
                    <Button
                      size="small"
                      appearance="subtle"
                      icon={<MoreHorizontalRegular />}
                      aria-label={`Change status of ${d.title}`}
                    />
                  </MenuTrigger>
                  <MenuPopover>
                    <MenuList>
                      {DIAGNOSIS_STATUSES.filter((s) => s !== d.status).map((s) => (
                        <MenuItem key={s} onClick={() => update.mutate({ diagnosis: d, status: s })}>
                          Mark {humanize(s).toLowerCase()}
                        </MenuItem>
                      ))}
                    </MenuList>
                  </MenuPopover>
                </Menu>
              )
            : undefined
        }
        emptyTitle={problemsOnly ? 'No active problems' : 'No diagnoses recorded'}
        emptyDescription="Diagnoses are added in encounters and form the problem list."
      />
    </>
  );
}
