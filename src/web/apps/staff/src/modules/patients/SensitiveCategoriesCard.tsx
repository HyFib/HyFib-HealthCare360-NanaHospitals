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
  makeStyles,
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
  Textarea,
  tokens,
  useRestoreFocusTarget,
} from '@fluentui/react-components';
import { ShieldLockRegular } from '@fluentui/react-icons';
import { ErrorState, Section, useNotify, useUnsavedChanges, useValuesChanged } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { errorMessage } from '@/modules/lab/common/errors';
import {
  patientApi,
  patientKeys,
  patientQueries,
  SENSITIVE_CATEGORIES,
  type SensitiveCategoriesDto,
  type SensitiveCategory,
} from './api';

const MANAGE = 'patients.sensitive.manage';

const sensitiveCategoryLabel: Record<SensitiveCategory, string> = {
  Hiv: 'HIV',
  MentalHealth: 'Mental health',
  Mtp: 'Termination of pregnancy (MTP)',
  SexualViolence: 'Sexual violence',
};

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '10px' },
  muted: { color: tokens.colorNeutralForeground3 },
  content: { display: 'flex', flexDirection: 'column', gap: '12px' },
});

/**
 * The record's sensitive categories (gap R-4) for the doctors who keep them: an HIV, mental-illness, abortion or
 * sexual-abuse diagnosis sets them, as does a sexual assault in the MLC register; a doctor can add or remove one, with a
 * reason. The clinical record of a patient in a category opens only to staff holding that category, or through
 * break-the-glass.
 */
export function SensitiveCategoriesCard({ patientId }: { patientId: string }) {
  const canManage = usePermission(MANAGE);
  if (!canManage) {
    return null;
  }
  return <Card patientId={patientId} />;
}

function Card({ patientId }: { patientId: string }) {
  const styles = useStyles();
  const restoreFocusTarget = useRestoreFocusTarget();
  const sensitive = useQuery(patientQueries.sensitive(patientId));
  const [editing, setEditing] = useState(false);
  const data = sensitive.data;

  return (
    <Section
      title="Confidential record"
      description="A record in a sensitive category opens only to staff allowed that category, or by audited emergency access. Demographics, bills and orders are not affected."
    >
      {sensitive.error ? (
        <ErrorState error={sensitive.error} onRetry={() => void sensitive.refetch()} />
      ) : (
        <div className={styles.stack}>
          <Text>
            {data
              ? data.categories.length > 0
                ? `Sensitive: ${data.categories.map((c) => sensitiveCategoryLabel[c]).join(', ')}`
                : 'Not in a sensitive category.'
              : 'Loading…'}
          </Text>
          <div>
            <Button
              icon={<ShieldLockRegular />}
              disabled={!data}
              onClick={() => setEditing(true)}
              {...restoreFocusTarget}
            >
              Change categories
            </Button>
          </div>
        </div>
      )}
      {editing && data && <CategoriesDialog patientId={patientId} current={data} onClose={() => setEditing(false)} />}
    </Section>
  );
}

function CategoriesDialog({
  patientId,
  current,
  onClose,
}: {
  patientId: string;
  current: SensitiveCategoriesDto;
  onClose: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [categories, setCategories] = useState<SensitiveCategory[]>(current.categories);
  const [reason, setReason] = useState('');
  const save = useMutation({
    mutationFn: () => patientApi.setSensitive(patientId, categories, reason.trim(), current.version),
    onSuccess: (saved) => {
      queryClient.setQueryData(patientKeys.sensitive(patientId), saved);
      notify.success('Categories saved', 'The change is recorded in the audit trail.');
      onClose();
    },
  });
  const guard = useUnsavedChanges({ dirty: useValuesChanged({ categories, reason }), busy: save.isPending, onClose });
  const valid = reason.trim().length >= 10;

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (valid && !save.isPending) {
              save.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>Sensitive categories</DialogTitle>
            <DialogContent className={styles.content}>
              {save.error && (
                <MessageBar intent="error">
                  <MessageBarBody>{errorMessage(save.error, 'The categories could not be saved.')}</MessageBarBody>
                </MessageBar>
              )}
              <div role="group" aria-label="Categories">
                {SENSITIVE_CATEGORIES.map((category) => (
                  <Checkbox
                    key={category}
                    label={sensitiveCategoryLabel[category]}
                    checked={categories.includes(category)}
                    onChange={(_, d) =>
                      setCategories((all) =>
                        d.checked
                          ? SENSITIVE_CATEGORIES.filter((c) => c === category || all.includes(c))
                          : all.filter((c) => c !== category),
                      )
                    }
                  />
                ))}
              </div>
              <Field label="Reason" required hint="At least 10 characters. It is kept in the audit trail.">
                <Textarea value={reason} maxLength={500} rows={2} onChange={(_, d) => setReason(d.value)} autoFocus />
              </Field>
              <Caption1 className={styles.muted}>
                Removing a category opens the record to every clinician again.
              </Caption1>
            </DialogContent>
            <DialogActions>
              <Button onClick={guard.requestClose} disabled={save.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={!valid || save.isPending}
                disabledFocusable={save.isPending}
                icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                Save
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}
