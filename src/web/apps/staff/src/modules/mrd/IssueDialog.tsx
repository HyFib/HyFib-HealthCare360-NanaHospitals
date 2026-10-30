import {
  Button,
  Caption1,
  Checkbox,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  makeStyles,
  Spinner,
  Text,
  tokens,
  useId,
} from '@fluentui/react-components';
import { SendRegular } from '@fluentui/react-icons';
import { formatDateTime, todayIso } from '@hc360/core';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { addDaysIso } from '@/modules/emr/encounterForm';
import { mrdQueries, type RecordRequestDto } from './api';
import { isCopyRequest, issueChoice } from './copies';

const useStyles = makeStyles({
  documents: { display: 'flex', flexDirection: 'column', gap: '4px' },
  list: { display: 'flex', flexDirection: 'column', maxHeight: '240px', overflowY: 'auto' },
  failed: { display: 'flex', alignItems: 'center', gap: '8px', flexWrap: 'wrap' },
  error: { color: tokens.colorPaletteRedForeground1 },
});

/** What issuing a request sends: the return date of a chart lent out, and the documents of a copy. */
export interface IssueChoice {
  dueBackOn: string | null;
  documents: { kind: string; id: string }[];
}

export function IssueDialog({
  request,
  busy,
  onConfirm,
  onCancel,
}: {
  request: RecordRequestDto | null;
  busy: boolean;
  onConfirm: (choice: IssueChoice) => void;
  onCancel: () => void;
}) {
  return (
    <Dialog open={request !== null} onOpenChange={(_, d) => !d.open && !busy && onCancel()}>
      <DialogSurface>
        {request && <IssueBody request={request} busy={busy} onConfirm={onConfirm} onCancel={onCancel} />}
      </DialogSurface>
    </Dialog>
  );
}

/**
 * Issues the chart, lent out until a return date, or a copy. A copy may be issued as documents: each one ticked is made
 * into a PDF now and kept as it is handed over, with the SHA-256 its certificate states (BSA 2023, s.63). A copy is kept
 * by whoever asked, so it has no return date; nor does a request issued as documents.
 */
function IssueBody({
  request,
  busy,
  onConfirm,
  onCancel,
}: {
  request: RecordRequestDto;
  busy: boolean;
  onConfirm: (choice: IssueChoice) => void;
  onCancel: () => void;
}) {
  const styles = useStyles();
  const labelId = useId('copy-documents');
  const hintId = useId('copy-documents-hint');
  const today = todayIso();
  const copy = isCopyRequest(request);
  const [dueBack, setDueBack] = useState(() => addDaysIso(today, request.purpose === 'PatientCare' ? 3 : 7));
  const [chosen, setChosen] = useState<ReadonlySet<string>>(new Set());
  // A chart lent to a ward does not list the patient's documents (listing them opens the chart): only a copy does.
  const [asCopy, setAsCopy] = useState(copy);
  // Until the documents are listed nothing is issued: a copy issued without them would have no files, for good, and a
  // chart to be given as documents would be lent instead.
  const documents = useQuery({ ...mrdQueries.requestDocuments(request.id), enabled: asCopy });
  const all = asCopy ? (documents.data ?? []) : [];
  const keyOf = (d: { kind: string; id: string }) => `${d.kind}/${d.id}`;
  const lent = !copy && chosen.size === 0;
  const toggle = (key: string, on: boolean) =>
    setChosen((current) => {
      const next = new Set(current);
      if (on) next.add(key);
      else next.delete(key);
      return next;
    });
  return (
    <DialogBody>
      <DialogTitle>{copy ? 'Issue the copy' : 'Issue the file'}</DialogTitle>
      <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
        <Text>
          {request.patientName} ({request.uhid}) to {request.requestedByName}
          {request.requestingDepartment ? `, ${request.requestingDepartment}` : ''}.
        </Text>
        {!copy && (
          <Checkbox
            label="Give a copy of documents instead of lending the file"
            checked={asCopy}
            onChange={(_, d) => {
              setAsCopy(d.checked === true);
              setChosen(new Set());
            }}
          />
        )}
        {asCopy && (
          // A group, not a Field: a Field gives every checkbox in it the same id, so a document's label would tick the
          // first box, and the Field's label would be every box's name.
          <div role="group" aria-labelledby={labelId} aria-describedby={hintId} className={styles.documents}>
            <Text id={labelId} weight="semibold">
              Documents for the copy
            </Text>
            <Caption1 id={hintId}>
              {copy
                ? 'Each document ticked is made into a PDF now and kept as it is handed over, with its SHA-256 for the certificate under the Bharatiya Sakshya Adhiniyam (s.63). Tick none for a copy given on paper.'
                : 'Each document ticked is made into a PDF now and kept as it is handed over, with its SHA-256. Tick none to lend the file itself.'}
            </Caption1>
            {documents.isLoading ? (
              <Spinner size="tiny" label="Listing the documents" />
            ) : documents.isError ? (
              <div className={styles.failed}>
                <Text role="alert" className={styles.error}>
                  The documents could not be listed.
                </Text>
                <Button
                  size="small"
                  disabled={documents.isFetching}
                  icon={documents.isFetching ? <Spinner size="tiny" aria-hidden /> : undefined}
                  onClick={() => void documents.refetch()}
                >
                  Try again
                </Button>
              </div>
            ) : all.length === 0 ? (
              <Caption1>No signed or released documents for this patient.</Caption1>
            ) : (
              <div className={styles.list}>
                <Checkbox
                  label="All documents"
                  checked={chosen.size === all.length ? true : chosen.size === 0 ? false : 'mixed'}
                  onChange={(_, d) => setChosen(d.checked === true ? new Set(all.map(keyOf)) : new Set())}
                />
                {all.map((d) => (
                  <Checkbox
                    key={keyOf(d)}
                    label={`${d.title} · ${formatDateTime(d.date)}`}
                    checked={chosen.has(keyOf(d))}
                    onChange={(_, e) => toggle(keyOf(d), e.checked === true)}
                  />
                ))}
              </div>
            )}
          </div>
        )}
        {lent && (
          <Field label="Return by" hint="Files not returned by this date show as overdue">
            <Input type="date" value={dueBack} min={today} onChange={(_, d) => setDueBack(d.value)} />
          </Field>
        )}
      </DialogContent>
      <DialogActions>
        <Button onClick={onCancel} disabled={busy}>
          Cancel
        </Button>
        <Button
          appearance="primary"
          disabled={busy || (asCopy && (documents.isLoading || documents.isError))}
          icon={busy ? <Spinner size="tiny" aria-hidden /> : <SendRegular />}
          onClick={() =>
            onConfirm(
              issueChoice(
                request,
                all.filter((d) => chosen.has(keyOf(d))).map((d) => ({ kind: d.kind, id: d.id })),
                dueBack,
              ),
            )
          }
        >
          {chosen.size > 0 ? `Issue ${chosen.size === 1 ? '1 document' : `${chosen.size} documents`}` : 'Issue'}
        </Button>
      </DialogActions>
    </DialogBody>
  );
}
