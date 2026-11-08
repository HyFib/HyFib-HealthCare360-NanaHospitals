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
  MessageBarTitle,
  Select,
  Spinner,
  Textarea,
} from '@fluentui/react-components';
import { AddRegular, PrintRegular } from '@fluentui/react-icons';
import { formatDateTime, todayIso } from '@hc360/core';
import { DataTable, FilterBar, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { errorMessage } from '@/modules/lab/common/errors';
import {
  ACCIDENT_KINDS,
  ACCIDENT_LABEL,
  BMW_PERMISSIONS,
  bmwApi,
  bmwKeys,
  bmwQueries,
  WASTE_CATEGORIES,
  type RecordAccidentRequest,
  type WasteAccident,
  type WasteAccidentKind,
  type WasteCategory,
} from './api';
import { printFormI } from './print';

/**
 * Accidents in handling bio-medical waste (needle-stick injuries, spills, exposures), each reported on Form I of the
 * BMW Management Rules 2016 to the prescribed authority and counted in the annual report (Form IV). Anyone who weighs
 * bags records one; the waste officer prints Form I to sign and send.
 */
export function AccidentsPanel() {
  const notify = useNotify();
  const canManage = usePermission(BMW_PERMISSIONS.manage);
  const [year, setYear] = useState(Number(todayIso().slice(0, 4)));
  const [recording, setRecording] = useState(false);
  const accidents = useQuery({ ...bmwQueries.accidents(year), enabled: canManage });
  const formI = useMutation({
    mutationFn: async (id: string) => printFormI(await bmwApi.formI(id)),
    onError: (error) => notify.error(error, 'Form I could not be printed'),
  });

  return (
    <Section
      flush
      actions={
        <Button appearance="primary" icon={<AddRegular />} onClick={() => setRecording(true)}>
          Report an accident
        </Button>
      }
    >
      {canManage ? (
        <>
          <FilterBar>
            <Field label="Year">
              <Input
                type="number"
                min={2000}
                value={String(year)}
                onChange={(_, d) => Number.isInteger(Number(d.value)) && setYear(Number(d.value))}
              />
            </Field>
          </FilterBar>
          <DataTable<WasteAccident>
            ariaLabel="Accidents reported"
            rows={accidents.data}
            loading={accidents.isLoading}
            error={accidents.error}
            onRetry={() => void accidents.refetch()}
            getRowId={(a) => a.id}
            size="small"
            columns={[
              { key: 'when', header: 'When', width: 170, render: (a) => formatDateTime(a.occurredAt) },
              {
                key: 'what',
                header: 'What happened',
                render: (a) => (
                  <span>
                    {ACCIDENT_LABEL[a.kind]} · {a.place}
                    <Caption1 block>{a.sequence}</Caption1>
                  </span>
                ),
              },
              { key: 'persons', header: 'Affected', width: 90, render: (a) => a.personsAffected },
              { key: 'by', header: 'Reported by', width: 160, render: (a) => a.reportedByName },
            ]}
            rowActions={(a) => (
              <Button
                size="small"
                icon={<PrintRegular />}
                disabled={formI.isPending}
                onClick={() => formI.mutate(a.id)}
              >
                Form I
              </Button>
            )}
            emptyTitle="No accidents reported this year"
          />
        </>
      ) : (
        <Caption1 style={{ padding: 16, display: 'block' }}>
          Report a needle-stick injury, a spill or an exposure as soon as it happens. The waste officer sends Form I to
          the State Pollution Control Board.
        </Caption1>
      )}
      {recording && <RecordAccidentDialog onClose={() => setRecording(false)} />}
    </Section>
  );
}

const nowLocal = () => {
  const now = new Date(Date.now() - new Date().getTimezoneOffset() * 60_000);
  return now.toISOString().slice(0, 16);
};

/** What Form I asks, item by item; the reporter's name and the date of reporting are added by the server. */
export function RecordAccidentDialog({ onClose }: { onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [form, setForm] = useState({
    occurredAt: nowLocal(),
    kind: 'NeedleStick' as WasteAccidentKind,
    place: '',
    sequence: '',
    category: '' as WasteCategory | '',
    wasteInvolved: '',
    personsAffected: '1',
    effects: '',
    emergencyMeasures: '',
    stepsToAlleviate: '',
    stepsToPrevent: '',
    designation: '',
  });
  const [tried, setTried] = useState(false);
  const set = (key: keyof typeof form) => (value: string) => setForm((f) => ({ ...f, [key]: value }));
  const missing = (['place', 'sequence', 'wasteInvolved', 'effects', 'emergencyMeasures'] as const).filter(
    (k) => !form[k].trim(),
  );
  const save = useMutation({
    mutationFn: () => {
      const request: RecordAccidentRequest = {
        occurredAt: new Date(form.occurredAt).toISOString(),
        kind: form.kind,
        place: form.place.trim(),
        sequence: form.sequence.trim(),
        category: form.category || null,
        wasteInvolved: form.wasteInvolved.trim(),
        personsAffected: Number(form.personsAffected) || 0,
        effects: form.effects.trim(),
        emergencyMeasures: form.emergencyMeasures.trim(),
        stepsToAlleviate: form.stepsToAlleviate.trim() || null,
        stepsToPrevent: form.stepsToPrevent.trim() || null,
        authorityInformedAt: null,
        designation: form.designation.trim() || null,
      };
      return bmwApi.recordAccident(request);
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: bmwKeys.all });
      notify.success('Accident reported', 'The waste officer files Form I with the State Pollution Control Board.');
      onClose();
    },
  });

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !save.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 680 }}>
        <form
          onSubmit={(e) => {
            e.preventDefault();
            setTried(true);
            if (missing.length === 0 && !save.isPending) {
              save.mutate();
            }
          }}
          noValidate
        >
          <DialogBody>
            <DialogTitle>Report an accident (Form I)</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              {save.error && (
                <MessageBar intent="error">
                  <MessageBarBody>
                    <MessageBarTitle>Not recorded</MessageBarTitle>
                    {errorMessage(save.error, 'The accident could not be recorded.')}
                  </MessageBarBody>
                </MessageBar>
              )}
              <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(200px, 1fr))', gap: 12 }}>
                <Field label="When it happened" required>
                  <Input
                    type="datetime-local"
                    value={form.occurredAt}
                    onChange={(_, d) => set('occurredAt')(d.value)}
                  />
                </Field>
                <Field label="Type of accident" required>
                  <Select value={form.kind} onChange={(_, d) => set('kind')(d.value)}>
                    {ACCIDENT_KINDS.map((k) => (
                      <option key={k} value={k}>
                        {ACCIDENT_LABEL[k]}
                      </option>
                    ))}
                  </Select>
                </Field>
                <Field
                  label="Where"
                  required
                  validationMessage={tried && !form.place.trim() ? 'Say where it happened.' : undefined}
                >
                  <Input value={form.place} maxLength={200} onChange={(_, d) => set('place')(d.value)} />
                </Field>
                <Field label="Waste category">
                  <Select value={form.category} onChange={(_, d) => set('category')(d.value)}>
                    <option value="">Not known</option>
                    {WASTE_CATEGORIES.map((c) => (
                      <option key={c} value={c}>
                        {c}
                      </option>
                    ))}
                  </Select>
                </Field>
              </div>
              <Field
                label="Sequence of events leading to the accident"
                required
                validationMessage={tried && !form.sequence.trim() ? 'Say what led to the accident.' : undefined}
              >
                <Textarea value={form.sequence} maxLength={2000} onChange={(_, d) => set('sequence')(d.value)} />
              </Field>
              <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(200px, 1fr))', gap: 12 }}>
                <Field
                  label="The waste involved"
                  required
                  validationMessage={tried && !form.wasteInvolved.trim() ? 'Say which waste was involved.' : undefined}
                >
                  <Input
                    value={form.wasteInvolved}
                    maxLength={500}
                    onChange={(_, d) => set('wasteInvolved')(d.value)}
                  />
                </Field>
                <Field label="Persons affected" required>
                  <Input
                    type="number"
                    min={0}
                    value={form.personsAffected}
                    onChange={(_, d) => set('personsAffected')(d.value)}
                  />
                </Field>
              </div>
              <Field
                label="Effects on people's health and the environment"
                required
                validationMessage={tried && !form.effects.trim() ? 'Assess the effects.' : undefined}
              >
                <Textarea value={form.effects} maxLength={2000} onChange={(_, d) => set('effects')(d.value)} />
              </Field>
              <Field
                label="Emergency measures taken"
                required
                validationMessage={tried && !form.emergencyMeasures.trim() ? 'Say what was done at once.' : undefined}
              >
                <Textarea
                  value={form.emergencyMeasures}
                  maxLength={2000}
                  onChange={(_, d) => set('emergencyMeasures')(d.value)}
                />
              </Field>
              <Field label="Steps taken to alleviate the effects">
                <Textarea
                  value={form.stepsToAlleviate}
                  maxLength={2000}
                  onChange={(_, d) => set('stepsToAlleviate')(d.value)}
                />
              </Field>
              <Field label="Steps taken to prevent it happening again">
                <Textarea
                  value={form.stepsToPrevent}
                  maxLength={2000}
                  onChange={(_, d) => set('stepsToPrevent')(d.value)}
                />
              </Field>
              <Field label="Your designation">
                <Input value={form.designation} maxLength={120} onChange={(_, d) => set('designation')(d.value)} />
              </Field>
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={save.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={save.isPending}
                icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                Report
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
