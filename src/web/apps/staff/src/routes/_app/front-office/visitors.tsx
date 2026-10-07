import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  SearchBox,
  Select,
  Switch,
  Text,
} from '@fluentui/react-components';
import { AddRegular, DoorArrowRightRegular, PrintRegular, ProhibitedRegular } from '@fluentui/react-icons';
import {
  escapeHtml,
  formatDateTime,
  letterheadHtml,
  normalizePhone,
  printDocument,
  printHtml,
  templateFor,
  useDebouncedValue,
  type Letterhead,
} from '@hc360/core';
import {
  barcodeSvg,
  ConfirmDialog,
  DataTable,
  FilterBar,
  FormGrid,
  Page,
  PageHeader,
  Section,
  StatusBadge,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useState } from 'react';
import { api } from '@/lib/api';
import { useLetterhead } from '@/lib/letterhead';
import { frontOfficeQueries, type IssueVisitorPassRequest, type VisitorPassDto } from '@/modules/front-office/api';
import { PatientPicker, type PickedPatient } from '@/modules/patients/PatientPicker';

export const Route = createFileRoute('/_app/front-office/visitors')({
  component: VisitorPassesPage,
});

const ID_PROOFS = ['Aadhaar', 'Driving licence', 'Voter ID', 'PAN card', 'Passport', 'Employee ID', 'Other'];

async function printPass(pass: VisitorPassDto, letterhead: Letterhead) {
  const barcode = await barcodeSvg(pass.passNumber, 'code128', { height: 8 });
  const template = templateFor(letterhead, 'other');
  const body = `
    <div class="pass">
      ${letterheadHtml(letterhead, template, { compact: true })}
      <div class="title">${pass.isAttendant ? 'ATTENDANT PASS' : 'VISITOR PASS'}</div>
      <div class="name">${escapeHtml(pass.visitorName)}</div>
      ${pass.patientName ? `<div>Visiting: ${escapeHtml(pass.patientName)}</div>` : ''}
      ${pass.location ? `<div>Ward / bed: ${escapeHtml(pass.location)}</div>` : ''}
      <div class="barcode">${barcode}</div>
      <div class="number">${escapeHtml(pass.passNumber)}</div>
      <div class="valid">Valid until ${escapeHtml(formatDateTime(pass.validUntil))}</div>
    </div>`;
  const styles = `
    .pass { width: 72mm; font-family: 'Segoe UI', sans-serif; text-align: center; font-size: 10pt; }
    .title { margin: 4px 0; letter-spacing: 2px; font-weight: 700; }
    .name { font-size: 14pt; font-weight: 700; }
    .barcode svg { width: 100%; height: 12mm; margin-top: 6px; }
    .number { font-family: Consolas, monospace; }
    .valid { margin-top: 4px; font-weight: 600; }`;
  await printHtml(printDocument(body, { title: `Pass ${pass.passNumber}`, pageSize: 'Receipt80', styles, template }), {
    pageSize: 'Receipt80',
    silent: true,
  });
}

