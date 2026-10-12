import {
  Badge,
  Button,
  Caption1,
  Field,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Select,
  SpinButton,
  Spinner,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  tokens,
  Tooltip,
} from '@fluentui/react-components';
import { AddRegular, DeleteRegular, SaveRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatInr, isApiError, useHotkey } from '@hc360/core';
import {
  applyServerErrors,
  ChoiceField,
  FormGrid,
  NumberField,
  Section,
  SwitchField,
  TextAreaField,
  TextField,
  useLeaveGuard,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { useDepartments } from '@/lib/lookups';
import { serviceCategoryLabel, type ServiceCategory } from '@/modules/emr/orderCart';
import { ServicePicker } from '@/modules/emr/ServicePicker';
import { checkupApi, checkupKeys, type PackageDto, type PackageGender, type PackageItemKind } from './api';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '16px' },
  addRow: {
    display: 'grid',
    gridTemplateColumns: 'minmax(240px, 1fr) minmax(200px, 280px) auto',
    gap: '8px',
    alignItems: 'end',
    '@media (max-width: 900px)': { gridTemplateColumns: '1fr' },
  },
  muted: { color: tokens.colorNeutralForeground3 },
  footer: {
    position: 'sticky',
    bottom: 0,
    display: 'flex',
    justifyContent: 'flex-end',
    alignItems: 'center',
    gap: '8px',
    padding: '12px 16px',
    backgroundColor: tokens.colorNeutralBackground1,
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
    boxShadow: tokens.shadow8,
    borderRadius: tokens.borderRadiusLarge,
    zIndex: 1,
  },
});

const headerSchema = z
  .object({
    code: z
      .string()
      .trim()
      .min(1, 'Enter the package code.')
      .max(32)
      .transform((v) => v.toUpperCase())
      .refine((v) => /^HC-[A-Z0-9-]+$/.test(v), 'Package codes look like HC-EXEC.'),
    name: z.string().trim().min(1, 'Enter the package name.').max(200),
    description: z.string().max(1000),
    gender: z.enum(['Any', 'Male', 'Female']),
    minAgeYears: z.number().int().min(0).max(120).nullable(),
    maxAgeYears: z.number().int().min(0).max(130).nullable(),
    price: z
      .number({ error: 'Enter the package price.' })
      .min(0, 'The price cannot be negative.')
      .max(9_999_999)
      .nullable(),
    isActive: z.boolean(),
  })
  .superRefine((v, ctx) => {
    if (v.minAgeYears !== null && v.maxAgeYears !== null && v.maxAgeYears < v.minAgeYears) {
      ctx.addIssue({
        code: 'custom',
        path: ['maxAgeYears'],
        message: 'Maximum age must not be below the minimum age.',
      });
    }
    if (v.price === null) {
      ctx.addIssue({ code: 'custom', path: ['price'], message: 'Enter the package price.' });
    }
  });

type HeaderValues = z.input<typeof headerSchema>;

interface PackageLine {
  key: string;
  kind: PackageItemKind;
  serviceItemId: string | null;
  departmentCode: string | null;
  name: string;
  code: string;
  category: ServiceCategory | null;
  gender: PackageGender;
  minAgeYears: number | null;
}

function linesFrom(pkg: PackageDto | null): PackageLine[] {
  return (pkg?.items ?? [])
    .toSorted((a, b) => a.sequence - b.sequence)
    .map((i) => ({
      key: i.id,
      kind: i.kind,
      serviceItemId: i.serviceItemId,
      departmentCode: i.departmentCode,
      name: i.name,
      code: i.serviceCode,
      category: i.category,
      gender: i.gender,
      minAgeYears: i.minAgeYears,
    }));
}

/**
 * Health package editor (MHC-01): who it is for (sex, age), the price and each test or consultation — optionally only
 * for one sex or above an age (e.g. PSA for men over 50). Saving also registers the package as a billable service.
 */
