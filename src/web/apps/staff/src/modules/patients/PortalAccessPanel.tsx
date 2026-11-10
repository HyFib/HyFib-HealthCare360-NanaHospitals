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
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  Subtitle2,
  Text,
  tokens,
} from '@fluentui/react-components';
import { PeopleAddRegular, ShieldKeyholeRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatDateTime, formatPhone, isApiError } from '@hc360/core';
import {
  ChoiceField,
  CloseRequestScope,
  DataTable,
  EmptyState,
  SelectField,
  StatusBadge,
  TextField,
  useCloseRequests,
  useNotify,
  useUnsavedChanges,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { Controller, useForm, useWatch } from 'react-hook-form';
import { usePermission } from '@/lib/permissions';
import { ReasonDialog } from '@/modules/inpatient/ReasonDialog';
import {
  patientKeys,
  patientQueries,
  portalFamilyApi,
  type FamilyDeskCodeSent,
  type LinkFamilyMemberRequest,
  type PortalFamilyLinkDto,
} from './api';
import {
  endedText,
  linkSourceText,
  ownNumberText,
  PORTAL_ACCESS_LIMITS,
  relationLabel,
  RELATION_OPTIONS,
  shareFormDefaults,
  shareFormSchema,
  shareServerErrors,
  toLinkRequest,
  type ShareFormValues,
} from './portalAccess';
import { PortalLinksSection } from './PortalLinksSection';

const useStyles = makeStyles({
  heading: { margin: '0 0 8px' },
  intro: { display: 'flex', flexDirection: 'column', gap: '4px', marginBottom: '12px', maxWidth: '72ch' },
  toolbar: { display: 'flex', gap: '12px', alignItems: 'center', flexWrap: 'wrap', marginBottom: '12px' },
  cell: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-start',
    gap: '2px',
    paddingBlock: '6px',
    whiteSpace: 'normal',
    overflowWrap: 'anywhere',
  },
  muted: { color: tokens.colorNeutralForeground3 },
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
});

interface Ending {
  link: PortalFamilyLinkDto;
  open: boolean;
}

/**
 * Who sees the patient's records in the patient portal (PP-01): everyone signed in with the patient's own number, and
 * family members on other numbers. Staff with `patients.consents` share the records with a family member's number once
 * they have checked who both people are and the patient (or their guardian) has agreed, and stop sharing when asked.
 * Links made in the portal, with a code sent to the patient's number, show here too. An ended link stays as history.
 * Above them, the links to the portal sent to the patient's own number (PortalLinksSection).
 */
