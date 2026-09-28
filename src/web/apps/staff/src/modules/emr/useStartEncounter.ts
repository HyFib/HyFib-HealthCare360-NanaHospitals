import { isApiError } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { emrApi, emrKeys, type StartEncounterRequest } from './api';

/**
 * Starts (or continues) an encounter and opens the workspace. If an unsigned encounter already exists for the visit,
 * the server answers 409 with its id and the doctor simply continues it.
 */
export function useStartEncounter() {
  const notify = useNotify();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (request: Partial<StartEncounterRequest> & { patientId: string }) =>
      emrApi.startEncounter({
        visitId: null,
        admissionId: null,
        type: null,
        departmentId: null,
        practitionerId: null,
        templateId: null,
        chiefComplaint: null,
        ...request,
      }),
    onSuccess: (encounter) => {
      void queryClient.invalidateQueries({ queryKey: emrKeys.all });
      void navigate({ to: '/emr/encounters/$encounterId', params: { encounterId: encounter.id } });
    },
    onError: (error) => {
      const existing = isApiError(error) && error.code === 'encounter.already_open' ? (error.data as { encounterId?: string } | undefined)?.encounterId : undefined;
      if (existing) {
        notify.info('Continuing the open encounter', 'An unsigned encounter already exists for this visit.');
        void navigate({ to: '/emr/encounters/$encounterId', params: { encounterId: existing } });
      } else {
        notify.error(error, 'Could not start the encounter');
      }
    },
  });
}
