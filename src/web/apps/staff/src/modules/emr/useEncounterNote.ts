import { zodResolver } from '@hookform/resolvers/zod';
import { isApiError, useHotkey } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { useFormDraft } from '@/lib/useFormDraft';
import { emrApi, emrKeys, type EncounterDetail } from './api';
import { encounterFormSchema, isSignable, toContent, toFormValues, type EncounterFormValues } from './encounterForm';

/**
 * Form state of the encounter note (SOAP, follow-up, referral): drafts are kept on this computer while typing, Ctrl+S
 * saves to the server, signing saves first, and signed notes change only through an amendment with a reason.
 */
export function useEncounterNote(encounter: EncounterDetail, today: string) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [defaults] = useState<EncounterFormValues>(() => toFormValues(encounter));
  const form = useForm<EncounterFormValues>({ resolver: zodResolver(encounterFormSchema(today)), defaultValues: defaults, mode: 'onBlur' });
  const draft = useFormDraft(form, `emr-encounter:${encounter.id}`);
  const [amending, setAmending] = useState(false);
  const locked = encounter.status !== 'InProgress' && !amending;

  const publish = (updated: EncounterDetail, values: EncounterFormValues) => {
    queryClient.setQueryData(emrKeys.encounter(updated.id), updated);
    form.reset(values);
    draft.discard();
    void queryClient.invalidateQueries({ queryKey: emrKeys.patient(updated.patientId) });
  };

  const onConflict = () => {
    void queryClient.invalidateQueries({ queryKey: emrKeys.encounter(encounter.id) });
    notify.warning('This encounter was changed in another window', 'Your text is kept on this screen. Save again to replace the other version.');
  };

  const valid = async (): Promise<EncounterFormValues | null> => ((await form.trigger()) ? form.getValues() : null);

  const save = useMutation({
    mutationFn: async () => {
      const values = await valid();
      if (!values) {
        return null;
      }
      const updated = await emrApi.updateEncounter(encounter.id, toContent(values), encounter.version);
      return { updated, values };
    },
    onSuccess: (result) => {
      if (result) {
        publish(result.updated, result.values);
        notify.success('Encounter saved');
      }
    },
    onError: (error) => (isApiError(error) && error.isConflict ? onConflict() : notify.error(error, 'Could not save the encounter')),
  });

  const sign = useMutation({
    mutationFn: async () => {
      const values = await valid();
      if (!values) {
        return null;
      }
      let version = encounter.version;
      if (form.formState.isDirty) {
        version = (await emrApi.updateEncounter(encounter.id, toContent(values), version)).version;
      }
      const updated = await emrApi.signEncounter(encounter.id, version);
      return { updated, values };
    },
    onSuccess: (result) => {
      if (result) {
        publish(result.updated, result.values);
        notify.success('Encounter signed', 'The note is locked. Later corrections are recorded as amendments.');
      }
    },
    onError: (error) => (isApiError(error) && error.isConflict ? onConflict() : notify.error(error, 'Could not sign the encounter')),
  });

  const amend = useMutation({
    mutationFn: async (reason: string) => {
      const values = await valid();
      if (!values) {
        return null;
      }
      const updated = await emrApi.amendEncounter(encounter.id, reason, toContent(values), encounter.version);
      return { updated, values };
    },
    onSuccess: (result) => {
      if (result) {
        publish(result.updated, result.values);
        setAmending(false);
        notify.success('Amendment recorded', 'The previous text is kept in the amendment history.');
      }
    },
    onError: (error) => (isApiError(error) && error.isConflict ? onConflict() : notify.error(error, 'Could not record the amendment')),
  });

  useHotkey('mod+s', () => !locked && encounter.status === 'InProgress' && !save.isPending && save.mutate(), { allowInInputs: true });

  const cancelAmendment = () => {
    form.reset(toFormValues(encounter));
    setAmending(false);
  };

  return {
    form,
    draft,
    defaults,
    locked,
    amending,
    startAmendment: () => setAmending(true),
    cancelAmendment,
    save,
    sign,
    amend,
    canSign: () => isSignable(form.getValues()),
  };
}

export type EncounterNote = ReturnType<typeof useEncounterNote>;
