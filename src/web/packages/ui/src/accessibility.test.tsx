import { Button, Checkbox, FluentProvider } from '@fluentui/react-components';
import { AddRegular, MoreHorizontalRegular, PeopleRegular } from '@fluentui/react-icons';
import { ApiError } from '@hc360/core';
import { act, useEffect, type ReactNode } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import { useForm } from 'react-hook-form';
import { afterEach, beforeEach, describe, it, vi } from 'vitest';
import { PatientBanner, PatientFlagBadges, type PatientBannerData } from './clinical/PatientBanner';
import { DataTable, type Column } from './data/DataTable';
import { ConfirmDialog, EmptyState, ErrorState, KpiCard, LoadingState, StatusBadge } from './feedback/feedback';
import { CheckboxGroup } from './forms/checkboxGroup';
import {
  ChoiceField,
  DateField,
  LookupField,
  NumberField,
  SelectField,
  SwitchField,
  TextAreaField,
  TextField,
} from './forms/fields';
import { FilterBar, FormGrid, FullRow, Page, PageHeader, Section } from './layout/layout';
import { expectNoAxeViolations } from './testing/axe';
import { hc360LightTheme } from './theme/theme';

/** The shared components as screens use them, checked with axe-core for WCAG 2.2 A and AA problems (SRS UX-10). */

let container: HTMLDivElement;
let root: Root;

