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
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  Tab,
  TabList,
  Text,
  Textarea,
  tokens,
  Tooltip,
} from '@fluentui/react-components';
import { DismissRegular, PeopleAddRegular, PersonDeleteRegular, WarningRegular } from '@fluentui/react-icons';
import { formatDateTime, isApiError } from '@hc360/core';
import {
  CloseRequestScope,
  DataTable,
  Section,
  useCloseRequests,
  useUnsavedChanges,
  useValuesChanged,
} from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { PatientPicker, type PickedPatient } from '@/modules/patients/PatientPicker';
import {
  AFFECTED_PAGE_SIZE,
  privacyApi,
  privacyKeys,
  privacyQueries,
  type AddAffectedResult,
  type AffectedPatientItem,
  type BreachDetail,
} from './api';
import type { BreachCommand } from './useBreachCommand';
import { breachSteps, noticeChannelsText, parseUhids, phoneTail } from './rules';

const useStyles = makeStyles({
  muted: { color: tokens.colorNeutralForeground3 },
  warn: { display: 'inline-flex', alignItems: 'center', gap: '4px', color: tokens.colorPaletteMarigoldForeground1 },
  content: { display: 'flex', flexDirection: 'column', gap: '12px' },
  chosen: { display: 'flex', flexDirection: 'column', gap: '4px', maxHeight: '220px', overflowY: 'auto' },
  chosenRow: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    padding: '2px 4px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  grow: { flex: 1, minWidth: 0 },
  mono: { fontFamily: tokens.fontFamilyMonospace },
});

/** Most patients pasted or chosen in one go (AddAffectedRequestValidator). */
const MAX_AT_ONCE = 5000;

/** The people affected by a breach: who is listed, and whether and how they were sent the notice. */
export function AffectedPeople({
  breach,
  page,
  onPageChange,
  command,
}: {
  breach: BreachDetail;
  page: number;
  onPageChange: (page: number) => void;
  command: BreachCommand;
}) {
  const styles = useStyles();
  const queryClient = useQueryClient();
  const [adding, setAdding] = useState(false);
  const affected = useQuery(privacyQueries.affected(breach.id, page));
  const step = breachSteps(breach).addPeople;
  const closed = breach.status === 'Closed';

  const remove = async (person: AffectedPatientItem) => {
    await command.run(() => privacyApi.removeAffected(breach.id, person.patientId), {
      success: `${person.fullName} removed from the list`,
      failure: 'Could not remove the patient',
    });
    void queryClient.invalidateQueries({ queryKey: privacyKeys.affected(breach.id) });
  };

  const addButton = (
    <Button
      icon={<PeopleAddRegular />}
      disabledFocusable={Boolean(step.blocker)}
      onClick={() => !step.blocker && setAdding(true)}
    >
      Add people
    </Button>
  );

  return (
    <Section
      title="People affected"
      description={`${breach.affectedListed.toLocaleString('en-IN')} listed · ${breach.notified.toLocaleString('en-IN')} sent the notice${
        breach.withoutContact > 0 ? ` · ${breach.withoutContact.toLocaleString('en-IN')} without contact details` : ''
      }`}
      actions={
        step.blocker ? (
          <Tooltip content={step.blocker} relationship="description">
            {addButton}
          </Tooltip>
        ) : (
          addButton
        )
      }
      flush
    >
      <DataTable
        ariaLabel="People affected"
        rows={affected.data?.items}
        loading={affected.isLoading}
        error={affected.error}
        onRetry={() => void affected.refetch()}
        getRowId={(p) => p.patientId}
        size="small"
        columns={[
          { key: 'uhid', header: 'UHID', width: 140, render: (p) => <Text font="monospace">{p.uhid}</Text> },
          { key: 'name', header: 'Name', render: (p) => p.fullName },
          {
            key: 'mobile',
            header: 'Mobile',
            width: 130,
            render: (p) => {
              const tail = phoneTail(p.phone);
              return tail ? `Ending ${tail}` : <Caption1 className={styles.muted}>None</Caption1>;
            },
          },
          {
            key: 'email',
            header: 'E-mail',
            width: 80,
            priority: 'low',
            render: (p) => (p.hasEmail ? 'Yes' : 'No'),
          },
          {
            key: 'notice',
            header: 'Notice',
            render: (p) =>
              p.notifiedAt ? (
                <span>
                  <Text>{formatDateTime(p.notifiedAt)}</Text>
                  <Caption1 block className={styles.muted}>
                    {noticeChannelsText(p.noticeChannels)}
                  </Caption1>
                </span>
              ) : p.noticeChannels === 'none' ? (
                <span className={styles.warn}>
                  <WarningRegular aria-hidden />
                  <Text>No contact details</Text>
                </span>
              ) : (
                <Caption1 className={styles.muted}>Not sent yet</Caption1>
              ),
          },
        ]}
        rowActions={(p) =>
          !p.notifiedAt && !closed ? (
            <Button
              size="small"
              appearance="subtle"
              icon={<PersonDeleteRegular />}
              disabled={command.busy}
              onClick={() => void remove(p)}
              aria-label={`Remove ${p.fullName} from the list`}
            >
              Remove
            </Button>
          ) : null
        }
        emptyTitle="Nobody is listed yet"
        emptyDescription="Add the patients affected, one by one or by pasting their UHIDs, so they can be sent the notice."
        page={page}
        pageSize={AFFECTED_PAGE_SIZE}
        totalCount={affected.data?.totalCount}
        onPageChange={onPageChange}
      />
      <AddPeopleDialog open={adding} breachId={breach.id} onClose={() => setAdding(false)} />
    </Section>
  );
}

