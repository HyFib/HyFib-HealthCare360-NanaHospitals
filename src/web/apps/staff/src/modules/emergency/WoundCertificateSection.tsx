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
  Input,
  MessageBar,
  MessageBarBody,
  Select,
  Text,
  Textarea,
} from '@fluentui/react-components';
import { AddRegular, DeleteRegular, PrintRegular } from '@fluentui/react-icons';
import { formatDateTime, printPrintableDocument } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useServerDocuments } from '@/lib/documents';
import { errorMessage } from '@/modules/lab/common/errors';
import { isoToLocal, localToIso } from '@/modules/quality/downtimeApi';
import {
  CLAUSE_LABELS,
  erApi,
  erKeys,
  INJURY_LABELS,
  injuryProblem,
  NATURE_LABELS,
  WEAPON_LABELS,
  type EmergencyCase,
  type GrievousClause,
  type InjuryKind,
  type InjuryNature,
  type InjuryWeapon,
  type WoundInjury,
} from './api';

const blank = (number: number): WoundInjury => ({
  number,
  kind: 'Laceration',
  site: '',
  size: '',
  description: '',
  weapon: 'Blunt',
  age: '',
  nature: 'Simple',
  clause: null,
  reservedFor: null,
});

/**
 * The wound certificate of a medico-legal case: each injury with its site, size, the weapon in the doctor's opinion, its
 * age and its nature in law (simple or grievous under the Bharatiya Nyaya Sanhita 2023, s.114–116, or the opinion reserved
 * until an X-ray or a specialist's opinion). The examining doctor signs it from their own sign-in, and then it does not
 * change. It prints, and the medico-legal report for a court is made from the record.
 */