function VisitorPassesPage() {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const letterhead = useLetterhead();
  const [activeOnly, setActiveOnly] = useState(true);
  const [page, setPage] = useState(1);
  const [term, setTerm] = useState('');
  const search = useDebouncedValue(term.trim(), 300);
  const [issuing, setIssuing] = useState(false);
  const [revoking, setRevoking] = useState<VisitorPassDto | null>(null);
  const passes = useQuery(frontOfficeQueries.visitorPasses(activeOnly, page, search || undefined));

  const close = useMutation({
    mutationFn: ({ pass, action }: { pass: VisitorPassDto; action: 'checkout' | 'revoke' }) =>
      api.post(`/api/v1/front-office/visitor-passes/${pass.id}/${action}`),
    onSuccess: (_, { action }) => {
      setRevoking(null);
      notify.success(action === 'checkout' ? 'Visitor checked out' : 'Pass revoked');
      void queryClient.invalidateQueries({ queryKey: ['front-office'] });
    },
    onError: (error) => notify.error(error, 'Could not update the pass'),
  });

  const print = (pass: VisitorPassDto) =>
    void printPass(pass, letterhead).catch((error: unknown) => notify.error(error, 'Printing failed'));

  return (
    <Page>
      <PageHeader
        title="Visitor and attendant passes"
        subtitle="Controls who can enter the wards. Passes expire on their own."
        actions={
          <Button appearance="primary" icon={<AddRegular />} onClick={() => setIssuing(true)}>
            Issue pass
          </Button>
        }
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 260px' }}>
              <SearchBox
                value={term}
                onChange={(_, d) => setTerm(d.value)}
                placeholder="Pass number, visitor or patient"
              />
            </Field>
            <Switch
              checked={activeOnly}
              onChange={(_, d) => {
                setActiveOnly(d.checked);
                setPage(1);
              }}
              label="Active passes only"
            />
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Visitor passes"
          rows={passes.data?.items}
          loading={passes.isLoading}
          error={passes.error}
          onRetry={() => void passes.refetch()}
          getRowId={(p) => p.id}
          columns={[
            { key: 'number', header: 'Pass', width: 130, render: (p) => <Text font="monospace">{p.passNumber}</Text> },
            { key: 'visitor', header: 'Visitor', render: (p) => <Text weight="semibold">{p.visitorName}</Text> },
            { key: 'kind', header: 'Kind', width: 100, render: (p) => (p.isAttendant ? 'Attendant' : 'Visitor') },
            {
              key: 'patient',
              header: 'Patient',
              priority: 'low',
              render: (p) => (p.patientName ? `${p.patientName} (${p.patientUhid})` : '—'),
            },
            { key: 'location', header: 'Ward / bed', priority: 'low', render: (p) => p.location ?? '—' },
            { key: 'until', header: 'Valid until', render: (p) => formatDateTime(p.validUntil) },
            { key: 'status', header: 'Status', render: (p) => <StatusBadge status={p.status} /> },
          ]}
          rowActions={(p) => (
            <span style={{ display: 'inline-flex', gap: 4 }}>
              <Button
                size="small"
                appearance="subtle"
                icon={<PrintRegular />}
                aria-label="Print pass"
                onClick={() => print(p)}
              />
              {p.status === 'Active' && (
                <>
                  <Button
                    size="small"
                    icon={<DoorArrowRightRegular />}
                    onClick={() => close.mutate({ pass: p, action: 'checkout' })}
                  >
                    Check out
                  </Button>
                  <Button
                    size="small"
                    appearance="subtle"
                    icon={<ProhibitedRegular />}
                    aria-label="Revoke pass"
                    onClick={() => setRevoking(p)}
                  />
                </>
              )}
            </span>
          )}
          emptyTitle={activeOnly ? 'No active passes' : 'No passes found'}
          page={page}
          pageSize={25}
          totalCount={passes.data?.totalCount}
          onPageChange={setPage}
          size="small"
        />
      </Section>

      {issuing && (
        <IssuePassDialog
          onClose={() => setIssuing(false)}
          onIssued={(pass) => {
            setIssuing(false);
            print(pass);
          }}
        />
      )}
      <ConfirmDialog
        open={revoking !== null}
        title="Revoke this pass?"
        confirmLabel="Revoke"
        destructive
        busy={close.isPending}
        onConfirm={() => revoking && close.mutate({ pass: revoking, action: 'revoke' })}
        onCancel={() => setRevoking(null)}
      >
        {revoking?.visitorName} will no longer be allowed into the ward with pass {revoking?.passNumber}.
      </ConfirmDialog>
    </Page>
  );
}

