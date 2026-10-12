import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  OverlayDrawer,
  SearchBox,
  Select,
  Spinner,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, DeleteRegular, DismissRegular, PeopleTeamRegular } from '@fluentui/react-icons';
import {
  formatDateOnly,
  formatDateTime,
  formatInr,
  isApiError,
  normalizePhone,
  todayIso,
  useDebouncedValue,
} from '@hc360/core';
import { DataTable, FilterBar, FormGrid, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { addDaysIso } from '@/modules/emr/encounterForm';
import { checkupApi, checkupKeys, checkupQueries, type CorporateBatchResult } from '@/modules/health-checkups/api';
import { checkupProblem } from '@/modules/health-checkups/itinerary';
import { PatientPicker, type PickedPatient } from '@/modules/patients/PatientPicker';

export const Route = createFileRoute('/_app/health-checkups/corporate/')({
  component: CorporateBatchesPage,
});

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
  people: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
    maxHeight: '260px',
    overflowY: 'auto',
    margin: 0,
    padding: 0,
    listStyle: 'none',
  },
  person: {
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'center',
    gap: '8px',
    padding: '4px 8px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  muted: { color: tokens.colorNeutralForeground3 },
});

const MAX_EMPLOYEES = 500;

function NewBatchDrawer({
  open,
  onClose,
  onBooked,
}: {
  open: boolean;
  onClose: () => void;
  onBooked: (result: CorporateBatchResult, people: PickedPatient[]) => void;
}) {
  return (
    <OverlayDrawer open={open} position="end" size="large" onOpenChange={(_, d) => !d.open && onClose()}>
      {open && <NewBatchForm onClose={onClose} onBooked={onBooked} />}
    </OverlayDrawer>
  );
}

