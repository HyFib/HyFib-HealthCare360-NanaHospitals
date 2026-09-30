import {
  Avatar,
  Button,
  Caption1,
  makeStyles,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Tab,
  TabList,
  Text,
  tokens,
} from '@fluentui/react-components';
import {
  BuildingBankRegular,
  EditRegular,
  MoreHorizontalRegular,
  PersonDeleteRegular,
  ShieldKeyholeRegular,
} from '@fluentui/react-icons';
import { formatAge, formatDateOnly, formatPhone, humanize } from '@hc360/core';
import { ErrorState, LoadingState, Page, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, Link, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { hrQueries, type EmployeeDetail } from '@/modules/hr/api';
import { BankDetailsDialog } from '@/modules/hr/BankDetailsDialog';
import { EmployeeAttendance } from '@/modules/hr/EmployeeAttendance';
import { EmployeeCredentials } from '@/modules/hr/EmployeeCredentials';
import { EmployeeLeave } from '@/modules/hr/EmployeeLeave';
import { EmployeePayslips } from '@/modules/hr/EmployeePayslips';
import { EmployeeSalary } from '@/modules/hr/EmployeeSalary';
import { employeeStatusLabel, HR_PERMISSIONS as P, PAYROLL_VIEWERS } from '@/modules/hr/labels';
import { SeparateDialog } from '@/modules/hr/SeparateDialog';
import { Detail, DetailGrid } from '@/modules/hr/shared/DetailGrid';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { UserName } from '@/modules/hr/shared/UserPicker';
import { Stack } from '@/modules/hr/shared/layout';

const TABS = ['details', 'pay', 'credentials', 'leave', 'attendance', 'salary', 'payslips'] as const;
type TabId = (typeof TABS)[number];

export const Route = createFileRoute('/_app/hr/employees/$employeeId/')({
  validateSearch: (s: Record<string, unknown>): { tab?: TabId } => ({
    tab: TABS.includes(s.tab as TabId) && s.tab !== 'details' ? (s.tab as TabId) : undefined,
  }),
  component: () => (
    <RequirePermission permission={P.employeesRead} what="employee records">
      <EmployeeProfilePage />
    </RequirePermission>
  ),
});

const useStyles = makeStyles({
  header: {
    display: 'flex',
    alignItems: 'center',
    gap: '16px',
    flexWrap: 'wrap',
    padding: '14px 16px',
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusLarge,
    boxShadow: tokens.shadow4,
    borderLeft: `4px solid ${tokens.colorBrandStroke1}`,
  },
  identity: { display: 'flex', flexDirection: 'column', gap: '2px', flex: 1, minWidth: '240px' },
  nameRow: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
  name: { fontSize: tokens.fontSizeBase500, fontWeight: tokens.fontWeightSemibold },
  code: { fontFamily: tokens.fontFamilyMonospace },
  muted: { color: tokens.colorNeutralForeground3 },
  actions: { display: 'flex', gap: '8px', flexWrap: 'wrap' },
  tabs: { padding: '0 8px' },
  body: { padding: '16px' },
});

function EmployeeProfilePage() {
  const styles = useStyles();
  const { employeeId } = Route.useParams();
  const { tab = 'details' } = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canManage = usePermission(P.employeesManage);
  const canAttendance = usePermission(P.attendanceManage);
  const canPayroll = usePermission(PAYROLL_VIEWERS);
  const [bankOpen, setBankOpen] = useState(false);
  const [separateOpen, setSeparateOpen] = useState(false);
  const employee = useQuery(hrQueries.employee(employeeId));

  if (employee.isLoading) {
    return <LoadingState />;
  }
  if (employee.error || !employee.data) {
    return (
      <Page maxWidth={900}>
        <ErrorState error={employee.error} onRetry={() => void employee.refetch()} />
      </Page>
    );
  }

  const e = employee.data;
  const separated = e.status === 'Separated';
  const picked = { id: e.id, code: e.employeeCode, name: e.fullName };
  const visibleTabs: { id: TabId; label: string }[] = [
    { id: 'details', label: 'Details' },
    { id: 'pay', label: 'Pay and statutory' },
    { id: 'credentials', label: 'Credentials' },
    { id: 'leave', label: 'Leave' },
    ...(canAttendance ? [{ id: 'attendance' as const, label: 'Attendance' }] : []),
    ...(canPayroll
      ? [
          { id: 'salary' as const, label: 'Salary' },
          { id: 'payslips' as const, label: 'Payslips' },
        ]
      : []),
  ];
  const current = visibleTabs.some((t) => t.id === tab) ? tab : 'details';

  return (
    <Page>
      <section className={styles.header} aria-label={`Employee ${e.fullName}, ${e.employeeCode}`}>
        <Avatar name={e.fullName} size={48} color="colorful" />
        <div className={styles.identity}>
          <div className={styles.nameRow}>
            <Text className={styles.name} as="h1">
              {e.fullName}
            </Text>
            <Text className={styles.code}>{e.employeeCode}</Text>
            <StatusBadge
              status={e.status}
              label={employeeStatusLabel[e.status]}
              tone={separated ? 'informative' : undefined}
            />
          </div>
          <Caption1 className={styles.muted}>
            {[
              e.designationName,
              e.departmentName,
              humanize(e.employmentType),
              `joined ${formatDateOnly(e.joiningDate)}`,
            ]
              .filter(Boolean)
              .join(' · ')}
          </Caption1>
        </div>
        <div className={styles.actions}>
          <Button appearance="subtle" onClick={() => void navigate({ to: '/hr/employees' })}>
            All employees
          </Button>
          {canManage && !separated && (
            <>
              <Button
                icon={<EditRegular />}
                onClick={() => void navigate({ to: '/hr/employees/$employeeId/edit', params: { employeeId } })}
              >
                Edit
              </Button>
              <Menu>
                <MenuTrigger disableButtonEnhancement>
                  <Button icon={<MoreHorizontalRegular />} aria-label="More actions" />
                </MenuTrigger>
                <MenuPopover>
                  <MenuList>
                    <MenuItem icon={<BuildingBankRegular />} onClick={() => setBankOpen(true)}>
                      Change bank account
                    </MenuItem>
                    <MenuItem icon={<PersonDeleteRegular />} onClick={() => setSeparateOpen(true)}>
                      Separate employee
                    </MenuItem>
                  </MenuList>
                </MenuPopover>
              </Menu>
            </>
          )}
        </div>
      </section>

      {separated && (
        <MessageBar intent="info" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>Separated on {formatDateOnly(e.leavingDate)}</MessageBarTitle>
            {humanize(e.separationReason)}
            {e.separationNote ? ` · ${e.separationNote}` : ''}. The record is kept for statutory retention and cannot be
            edited.
          </MessageBarBody>
        </MessageBar>
      )}

      <Section flush>
        <div className={styles.tabs}>
          <TabList
            selectedValue={current}
            onTabSelect={(_, d) =>
              void navigate({ search: { tab: d.value === 'details' ? undefined : (d.value as TabId) }, replace: true })
            }
          >
            {visibleTabs.map((t) => (
              <Tab key={t.id} value={t.id}>
                {t.label}
              </Tab>
            ))}
          </TabList>
        </div>
        <div className={styles.body}>
          {current === 'details' && <DetailsTab employee={e} />}
          {current === 'pay' && (
            <PayTab employee={e} canChange={canManage && !separated} onChangeBank={() => setBankOpen(true)} />
          )}
          {current === 'credentials' && (
            <EmployeeCredentials employeeId={e.id} employeeName={e.fullName} readOnly={separated} />
          )}
          {current === 'leave' && <EmployeeLeave employee={picked} readOnly={separated} />}
          {current === 'attendance' && <EmployeeAttendance employee={picked} />}
          {current === 'salary' && <EmployeeSalary employeeId={e.id} employeeName={e.fullName} separated={separated} />}
          {current === 'payslips' && <EmployeePayslips employee={picked} />}
        </div>
      </Section>

      {canManage && !separated && (
        <>
          <BankDetailsDialog open={bankOpen} onClose={() => setBankOpen(false)} employee={e} />
          <SeparateDialog open={separateOpen} onClose={() => setSeparateOpen(false)} employee={e} />
        </>
      )}
    </Page>
  );
}

function DetailsTab({ employee: e }: { employee: EmployeeDetail }) {
  const shifts = useQuery(hrQueries.shifts(true));
  const shift = shifts.data?.find((s) => s.id === e.defaultShiftId);
  const address = [
    e.address.line1,
    e.address.line2,
    e.address.city,
    e.address.district,
    e.address.state,
    e.address.pinCode,
  ]
    .filter(Boolean)
    .join(', ');
  return (
    <Stack gap={20}>
      <DetailGrid>
        <Detail label="Date of birth">
          {e.dateOfBirth ? `${formatDateOnly(e.dateOfBirth)} (${formatAge(e.dateOfBirth)})` : null}
        </Detail>
        <Detail label="Gender">{e.gender}</Detail>
        <Detail label="Mobile">{e.phone ? formatPhone(e.phone) : null}</Detail>
        <Detail label="Email">{e.email}</Detail>
        <Detail label="Address" wide>
          {address}
        </Detail>
        <Detail label="Emergency contact">
          {e.emergencyContactName
            ? `${e.emergencyContactName}${e.emergencyContactPhone ? ` · ${formatPhone(e.emergencyContactPhone)}` : ''}`
            : null}
        </Detail>
      </DetailGrid>
      <DetailGrid>
        <Detail label="Department">{e.departmentName}</Detail>
        <Detail label="Designation">{e.designationName}</Detail>
        <Detail label="Employment">{e.employmentType}</Detail>
        <Detail label="Joined">{formatDateOnly(e.joiningDate)}</Detail>
        <Detail label="Confirmed">{e.confirmationDate ? formatDateOnly(e.confirmationDate) : null}</Detail>
        <Detail label="Reporting manager">
          {e.reportingManagerId ? (
            <Link to="/hr/employees/$employeeId" params={{ employeeId: e.reportingManagerId }}>
              {e.reportingManagerName ?? 'Open'}
            </Link>
          ) : null}
        </Detail>
        <Detail label="Usual shift">
          {shift ? `${shift.code} · ${shift.name}` : e.defaultShiftId ? 'Assigned' : 'Rostered'}
        </Detail>
        <Detail label="Fixed weekly off">{e.weeklyOff ?? 'Rostered'}</Detail>
        <Detail label="Attendance">{e.isAttendanceExempt ? 'Exempt' : 'Tracked by terminal'}</Detail>
        <Detail label="Terminal PIN" mono>
          {e.biometricPin}
        </Detail>
        <Detail label="HealthCare360 sign-in">{e.userId ? <UserName id={e.userId} /> : 'Not linked'}</Detail>
        <Detail label="Doctor record">{e.practitionerId ? 'Linked' : null}</Detail>
        {e.notes && (
          <Detail label="Notes" wide>
            {e.notes}
          </Detail>
        )}
      </DetailGrid>
    </Stack>
  );
}

function PayTab({
  employee: e,
  canChange,
  onChangeBank,
}: {
  employee: EmployeeDetail;
  canChange: boolean;
  onChangeBank: () => void;
}) {
  return (
    <Stack gap={16}>
      <MessageBar intent="info" icon={<ShieldKeyholeRegular />} layout="multiline">
        <MessageBarBody>
          Account, IFSC and PAN are stored encrypted and only ever shown masked. The full account number leaves the
          server only in the payroll bank-transfer file, and every download is recorded.
        </MessageBarBody>
      </MessageBar>
      <DetailGrid>
        <Detail label="Salary paid by">{humanize(e.paymentMode)}</Detail>
        <Detail label="Bank">{e.bankName}</Detail>
        <Detail label="Account number" mono>
          {e.bankAccountMasked}
        </Detail>
        <Detail label="IFSC" mono>
          {e.bankIfscMasked}
        </Detail>
        <Detail label="PAN" mono>
          {e.panMasked}
        </Detail>
        <Detail label="UAN" mono>
          {e.uan}
        </Detail>
        <Detail label="PF member number" mono>
          {e.pfNumber}
        </Detail>
        <Detail label="ESI insurance number" mono>
          {e.esiNumber}
        </Detail>
      </DetailGrid>
      {canChange && (
        <div>
          <Button icon={<BuildingBankRegular />} onClick={onChangeBank}>
            Change bank account
          </Button>
        </div>
      )}
    </Stack>
  );
}
