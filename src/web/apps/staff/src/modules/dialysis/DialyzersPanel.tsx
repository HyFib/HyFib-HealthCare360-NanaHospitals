import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  makeStyles,
  ProgressBar,
  Spinner,
  Switch,
  Text,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, DeleteRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatDate } from '@hc360/core';
import { DataTable, FormGrid, NumberField, Section, StatusBadge, TextField, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { applyFormErrors } from '@/modules/ot/shared/errors';
import { ActionError, ReasonDialog } from '@/modules/ot/shared/feedback';
import { dialysisApi, dialysisKeys, dialysisQueries, type DialysisProfileDto, type DialyzerDto } from './api';

const useStyles = makeStyles({
  uses: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: '120px' },
  muted: { color: tokens.colorNeutralForeground3 },
  mono: { fontFamily: tokens.fontFamilyMonospace },
  toolbar: { display: 'flex', gap: '12px', alignItems: 'center', flexWrap: 'wrap' },
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
});

/**
 * The patient's own dialyzers (DIA-04): uses so far and left, and discarding. A dialyzer is never shared between
 * patients; it is discarded automatically when it reaches its maximum uses.
 */
export function DialyzersPanel({
  patientId,
  profile,
  canManage,
}: {
  patientId: string;
  profile: DialysisProfileDto | null;
  canManage: boolean;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [showDiscarded, setShowDiscarded] = useState(false);
  const [registering, setRegistering] = useState(false);
  const [discarding, setDiscarding] = useState<DialyzerDto | null>(null);
  const dialyzers = useQuery(dialysisQueries.dialyzers(patientId, showDiscarded));

  const discard = useMutation({
    mutationFn: ({ id, reason }: { id: string; reason: string }) => dialysisApi.discardDialyzer(id, reason),
    onSuccess: () => {
      setDiscarding(null);
      notify.success('Dialyzer discarded');
      void queryClient.invalidateQueries({ queryKey: ['dialysis', 'dialyzers', patientId] });
    },
  });

  return (
    <Section
      title="Dialyzers"
      description={
        profile
          ? profile.effectiveMaxDialyzerUses === 1
            ? 'Single use for this patient: a new dialyzer every session.'
            : `Reused for this patient only, up to ${profile.effectiveMaxDialyzerUses} uses each.`
          : undefined
      }
      actions={
        <div className={styles.toolbar}>
          <Switch checked={showDiscarded} onChange={(_, d) => setShowDiscarded(d.checked)} label="Show discarded" />
          {canManage && profile && (
            <Button icon={<AddRegular />} onClick={() => setRegistering(true)}>
              Register dialyzer
            </Button>
          )}
        </div>
      }
      flush
    >
      <DataTable
        ariaLabel="Dialyzers"
        rows={dialyzers.data}
        loading={dialyzers.isLoading}
        error={dialyzers.error}
        onRetry={() => void dialyzers.refetch()}
        getRowId={(d) => d.id}
        size="small"
        columns={[
          { key: 'serial', header: 'Serial', render: (d) => <Text className={styles.mono}>{d.serialNumber}</Text> },
          {
            key: 'type',
            header: 'Dialyzer',
            render: (d) =>
              [d.brand, d.model, d.surfaceAreaM2 ? `${d.surfaceAreaM2} m²` : null].filter(Boolean).join(' · ') || '—',
          },
          {
            key: 'uses',
            header: 'Uses',
            render: (d) => (
              <span className={styles.uses}>
                <Text>
                  {d.usesCount} of {d.maxUses} used
                </Text>
                <ProgressBar
                  value={d.maxUses ? d.usesCount / d.maxUses : 0}
                  color={d.remainingUses === 0 ? 'error' : 'brand'}
                  aria-label={`${d.usesCount} of ${d.maxUses} uses`}
                />
              </span>
            ),
          },
          {
            key: 'last',
            header: 'Last used',
            priority: 'low',
            render: (d) => (d.lastUsedAt ? formatDate(d.lastUsedAt) : 'Not used yet'),
          },
          {
            key: 'status',
            header: 'Status',
            render: (d) =>
              d.isDiscarded ? (
                <span>
                  <StatusBadge status="Discarded" />
                  {d.discardReason && (
                    <Caption1 block className={styles.muted}>
                      {d.discardReason}
                    </Caption1>
                  )}
                </span>
              ) : (
                <StatusBadge
                  status="Active"
                  tone="success"
                  label={`${d.remainingUses} use${d.remainingUses === 1 ? '' : 's'} left`}
                />
              ),
          },
        ]}
        rowActions={
          canManage
            ? (d) =>
                d.isDiscarded ? null : (
                  <Button
                    size="small"
                    appearance="subtle"
                    icon={<DeleteRegular />}
                    aria-label={`Discard dialyzer ${d.serialNumber}`}
                    onClick={() => setDiscarding(d)}
                  />
                )
            : undefined
        }
        emptyTitle="No dialyzers"
        emptyDescription={canManage ? 'Register one here, or at the chair when starting a session.' : undefined}
      />
      {registering && <RegisterDialyzerDialog patientId={patientId} onClose={() => setRegistering(false)} />}
      <ReasonDialog
        open={discarding !== null}
        title={`Discard dialyzer ${discarding?.serialNumber ?? ''}?`}
        description="A discarded dialyzer can never be used again."
        confirmLabel="Discard"
        destructive
        busy={discard.isPending}
        error={discard.error}
        presets={['Failed pressure or leak test', 'Fibre bundle volume too low', 'Visible clotting', 'Damaged housing']}
        onConfirm={(reason) => discarding && discard.mutate({ id: discarding.id, reason })}
        onCancel={() => {
          setDiscarding(null);
          discard.reset();
        }}
      />
    </Section>
  );
}

const registerSchema = z
  .object({
    serialNumber: z.string().trim().min(1, 'Scan or type the serial number.').max(64),
    brand: z.string().trim().max(100),
    model: z.string().trim().max(100),
    surfaceAreaM2: z.number().nullable(),
  })
  .superRefine((v, ctx) => {
    if (v.surfaceAreaM2 !== null && (v.surfaceAreaM2 < 0.1 || v.surfaceAreaM2 > 3)) {
      ctx.addIssue({ code: 'custom', path: ['surfaceAreaM2'], message: 'Between 0.1 and 3 m².' });
    }
  });

type RegisterValues = z.infer<typeof registerSchema>;

function RegisterDialyzerDialog({ patientId, onClose }: { patientId: string; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<RegisterValues>({
    resolver: zodResolver(registerSchema),
    defaultValues: { serialNumber: '', brand: '', model: '', surfaceAreaM2: null },
  });
  const register = useMutation({
    mutationFn: (v: RegisterValues) =>
      dialysisApi.registerDialyzer(patientId, {
        serialNumber: v.serialNumber.toUpperCase(),
        brand: v.brand || null,
        model: v.model || null,
        surfaceAreaM2: v.surfaceAreaM2,
      }),
    onSuccess: (dialyzer) => {
      notify.success(
        `Dialyzer ${dialyzer.serialNumber} registered`,
        `Up to ${dialyzer.maxUses} use${dialyzer.maxUses === 1 ? '' : 's'} for this patient.`,
      );
      void queryClient.invalidateQueries({ queryKey: ['dialysis', 'dialyzers', patientId] });
      void queryClient.invalidateQueries({ queryKey: dialysisKeys.profile(patientId) });
      onClose();
    },
    onError: (error) => {
      applyFormErrors(error, form.setError);
    },
  });

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <form onSubmit={form.handleSubmit((v) => register.mutate(v))} noValidate>
          <DialogBody>
            <DialogTitle>Register a dialyzer</DialogTitle>
            <DialogContent className={styles.body}>
              <Text>The dialyzer is labelled with the patient&apos;s name and used for this patient only.</Text>
              <FormGrid columns={2}>
                <TextField
                  control={form.control}
                  name="serialNumber"
                  label="Serial number"
                  required
                  autoFocus
                  maxLength={64}
                />
                <NumberField
                  control={form.control}
                  name="surfaceAreaM2"
                  label="Surface area (m²)"
                  min={0.1}
                  max={3}
                  step={0.1}
                />
                <TextField control={form.control} name="brand" label="Brand" maxLength={100} />
                <TextField control={form.control} name="model" label="Model" maxLength={100} />
              </FormGrid>
              <ActionError error={register.error} />
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={register.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={register.isPending}
                icon={register.isPending ? <Spinner size="tiny" /> : undefined}
              >
                Register
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