function NewBatchForm({
  onClose,
  onBooked,
}: {
  onClose: () => void;
  onBooked: (result: CorporateBatchResult, people: PickedPatient[]) => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const today = todayIso();
  const packages = useQuery(checkupQueries.packages());
  const [corporateName, setCorporateName] = useState('');
  const [contactPerson, setContactPerson] = useState('');
  const [contactPhone, setContactPhone] = useState('');
  const [reference, setReference] = useState('');
  const [packageId, setPackageId] = useState('');
  const [date, setDate] = useState(addDaysIso(today, 1));
  const [notes, setNotes] = useState('');
  const [people, setPeople] = useState<PickedPatient[]>([]);
  const [error, setError] = useState<string | null>(null);
  const phoneValid = !contactPhone.trim() || normalizePhone(contactPhone) !== null;
  const dateValid = date >= today && date <= addDaysIso(today, 365);
  const selected = packages.data?.find((p) => p.id === packageId);
  const valid =
    corporateName.trim().length > 0 &&
    Boolean(packageId) &&
    dateValid &&
    phoneValid &&
    people.length > 0 &&
    people.length <= MAX_EMPLOYEES;

  const book = useMutation({
    mutationFn: () =>
      checkupApi.bookBatch({
        corporateName: corporateName.trim(),
        contactPerson: contactPerson.trim() || null,
        contactPhone: contactPhone.trim() || null,
        reference: reference.trim() || null,
        packageId,
        scheduledDate: date,
        patientIds: people.map((p) => p.id),
        notes: notes.trim() || null,
      }),
    onSuccess: (result) => {
      void queryClient.invalidateQueries({ queryKey: checkupKeys.all });
      onBooked(result, people);
    },
    onError: (e) => {
      if (isApiError(e)) {
        const wording = checkupProblem(e, 'book');
        setError(`${wording.title}. ${wording.message}`);
      } else {
        notify.error(e, 'Could not book the batch');
      }
    },
  });

  return (
    <>
      <DrawerHeader>
        <DrawerHeaderTitle
          action={<Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={onClose} />}
        >
          New corporate batch
        </DrawerHeaderTitle>
      </DrawerHeader>
      <DrawerBody>
        <div className={styles.form}>
          <FormGrid columns={2}>
            <Field label="Company" required>
              <Input autoFocus value={corporateName} onChange={(_, d) => setCorporateName(d.value.slice(0, 200))} />
            </Field>
            <Field label="Purchase order / agreement">
              <Input value={reference} onChange={(_, d) => setReference(d.value.slice(0, 100))} />
            </Field>
            <Field label="Contact person">
              <Input value={contactPerson} onChange={(_, d) => setContactPerson(d.value.slice(0, 200))} />
            </Field>
            <Field
              label="Contact phone"
              validationState={phoneValid ? 'none' : 'error'}
              validationMessage={phoneValid ? undefined : 'Enter a valid phone number.'}
            >
              <Input type="tel" value={contactPhone} onChange={(_, d) => setContactPhone(d.value)} />
            </Field>
            <Field label="Package" required>
              <Select value={packageId} onChange={(_, d) => setPackageId(d.value)}>
                <option value="">Choose a package</option>
                {(packages.data ?? []).map((p) => (
                  <option key={p.id} value={p.id}>
                    {p.name} · {formatInr(p.price, { whole: true })}
                  </option>
                ))}
              </Select>
            </Field>
            <Field
              label="Check-up date"
              required
              validationState={dateValid ? 'none' : 'error'}
              validationMessage={dateValid ? undefined : 'Choose a date from today up to one year ahead.'}
            >
              <Input
                type="date"
                value={date}
                min={today}
                max={addDaysIso(today, 365)}
                onChange={(_, d) => setDate(d.value)}
              />
            </Field>
          </FormGrid>
          <PatientPicker
            label={`Add employee (${people.length} of up to ${MAX_EMPLOYEES})`}
            value={null}
            hint="Employees must be registered patients. Ineligible ones (age or sex) are reported, not booked."
            onChange={(p) => {
              if (p && !people.some((x) => x.id === p.id)) {
                setPeople((current) => [...current, p]);
              }
            }}
          />
          {people.length > 0 && (
            <ul className={styles.people} aria-label="Employees in this batch">
              {people.map((p) => (
                <li key={p.id} className={styles.person}>
                  <Text>
                    {p.fullName} <Caption1 className={styles.muted}>· {p.uhid}</Caption1>
                  </Text>
                  <Button
                    size="small"
                    appearance="subtle"
                    icon={<DeleteRegular />}
                    aria-label={`Remove ${p.fullName}`}
                    onClick={() => setPeople((current) => current.filter((x) => x.id !== p.id))}
                  />
                </li>
              ))}
            </ul>
          )}
          <Field label="Notes">
            <Textarea value={notes} onChange={(_, d) => setNotes(d.value.slice(0, 1000))} rows={2} resize="vertical" />
          </Field>
          {selected && people.length > 0 && (
            <Caption1 className={styles.muted}>
              Package value for {people.length} {people.length === 1 ? 'employee' : 'employees'}:{' '}
              {formatInr(selected.price * people.length, { whole: true })} (each is billed at check-in)
            </Caption1>
          )}
          {error && (
            <MessageBar intent="error">
              <MessageBarBody>{error}</MessageBarBody>
            </MessageBar>
          )}
        </div>
      </DrawerBody>
      <DrawerFooter>
        <Button
          appearance="primary"
          icon={book.isPending ? <Spinner size="tiny" aria-hidden /> : <PeopleTeamRegular />}
          disabled={!valid || book.isPending}
          onClick={() => book.mutate()}
        >
          Book {people.length} {people.length === 1 ? 'employee' : 'employees'}
        </Button>
        <Button onClick={onClose}>Cancel</Button>
      </DrawerFooter>
    </>
  );
}

/** Corporate health-check drives (MHC-05): one package for many employees on one day. */
function CorporateBatchesPage() {
  const styles = useStyles();
  const navigate = useNavigate();
  const [term, setTerm] = useState('');
  const [page, setPage] = useState(1);
  const search = useDebouncedValue(term.trim(), 300);
  const batches = useQuery(checkupQueries.batches(search, page));
  const packages = useQuery(checkupQueries.packages('', true));
  const [creating, setCreating] = useState(false);
  const [result, setResult] = useState<{ result: CorporateBatchResult; people: PickedPatient[] } | null>(null);
  const packageName = (id: string) => packages.data?.find((p) => p.id === id)?.name ?? '—';

  return (
    <Page>
      <PageHeader
        title="Corporate batches"
        subtitle="Book one package for a company's employees on one day. Each employee is checked in and billed individually."
        actions={
          <Button appearance="primary" icon={<AddRegular />} onClick={() => setCreating(true)}>
            New batch
          </Button>
        }
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 260px' }}>
              <SearchBox
                value={term}
                onChange={(_, d) => {
                  setTerm(d.value);
                  setPage(1);
                }}
                placeholder="Company or batch number"
              />
            </Field>
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Corporate batches"
          rows={batches.data?.items}
          loading={batches.isLoading}
          error={batches.error}
          onRetry={() => void batches.refetch()}
          getRowId={(b) => b.id}
          onRowClick={(b) => void navigate({ to: '/health-checkups/corporate/$batchId', params: { batchId: b.id } })}
          size="small"
          columns={[
            {
              key: 'company',
              header: 'Company',
              render: (b) => (
                <span>
                  <Text weight="semibold">{b.corporateName}</Text>
                  <Caption1 block className={styles.muted}>
                    {b.batchNumber}
                    {b.reference ? ` · ${b.reference}` : ''}
                  </Caption1>
                </span>
              ),
            },
            { key: 'date', header: 'Date', width: 120, render: (b) => formatDateOnly(b.scheduledDate) },
            { key: 'package', header: 'Package', render: (b) => packageName(b.packageId) },
            { key: 'booked', header: 'Booked', width: 90, align: 'end', render: (b) => b.bookedCount },
            {
              key: 'skipped',
              header: 'Not booked',
              width: 100,
              align: 'end',
              priority: 'low',
              render: (b) => b.skippedCount,
            },
            {
              key: 'contact',
              header: 'Contact',
              priority: 'low',
              render: (b) => [b.contactPerson, b.contactPhone].filter(Boolean).join(' · ') || '—',
            },
            { key: 'created', header: 'Created', priority: 'low', render: (b) => formatDateTime(b.createdAt) },
          ]}
          emptyTitle="No corporate batches"
          emptyDescription="Use New batch to book a company's employees together."
          page={page}
          pageSize={25}
          totalCount={batches.data?.totalCount}
          onPageChange={setPage}
        />
      </Section>

      <NewBatchDrawer
        open={creating}
        onClose={() => setCreating(false)}
        onBooked={(r, people) => {
          setCreating(false);
          setResult({ result: r, people });
        }}
      />
      <Dialog open={result !== null} onOpenChange={(_, d) => !d.open && setResult(null)}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle>Batch {result?.result.batchNumber} booked</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              <Text>
                {result?.result.booked} {result?.result.booked === 1 ? 'employee was' : 'employees were'} booked.
              </Text>
              {result && result.result.skipped.length > 0 && (
                <MessageBar intent="warning" layout="multiline">
                  <MessageBarBody>
                    <MessageBarTitle>{result.result.skipped.length} not booked</MessageBarTitle>
                    <ul style={{ margin: '4px 0 0', paddingLeft: 18 }}>
                      {result.result.skipped.map((s) => (
                        <li key={s.patientId}>
                          {result.people.find((p) => p.id === s.patientId)?.fullName ?? 'Patient'}: {s.reason}
                        </li>
                      ))}
                    </ul>
                  </MessageBarBody>
                </MessageBar>
              )}
            </DialogContent>
            <DialogActions>
              <Button onClick={() => setResult(null)}>Close</Button>
              <Button
                appearance="primary"
                onClick={() => {
                  const id = result?.result.batchId;
                  setResult(null);
                  if (id) {
                    void navigate({ to: '/health-checkups/corporate/$batchId', params: { batchId: id } });
                  }
                }}
              >
                Open batch
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </Page>
  );
}
