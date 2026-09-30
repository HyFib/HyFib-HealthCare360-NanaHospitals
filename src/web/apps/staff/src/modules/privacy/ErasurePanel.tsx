import {
  Button,
  Caption1,
  Checkbox,
  makeStyles,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  Subtitle2,
  Text,
  tokens,
  Tooltip,
} from '@fluentui/react-components';
import { EraserRegular, SendRegular } from '@fluentui/react-icons';
import { formatDateOnly } from '@hc360/core';
import { ConfirmDialog, ErrorState, Section } from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import {
  privacyApi,
  privacyKeys,
  privacyQueries,
  type DataRequestDetail,
  type ErasureAssessment,
  type ErasureOutcome,
} from './api';
import { erasureDoneLabel, type ErasureItem } from './labels';
import { PlainTable } from './PlainTable';
import { isOpenStatus, keptUntilText } from './rules';
import type { RequestCommand } from './useRequestCommand';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '14px' },
  block: { display: 'flex', flexDirection: 'column', gap: '6px' },
  options: { display: 'flex', flexDirection: 'column', gap: '4px' },
  optionLabel: { display: 'flex', flexDirection: 'column', gap: '2px' },
  muted: { color: tokens.colorNeutralForeground3 },
  reason: { color: tokens.colorPaletteMarigoldForeground1 },
  list: { margin: 0, paddingLeft: '20px' },
});

function keptSummary(a: { keepUntil: string | null; keepPermanently: boolean }): string {
  if (a.keepPermanently) {
    return 'Some records must be kept permanently.';
  }
  return a.keepUntil
    ? `The rest is kept until ${formatDateOnly(a.keepUntil)}, as the law requires.`
    : 'Nothing else has to be kept.';
}

/**
 * Erasure requests: what can go at once, what the law requires the hospital to keep (and until when), and what was
 * erased. Erasing needs a checked requester and cannot be undone.
 */
export function ErasurePanel({
  request,
  canManage,
  command,
  outcome,
  onErased,
  onAnswer,
}: {
  request: DataRequestDetail;
  canManage: boolean;
  command: RequestCommand;
  /** What was erased in this visit, to show and to start the answer with. */
  outcome: ErasureOutcome | null;
  onErased: (outcome: ErasureOutcome) => void;
  onAnswer: () => void;
}) {
  const styles = useStyles();
  const open = isOpenStatus(request.status);

  if (!request.patientId) {
    return (
      <Section title="Erasure">
        <Text>
          The person is not a registered patient, so there is nothing in HC360 to erase for them. Say so in the reply.
        </Text>
      </Section>
    );
  }

  if (!open) {
    return (
      <Section title="Erasure">
        <div className={styles.stack}>
          <div className={styles.block}>
            <Subtitle2 as="h3">What was erased</Subtitle2>
            {request.erasedItems.length === 0 ? (
              <Text className={styles.muted}>Nothing was erased for this request.</Text>
            ) : (
              <ul className={styles.list}>
                {request.erasedItems.map((item) => (
                  <li key={item}>
                    <Text>{erasureDoneLabel(item)}</Text>
                  </li>
                ))}
              </ul>
            )}
          </div>
          <div className={styles.block}>
            <Subtitle2 as="h3">What is kept</Subtitle2>
            <Text>
              {request.retainedPermanently || request.retainedUntil
                ? `${keptUntilText(request.retainedUntil, request.retainedPermanently)}, as the law requires. The retention review lists it until then.`
                : 'Nothing had to be kept.'}
            </Text>
          </div>
        </div>
      </Section>
    );
  }

  return (
    <OpenErasure
      request={request}
      canManage={canManage}
      command={command}
      outcome={outcome}
      onErased={onErased}
      onAnswer={onAnswer}
    />
  );
}