function AddPeopleDialog({ open, breachId, onClose }: { open: boolean; breachId: string; onClose: () => void }) {
  const [busy, setBusy] = useState(false);
  const closing = useCloseRequests({ onClose });
  return (
    <Dialog open={open} onOpenChange={closing.onOpenChange}>
      <DialogSurface style={{ maxWidth: 620 }}>
        <CloseRequestScope value={closing}>
          {open && <AddPeopleBody breachId={breachId} busy={busy} setBusy={setBusy} onClose={onClose} />}
        </CloseRequestScope>
      </DialogSurface>
    </Dialog>
  );
}

function AddPeopleBody({
  breachId,
  busy,
  setBusy,
  onClose,
}: {
  breachId: string;
  busy: boolean;
  setBusy: (busy: boolean) => void;
  onClose: () => void;
}) {
  const styles = useStyles();
  const queryClient = useQueryClient();
  const [tab, setTab] = useState<'pick' | 'paste'>('pick');
  const [chosen, setChosen] = useState<PickedPatient[]>([]);
  const [pasted, setPasted] = useState('');
  const [result, setResult] = useState<AddAffectedResult | null>(null);
  const [failure, setFailure] = useState<string | null>(null);
  const uhids = parseUhids(pasted);
  const total = chosen.length + uhids.length;
  const tooMany = chosen.length > MAX_AT_ONCE || uhids.length > MAX_AT_ONCE;

  const add = async () => {
    if (total === 0 || tooMany) {
      return;
    }
    setBusy(true);
    setFailure(null);
    try {
      const added = await privacyApi.addAffected(
        breachId,
        chosen.map((p) => p.id),
        uhids,
      );
      setResult(added);
      setChosen([]);
      setPasted('');
      void queryClient.invalidateQueries({ queryKey: privacyKeys.breaches });
    } catch (error) {
      setFailure(isApiError(error) ? error.userMessage : 'The patients could not be added. Try again.');
    } finally {
      setBusy(false);
    }
  };

  const guard = useUnsavedChanges({ dirty: useValuesChanged({ chosen, pasted }) && result === null, busy, onClose });

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        void add();
      }}
    >
      <DialogBody>
        <DialogTitle>Add the people affected</DialogTitle>
        <DialogContent className={styles.content}>
          {result && (
            <MessageBar intent={result.notFound.length > 0 ? 'warning' : 'success'} layout="multiline">
              <MessageBarBody>
                <MessageBarTitle>
                  {result.added.toLocaleString('en-IN')} added
                  {result.alreadyListed > 0 ? ` · ${result.alreadyListed.toLocaleString('en-IN')} already listed` : ''}
                </MessageBarTitle>
                {result.notFound.length > 0 && (
                  <>
                    Not found ({result.notFound.length.toLocaleString('en-IN')}):{' '}
                    <span className={styles.mono}>{result.notFound.slice(0, 20).join(', ')}</span>
                    {result.notFound.length > 20 ? ` and ${result.notFound.length - 20} more` : ''}. Check them and try
                    again.
                  </>
                )}
              </MessageBarBody>
            </MessageBar>
          )}
          {failure && (
            <MessageBar intent="error">
              <MessageBarBody>{failure}</MessageBarBody>
            </MessageBar>
          )}
          <TabList selectedValue={tab} onTabSelect={(_, d) => setTab(d.value as 'pick' | 'paste')}>
            <Tab value="pick">Choose patients{chosen.length > 0 ? ` (${chosen.length})` : ''}</Tab>
            <Tab value="paste">Paste UHIDs{uhids.length > 0 ? ` (${uhids.length.toLocaleString('en-IN')})` : ''}</Tab>
          </TabList>
          {tab === 'pick' ? (
            <>
              <PatientPicker
                label="Find a patient"
                value={null}
                autoFocus
                onChange={(patient) =>
                  patient &&
                  setChosen((current) => (current.some((p) => p.id === patient.id) ? current : [...current, patient]))
                }
                hint="Each patient you choose is added below."
              />
              {chosen.length > 0 && (
                <div className={styles.chosen} role="list" aria-label="Patients chosen">
                  {chosen.map((p) => (
                    <div key={p.id} className={styles.chosenRow} role="listitem">
                      <Text className={styles.grow}>
                        {p.fullName} · <span className={styles.mono}>{p.uhid}</span>
                      </Text>
                      <Button
                        size="small"
                        appearance="subtle"
                        icon={<DismissRegular />}
                        aria-label={`Take ${p.fullName} off this list`}
                        onClick={() => setChosen((current) => current.filter((c) => c.id !== p.id))}
                      />
                    </div>
                  ))}
                </div>
              )}
            </>
          ) : (
            <Field
              label="UHIDs"
              hint="One per line, or separated by commas. Up to 5,000 at a time; a merged record counts as the one it was merged into."
              validationMessage={uhids.length > MAX_AT_ONCE ? 'Paste at most 5,000 UHIDs at a time.' : undefined}
              validationState={uhids.length > MAX_AT_ONCE ? 'error' : 'none'}
            >
              <Textarea value={pasted} onChange={(_, d) => setPasted(d.value)} rows={8} resize="vertical" autoFocus />
            </Field>
          )}
          <Caption1 className={styles.muted}>
            {total === 0
              ? 'Nobody chosen yet.'
              : `${total.toLocaleString('en-IN')} to add. Patients already on the list are skipped.`}
          </Caption1>
        </DialogContent>
        <DialogActions>
          <Button onClick={guard.requestClose} disabled={busy}>
            {result ? 'Done' : 'Cancel'}
          </Button>
          <Button
            type="submit"
            appearance="primary"
            disabled={busy || total === 0 || tooMany}
            icon={busy ? <Spinner size="tiny" aria-hidden /> : undefined}
          >
            Add to the list
          </Button>
        </DialogActions>
      </DialogBody>
      {guard.dialogs}
    </form>
  );
}