function IssuePassDialog({ onClose, onIssued }: { onClose: () => void; onIssued: (pass: VisitorPassDto) => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [patient, setPatient] = useState<PickedPatient | null>(null);
  const [visitorName, setVisitorName] = useState('');
  const [phone, setPhone] = useState('');
  const [relation, setRelation] = useState('');
  const [idProofType, setIdProofType] = useState('');
  const [idProofNumber, setIdProofNumber] = useState('');
  const [purpose, setPurpose] = useState('');
  const [location, setLocation] = useState('');
  const [isAttendant, setIsAttendant] = useState(false);
  const [validHours, setValidHours] = useState(4);

  const phoneInvalid = phone.trim() !== '' && normalizePhone(phone) === null;

  const issue = useMutation({
    mutationFn: async (): Promise<VisitorPassDto> => {
      const body: IssueVisitorPassRequest = {
        patientId: patient?.id ?? null,
        visitorName: visitorName.trim(),
        phone: phone.trim() || null,
        relation: relation.trim() || null,
        idProofType: idProofType || null,
        idProofNumber: idProofNumber.trim() || null,
        purpose: purpose.trim() || null,
        location: location.trim() || null,
        isAttendant,
        validHours,
      };
      const created = await api.post<{ id: string; passNumber: string; validUntil: string }>(
        '/api/v1/front-office/visitor-passes',
        body,
      );
      // The server returns the identifiers; the rest of the printable pass comes from what was entered.
      return {
        id: created.id,
        passNumber: created.passNumber,
        patientId: body.patientId,
        patientName: patient?.fullName ?? null,
        patientUhid: patient?.uhid ?? null,
        visitorName: body.visitorName,
        phone: body.phone,
        relation: body.relation,
        idProofType: body.idProofType,
        idProofLast4: null,
        purpose: body.purpose,
        location: body.location,
        isAttendant: body.isAttendant,
        validFrom: new Date().toISOString(),
        validUntil: created.validUntil,
        status: 'Active',
        checkedOutAt: null,
      };
    },
    onSuccess: (pass) => {
      notify.success('Pass issued', pass.passNumber);
      void queryClient.invalidateQueries({ queryKey: ['front-office'] });
      onIssued(pass);
    },
    onError: (error) => notify.error(error, 'Could not issue the pass'),
  });

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 640 }}>
        <DialogBody>
          <DialogTitle>Issue visitor pass</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <PatientPicker
              value={patient}
              onChange={setPatient}
              label="Patient being visited"
              hint="Optional for general visitors"
            />
            <FormGrid columns={2}>
              <Field label="Visitor name" required>
                <Input value={visitorName} onChange={(_, d) => setVisitorName(d.value)} autoFocus />
              </Field>
              <Field
                label="Phone"
                validationMessage={phoneInvalid ? 'Enter a valid phone number.' : undefined}
                validationState={phoneInvalid ? 'error' : 'none'}
              >
                <Input type="tel" value={phone} onChange={(_, d) => setPhone(d.value)} />
              </Field>
              <Field label="Relation to patient">
                <Input value={relation} onChange={(_, d) => setRelation(d.value)} placeholder="e.g. Son" />
              </Field>
              <Field label="Ward / bed">
                <Input value={location} onChange={(_, d) => setLocation(d.value)} placeholder="e.g. Ward 3, bed 12" />
              </Field>
              <Field label="ID proof">
                <Select value={idProofType} onChange={(_, d) => setIdProofType(d.value)}>
                  <option value="">None</option>
                  {ID_PROOFS.map((p) => (
                    <option key={p} value={p}>
                      {p}
                    </option>
                  ))}
                </Select>
              </Field>
              <Field label="ID number" hint="Only the last 4 characters are kept">
                <Input value={idProofNumber} onChange={(_, d) => setIdProofNumber(d.value)} disabled={!idProofType} />
              </Field>
              <Field label="Purpose">
                <Input value={purpose} onChange={(_, d) => setPurpose(d.value)} />
              </Field>
              <Field label="Valid for">
                <Select value={String(validHours)} onChange={(_, d) => setValidHours(Number(d.value))}>
                  {[2, 4, 8, 12, 24, 48, 72, 168].map((h) => (
                    <option key={h} value={h}>
                      {h < 24 ? `${h} hours` : `${h / 24} day${h > 24 ? 's' : ''}`}
                    </option>
                  ))}
                </Select>
              </Field>
            </FormGrid>
            <Switch
              checked={isAttendant}
              onChange={(_, d) => {
                setIsAttendant(d.checked);
                setValidHours(d.checked ? 24 : 4);
              }}
              label="Attendant (stays with the in-patient)"
            />
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>Cancel</Button>
            <Button
              appearance="primary"
              disabled={!visitorName.trim() || phoneInvalid || issue.isPending}
              onClick={() => issue.mutate()}
            >
              Issue and print
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