export function WoundCertificateSection({
  c,
  canMlc,
  canReport,
}: {
  c: EmergencyCase;
  canMlc: boolean;
  canReport: boolean;
}) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const { print } = useServerDocuments();
  const w = c.woundCertificate ?? null;
  const [examinedAt, setExaminedAt] = useState(() => isoToLocal(w ? new Date(w.examinedAt) : new Date()));
  const [marks, setMarks] = useState(w?.identificationMarks ?? '');
  const [history, setHistory] = useState(w?.history ?? '');
  const [remarks, setRemarks] = useState(w?.remarks ?? '');
  const [injuries, setInjuries] = useState<WoundInjury[]>(w?.injuries.length ? w.injuries : [blank(1)]);
  const [reporting, setReporting] = useState(false);
  const refresh = () => void queryClient.invalidateQueries({ queryKey: erKeys.all });
  const editable = canMlc && !w?.isSigned;
  const problem = injuryProblem(injuries) ?? (marks.trim() ? null : 'Record the marks of identification.');

  const save = useMutation({
    mutationFn: () =>
      erApi.saveWoundCertificate(c.id, {
        examinedAt: localToIso(examinedAt)!,
        identificationMarks: marks.trim(),
        history: history.trim() || null,
        injuries: injuries.map((i) => ({
          ...i,
          size: i.size?.trim() || null,
          reservedFor: i.reservedFor?.trim() || null,
        })),
        remarks: remarks.trim() || null,
        version: w?.version ?? null,
      }),
    onSuccess: () => {
      notify.success('Wound certificate saved', 'Sign it once the injuries are as you found them.');
      refresh();
    },
  });
  const sign = useMutation({
    mutationFn: () => erApi.signWoundCertificate(c.id, w!.version),
    onSuccess: () => {
      notify.success(
        'Wound certificate signed',
        'It no longer changes; give a reserved opinion in a supplementary certificate.',
      );
      refresh();
    },
  });

  const change = (number: number, patch: Partial<WoundInjury>) =>
    setInjuries((all) => all.map((i) => (i.number === number ? { ...i, ...patch } : i)));
  const remove = (number: number) =>
    setInjuries((all) => all.filter((i) => i.number !== number).map((i, index) => ({ ...i, number: index + 1 })));

  return (
    <section aria-label="Wound certificate" style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
      <Text weight="semibold">Wound certificate</Text>
      {w?.isSigned && (
        <Caption1>
          Signed by {w.doctorName}
          {w.doctorRegistration ? ` (Reg. ${w.doctorRegistration})` : ''} on {formatDateTime(w.signedAt!)}. It no longer
          changes.
        </Caption1>
      )}
      {!w && !canMlc && <Caption1>No wound certificate yet.</Caption1>}
      {(w || canMlc) && (
        <>
          <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
            <Field label="Examined" required style={{ minWidth: 220 }}>
              <Input
                type="datetime-local"
                value={examinedAt}
                onChange={(_, d) => setExaminedAt(d.value)}
                disabled={!editable}
              />
            </Field>
            <Field label="Marks of identification" required style={{ flex: 1, minWidth: 260 }}>
              <Input value={marks} onChange={(_, d) => setMarks(d.value)} disabled={!editable} />
            </Field>
          </div>
          <Field label="Alleged history">
            <Textarea value={history} onChange={(_, d) => setHistory(d.value)} disabled={!editable} />
          </Field>
          {injuries.map((i) => (
            <fieldset key={i.number} style={{ border: '1px solid #ccc', borderRadius: 4, padding: 8 }}>
              <legend>Injury {i.number}</legend>
              <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
                <Field label="Injury" style={{ minWidth: 160 }}>
                  <Select
                    value={i.kind}
                    onChange={(_, d) => change(i.number, { kind: d.value as InjuryKind })}
                    disabled={!editable}
                  >
                    {Object.entries(INJURY_LABELS).map(([k, label]) => (
                      <option key={k} value={k}>
                        {label}
                      </option>
                    ))}
                  </Select>
                </Field>
                <Field label="Site" required style={{ flex: 1, minWidth: 200 }}>
                  <Input value={i.site} onChange={(_, d) => change(i.number, { site: d.value })} disabled={!editable} />
                </Field>
                <Field label="Size (cm)" style={{ width: 140 }}>
                  <Input
                    value={i.size ?? ''}
                    onChange={(_, d) => change(i.number, { size: d.value })}
                    disabled={!editable}
                  />
                </Field>
              </div>
              <Field label="Description" required>
                <Input
                  value={i.description}
                  onChange={(_, d) => change(i.number, { description: d.value })}
                  disabled={!editable}
                />
              </Field>
              <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
                <Field label="Weapon" style={{ minWidth: 160 }}>
                  <Select
                    value={i.weapon}
                    onChange={(_, d) => change(i.number, { weapon: d.value as InjuryWeapon })}
                    disabled={!editable}
                  >
                    {Object.entries(WEAPON_LABELS).map(([k, label]) => (
                      <option key={k} value={k}>
                        {label}
                      </option>
                    ))}
                  </Select>
                </Field>
                <Field label="Age of injury" required style={{ minWidth: 180 }}>
                  <Input value={i.age} onChange={(_, d) => change(i.number, { age: d.value })} disabled={!editable} />
                </Field>
                <Field label="Nature" style={{ minWidth: 190 }}>
                  <Select
                    value={i.nature}
                    onChange={(_, d) => change(i.number, { nature: d.value as InjuryNature })}
                    disabled={!editable}
                  >
                    {Object.entries(NATURE_LABELS).map(([k, label]) => (
                      <option key={k} value={k}>
                        {label}
                      </option>
                    ))}
                  </Select>
                </Field>
                {i.nature === 'Grievous' && (
                  <Field label="Kind of grievous hurt" required style={{ minWidth: 280 }}>
                    <Select
                      value={i.clause ?? ''}
                      onChange={(_, d) => change(i.number, { clause: (d.value || null) as GrievousClause | null })}
                      disabled={!editable}
                    >
                      <option value="">Choose (BNS s.116)</option>
                      {Object.entries(CLAUSE_LABELS).map(([k, label]) => (
                        <option key={k} value={k}>
                          {label}
                        </option>
                      ))}
                    </Select>
                  </Field>
                )}
                {i.nature === 'Reserved' && (
                  <Field label="Reserved pending" required style={{ minWidth: 220 }}>
                    <Input
                      value={i.reservedFor ?? ''}
                      onChange={(_, d) => change(i.number, { reservedFor: d.value })}
                      disabled={!editable}
                    />
                  </Field>
                )}
                {editable && injuries.length > 1 && (
                  <Button
                    aria-label={`Remove injury ${i.number}`}
                    icon={<DeleteRegular />}
                    appearance="subtle"
                    onClick={() => remove(i.number)}
                  />
                )}
              </div>
            </fieldset>
          ))}
          {editable && (
            <Button
              icon={<AddRegular />}
              onClick={() => setInjuries((all) => [...all, blank(all.length + 1)])}
              style={{ alignSelf: 'flex-start' }}
            >
              Add an injury
            </Button>
          )}
          <Field label="Remarks">
            <Textarea value={remarks} onChange={(_, d) => setRemarks(d.value)} disabled={!editable} />
          </Field>
          {editable && problem && <Caption1>{problem}</Caption1>}
          {(save.error ?? sign.error) && (
            <MessageBar intent="error">
              <MessageBarBody>
                {errorMessage(save.error ?? sign.error, 'The wound certificate could not be saved.')}
              </MessageBarBody>
            </MessageBar>
          )}
          <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
            {editable && (
              <Button appearance="primary" disabled={problem !== null || save.isPending} onClick={() => save.mutate()}>
                Save the certificate
              </Button>
            )}
            {editable && w && (
              <Button disabled={sign.isPending} onClick={() => sign.mutate()}>
                Sign as the examining doctor
              </Button>
            )}
            {w && (
              <Button icon={<PrintRegular />} onClick={() => print.mutate(erApi.woundCertificateDocument(c.id))}>
                Print the wound certificate
              </Button>
            )}
          </div>
        </>
      )}
      {c.mlc && canReport && (
        <Button style={{ alignSelf: 'flex-start' }} onClick={() => setReporting(true)}>
          Medico-legal report for a court…
        </Button>
      )}
      {reporting && <CourtReportDialog caseId={c.id} onClose={() => setReporting(false)} />}
    </section>
  );
}