beforeEach(() => {
  (globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
  // Fluent's MessageBar measures itself; jsdom has no ResizeObserver.
  vi.stubGlobal(
    'ResizeObserver',
    class {
      observe() {}
      unobserve() {}
      disconnect() {}
    },
  );
  container = document.createElement('div');
  document.body.append(container);
  root = createRoot(container);
});

afterEach(() => {
  act(() => root.unmount());
  container.remove();
  vi.unstubAllGlobals();
});

async function show(ui: ReactNode) {
  await act(async () => root.render(<FluentProvider theme={hc360LightTheme}>{ui}</FluentProvider>));
}

interface Patient {
  id: string;
  name: string;
  uhid: string;
  status: string;
}

const PATIENTS: Patient[] = [
  { id: '1', name: 'Asha Devi', uhid: 'NH26000123', status: 'Active' },
  { id: '2', name: 'Ravi Shankar', uhid: 'NH26000124', status: 'Deceased' },
];

const COLUMNS: Column<Patient>[] = [
  { key: 'name', header: 'Name', sortable: true, render: (row) => row.name },
  { key: 'uhid', header: 'UHID', render: (row) => row.uhid, priority: 'low' },
  { key: 'status', header: 'Status', align: 'end', render: (row) => <StatusBadge status={row.status} /> },
];

describe('DataTable', () => {
  it('has no problems with rows, sorting, row buttons and paging', async () => {
    await show(
      <DataTable
        ariaLabel="Patients"
        columns={COLUMNS}
        rows={PATIENTS}
        getRowId={(row) => row.id}
        onRowClick={() => undefined}
        sort={{ key: 'name', desc: false }}
        onSortChange={() => undefined}
        rowActions={(row) => (
          <>
            <Button size="small">Open</Button>
            <Button
              size="small"
              appearance="subtle"
              icon={<MoreHorizontalRegular />}
              aria-label={`More for ${row.name}`}
            />
          </>
        )}
        page={2}
        pageSize={2}
        totalCount={7}
        onPageChange={() => undefined}
      />,
    );
    await expectNoAxeViolations(container);
  });

  it('has no problems while loading', async () => {
    await show(
      <DataTable
        ariaLabel="Patients"
        columns={COLUMNS}
        rows={undefined}
        loading
        getRowId={(row) => row.id}
        rowActions={() => <Button size="small">Open</Button>}
      />,
    );
    await expectNoAxeViolations(container);
  });

  it('has no problems when empty', async () => {
    await show(
      <DataTable
        ariaLabel="Patients"
        columns={COLUMNS}
        rows={[]}
        getRowId={(row) => row.id}
        emptyTitle="No patients yet"
        emptyDescription="Register a patient to see them here."
        emptyAction={
          <Button appearance="primary" icon={<AddRegular />}>
            Register patient
          </Button>
        }
      />,
    );
    await expectNoAxeViolations(container);
  });

  it('has no problems when the rows cannot be loaded', async () => {
    await show(
      <DataTable
        ariaLabel="Patients"
        columns={COLUMNS}
        rows={undefined}
        error={new ApiError({ status: 500, title: 'Something went wrong', traceId: '00-abc' })}
        onRetry={() => undefined}
        getRowId={(row) => row.id}
      />,
    );
    await expectNoAxeViolations(container);
  });
});

interface Registration {
  name: string;
  notes: string;
  age: number | null;
  department: string | null;
  dateOfBirth: string | null;
  smsConsent: boolean;
  gender: string;
  doctor: string | null;
}

/** Every form field, laid out as a screen does, optionally with each field's validation message showing. */
function RegistrationForm({ withErrors }: { withErrors?: boolean }) {
  const form = useForm<Registration>({
    defaultValues: {
      name: '',
      notes: '',
      age: null,
      department: null,
      dateOfBirth: null,
      smsConsent: true,
      gender: '',
      doctor: null,
    },
  });
  useEffect(() => {
    if (withErrors) {
      for (const name of ['name', 'notes', 'age', 'department', 'dateOfBirth', 'gender', 'doctor'] as const) {
        form.setError(name, { type: 'server', message: 'Check this field' });
      }
    }
  }, [form, withErrors]);
  const { control } = form;
  return (
    <form aria-label="Register patient" onSubmit={(event) => event.preventDefault()}>
      <FormGrid columns={3}>
        <TextField control={control} name="name" label="Full name" required hint="As on an ID card" />
        <NumberField control={control} name="age" label="Age (years)" min={0} max={130} />
        <DateField control={control} name="dateOfBirth" label="Date of birth" max="2026-10-10" />
        <SelectField
          control={control}
          name="department"
          label="Department"
          placeholder="Choose a department"
          options={[
            { value: 'gm', label: 'General medicine' },
            { value: 'ortho', label: 'Orthopaedics' },
          ]}
        />
        <ChoiceField
          control={control}
          name="gender"
          label="Gender"
          required
          options={[
            { value: 'Female', label: 'Female' },
            { value: 'Male', label: 'Male' },
            { value: 'Other', label: 'Other' },
          ]}
        />
        <LookupField
          control={control}
          name="doctor"
          label="Consulting doctor"
          load={async () => [{ id: 'd1', code: 'D001', name: 'Dr. Meera Iyer' }]}
          initialItems={[{ id: 'd1', code: 'D001', name: 'Dr. Meera Iyer' }]}
        />
        <SwitchField control={control} name="smsConsent" label="Send SMS updates" hint="Appointment reminders" />
        <FullRow>
          <CheckboxGroup
            label="Remind by"
            required
            hint="Every channel ticked gets the reminder"
            validationMessage={withErrors ? 'Check this field' : undefined}
          >
            <Checkbox label="SMS" />
            <Checkbox label="WhatsApp" />
            <Checkbox label="Email" />
          </CheckboxGroup>
        </FullRow>
        <FullRow>
          <TextAreaField control={control} name="notes" label="Notes" maxLength={500} />
        </FullRow>
      </FormGrid>
      <Button type="submit" appearance="primary">
        Register
      </Button>
    </form>
  );
}

describe('form fields', () => {
  it('have no problems', async () => {
    await show(<RegistrationForm />);
    await expectNoAxeViolations(container);
  });

  it('have no problems while they show validation messages', async () => {
    await show(<RegistrationForm withErrors />);
    await expectNoAxeViolations(container);
  });
});

describe('feedback', () => {
  it('has no problems in states, badges and KPI cards', async () => {
    await show(
      <>
        <LoadingState />
        <EmptyState
          title="No bills today"
          description="Create a bill to see it here."
          action={<Button appearance="primary">New bill</Button>}
        />
        <ErrorState
          error={new ApiError({ status: 500, title: 'Something went wrong', traceId: '00-abc' })}
          onRetry={() => undefined}
        />
        <ErrorState error={new Error('offline')} />
        {['Completed', 'PartiallyPaid', 'InProgress', 'Cancelled', 'Unknown'].map((status) => (
          <StatusBadge key={status} status={status} />
        ))}
        <KpiCard label="Patients today" value={42} hint="8 more than yesterday" icon={<PeopleRegular />} />
        <KpiCard label="Waiting" value={7} onClick={() => undefined} />
      </>,
    );
    await expectNoAxeViolations(container);
  });

  it('has no problems in an open confirmation dialog', async () => {
    await show(
      <ConfirmDialog
        open
        title="Cancel this bill?"
        confirmLabel="Cancel bill"
        destructive
        onConfirm={vi.fn()}
        onCancel={vi.fn()}
      >
        The bill will be kept, marked as cancelled.
      </ConfirmDialog>,
    );
    await expectNoAxeViolations(document.body);
  });

  it('has no problems in a confirmation dialog while it works', async () => {
    await show(
      <ConfirmDialog open busy title="Cancel this bill?" onConfirm={vi.fn()} onCancel={vi.fn()}>
        The bill will be kept, marked as cancelled.
      </ConfirmDialog>,
    );
    await expectNoAxeViolations(document.body);
  });
});

describe('page anatomy', () => {
  it('has no problems in a page with a header, breadcrumbs, filters and sections', async () => {
    await show(
      <Page>
        <PageHeader
          title="Patients"
          subtitle="Everyone registered at this hospital"
          icon={<PeopleRegular />}
          breadcrumbs={[{ label: 'Front office', onClick: () => undefined }, { label: 'Patients' }]}
          actions={
            <Button appearance="primary" icon={<AddRegular />}>
              Register patient
            </Button>
          }
        />
        <FilterBar>
          <Button>Today</Button>
        </FilterBar>
        <Section title="Recent" description="Registered this week" actions={<Button size="small">See all</Button>}>
          <p>Nobody yet.</p>
        </Section>
        <Section flush>
          <p>A section without a title.</p>
        </Section>
      </Page>,
    );
    await expectNoAxeViolations(container);
  });
});

const PATIENT: PatientBannerData = {
  id: 'p1',
  uhid: 'NH26000123',
  fullName: 'Asha Devi',
  gender: 'Female',
  age: '39Y',
  phone: '+919876543210',
  bloodGroup: 'BPositive',
  category: 'Staff',
  isVip: true,
  isDeceased: false,
  allergies: ['Penicillin', 'Peanuts'],
  allergyStatus: 'recorded',
  alerts: [
    'Fall risk',
    { label: 'Isolation: contact', tone: 'danger' },
    { label: 'Interpreter needed', tone: 'informative' },
  ],
  payer: 'Star Health',
  photoUrl: null,
};

describe('patient banner', () => {
  it('has no problems with every flag showing', async () => {
    await show(<PatientBanner patient={PATIENT} actions={<Button size="small">Edit</Button>} />);
    await expectNoAxeViolations(container);
  });

  it('has no problems for each allergy state', async () => {
    await show(
      <>
        <PatientFlagBadges flags={{ allergyStatus: 'none' }} />
        <PatientFlagBadges flags={{ allergyStatus: 'unknown' }} />
        <PatientFlagBadges flags={{ allergyStatus: 'checking' }} />
        <PatientFlagBadges flags={{ allergyStatus: 'unavailable', allergyNote: 'Hidden: restricted record' }} />
        <PatientBanner
          patient={{ ...PATIENT, isVip: false, isDeceased: true, allergies: [], alerts: [], photoUrl: 'data:,' }}
        />
      </>,
    );
    await expectNoAxeViolations(container);
  });
});