export function PackageForm({ pkg, onSaved }: { pkg: PackageDto | null; onSaved: (saved: PackageDto) => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const departments = useDepartments('Clinical');
  const [current, setCurrent] = useState<PackageDto | null>(pkg);
  const [lines, setLines] = useState<PackageLine[]>(() => linesFrom(pkg));
  const [consultDept, setConsultDept] = useState('');
  const [linesChanged, setLinesChanged] = useState(false);
  const form = useForm<HeaderValues>({
    resolver: zodResolver(headerSchema),
    defaultValues: {
      code: pkg?.code ?? 'HC-',
      name: pkg?.name ?? '',
      description: pkg?.description ?? '',
      gender: pkg?.gender ?? 'Any',
      minAgeYears: pkg?.minAgeYears ?? null,
      maxAgeYears: pkg?.maxAgeYears ?? null,
      price: pkg?.price ?? null,
      isActive: pkg?.isActive ?? true,
    },
  });

  const changeLines = (next: PackageLine[]) => {
    setLines(next);
    setLinesChanged(true);
  };

  const updateLine = (key: string, patch: Partial<PackageLine>) =>
    changeLines(lines.map((l) => (l.key === key ? { ...l, ...patch } : l)));

  const save = useMutation({
    mutationFn: (values: HeaderValues) =>
      checkupApi.savePackage(current?.id ?? null, {
        code: values.code.trim().toUpperCase(),
        name: values.name.trim(),
        description: values.description.trim() || null,
        gender: values.gender,
        minAgeYears: values.minAgeYears,
        maxAgeYears: values.maxAgeYears,
        price: values.price ?? 0,
        isActive: values.isActive,
        items: lines.map((l) => ({
          kind: l.kind,
          serviceItemId: l.kind === 'Investigation' ? l.serviceItemId : null,
          departmentCode: l.kind === 'Consultation' ? l.departmentCode : null,
          gender: l.gender,
          minAgeYears: l.minAgeYears,
        })),
        version: current?.version ?? null,
      }),
    onSuccess: (saved) => {
      setCurrent(saved);
      setLinesChanged(false);
      form.reset(form.getValues());
      void queryClient.invalidateQueries({ queryKey: checkupKeys.all });
      notify.success(`${saved.code} saved`, saved.serviceItemId ? 'It can be booked and billed.' : undefined);
      onSaved(saved);
    },
    onError: (error) => {
      if (isApiError(error) && error.isConflict) {
        notify.error(error, 'The package was changed by someone else');
      } else if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the package');
      }
    },
  });

  const submit = form.handleSubmit((values) => {
    if (lines.length === 0) {
      notify.warning('Add at least one test or consultation');
      return;
    }
    save.mutate(values);
  });

  useHotkey('mod+s', () => !save.isPending && void submit(), { allowInInputs: true });

  const departmentOptions = departments.data ?? [];
  const departmentName = (code: string | null) => departmentOptions.find((d) => d.code === code)?.name ?? code ?? '—';

  // Leaving the screen or closing the window with unsaved changes asks first (SRS UX-08).
  useLeaveGuard({ dirty: form.formState.isDirty || linesChanged, busy: save.isPending });

  return (
    <form onSubmit={submit} noValidate className={styles.stack}>
      {current && !current.serviceItemId && (
        <MessageBar intent="warning">
          <MessageBarBody>
            This package has no billing item yet, so check-in is refused. Save it once to register it for billing.
          </MessageBarBody>
        </MessageBar>
      )}
      <Section title="Package">
        <FormGrid columns={3}>
          <TextField
            control={form.control}
            name="code"
            label="Code"
            required
            maxLength={32}
            hint="e.g. HC-EXEC"
            autoFocus={!pkg}
          />
          <TextField control={form.control} name="name" label="Name" required maxLength={200} />
          <NumberField
            control={form.control}
            name="price"
            label="Price (₹, incl. GST if any)"
            required
            min={0}
            step={1}
          />
          <ChoiceField
            control={form.control}
            name="gender"
            label="For"
            options={[
              { value: 'Any', label: 'Everyone' },
              { value: 'Male', label: 'Men' },
              { value: 'Female', label: 'Women' },
            ]}
          />
          <NumberField control={form.control} name="minAgeYears" label="Minimum age (years)" min={0} max={120} />
          <NumberField control={form.control} name="maxAgeYears" label="Maximum age (years)" min={0} max={130} />
        </FormGrid>
        <TextAreaField
          control={form.control}
          name="description"
          label="Description for patients"
          rows={2}
          maxLength={1000}
        />
        <SwitchField control={form.control} name="isActive" label="Offered for booking" />
      </Section>

      <Section
        title={`Tests and consultations (${lines.length})`}
        description="Each line becomes a station on the check-up day. Limit a line by sex or age where it applies."
      >
        <div className={styles.stack}>
          <div className={styles.addRow}>
            <ServicePicker
              label="Add a test"
              categories={['Laboratory', 'Radiology', 'Cardiology', 'Procedure', 'Physiotherapy', 'Dietary']}
              onPick={(service) => {
                if (lines.some((l) => l.serviceItemId === service.id)) {
                  notify.info(`${service.name} is already in the package`);
                  return;
                }
                changeLines([
                  ...lines,
                  {
                    key: `new-${service.id}`,
                    kind: 'Investigation',
                    serviceItemId: service.id,
                    departmentCode: null,
                    name: service.name,
                    code: service.code,
                    category: service.category,
                    gender: 'Any',
                    minAgeYears: null,
                  },
                ]);
              }}
            />
            <Field label="Or a consultation in">
              <Select value={consultDept} onChange={(_, d) => setConsultDept(d.value)}>
                <option value="">Choose department</option>
                {departmentOptions.map((d) => (
                  <option key={d.id} value={d.code}>
                    {d.name}
                  </option>
                ))}
              </Select>
            </Field>
            <Button
              icon={<AddRegular />}
              disabled={!consultDept}
              onClick={() => {
                changeLines([
                  ...lines,
                  {
                    key: `new-consult-${consultDept}-${lines.length}`,
                    kind: 'Consultation',
                    serviceItemId: null,
                    departmentCode: consultDept,
                    name: `Consultation: ${departmentName(consultDept)}`,
                    code: 'CONS-NEW',
                    category: 'Consultation',
                    gender: 'Any',
                    minAgeYears: null,
                  },
                ]);
                setConsultDept('');
              }}
            >
              Add consultation
            </Button>
          </div>
          {lines.length === 0 ? (
            <Text className={styles.muted}>
              No tests yet. Search above to add laboratory, imaging and cardiology tests.
            </Text>
          ) : (
            <Table size="small" aria-label="Package lines">
              <TableHeader>
                <TableRow>
                  <TableHeaderCell>Test or consultation</TableHeaderCell>
                  <TableHeaderCell style={{ width: 130 }}>Type</TableHeaderCell>
                  <TableHeaderCell style={{ width: 140 }}>Only for</TableHeaderCell>
                  <TableHeaderCell style={{ width: 130 }}>From age</TableHeaderCell>
                  <TableHeaderCell style={{ width: 50 }} aria-label="Remove" />
                </TableRow>
              </TableHeader>
              <TableBody>
                {lines.map((line) => (
                  <TableRow key={line.key}>
                    <TableCell>
                      <Text weight="semibold">
                        {line.kind === 'Consultation'
                          ? `Consultation: ${departmentName(line.departmentCode)}`
                          : line.name}
                      </Text>
                      <Caption1 block className={styles.muted}>
                        {line.code}
                      </Caption1>
                    </TableCell>
                    <TableCell>
                      {line.kind === 'Consultation' ? (
                        <Badge appearance="tint" color="brand">
                          Consultation
                        </Badge>
                      ) : (
                        <Badge appearance="tint">{line.category ? serviceCategoryLabel[line.category] : 'Test'}</Badge>
                      )}
                    </TableCell>
                    <TableCell>
                      <Select
                        size="small"
                        value={line.gender}
                        onChange={(_, d) => updateLine(line.key, { gender: d.value as PackageGender })}
                        aria-label={`Who gets ${line.name}`}
                      >
                        <option value="Any">Everyone</option>
                        <option value="Male">Men</option>
                        <option value="Female">Women</option>
                      </Select>
                    </TableCell>
                    <TableCell>
                      <SpinButton
                        size="small"
                        min={0}
                        max={120}
                        value={line.minAgeYears}
                        placeholder="Any"
                        onChange={(_, d) => {
                          const raw =
                            d.value ??
                            (d.displayValue === '' || d.displayValue === undefined ? null : Number(d.displayValue));
                          updateLine(line.key, {
                            minAgeYears:
                              raw === null || Number.isNaN(raw) ? null : Math.max(0, Math.min(120, Math.round(raw))),
                          });
                        }}
                        aria-label={`Minimum age for ${line.name}`}
                      />
                    </TableCell>
                    <TableCell>
                      <Tooltip content="Remove" relationship="label">
                        <Button
                          size="small"
                          appearance="subtle"
                          icon={<DeleteRegular />}
                          onClick={() => changeLines(lines.filter((l) => l.key !== line.key))}
                        />
                      </Tooltip>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </div>
      </Section>

      <div className={styles.footer}>
        {current && <Caption1 className={styles.muted}>Saved price {formatInr(current.price)}</Caption1>}
        {(form.formState.isDirty || linesChanged) && <Caption1 className={styles.muted}>Unsaved changes</Caption1>}
        <Button
          type="submit"
          appearance="primary"
          icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : <SaveRegular />}
          disabled={save.isPending}
        >
          Save package (Ctrl+S)
        </Button>
      </div>
    </form>
  );
}
