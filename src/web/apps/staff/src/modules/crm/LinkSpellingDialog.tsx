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
  MessageBar,
  MessageBarBody,
  Radio,
  RadioGroup,
  SearchBox,
  Spinner,
} from '@fluentui/react-components';
import { isApiError, useDebouncedValue } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { ignoreEscapeWhile } from '@/lib/dialogFocus';
import { crmApi, crmKeys, crmQueries } from './api';

/** Most spellings one doctor keeps (the server's limit). */
const MAX_SPELLINGS = 10;

/**
 * Adds an unmatched "Referred by" text (for example "Dr RK") to a registered doctor as another spelling, so those
 * visits count for the doctor from now on (and in past periods, because referrals are worked out from visits).
 */
export function LinkSpellingDialog({ text, onClose }: { text: string | null; onClose: () => void }) {
  return (
    <Dialog open={text !== null} onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 560 }}>{text !== null && <Body text={text} onClose={onClose} />}</DialogSurface>
    </Dialog>
  );
}

function Body({ text, onClose }: { text: string; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [term, setTerm] = useState('');
  const debounced = useDebouncedValue(term.trim(), 300);
  const [doctorId, setDoctorId] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const doctors = useQuery(crmQueries.referringDoctors({ search: debounced || undefined, activeOnly: true }));
  const items = doctors.data?.items ?? [];

  const save = async () => {
    if (!doctorId) {
      setError('Choose the doctor.');
      return;
    }
    setBusy(true);
    setError(null);
    try {
      const doctor = await queryClient.fetchQuery({ ...crmQueries.referringDoctor(doctorId), staleTime: 0 });
      if (doctor.alternateNames.some((n) => n.toLowerCase() === text.toLowerCase())) {
        notify.info('Already linked', `${doctor.name} already has this spelling.`);
        onClose();
        return;
      }
      if (doctor.alternateNames.length >= MAX_SPELLINGS) {
        setError(`${doctor.name} already has ${MAX_SPELLINGS} other spellings. Remove one on the doctor's page first.`);
        return;
      }
      await crmApi.saveReferringDoctor(doctor.id, {
        name: doctor.name,
        clinic: doctor.clinic,
        phone: doctor.phone,
        email: doctor.email,
        specialty: doctor.specialty,
        city: doctor.city,
        registrationNumber: doctor.registrationNumber,
        alternateNames: [...doctor.alternateNames, text],
        notes: doctor.notes,
        isActive: doctor.isActive,
        version: doctor.version,
      });
      notify.success('Spelling added', `Visits referred by “${text}” now count for ${doctor.name}.`);
      void queryClient.invalidateQueries({ queryKey: crmKeys.referrals });
      onClose();
    } catch (err) {
      if (isApiError(err) && (err.isValidation || err.code === 'concurrency')) {
        setError(err.userMessage);
      } else {
        notify.error(err, 'Could not add the spelling');
      }
    } finally {
      setBusy(false);
    }
  };

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        if (!busy) {
          void save();
        }
      }}
      onKeyDown={ignoreEscapeWhile(busy)}
      noValidate
    >
      <DialogBody>
        <DialogTitle>Link “{text}” to a doctor</DialogTitle>
        <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
          <Caption1>Visits whose “Referred by” reads exactly this will count for the doctor you choose.</Caption1>
          <Field label="Find the doctor">
            <SearchBox
              value={term}
              onChange={(_, d) => setTerm(d.value)}
              placeholder="Name, clinic or area"
              autoFocus
            />
          </Field>
          {doctors.isLoading ? (
            <Spinner size="tiny" label="Searching…" labelPosition="after" />
          ) : items.length === 0 ? (
            <Caption1>No active doctor matches. Add the doctor instead.</Caption1>
          ) : (
            <RadioGroup value={doctorId} onChange={(_, d) => setDoctorId(d.value)} aria-label="Referring doctors">
              {items.slice(0, 8).map((d) => (
                <Radio key={d.id} value={d.id} label={[d.name, d.clinic, d.city].filter(Boolean).join(' · ')} />
              ))}
            </RadioGroup>
          )}
          {error && (
            <MessageBar intent="error">
              <MessageBarBody>{error}</MessageBarBody>
            </MessageBar>
          )}
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose} disabled={busy}>
            Cancel
          </Button>
          <Button
            type="submit"
            appearance="primary"
            disabled={busy || !doctorId}
            // Keeps focus while busy (a disabled button drops it); Enter can still submit, which is ignored.
            disabledFocusable={busy}
            icon={busy ? <Spinner size="tiny" aria-hidden /> : undefined}
          >
            Add spelling
          </Button>
        </DialogActions>
      </DialogBody>
    </form>
  );
}
