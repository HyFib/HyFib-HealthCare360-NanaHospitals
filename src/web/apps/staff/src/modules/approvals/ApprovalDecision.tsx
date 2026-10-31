import {
  Badge,
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  makeStyles,
  Spinner,
  Text,
  Textarea,
} from '@fluentui/react-components';
import { isApiError } from '@hc360/core';
import { useNotify, useUnsavedChanges, useValuesChanged } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { ActionError } from '@/modules/billing/ui';
import { APPROVAL_STATUS_LABEL, approvalKeys, approvalsApi, type ApprovalRequestDto, type ApprovalStatus } from './api';

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
});

export type Decision = 'approve' | 'reject' | 'withdraw';

const TITLE: Record<Decision, string> = { approve: 'Approve', reject: 'Reject', withdraw: 'Withdraw' };

const STATUS_COLOR: Record<ApprovalStatus, 'warning' | 'success' | 'danger' | 'informative'> = {
  Pending: 'warning',
  Approved: 'success',
  Rejected: 'danger',
  Withdrawn: 'informative',
  Failed: 'danger',
};

export function ApprovalStatusBadge({ request }: { request: Pick<ApprovalRequestDto, 'status' | 'isExpired'> }) {
  return request.isExpired ? (
    <Badge appearance="tint" color="informative">
      Expired
    </Badge>
  ) : (
    <Badge appearance="tint" color={STATUS_COLOR[request.status]}>
      {APPROVAL_STATUS_LABEL[request.status]}
    </Badge>
  );
}

/**
 * Approves (the change is made then, by the server, on the record as it was asked for), rejects (with a reason the
 * person who asked is told) or withdraws (only the person who asked) a request.
 */
export function ApprovalDecisionDialog({
  request,
  decision,
  onClose,
}: {
  request: ApprovalRequestDto;
  decision: Decision;
  onClose: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [text, setText] = useState('');
  const reasonNeeded = decision === 'reject';
  const valid = !reasonNeeded || text.trim().length >= 5;

  const decide = useMutation({
    mutationFn: () =>
      decision === 'approve'
        ? approvalsApi.approve(request.id, text.trim() || null, request.version)
        : decision === 'reject'
          ? approvalsApi.reject(request.id, text.trim(), request.version)
          : approvalsApi.withdraw(request.id, request.version),
    onSuccess: (decided) => {
      notify.success(
        decision === 'approve'
          ? `${request.title} approved`
          : decision === 'reject'
            ? 'Request rejected'
            : 'Request withdrawn',
        decision === 'approve' ? (decided.outcome ?? request.summary) : request.summary,
      );
      guard.close();
    },
    // A refusal (the record changed since it was asked for, so the request failed) changes the request too.
    onSettled: () => void queryClient.invalidateQueries({ queryKey: approvalKeys.all }),
  });
  // A refusal ends this decision: the request may have failed, or changed, so it is decided again from the request as
  // it is now (the page behind is refreshed), never sent again from here with the version it had. Only a note or reason
  // the server could not accept is corrected in place.
  const refused = decide.isError && !(isApiError(decide.error) && decide.error.isValidation);

  const guard = useUnsavedChanges({
    dirty: useValuesChanged({ text }, true) && !refused,
    busy: decide.isPending,
    open: true,
    onClose,
  });

  return (
    <Dialog open onOpenChange={guard.onOpenChange} modalType="alert">
      <DialogSurface style={{ maxWidth: 520 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (valid && !decide.isPending && !refused) {
              decide.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>{`${TITLE[decision]}: ${request.title.toLowerCase()}`}</DialogTitle>
            <DialogContent className={styles.body}>
              <Text>{request.summary}</Text>
              {decision === 'approve' && (
                <Text>
                  It is made now, on the record as it was when {request.requestedByName} asked for it. If the record has
                  changed since, nothing is changed and they are asked to ask again.
                </Text>
              )}
              {decision === 'withdraw' && <Text>Nothing is changed, and the request leaves the approvers' list.</Text>}
              {decision !== 'withdraw' && (
                <Field
                  label={reasonNeeded ? 'Reason' : 'Note'}
                  required={reasonNeeded}
                  hint={
                    reasonNeeded
                      ? `At least 5 characters. ${request.requestedByName} is told.`
                      : 'Optional. Kept with the approval.'
                  }
                >
                  <Textarea value={text} onChange={(_, data) => setText(data.value)} maxLength={500} rows={2} />
                </Field>
              )}
              <ActionError error={decide.error} />
            </DialogContent>
            <DialogActions>
              <Button onClick={guard.requestClose} disabled={decide.isPending}>
                {refused ? 'Close' : 'Cancel'}
              </Button>
              {!refused && (
                <Button
                  type="submit"
                  appearance="primary"
                  disabled={!valid || decide.isPending}
                  disabledFocusable={decide.isPending}
                  icon={decide.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
                >
                  {TITLE[decision]}
                </Button>
              )}
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}