function OpenErasure({
  request,
  canManage,
  command,
  outcome,
  onErased,
  onAnswer,
}: {
  request: DataRequestDetail;
  canManage: boolean;
  command: RequestCommand;
  outcome: ErasureOutcome | null;
  onErased: (outcome: ErasureOutcome) => void;
  onAnswer: () => void;
}) {
  const styles = useStyles();
  const queryClient = useQueryClient();
  const assessment = useQuery(privacyQueries.erasure(request.id));
  const [selected, setSelected] = useState<ErasureItem[]>([]);
  const [confirming, setConfirming] = useState(false);
  const verified = request.identityVerifiedAt !== null;

  const erase = async (a: ErasureAssessment) => {
    const items = selected.filter((item) => a.options.some((o) => o.item === item && o.available));
    const result = await command.run(() => privacyApi.erase(request.id, items), {
      success: 'Data erased',
      successBody: 'Answer the request to tell the requester what was done.',
      failure: 'Could not erase the data',
    });
    setConfirming(false);
    void queryClient.invalidateQueries({ queryKey: privacyKeys.erasure(request.id) });
    if (result.ok) {
      setSelected([]);
      onErased(result.value);
    }
  };

  const toggle = (item: ErasureItem, checked: boolean) =>
    setSelected((current) => (checked ? [...current, item] : current.filter((i) => i !== item)));

  const a = assessment.data;
  const chosen = a ? a.options.filter((o) => o.available && selected.includes(o.item)) : [];
  const blocker = !verified
    ? 'Check the requester’s identity first.'
    : chosen.length === 0
      ? 'Choose what to erase first.'
      : null;

  return (
    <Section title="Erasure" description="What can go now, and what the law requires the hospital to keep.">
      <div className={styles.stack}>
        {outcome ? (
          <MessageBar intent="success" layout="multiline">
            <MessageBarBody>
              <MessageBarTitle>Erased</MessageBarTitle>
              {outcome.summary}
            </MessageBarBody>
            {canManage && (
              <MessageBarActions>
                <Button icon={<SendRegular />} onClick={onAnswer}>
                  Answer the request
                </Button>
              </MessageBarActions>
            )}
          </MessageBar>
        ) : (
          request.erasedItems.length > 0 && (
            <MessageBar intent="info" layout="multiline">
              <MessageBarBody>
                <MessageBarTitle>Already erased</MessageBarTitle>
                {request.erasedItems.map(erasureDoneLabel).join('; ')}.
              </MessageBarBody>
            </MessageBar>
          )
        )}
        {!verified && (
          <MessageBar intent="warning" layout="multiline">
            <MessageBarBody>
              <MessageBarTitle>Identity not checked</MessageBarTitle>
              Nothing can be erased until the requester’s identity, and their authority to act for the patient, have
              been checked.
            </MessageBarBody>
          </MessageBar>
        )}

        {assessment.error ? (
          <ErrorState error={assessment.error} onRetry={() => void assessment.refetch()} />
        ) : !a ? (
          <Spinner size="small" label="Working out what can be erased…" labelPosition="after" />
        ) : (
          <>
            <div className={styles.block}>
              <Subtitle2 as="h3">What can be erased now</Subtitle2>
              <div className={styles.options} role="group" aria-label="What can be erased now">
                {a.options.map((option) => (
                  <Checkbox
                    key={option.item}
                    checked={option.available && selected.includes(option.item)}
                    disabled={!option.available || !canManage || !verified || command.busy}
                    onChange={(_, d) => toggle(option.item, Boolean(d.checked))}
                    label={
                      <span className={styles.optionLabel}>
                        <Text weight="semibold">{option.title}</Text>
                        <Caption1 className={styles.muted}>{option.description}</Caption1>
                        {!option.available && option.reason && (
                          <Caption1 className={styles.reason}>Not possible: {option.reason}</Caption1>
                        )}
                      </span>
                    }
                  />
                ))}
              </div>
              {canManage && (
                <div>
                  <Tooltip
                    content={blocker ?? 'Asks you to confirm before anything is erased.'}
                    relationship="description"
                  >
                    <Button
                      icon={<EraserRegular />}
                      disabledFocusable={Boolean(blocker) || command.busy}
                      onClick={() => !blocker && setConfirming(true)}
                    >
                      Erase selected
                    </Button>
                  </Tooltip>
                </div>
              )}
            </div>

            <div className={styles.block}>
              <Subtitle2 as="h3">What must be kept</Subtitle2>
              {a.holds.length === 0 ? (
                <Text className={styles.muted}>No records have to be kept for this patient.</Text>
              ) : (
                <PlainTable
                  ariaLabel="Records that must be kept"
                  rows={a.holds}
                  getRowId={(h) => h.key}
                  columns={[
                    {
                      key: 'what',
                      header: 'What',
                      width: '24%',
                      render: (h) => <Text weight="semibold">{h.title}</Text>,
                    },
                    { key: 'why', header: 'Why', render: (h) => h.reason },
                    {
                      key: 'until',
                      header: 'Kept',
                      width: 170,
                      render: (h) => keptUntilText(h.until, h.permanently),
                    },
                  ]}
                />
              )}
              <Caption1 className={styles.muted}>
                {keptSummary(a)} Erased data leaves the backups as they expire; the audit trail is never changed.
              </Caption1>
            </div>
          </>
        )}
      </div>

      <ConfirmDialog
        open={confirming}
        title="Erase the selected data?"
        confirmLabel="Erase"
        destructive
        busy={command.busy}
        onConfirm={() => a && void erase(a)}
        onCancel={() => setConfirming(false)}
      >
        <Text as="p">This will:</Text>
        <ul className={styles.list}>
          {chosen.map((o) => (
            <li key={o.item}>
              <Text>{o.title}</Text>
            </li>
          ))}
        </ul>
        <Text as="p" weight="semibold">
          This cannot be undone.
        </Text>
        <Text as="p">Medical records, bills and the audit trail are kept as the law requires.</Text>
      </ConfirmDialog>
    </Section>
  );
}