/** The court (or the police, on requisition) the report goes to: printed from the MLC record, its issue audited. */
function CourtReportDialog({ caseId, onClose }: { caseId: string; onClose: () => void }) {
  const notify = useNotify();
  const [court, setCourt] = useState('');
  const [reference, setReference] = useState('');
  const [requisition, setRequisition] = useState('');
  const make = useMutation({
    mutationFn: async () => {
      const document = await erApi.courtReport(
        caseId,
        court.trim(),
        reference.trim() || null,
        requisition.trim() || null,
      );
      await printPrintableDocument(document);
    },
    onSuccess: () => {
      notify.success('Medico-legal report made', 'Its issue is on the patient’s record with the court’s name.');
      onClose();
    },
  });

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>Medico-legal report for a court</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
            <Caption1>
              Made from the MLC record: the facts as registered, the police intimation, the signed wound certificate and
              the outcome in the emergency department.
            </Caption1>
            <Field label="Court (or authority)" required>
              <Input value={court} onChange={(_, d) => setCourt(d.value)} />
            </Field>
            <Field label="Case number">
              <Input value={reference} onChange={(_, d) => setReference(d.value)} />
            </Field>
            <Field label="In reply to (summons or requisition)">
              <Input value={requisition} onChange={(_, d) => setRequisition(d.value)} />
            </Field>
            {make.error && (
              <MessageBar intent="error">
                <MessageBarBody>{errorMessage(make.error, 'The report could not be made.')}</MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>Cancel</Button>
            <Button appearance="primary" disabled={!court.trim() || make.isPending} onClick={() => make.mutate()}>
              Make and print
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
