import {
  Button,
  Caption1,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  OverlayDrawer,
  Spinner,
  tokens,
} from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { isApiError, isIndianMobile, normalizePhone, todayIso } from '@hc360/core';
import {
  applyServerErrors,
  DateField,
  FormGrid,
  SelectField,
  SwitchField,
  TextAreaField,
  TextField,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { toOptions, useDepartments, usePractitioners } from '@/lib/lookups';
import { admissionApi, inpatientKeys, type AdmissionDetail } from './api';

const schema = z
  .object({
    departmentId: z.string().nullable(),
    consultantPractitionerId: z.string().nullable(),
    provisionalDiagnosis: z.string().trim().max(500),
    expectedDischargeDate: z.string().nullable(),
    policyNumber: z
      .string()
      .trim()
      .max(64)
      .regex(/^[A-Za-z0-9/ -]*$/, "Use letters, digits, spaces, '/' or '-'."),
    removePolicy: z.boolean(),
    isMlc: z.boolean(),
    mlcNumber: z.string().trim().max(64),
    attendantName: z.string().trim().max(200),
    attendantRelation: z.string().trim().max(64),
    attendantPhone: z.string().trim(),
    notes: z.string().trim().max(1000),
  })
  .superRefine((v, ctx) => {
    if (!v.consultantPractitionerId) {
      ctx.addIssue({ code: 'custom', path: ['consultantPractitionerId'], message: 'Choose the consultant in charge.' });
    }
    if (v.isMlc && !v.mlcNumber) {
      ctx.addIssue({ code: 'custom', path: ['mlcNumber'], message: 'Enter the MLC number.' });
    }
    if (v.attendantPhone && !isIndianMobile(v.attendantPhone)) {
      ctx.addIssue({ code: 'custom', path: ['attendantPhone'], message: 'Enter a 10-digit mobile number.' });
    }
  });

type EditValues = z.infer<typeof schema>;

/** Edit admission details. The payer is fixed at admission (changed by the insurance desk in Billing). */
export function EditAdmissionDrawer({
  admission,
  open,
  onClose,
}: {
  admission: AdmissionDetail;
  open: boolean;
  onClose: () => void;
}) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<EditValues>({
    resolver: zodResolver(schema),
    defaultValues: {
      departmentId: admission.departmentId,
      consultantPractitionerId: admission.consultantPractitionerId,
      provisionalDiagnosis: admission.provisionalDiagnosis ?? '',
      expectedDischargeDate: admission.expectedDischargeDate,
      policyNumber: '',
      removePolicy: false,
      isMlc: admission.isMlc,
      mlcNumber: admission.mlcNumber ?? '',
      attendantName: admission.attendantName ?? '',
      attendantRelation: admission.attendantRelation ?? '',
      attendantPhone: admission.attendantPhone ?? '',
      notes: admission.notes ?? '',
    },
  });
  const [departmentId, isMlc] = useWatch({ control: form.control, name: ['departmentId', 'isMlc'] });
  const departments = useDepartments('Clinical');
  const practitioners = usePractitioners(departmentId);

  const save = useMutation({
    mutationFn: (v: EditValues) =>
      admissionApi.update(admission.id, {
        departmentId: v.departmentId,
        consultantPractitionerId: v.consultantPractitionerId!,
        provisionalDiagnosis: v.provisionalDiagnosis || null,
        expectedDischargeDate: v.expectedDischargeDate,
        policyNumber: v.removePolicy ? '' : v.policyNumber || null,
        isMlc: v.isMlc,
        mlcNumber: v.isMlc ? v.mlcNumber : null,
        attendantName: v.attendantName || null,
        attendantRelation: v.attendantRelation || null,
        attendantPhone: v.attendantPhone ? normalizePhone(v.attendantPhone) : null,
        notes: v.notes || null,
        version: admission.version,
      }),
    onSuccess: () => {
      notify.success('Admission details saved');
      void queryClient.invalidateQueries({ queryKey: inpatientKeys.all });
      onClose();
    },
    onError: (error) => {
      if (isApiError(error) && error.isConflict) {
        notify.error(error, 'Someone else changed this admission');
        void queryClient.invalidateQueries({ queryKey: inpatientKeys.admission(admission.id) });
        onClose();
        return;
      }
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the admission');
      }
    },
  });

  return (
    <OverlayDrawer
      open={open}
      position="end"
      size="medium"
      onOpenChange={(_, d) => !d.open && !save.isPending && onClose()}
    >
      <form
        onSubmit={form.handleSubmit((values) => !save.isPending && save.mutate(values))}
        noValidate
        style={{ display: 'contents' }}
      >
        <DrawerHeader>
          <DrawerHeaderTitle
            action={<Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={onClose} />}
          >
            Edit admission {admission.admissionNumber}
          </DrawerHeaderTitle>
          <Caption1 style={{ color: tokens.colorNeutralForeground3 }}>
            The payer cannot be changed here; the insurance desk changes it in Billing.
          </Caption1>
        </DrawerHeader>
        <DrawerBody>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 12, paddingBottom: 12 }}>
            <FormGrid columns={2} minColumnWidth={200}>
              <SelectField
                control={form.control}
                name="departmentId"
                label="Department"
                placeholder="Not set"
                options={toOptions(departments.data)}
              />
              <SelectField
                control={form.control}
                name="consultantPractitionerId"
                label="Consultant in charge"
                required
                placeholder="Choose consultant"
                options={toOptions(practitioners.data)}
              />
            </FormGrid>
            <TextAreaField
              control={form.control}
              name="provisionalDiagnosis"
              label="Provisional diagnosis"
              rows={2}
              maxLength={500}
            />
            <DateField
              control={form.control}
              name="expectedDischargeDate"
              label="Expected discharge date"
              min={todayIso()}
            />
            <TextField
              control={form.control}
              name="policyNumber"
              label="New policy / TPA card number"
              hint={
                admission.policyNumberMasked
                  ? `On file: ${admission.policyNumberMasked}. Leave blank to keep it.`
                  : 'Leave blank if there is none.'
              }
              maxLength={64}
              autoComplete="off"
            />
            {admission.policyNumberMasked && (
              <SwitchField control={form.control} name="removePolicy" label="Remove the policy number on file" />
            )}
            <SwitchField control={form.control} name="isMlc" label="Medico-legal case (MLC)" />
            {isMlc && <TextField control={form.control} name="mlcNumber" label="MLC number" required maxLength={64} />}
            <FormGrid columns={3} minColumnWidth={160}>
              <TextField control={form.control} name="attendantName" label="Attendant name" maxLength={200} />
              <TextField control={form.control} name="attendantRelation" label="Relation" maxLength={64} />
              <TextField
                control={form.control}
                name="attendantPhone"
                label="Attendant mobile"
                type="tel"
                maxLength={14}
              />
            </FormGrid>
            <TextAreaField control={form.control} name="notes" label="Notes" rows={3} maxLength={1000} />
          </div>
        </DrawerBody>
        <DrawerFooter>
          <Button
            type="submit"
            appearance="primary"
            disabled={save.isPending}
            // Keeps focus while saving (a disabled button drops it); Enter can still submit, which is ignored.
            disabledFocusable={save.isPending}
            icon={save.isPending ? <Spinner size="tiny" /> : undefined}
          >
            Save
          </Button>
          <Button onClick={onClose}>Cancel</Button>
        </DrawerFooter>
      </form>
    </OverlayDrawer>
  );
}
