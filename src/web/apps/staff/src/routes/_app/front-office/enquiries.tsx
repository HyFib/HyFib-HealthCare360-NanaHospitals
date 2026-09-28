import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  SearchBox,
  Tab,
  TabList,
  Text,
} from '@fluentui/react-components';
import { AddRegular, PersonAddRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatRelative, humanize, normalizePhone, useDebouncedValue } from '@hc360/core';
import { applyServerErrors, DataTable, FilterBar, FormGrid, Page, PageHeader, Section, SelectField, StatusBadge, TextAreaField, TextField, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { api } from '@/lib/api';
import { enumOptions } from '@/lib/lookups';
import { Can } from '@/lib/permissions';
import { frontOfficeQueries, type EnquiryChannel, type EnquiryDto, type EnquiryStatus, type EnquiryType } from '@/modules/front-office/api';

export const Route = createFileRoute('/_app/front-office/enquiries')({
  component: EnquiriesPage,
});

const TYPES = ['General', 'Appointment', 'DoctorAvailability', 'Billing', 'Insurance', 'HealthPackage', 'Admission', 'Complaint', 'Other'] as const;
const CHANNELS = ['WalkIn', 'Phone', 'WhatsApp', 'Email', 'Website'] as const;
const STATUSES = ['Open', 'InProgress', 'Resolved', 'Converted'] as const;

const schema = z.object({
  name: z.string().trim().min(1, 'Enter the caller or visitor name.').max(200),
  phone: z
    .string()
    .trim()
    .refine((v) => !v || normalizePhone(v) !== null, 'Enter a valid phone number.'),
  type: z.enum(TYPES),
  channel: z.enum(CHANNELS),
  details: z.string().trim().min(1, 'Describe the enquiry.').max(2000),
  status: z.enum(STATUSES),
  response: z.string().trim().max(2000),
});

type EnquiryForm = z.infer<typeof schema>;

function EnquiriesPage() {
  const navigate = useNavigate();
  const [status, setStatus] = useState<EnquiryStatus | 'All'>('Open');
  const [page, setPage] = useState(1);
  const [term, setTerm] = useState('');
  const search = useDebouncedValue(term.trim(), 300);
  const [editing, setEditing] = useState<EnquiryDto | 'new' | null>(null);
  const enquiries = useQuery(frontOfficeQueries.enquiries(status === 'All' ? undefined : status, page, search || undefined));

  return (
    <Page>
      <PageHeader
        title="Enquiries"
        subtitle="Walk-in, phone and WhatsApp questions, with the answer given"
        actions={
          <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing('new')}>
            New enquiry
          </Button>
        }
      />
      <Section flush>
        <div style={{ padding: '0 16px 8px' }}>
          <FilterBar>
            <TabList
              selectedValue={status}
              onTabSelect={(_, d) => {
                setStatus(d.value as EnquiryStatus | 'All');
                setPage(1);
              }}
            >
              <Tab value="Open">Open</Tab>
              <Tab value="InProgress">In progress</Tab>
              <Tab value="Resolved">Resolved</Tab>
              <Tab value="Converted">Converted</Tab>
              <Tab value="All">All</Tab>
            </TabList>
            <Field style={{ marginLeft: 'auto', minWidth: 240 }}>
              <SearchBox value={term} onChange={(_, d) => setTerm(d.value)} placeholder="Name or phone" aria-label="Search enquiries" />
            </Field>
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Enquiries"
          rows={enquiries.data?.items}
          loading={enquiries.isLoading}
          error={enquiries.error}
          onRetry={() => void enquiries.refetch()}
          getRowId={(e) => e.id}
          onRowClick={setEditing}
          columns={[
            { key: 'when', header: 'Received', width: 130, render: (e) => formatRelative(e.createdAt) },
            { key: 'name', header: 'Name', render: (e) => <Text weight="semibold">{e.name}</Text> },
            { key: 'phone', header: 'Phone', priority: 'low', render: (e) => e.phone ?? '—' },
            { key: 'type', header: 'About', render: (e) => humanize(e.type) },
            { key: 'channel', header: 'Via', priority: 'low', render: (e) => humanize(e.channel) },
            { key: 'details', header: 'Details', priority: 'low', render: (e) => e.details },
            { key: 'status', header: 'Status', render: (e) => <StatusBadge status={e.status} /> },
          ]}
          emptyTitle="No enquiries here"
          page={page}
          pageSize={25}
          totalCount={enquiries.data?.totalCount}
          onPageChange={setPage}
          size="small"
        />
      </Section>
      {editing && (
        <EnquiryDialog
          enquiry={editing === 'new' ? null : editing}
          onClose={() => setEditing(null)}
          onRegister={(name, phone) => void navigate({ to: '/patients/register', search: { name, phone: phone ?? undefined } })}
        />
      )}
    </Page>
  );
}

