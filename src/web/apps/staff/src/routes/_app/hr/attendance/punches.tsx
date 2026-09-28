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
  Input,
  MessageBar,
  MessageBarBody,
  Spinner,
  Tab,
  TabList,
  Text,
} from '@fluentui/react-components';
import { FingerprintRegular, PersonLinkRegular } from '@fluentui/react-icons';
import { formatDateTime, humanize, isApiError, todayIso } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { hrApi, hrKeys, hrQueries, type UnmatchedPinDto } from '@/modules/hr/api';
import { EmployeePicker, type PickedEmployee } from '@/modules/hr/EmployeePicker';
import { withBiometricPin } from '@/modules/hr/employeeModel';
import { HR_PERMISSIONS as P } from '@/modules/hr/labels';
import { ATTENDANCE_NAV } from '@/modules/hr/nav';
import { addDays, diffDays, isIsoDate } from '@/modules/hr/shared/dates';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { SubNav } from '@/modules/hr/shared/SubNav';
import { FilterPad } from '@/modules/hr/shared/layout';

interface PunchSearch {
  tab?: 'unmatched';
  employeeId?: string;
  from?: string;
  to?: string;
}

export const Route = createFileRoute('/_app/hr/attendance/punches')({
  validateSearch: (s: Record<string, unknown>): PunchSearch => ({
    tab: s.tab === 'unmatched' ? 'unmatched' : undefined,
    employeeId: typeof s.employeeId === 'string' && s.employeeId ? s.employeeId : undefined,
    from: isIsoDate(s.from) ? s.from : undefined,
    to: isIsoDate(s.to) ? s.to : undefined,
  }),
  component: () => (
    <RequirePermission permission={P.attendanceManage} what="terminal punches">
      <PunchesPage />
    </RequirePermission>
  ),
});

function PunchesPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });

  return (
    <Page>
      <PageHeader
        title="Punches"
        subtitle="Raw in/out records received from the attendance terminals. First punch of the day is the in-time, last is the out-time."
        icon={<FingerprintRegular />}
      />
      <SubNav items={ATTENDANCE_NAV} ariaLabel="Attendance screens" />
      <TabList selectedValue={search.tab ?? 'punches'} onTabSelect={(_, d) => void navigate({ search: (prev) => ({ ...prev, tab: d.value === 'unmatched' ? 'unmatched' : undefined }) })}>
        <Tab value="punches">Punches by day</Tab>
        <Tab value="unmatched">Unknown PINs</Tab>
      </TabList>
      {search.tab === 'unmatched' ? <UnmatchedPins /> : <PunchList search={search} />}
    </Page>
  );
}

function PunchList({ search }: { search: PunchSearch }) {
  const navigate = useNavigate({ from: Route.fullPath });
  const canReadEmployees = usePermission(P.employeesRead);
  const today = todayIso();
  const to = search.to ?? today;
  const from = search.from ?? addDays(to, -1);
  const tooLong = diffDays(from, to) > 31 || diffDays(from, to) < 0;
  const employee = useQuery({ ...hrQueries.employee(search.employeeId ?? ''), enabled: Boolean(search.employeeId) && canReadEmployees });
  const punches = useQuery({ ...hrQueries.punches(from, to, search.employeeId), enabled: !tooLong });
  const picked: PickedEmployee | null = search.employeeId
    ? { id: search.employeeId, code: employee.data?.employeeCode ?? '', name: employee.data?.fullName ?? punches.data?.find((p) => p.employeeName)?.employeeName ?? 'Selected employee' }
    : null;

  return (
    <Section flush>
      <FilterPad>
        <FilterBar>
          {canReadEmployees && (
            <div style={{ minWidth: 280 }}>
              <EmployeePicker
                label="Employee"
                value={picked}
                onChange={(value) => void navigate({ search: (prev) => ({ ...prev, employeeId: value?.id }) })}
                hint="Leave empty to see every punch in the period."
              />
            </div>
          )}
          <Field label="From">
            <Input type="date" value={from} max={today} onChange={(_, d) => isIsoDate(d.value) && void navigate({ search: (prev) => ({ ...prev, from: d.value }) })} />
          </Field>
          <Field label="To" validationMessage={tooLong ? 'Choose at most 31 days.' : undefined} validationState={tooLong ? 'error' : 'none'}>
            <Input type="date" value={to} max={today} onChange={(_, d) => isIsoDate(d.value) && void navigate({ search: (prev) => ({ ...prev, to: d.value }) })} />
          </Field>
        </FilterBar>
      </FilterPad>
      <DataTable
        ariaLabel="Punches"
        rows={tooLong ? [] : punches.data}
        loading={punches.isLoading}
        error={punches.error}
        onRetry={() => void punches.refetch()}
        getRowId={(p) => p.id}
        columns={[
          { key: 'time', header: 'Time', width: 190, render: (p) => formatDateTime(p.punchedAt) },
          {
            key: 'employee',
            header: 'Employee',
            render: (p) => (p.employeeName ? <Text weight="semibold">{p.employeeName}</Text> : <Caption1>Unknown PIN {p.pin}</Caption1>),
          },
          { key: 'pin', header: 'PIN', width: 90, priority: 'low', render: (p) => <Text font="monospace">{p.pin}</Text> },
          { key: 'direction', header: 'In / out', width: 90, render: (p) => (p.direction === 'Unknown' ? '—' : p.direction) },
          { key: 'source', header: 'From', priority: 'low', render: (p) => (p.source === 'Regularisation' ? 'Approved correction' : (p.deviceName ?? humanize(p.source))) },
        ]}
        emptyTitle="No punches in this period"
        emptyDescription="Check that the terminal shows as online on the Terminals screen."
        size="small"
      />
    </Section>
  );
}