export function PortalAccessPanel({ patientId }: { patientId: string }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canRead = usePermission('patients.read');
  const canManage = usePermission('patients.consents');
  const access = useQuery({ ...patientQueries.portalFamily(patientId), enabled: canRead });
  const [sharing, setSharing] = useState(false);
  const [ending, setEnding] = useState<Ending | null>(null);
  // The link is kept while the dialog closes, so its text does not change during the animation.
  const closeEnding = () => setEnding((current) => current && { ...current, open: false });

  const revoke = useMutation({
    mutationFn: ({ link, reason }: { link: PortalFamilyLinkDto; reason: string }) =>
      portalFamilyApi.revoke(link.id, reason),
    onSuccess: (_, { link }) => {
      notify.success(
        'Sharing stopped',
        `${formatPhone(link.accountPhone)} no longer sees the patient’s records in the portal.`,
      );
      closeEnding();
      void queryClient.invalidateQueries({ queryKey: patientKeys.portalFamily(patientId) });
    },
    onError: (error) => {
      // Ended meanwhile, by the patient or the family member in the portal: the list behind the message shows it.
      if (isApiError(error) && [404, 422].includes(error.status)) {
        void queryClient.invalidateQueries({ queryKey: patientKeys.portalFamily(patientId) });
      }
    },
  });

  if (!canRead) {
    return (
      <EmptyState
        title="Portal access is not available to you"
        description="Ask your administrator for access to patient records."
        icon={<ShieldKeyholeRegular />}
      />
    );
  }

  return (
    <>
      <PortalLinksSection patientId={patientId} />
      <Subtitle2 as="h2" className={styles.heading}>
        Who sees the records in the portal
      </Subtitle2>
      <div className={styles.intro}>
        {access.data && <Text>{ownNumberText(access.data)}</Text>}
        <Caption1 className={styles.muted}>
          A family member on another number sees them too once the records are shared with their number, here or in the
          portal with a code sent to the patient’s number. The patient can stop it at any time.
        </Caption1>
      </div>
      {canManage && access.data && (
        <div className={styles.toolbar}>
          <Button appearance="primary" icon={<PeopleAddRegular />} onClick={() => setSharing(true)}>
            Share with a family member
          </Button>
        </div>
      )}
      <DataTable
        ariaLabel="Family members who see the records in the portal"
        rows={access.data?.links}
        loading={access.isLoading}
        error={access.error}
        onRetry={() => void access.refetch()}
        getRowId={(l) => l.id}
        size="small"
        columns={[
          {
            key: 'number',
            header: 'Family member',
            render: (l) => (
              <span className={styles.cell}>
                <Text weight="semibold">{formatPhone(l.accountPhone)}</Text>
                <Caption1 className={styles.muted}>The patient is their {relationLabel[l.relation]}</Caption1>
              </span>
            ),
          },
          {
            key: 'how',
            header: 'Shared',
            render: (l) => (
              <span className={styles.cell}>
                <Text>{formatDateTime(l.linkedAt)}</Text>
                <Caption1 className={styles.muted}>
                  {linkSourceText(l)}
                  {l.source === 'FrontDesk' && l.linkedByName ? `, recorded by ${l.linkedByName}` : ''}
                </Caption1>
              </span>
            ),
          },
          { key: 'status', header: 'Status', render: (l) => <LinkStatus link={l} /> },
        ]}
        rowActions={
          canManage
            ? (l) =>
                l.isActive ? (
                  <Button
                    size="small"
                    aria-label={`Stop sharing with ${formatPhone(l.accountPhone)}`}
                    onClick={() => {
                      revoke.reset();
                      setEnding({ link: l, open: true });
                    }}
                  >
                    Stop sharing
                  </Button>
                ) : null
            : undefined
        }
        emptyTitle="Not shared with other numbers"
        emptyDescription={
          canManage
            ? 'When the patient asks for a family member on another number to see their records, share them here.'
            : 'Family members on other numbers who see the records appear here.'
        }
      />

      {access.data && (
        <ShareDialog
          patientId={patientId}
          ownPhone={access.data.ownPhone}
          open={sharing}
          onClose={() => setSharing(false)}
        />
      )}
      <ReasonDialog
        open={ending?.open ?? false}
        title={ending ? `Stop sharing with ${formatPhone(ending.link.accountPhone)}` : 'Stop sharing'}
        description={
          <Text block>
            The family member stops seeing the patient’s records in the portal at once. The link stays in the list,
            marked ended, with your name, the time and the reason.
          </Text>
        }
        placeholder="For example: the patient asked at the front desk"
        hint="Kept with the link, with your name and the time."
        confirmLabel="Stop sharing"
        destructive
        maxLength={PORTAL_ACCESS_LIMITS.reason}
        busy={revoke.isPending}
        error={revoke.error}
        onConfirm={(reason) => ending && revoke.mutate({ link: ending.link, reason })}
        onCancel={closeEnding}
      />
    </>
  );
}

function LinkStatus({ link }: { link: PortalFamilyLinkDto }) {
  const styles = useStyles();
  const ended = endedText(link);
  return (
    <span className={styles.cell}>
      <StatusBadge
        status={link.isActive ? 'active' : 'ended'}
        tone={link.isActive ? 'success' : 'subtle'}
        label={link.isActive ? 'Sees the records' : 'Ended'}
      />
      {ended && <Caption1>{ended}</Caption1>}
      {link.revokedReason && <Caption1 className={styles.muted}>Reason: {link.revokedReason}</Caption1>}
    </span>
  );
}