function EnquiryDialog({ enquiry, onClose, onRegister }: { enquiry: EnquiryDto | null; onClose: () => void; onRegister: (name: string, phone: string | null) => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<EnquiryForm>({
    resolver: zodResolver(schema),
    defaultValues: {
      name: enquiry?.name ?? '',
      phone: enquiry?.phone ?? '',
      type: enquiry?.type ?? 'General',
      channel: enquiry?.channel ?? 'WalkIn',
      details: enquiry?.details ?? '',
      status: enquiry?.status ?? 'Open',
      response: enquiry?.response ?? '',
    },
  });

  const save = useMutation({
    mutationFn: (values: EnquiryForm) => {
      const body = {
        name: values.name,
        phone: values.phone || null,
        type: values.type as EnquiryType,
        channel: values.channel as EnquiryChannel,
        details: values.details,
        status: values.status as EnquiryStatus,
        response: values.response || null,
        patientId: enquiry?.patientId ?? null,
        version: enquiry?.version ?? null,
      };
      return enquiry ? api.put(`/api/v1/front-office/enquiries/${enquiry.id}`, body) : api.post('/api/v1/front-office/enquiries', body);
    },
    onSuccess: () => {
      notify.success(enquiry ? 'Enquiry updated' : 'Enquiry recorded');
      void queryClient.invalidateQueries({ queryKey: ['front-office'] });
      onClose();
    },
    onError: (error) => {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the enquiry');
      }
    },
  });

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 640 }}>
        <form onSubmit={form.handleSubmit((values) => save.mutate(values))} noValidate>
          <DialogBody>
            <DialogTitle>{enquiry ? 'Enquiry' : 'New enquiry'}</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              <FormGrid columns={2}>
                <TextField control={form.control} name="name" label="Name" required autoFocus />
                <TextField control={form.control} name="phone" label="Phone" type="tel" />
                <SelectField control={form.control} name="type" label="About" options={enumOptions(TYPES, { DoctorAvailability: 'Doctor availability', HealthPackage: 'Health package' })} />
                <SelectField control={form.control} name="channel" label="Received via" options={enumOptions(CHANNELS, { WalkIn: 'Walk-in', WhatsApp: 'WhatsApp' })} />
              </FormGrid>
              <TextAreaField control={form.control} name="details" label="What did they ask?" required rows={3} maxLength={2000} />
              <TextAreaField control={form.control} name="response" label="Answer given / action taken" rows={2} maxLength={2000} />
              <SelectField control={form.control} name="status" label="Status" options={enumOptions(STATUSES, { InProgress: 'In progress' })} />
            </DialogContent>
            <DialogActions>
              <Can permission="patients.register">
                <Button
                  icon={<PersonAddRegular />}
                  onClick={() => {
                    const { name, phone } = form.getValues();
                    onRegister(name, phone || null);
                  }}
                >
                  Register as patient
                </Button>
              </Can>
              <Button onClick={onClose}>Cancel</Button>
              <Button type="submit" appearance="primary" disabled={save.isPending}>
                Save
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
