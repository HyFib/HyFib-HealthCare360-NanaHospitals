import {
  Badge,
  Button,
  Caption1,
  Field,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Radio,
  Spinner,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  Textarea,
  tokens,
  Tooltip,
} from '@fluentui/react-components';
import { AddRegular, CheckmarkCircleRegular, DeleteRegular, OpenRegular, SaveRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime, isApiError, useHotkey, useSession } from '@hc360/core';
import {
  ConfirmDialog,
  ErrorState,
  FormGrid,
  LoadingState,
  Page,
  PageHeader,
  Section,
  StatusBadge,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { ICD10_RELEASE, mrdApi, mrdKeys, mrdQueries, type CodingDto } from '@/modules/mrd/api';
import { IcdCodePicker, ProcedureCodePicker } from '@/modules/mrd/CodePicker';
import {
  addLine,
  applySuggestions,
  codingProblems,
  ordered,
  removeLine,
  setPrimary,
  toRequestLines,
  verification,
  type CodingLine,
} from '@/modules/mrd/coding';

export const Route = createFileRoute('/_app/mrd/coding/$codingId')({
  component: CodingPage,
});

const useStyles = makeStyles({
  layout: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) minmax(280px, 360px)',
    gap: '16px',
    alignItems: 'start',
    '@media (max-width: 1100px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  stack: { display: 'flex', flexDirection: 'column', gap: '12px' },
  code: { fontFamily: tokens.fontFamilyMonospace, fontWeight: tokens.fontWeightSemibold },
  muted: { color: tokens.colorNeutralForeground3 },
  suggestion: {
    display: 'flex',
    gap: '8px',
    alignItems: 'center',
    justifyContent: 'space-between',
    padding: '6px 0',
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  actions: { display: 'flex', gap: '8px', flexWrap: 'wrap', alignItems: 'center' },
});

function CodingPage() {
  const { codingId } = Route.useParams();
  const coding = useQuery(mrdQueries.coding(codingId));
  if (coding.isLoading) {
    return <LoadingState />;
  }
  if (coding.error || !coding.data) {
    return (
      <Page maxWidth={900}>
        <ErrorState error={coding.error} onRetry={() => void coding.refetch()} />
      </Page>
    );
  }
  return <CodingEditor key={coding.data.id} coding={coding.data} />;
}

function toLines(coding: CodingDto): CodingLine[] {
  return coding.entries
    .toSorted((a, b) => a.sequence - b.sequence)
    .map((e) => ({ kind: e.kind, code: e.code, title: e.title, isPrimary: e.isPrimary }));
}

/** Codes one episode (ICD-10 diagnoses, ICD-10-PCS procedures) with the doctors' diagnoses as suggestions; a second coder verifies. */
function CodingEditor({ coding }: { coding: CodingDto }) {
  const styles = useStyles();
  const notify = useNotify();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const userId = useSession((s) => s.user?.id);
  const canReadChart = usePermission('clinical.emr.read');
  const suggestions = useQuery(mrdQueries.suggestions(coding.id));
  const [lines, setLines] = useState<CodingLine[]>(() => toLines(coding));
  const [notes, setNotes] = useState(coding.notes ?? '');
  const [dirty, setDirty] = useState(false);
  const [confirmVerify, setConfirmVerify] = useState(false);
  const problems = codingProblems(lines);
  const verify = verification(coding, userId);

  const change = (next: CodingLine[]) => {
    setLines(next);
    setDirty(true);
  };

  const publish = (updated: CodingDto) => {
    queryClient.setQueryData(mrdKeys.coding(updated.id), updated);
    void queryClient.invalidateQueries({ queryKey: ['mrd', 'codings'] });
  };

  const save = useMutation({
    mutationFn: () => mrdApi.saveCoding(coding.id, toRequestLines(lines), notes.trim() || null, coding.version),
    onSuccess: (updated) => {
      publish(updated);
      setLines(toLines(updated));
      setDirty(false);
      notify.success('Coding saved', 'A second coder can now verify it.');
    },
    onError: (error) => {
      if (isApiError(error) && error.isConflict) {
        notify.error(error, 'This coding was changed by someone else');
        void queryClient.invalidateQueries({ queryKey: mrdKeys.coding(coding.id) });
      } else {
        notify.error(error, 'Could not save the coding');
      }
    },
  });

  const verifyMutation = useMutation({
    mutationFn: () => mrdApi.verifyCoding(coding.id, coding.version),
    onSuccess: (updated) => {
      setConfirmVerify(false);
      publish(updated);
      notify.success('Coding verified', 'It is now used for claims and the morbidity index.');
    },
    onError: (error) => {
      setConfirmVerify(false);
      notify.error(error, 'Could not verify the coding');
    },
  });

  useHotkey('mod+s', () => dirty && problems.length === 0 && !save.isPending && save.mutate(), { allowInInputs: true });

  const coded = (suggestions.data ?? []).filter((s) => s.code);
  const admissionId = coding.admissionId;

  return (
    <Page>
      <PageHeader
        title={`Coding · ${coding.patientName ?? 'Patient'}`}
        subtitle={`${coding.uhid ?? ''} · ${coding.admissionId ? 'In-patient stay' : 'Out-patient encounter'}${coding.episodeDate ? ` · ${formatDateOnly(coding.episodeDate)}` : ''}`}
        breadcrumbs={[
          { label: 'Coding queue', onClick: () => void navigate({ to: '/mrd/coding' }) },
          { label: coding.uhid ?? 'Episode' },
        ]}
        actions={
          <div className={styles.actions}>
            <StatusBadge status={coding.status} />
            {canReadChart && (
              <Button
                icon={<OpenRegular />}
                onClick={() =>
                  void navigate({ to: '/emr/patients/$patientId', params: { patientId: coding.patientId } })
                }
              >
                Clinical chart
              </Button>
            )}
            {canReadChart && admissionId && (
              <Button
                icon={<OpenRegular />}
                onClick={() =>
                  void navigate({
                    to: '/emr/patients/$patientId/discharge/$admissionId',
                    params: { patientId: coding.patientId, admissionId },
                  })
                }
              >
                Discharge summary
              </Button>
            )}
          </div>
        }
      />

      {coding.status === 'Verified' && (
        <MessageBar intent="success">
          <MessageBarBody>
            Verified {formatDateTime(coding.verifiedAt)}. Saving new codes clears the verification and sends it back for
            checking.
          </MessageBarBody>
        </MessageBar>
      )}

      <div className={styles.layout}>
        <div className={styles.stack}>
          <Section title="Codes" description="Exactly one principal diagnosis. Procedures are coded in ICD-10-PCS.">
            <div className={styles.stack}>
              <FormGrid columns={2}>
                <IcdCodePicker
                  label="Add diagnosis (ICD-10)"
                  billableOnly
                  release={ICD10_RELEASE}
                  hint="Only ICD-10 codes valid for claims are offered"
                  onPick={(code) => change(addLine(lines, { kind: 'Diagnosis', code: code.code, title: code.title }))}
                />
                <ProcedureCodePicker
                  label="Add procedure (ICD-10-PCS)"
                  onPick={(code) => change(addLine(lines, { kind: 'Procedure', code: code.code, title: code.title }))}
                />
              </FormGrid>
              {lines.length === 0 ? (
                <Text className={styles.muted}>
                  No codes yet. Start from the doctors' diagnoses on the right or search above.
                </Text>
              ) : (
                <Table size="small" aria-label="Codes">
                  <TableHeader>
                    <TableRow>
                      <TableHeaderCell style={{ width: 90 }}>Principal</TableHeaderCell>
                      <TableHeaderCell style={{ width: 110 }}>Code</TableHeaderCell>
                      <TableHeaderCell>Title</TableHeaderCell>
                      <TableHeaderCell style={{ width: 110 }}>Type</TableHeaderCell>
                      <TableHeaderCell style={{ width: 60 }} aria-label="Remove" />
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {ordered(lines).map((line) => (
                      <TableRow key={`${line.kind}-${line.code}`}>
                        <TableCell>
                          {line.kind === 'Diagnosis' ? (
                            <Radio
                              checked={line.isPrimary}
                              name="principal"
                              aria-label={`Make ${line.code} the principal diagnosis`}
                              onChange={() => change(setPrimary(lines, line))}
                            />
                          ) : null}
                        </TableCell>
                        <TableCell>
                          <span className={styles.code}>{line.code}</span>
                        </TableCell>
                        <TableCell>{line.title}</TableCell>
                        <TableCell>{line.kind === 'Diagnosis' ? 'Diagnosis' : 'Procedure'}</TableCell>
                        <TableCell>
                          <Tooltip content="Remove" relationship="label">
                            <Button
                              size="small"
                              appearance="subtle"
                              icon={<DeleteRegular />}
                              onClick={() => change(removeLine(lines, line))}
                            />
                          </Tooltip>
                        </TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              )}
              <Field label="Coder's notes">
                <Textarea
                  value={notes}
                  onChange={(_, d) => {
                    setNotes(d.value.slice(0, 1000));
                    setDirty(true);
                  }}
                  rows={2}
                  resize="vertical"
                />
              </Field>
              {problems.length > 0 && lines.length > 0 && (
                <MessageBar intent="warning">
                  <MessageBarBody>{problems.join(' ')}</MessageBarBody>
                </MessageBar>
              )}
              <div className={styles.actions}>
                <Button
                  appearance="primary"
                  icon={save.isPending ? <Spinner size="tiny" /> : <SaveRegular />}
                  disabled={!dirty || problems.length > 0 || save.isPending}
                  onClick={() => save.mutate()}
                >
                  Save codes (Ctrl+S)
                </Button>
                <Tooltip content={verify.reason ?? 'Confirm the coding is correct'} relationship="description">
                  <Button
                    icon={<CheckmarkCircleRegular />}
                    disabled={!verify.allowed || dirty || verifyMutation.isPending}
                    onClick={() => setConfirmVerify(true)}
                  >
                    Verify
                  </Button>
                </Tooltip>
                {!verify.allowed && verify.reason && <Caption1 className={styles.muted}>{verify.reason}</Caption1>}
              </div>
            </div>
          </Section>
        </div>

        <Section
          title="Doctors' diagnoses"
          description="Recorded clinically for this episode"
          actions={
            coded.length > 0 && (
              <Button size="small" icon={<AddRegular />} onClick={() => change(applySuggestions(lines, coded))}>
                Add all coded
              </Button>
            )
          }
        >
          {suggestions.isLoading ? (
            <LoadingState />
          ) : (suggestions.data ?? []).length === 0 ? (
            <Text className={styles.muted}>
              No diagnoses were recorded for this episode. Read the discharge summary and code from it.
            </Text>
          ) : (
            <div>
              {suggestions.data?.map((s, index) => (
                <div key={`${s.code ?? s.title}-${index}`} className={styles.suggestion}>
                  <span>
                    {s.code ? (
                      <span className={styles.code}>{s.code} </span>
                    ) : (
                      <Badge appearance="outline" color="warning" size="small">
                        No code
                      </Badge>
                    )}{' '}
                    <Text>{s.title}</Text>
                    <Caption1 block className={styles.muted}>
                      {s.type}
                      {s.isPrimary ? ' · primary' : ''} · {s.source}
                    </Caption1>
                  </span>
                  {s.code !== null && (
                    <Button
                      size="small"
                      icon={<AddRegular />}
                      aria-label={`Add ${s.code}`}
                      onClick={() => change(addLine(lines, { kind: 'Diagnosis', code: s.code ?? '', title: s.title }))}
                    />
                  )}
                </div>
              ))}
            </div>
          )}
        </Section>
      </div>

      <ConfirmDialog
        open={confirmVerify}
        title="Verify this coding?"
        confirmLabel="Verify"
        busy={verifyMutation.isPending}
        onConfirm={() => verifyMutation.mutate()}
        onCancel={() => setConfirmVerify(false)}
      >
        You confirm that the codes match the documentation of this episode. Verified coding is used for insurance claims
        and the morbidity index.
      </ConfirmDialog>
    </Page>
  );
}