function ShareDialog({
  patientId,
  ownPhone,
  open,
  onClose,
}: {
  patientId: string;
  ownPhone: string | null;
  open: boolean;
  onClose: () => void;
}) {
  const closing = useCloseRequests({ onClose });
  return (
    <Dialog open={open} onOpenChange={closing.onOpenChange}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <CloseRequestScope value={closing}>
          {open && <ShareForm patientId={patientId} ownPhone={ownPhone} onClose={onClose} />}
        </CloseRequestScope>
      </DialogSurface>
    </Dialog>
  );
}

/** The share form. The dialog mounts it each time it opens, so it always starts empty. */
export function ShareForm({
  patientId,
  ownPhone,
  onClose,
}: {
  patientId: string;
  ownPhone: string | null;
  onClose: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [problems, setProblems] = useState<string[]>([]);
  const schema = useMemo(() => shareFormSchema(ownPhone), [ownPhone]);
  const form = useForm<ShareFormValues>({
    resolver: zodResolver(schema),
    mode: 'onTouched',
    defaultValues: shareFormDefaults(),
  });
  const agreedBy = useWatch({ control: form.control, name: 'agreedBy' });
  // Step two: the code sent to the patient's own number, entered as the patient reads it out (MAKER-CHECKER.md, slice 14).
  const [sent, setSent] = useState<{ code: FamilyDeskCodeSent; request: LinkFamilyMemberRequest } | null>(null);
  const [code, setCode] = useState('');
  const [codeProblem, setCodeProblem] = useState<string | null>(null);

  const save = useMutation({
    mutationFn: (request: LinkFamilyMemberRequest) => portalFamilyApi.sendCode(patientId, request),
    onSuccess: (result, request) => {
      setSent({ code: result, request });
      setCode('');
      setCodeProblem(null);
    },
    onError: (error) => {
      const { fields, other } = shareServerErrors(error);
      fields.forEach(([field, message], index) =>
        form.setError(field, { type: 'server', message }, { shouldFocus: index === 0 }),
      );
      setProblems(other);
    },
  });

  const confirm = useMutation({
    mutationFn: () => {
      if (!sent) {
        throw new Error('No code was sent.');
      }
      return portalFamilyApi.confirm(patientId, sent.code.codeId, code.trim(), sent.request.consentGivenBy);
    },
    onSuccess: () => {
      notify.success(
        'Records shared',
        `${formatPhone(sent?.request.accountPhone ?? '')} sees the patient’s records the next time they open the patient portal.`,
      );
      void queryClient.invalidateQueries({ queryKey: patientKeys.portalFamily(patientId) });
      onClose();
    },
    onError: (error) => {
      if (isApiError(error) && error.isValidation) {
        setCodeProblem(
          'That is not the code sent to the patient. Check it with the patient; a few wrong tries spend it.',
        );
      } else if (isApiError(error)) {
        setCodeProblem(error.userMessage);
      } else {
        setCodeProblem('Please try again.');
      }
    },
  });

  const guard = useUnsavedChanges({
    dirty: form.formState.isDirty,
    busy: save.isPending || confirm.isPending,
    onClose,
  });

  if (sent) {
    return (
      <form
        onSubmit={(event) => {
          event.preventDefault();
          if (code.trim()) {
            confirm.mutate();
          }
        }}
        noValidate
      >
        <DialogBody>
          <DialogTitle>Enter the patient’s code</DialogTitle>
          <DialogContent className={styles.form}>
            <MessageBar intent="info" layout="multiline">
              <MessageBarBody>
                <MessageBarTitle>Code sent to the patient’s own number</MessageBarTitle>A code went by SMS to{' '}
                {sent.code.sentTo}, the patient’s registered mobile number. Ask the patient to read it out. Nothing is
                shared with {formatPhone(sent.request.accountPhone)} until it is entered. It works until{' '}
                {formatDateTime(sent.code.expiresAt)}.
              </MessageBarBody>
            </MessageBar>
            <Field
              label="Code from the patient’s SMS"
              required
              validationMessage={codeProblem ?? undefined}
              validationState={codeProblem ? 'error' : 'none'}
            >
              <Input
                value={code}
                onChange={(_, data) => setCode(data.value)}
                inputMode="numeric"
                autoComplete="one-time-code"
                maxLength={10}
              />
            </Field>
          </DialogContent>
          <DialogActions>
            <Button onClick={guard.requestClose} disabled={confirm.isPending}>
              Cancel
            </Button>
            <Button onClick={() => save.mutate(sent.request)} disabled={save.isPending || confirm.isPending}>
              Send a new code
            </Button>
            <Button
              type="submit"
              appearance="primary"
              disabled={!code.trim() || confirm.isPending}
              icon={confirm.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
            >
              Share
            </Button>
          </DialogActions>
        </DialogBody>
        {guard.dialogs}
      </form>
    );
  }

  return (
    <form
      onSubmit={form.handleSubmit((values) => {
        setProblems([]);
        save.mutate(toLinkRequest(values));
      })}
      noValidate
    >
      <DialogBody>
        <DialogTitle>Share with a family member</DialogTitle>
        <DialogContent className={styles.form}>
          <Text block>
            The family member signs in to the patient portal with their own number and sees the patient’s appointments,
            prescriptions, reports, bills and discharge summaries, and can book and pay for them.
          </Text>
          {ownPhone ? (
            <Caption1 block>
              A code goes by SMS to the patient’s own number, {formatPhone(ownPhone)}: the patient reads it out to you
              before anything is shared.
            </Caption1>
          ) : (
            <MessageBar intent="warning" layout="multiline">
              <MessageBarBody>
                <MessageBarTitle>No mobile number to send the code to</MessageBarTitle>
                The patient has no mobile number of their own, so a code cannot be sent to them and their records cannot
                be shared in the portal. Record their number first.
              </MessageBarBody>
            </MessageBar>
          )}
          <TextField
            control={form.control}
            name="accountPhone"
            label="Family member’s mobile number"
            required
            type="tel"
            maxLength={16}
            autoComplete="off"
            hint="The number they sign in to the portal with."
          />
          <SelectField
            control={form.control}
            name="relation"
            label="The patient is their"
            required
            placeholder="Choose the relation"
            options={RELATION_OPTIONS}
          />
          <ChoiceField
            control={form.control}
            name="agreedBy"
            label="Who agreed"
            options={[
              { value: 'patient', label: 'The patient' },
              { value: 'guardian', label: 'A guardian, for the patient' },
            ]}
          />
          {agreedBy === 'guardian' && (
            <TextField
              control={form.control}
              name="guardianName"
              label="Guardian’s name"
              required
              maxLength={PORTAL_ACCESS_LIMITS.guardianName}
              autoComplete="off"
              hint="For a child, or a patient who cannot decide for themselves."
            />
          )}
          <Controller
            control={form.control}
            name="identityChecked"
            render={({ field, fieldState }) => (
              <Field
                required
                validationMessage={fieldState.error?.message}
                validationState={fieldState.error ? 'error' : 'none'}
              >
                <Checkbox
                  ref={field.ref}
                  name={field.name}
                  onBlur={field.onBlur}
                  checked={field.value}
                  onChange={(_, data) => field.onChange(data.checked === true)}
                  label="I have checked the identity documents of the patient (or guardian) and the family member."
                />
              </Field>
            )}
          />
          {problems.length > 0 && (
            <MessageBar intent="error" layout="multiline" politeness="assertive">
              <MessageBarBody>
                <MessageBarTitle>The records were not shared</MessageBarTitle>
                {problems.join(' ')}
              </MessageBarBody>
            </MessageBar>
          )}
        </DialogContent>
        <DialogActions>
          <Button onClick={guard.requestClose} disabled={save.isPending}>
            Cancel
          </Button>
          <Button
            type="submit"
            appearance="primary"
            disabled={!form.formState.isValid || save.isPending || !ownPhone}
            icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
          >
            Send code to the patient
          </Button>
        </DialogActions>
      </DialogBody>
      {guard.dialogs}
    </form>
  );
}