function UnmatchedPins() {
  const canManage = usePermission(P.employeesManage);
  const canRead = usePermission(P.employeesRead);
  const canAssign = canManage && canRead;
  const pins = useQuery(hrQueries.unmatchedPins());
  const [assigning, setAssigning] = useState<UnmatchedPinDto | null>(null);

  return (
    <Section flush title="PINs not linked to any employee" description="Usually an enrolment typo. Punches are kept and are counted once the PIN is assigned.">
      <DataTable
        ariaLabel="Unknown PINs"
        rows={pins.data}
        loading={pins.isLoading}
        error={pins.error}
        onRetry={() => void pins.refetch()}
        getRowId={(p) => p.pin}
        columns={[
          { key: 'pin', header: 'PIN', width: 120, render: (p) => <Text font="monospace">{p.pin}</Text> },
          { key: 'count', header: 'Punches', align: 'end', width: 100, render: (p) => p.punchCount },
          { key: 'first', header: 'First seen', render: (p) => formatDateTime(p.firstPunchAt) },
          { key: 'last', header: 'Last seen', render: (p) => formatDateTime(p.lastPunchAt) },
        ]}
        rowActions={
          canAssign
            ? (p) => (
                <Button size="small" icon={<PersonLinkRegular />} onClick={() => setAssigning(p)}>
                  Assign to employee
                </Button>
              )
            : undefined
        }
        emptyTitle="Every PIN is linked"
        emptyDescription="All punches received belong to known employees."
        size="small"
      />
      {assigning && <AssignPinDialog pin={assigning} onClose={() => setAssigning(null)} />}
    </Section>
  );
}

function AssignPinDialog({ pin, onClose }: { pin: UnmatchedPinDto; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [employee, setEmployee] = useState<PickedEmployee | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const assign = async () => {
    if (!employee) {
      setError('Choose the employee.');
      return;
    }
    setBusy(true);
    try {
      const detail = await queryClient.fetchQuery(hrQueries.employee(employee.id));
      await hrApi.updateEmployee(detail.id, withBiometricPin(detail, pin.pin));
      notify.success('PIN assigned', `${pin.pin} → ${employee.name}. Recompute attendance for the days concerned.`);
      void queryClient.invalidateQueries({ queryKey: hrKeys.all });
      onClose();
    } catch (err) {
      if (isApiError(err) && err.code === 'employee.pin_in_use') {
        setError(err.userMessage);
      } else {
        notify.error(err, 'Could not assign the PIN');
      }
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !busy && onClose()}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <DialogBody>
          <DialogTitle>Assign PIN {pin.pin}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <EmployeePicker value={employee} onChange={setEmployee} required autoFocus validationMessage={error ?? undefined} />
            <MessageBar intent="info">
              <MessageBarBody>The employee's terminal PIN is changed to {pin.pin}. Their {pin.punchCount} earlier punch(es) are linked to them.</MessageBarBody>
            </MessageBar>
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose} disabled={busy}>
              Cancel
            </Button>
            <Button appearance="primary" disabled={busy || !employee} icon={busy ? <Spinner size="tiny" /> : undefined} onClick={() => void assign()}>
              Assign PIN
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
