import {
  Button,
  Field,
  Input,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
} from '@fluentui/react-components';
import { zodResolver } from '@hookform/resolvers/zod';
import { isApiError, normalizePhone } from '@hc360/core';
import {
  applyServerErrors,
  ChoiceField,
  FormGrid,
  NumberField,
  SelectField,
  TextAreaField,
  TextField,
  useNotify,
} from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { z } from 'zod';
import { toOptions, useDepartments } from '@/lib/lookups';
import { usePermission } from '@/lib/permissions';
import { fromIstInput, toIstInput } from '@/modules/hr/shared/dates';
import { EditorDrawer } from '@/modules/hr/shared/EditorDrawer';
import { UserPicker } from '@/modules/hr/shared/UserPicker';
import { useUserName } from '@/modules/hr/shared/users';
import { crmApi, crmKeys, crmQueries, type LeadDetail } from './api';
import { CRM_PERMISSIONS, LEAD_SOURCES, leadSourceLabel } from './labels';

const NAME = /^[\p{L} .'-]+$/u;

const schema = z
  .object({
    name: z
      .string()
      .trim()
      .min(1, 'Enter the name.')
      .max(200)
      .regex(NAME, 'Use letters, spaces, dots, apostrophes or hyphens.'),
    phone: z
      .string()
      .trim()
      .refine((v) => !v || normalizePhone(v) !== null, 'Enter a valid mobile number.'),
    email: z
      .string()
      .trim()
      .max(256)
      .refine((v) => !v || /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(v), 'Enter a valid email address.'),
    gender: z.enum(['Female', 'Male', 'Other', 'Unknown']),
    ageYears: z.number().int().min(0).max(120).nullable(),
    city: z.string().trim().max(100),
    source: z.enum(LEAD_SOURCES),
    sourceDetail: z.string().trim().max(200),
    referringDoctorId: z.string().nullable(),
    interestDepartmentId: z.string().nullable(),
    interestPackage: z.string().trim().max(200),
    notes: z.string().trim().max(2000),
    owner: z.object({ id: z.string(), name: z.string() }).nullable(),
    nextFollowUpAt: z.string(),
  })
  .refine((v) => Boolean(v.phone || v.email), {
    path: ['phone'],
    message: 'Enter a phone number or an email address to follow up.',
  });

type Values = z.infer<typeof schema>;

/** Capture or edit an enquiry (lead). A second open lead for the same mobile number is caught before it is created. */
export function LeadDrawer({ lead, onClose }: { lead: LeadDetail | null; onClose: () => void }) {
  const navigate = useNavigate();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const departments = useDepartments('Clinical');
  const canReferrals = usePermission(CRM_PERMISSIONS.referralsManage);
  const doctors = useQuery({ ...crmQueries.referringDoctors({ activeOnly: true }), enabled: canReferrals });
  const ownerName = useUserName(lead?.ownerUserId);
  const [duplicate, setDuplicate] = useState<{ message: string; leadId: string | null } | null>(null);
  const [busy, setBusy] = useState(false);
  const form = useForm<Values>({
    resolver: zodResolver(schema),
    defaultValues: {
      name: lead?.name ?? '',
      phone: lead?.phone ?? '',
      email: lead?.email ?? '',
      gender: lead?.gender ?? 'Unknown',
      ageYears: lead?.ageYears ?? null,
      city: lead?.city ?? '',
      source: lead?.source ?? 'Phone',
      sourceDetail: lead?.sourceDetail ?? '',
      referringDoctorId: lead?.referringDoctorId ?? null,
      interestDepartmentId: lead?.interestDepartmentId ?? null,
      interestPackage: lead?.interestPackage ?? '',
      notes: lead?.notes ?? '',
      owner: lead?.ownerUserId ? { id: lead.ownerUserId, name: ownerName ?? 'Owner' } : null,
      nextFollowUpAt: toIstInput(lead?.nextFollowUpAt),
    },
  });

  const save = async (v: Values, confirmedNotDuplicate: boolean) => {
    setBusy(true);
    const request = {
      name: v.name.trim(),
      phone: v.phone.trim() ? normalizePhone(v.phone) : null,
      email: v.email.trim() || null,
      gender: v.gender === 'Unknown' ? null : v.gender,
      ageYears: v.ageYears,
      city: v.city.trim() || null,
      source: v.source,
      sourceDetail: v.sourceDetail.trim() || null,
      campaignId: lead?.campaignId ?? null,
      referringDoctorId: v.referringDoctorId,
      interestDepartmentId: v.interestDepartmentId,
      interestPackage: v.interestPackage.trim() || null,
      notes: v.notes.trim() || null,
      ownerUserId: v.owner?.id ?? null,
      nextFollowUpAt: fromIstInput(v.nextFollowUpAt),
      confirmedNotDuplicate,
      version: lead?.version ?? null,
    };
    try {
      if (lead) {
        await crmApi.updateLead(lead.id, request);
        notify.success('Lead saved');
        void queryClient.invalidateQueries({ queryKey: crmKeys.leads });
        onClose();
      } else {
        const created = await crmApi.createLead(request);
        notify.success(
          'Lead added',
          created.existingPatientId
            ? `${created.leadNumber}. This mobile number already belongs to a registered patient.`
            : created.leadNumber,
        );
        void queryClient.invalidateQueries({ queryKey: crmKeys.leads });
        onClose();
        void navigate({ to: '/crm/leads/$leadId', params: { leadId: created.id } });
      }
    } catch (error) {
      if (isApiError(error) && error.code === 'lead.duplicate') {
        const data = error.data as { leadId?: string } | undefined;
        setDuplicate({ message: error.userMessage, leadId: data?.leadId ?? null });
      } else if (isApiError(error) && error.code === 'concurrency') {
        notify.error(error, 'Someone else changed this lead');
        void queryClient.invalidateQueries({ queryKey: crmKeys.leads });
      } else if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the lead');
      }
    } finally {
      setBusy(false);
    }
  };

  return (
    <EditorDrawer
      open
      size="medium"
      title={lead ? `${lead.leadNumber} · ${lead.name}` : 'New lead'}
      dirty={form.formState.isDirty}
      onClose={onClose}
      onSubmit={form.handleSubmit((v) => void save(v, false))}
      busy={busy}
      submitLabel={lead ? 'Save' : 'Add lead'}
    >
      {duplicate && (
        <MessageBar intent="warning" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>This number already has an open lead</MessageBarTitle>
            {duplicate.message}
          </MessageBarBody>
          <MessageBarActions>
            {duplicate.leadId && (
              <Button
                onClick={() => {
                  onClose();
                  void navigate({ to: '/crm/leads/$leadId', params: { leadId: duplicate.leadId! } });
                }}
              >
                Open that lead
              </Button>
            )}
            <Button appearance="subtle" onClick={() => void form.handleSubmit((v) => void save(v, true))()}>
              It is a different person
            </Button>
          </MessageBarActions>
        </MessageBar>
      )}
      <TextField control={form.control} name="name" label="Name" required autoFocus maxLength={200} />
      <FormGrid columns={2} minColumnWidth={180}>
        <TextField control={form.control} name="phone" label="Mobile" type="tel" hint="10 digits" />
        <TextField control={form.control} name="email" label="Email" type="email" />
      </FormGrid>
      <ChoiceField
        control={form.control}
        name="gender"
        label="Gender"
        options={[
          { value: 'Female', label: 'Female' },
          { value: 'Male', label: 'Male' },
          { value: 'Other', label: 'Other' },
          { value: 'Unknown', label: 'Not given' },
        ]}
      />
      <FormGrid columns={2} minColumnWidth={180}>
        <NumberField control={form.control} name="ageYears" label="Age (years)" min={0} max={120} />
        <TextField control={form.control} name="city" label="City / area" />
        <SelectField
          control={form.control}
          name="source"
          label="How they found us"
          required
          options={LEAD_SOURCES.map((s) => ({ value: s, label: leadSourceLabel[s] }))}
        />
        <TextField control={form.control} name="sourceDetail" label="Source detail" hint="Camp name, ad, company…" />
        {canReferrals && (
          <SelectField
            control={form.control}
            name="referringDoctorId"
            label="Referring doctor"
            placeholder="None"
            options={(doctors.data?.items ?? []).map((d) => ({
              value: d.id,
              label: `${d.name}${d.clinic ? `, ${d.clinic}` : ''}`,
            }))}
          />
        )}
        <SelectField
          control={form.control}
          name="interestDepartmentId"
          label="Interested in (department)"
          placeholder="Not sure yet"
          options={toOptions(departments.data)}
        />
      </FormGrid>
      <TextField
        control={form.control}
        name="interestPackage"
        label="Package or service asked about"
        placeholder="For example: Master health check-up"
      />
      <TextAreaField control={form.control} name="notes" label="Notes" rows={3} maxLength={2000} />
      <FormGrid columns={2} minColumnWidth={200}>
        <Controller
          control={form.control}
          name="owner"
          render={({ field }) => <UserPicker label="Owner" value={field.value} onChange={field.onChange} />}
        />
        <Controller
          control={form.control}
          name="nextFollowUpAt"
          render={({ field }) => (
            <Field label="Call back on" hint="Hospital time">
              <Input type="datetime-local" value={field.value} onChange={(_, d) => field.onChange(d.value)} />
            </Field>
          )}
        />
      </FormGrid>
    </EditorDrawer>
  );
}
