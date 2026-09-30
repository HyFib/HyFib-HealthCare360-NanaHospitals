import { Button, Field, Select } from '@fluentui/react-components';
import { AddCircleRegular, CalendarPersonRegular, EditRegular } from '@fluentui/react-icons';
import { ConfirmDialog, EmptyState, FilterBar, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { hrApi, hrKeys, hrQueries } from '@/modules/hr/api';
import { EmployeePicker, type PickedEmployee } from '@/modules/hr/EmployeePicker';
import { AdjustBalanceDialog, BalancesTable } from '@/modules/hr/EmployeeLeave';
import { BALANCE_VIEWERS, HR_PERMISSIONS as P } from '@/modules/hr/labels';
import { LEAVE_NAV } from '@/modules/hr/nav';
import { currentMonth, yearOptions } from '@/modules/hr/shared/dates';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { SubNav } from '@/modules/hr/shared/SubNav';
import { FilterPad } from '@/modules/hr/shared/layout';

interface BalanceSearch {
  employeeId?: string;
  employeeName?: string;
  employeeCode?: string;
  year?: number;
}

export const Route = createFileRoute('/_app/hr/leave/balances')({
  validateSearch: (s: Record<string, unknown>): BalanceSearch => ({
    employeeId: typeof s.employeeId === 'string' && s.employeeId ? s.employeeId : undefined,
    employeeName: typeof s.employeeName === 'string' && s.employeeName ? s.employeeName : undefined,
    employeeCode: typeof s.employeeCode === 'string' && s.employeeCode ? s.employeeCode : undefined,
    year: typeof s.year === 'number' && s.year >= 2000 && s.year <= 2100 ? s.year : undefined,
  }),
  component: () => (
    <RequirePermission permission={BALANCE_VIEWERS} what="leave balances">
      <BalancesPage />
    </RequirePermission>
  ),
});

function BalancesPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canApprove = usePermission(P.leaveApprove);
  const year = search.year ?? currentMonth().year;
  const [crediting, setCrediting] = useState(false);
  const [adjusting, setAdjusting] = useState(false);
  const employee: PickedEmployee | null = search.employeeId
    ? { id: search.employeeId, code: search.employeeCode ?? '', name: search.employeeName ?? 'Selected employee' }
    : null;
  const balances = useQuery({
    ...hrQueries.leaveBalances(search.employeeId ?? '', year),
    enabled: Boolean(search.employeeId),
  });

  const credit = useMutation({
    mutationFn: () => hrApi.creditLeave(year),
    onSuccess: (result) => {
      setCrediting(false);
      notify.success(
        `Leave credited for ${year}`,
        `${result.employees} employee(s); ${result.balancesCreated} new balance(s).`,
      );
      void queryClient.invalidateQueries({ queryKey: hrKeys.leave });
      void queryClient.invalidateQueries({ queryKey: hrKeys.me });
    },
    onError: (error) => notify.error(error, 'Could not credit the leave'),
  });

  return (
    <Page>
      <PageHeader
        title="Leave balances"
        subtitle="Yearly quota, carry-forward, adjustments and leave taken for each employee"
        icon={<CalendarPersonRegular />}
        actions={
          canApprove && (
            <Button appearance="primary" icon={<AddCircleRegular />} onClick={() => setCrediting(true)}>
              Credit leave for {year}
            </Button>
          )
        }
      />
      <SubNav items={LEAVE_NAV} ariaLabel="Leave screens" />
      <Section flush>
        <FilterPad>
          <FilterBar>
            <div style={{ minWidth: 300 }}>
              <EmployeePicker
                value={employee}
                onChange={(value) =>
                  void navigate({
                    search: (prev) => ({
                      ...prev,
                      employeeId: value?.id,
                      employeeName: value?.name,
                      employeeCode: value?.code,
                    }),
                  })
                }
                autoFocus={!employee}
              />
            </div>
            <Field label="Year">
              <Select
                value={String(year)}
                onChange={(_, d) => void navigate({ search: (prev) => ({ ...prev, year: Number(d.value) }) })}
              >
                {yearOptions().map((y) => (
                  <option key={y} value={y}>
                    {y}
                  </option>
                ))}
              </Select>
            </Field>
            {canApprove && employee && (
              <Button icon={<EditRegular />} style={{ alignSelf: 'flex-end' }} onClick={() => setAdjusting(true)}>
                Adjust balance
              </Button>
            )}
          </FilterBar>
        </FilterPad>
        {employee ? (
          <BalancesTable
            balances={balances.data}
            loading={balances.isLoading}
            error={balances.error}
            onRetry={() => void balances.refetch()}
          />
        ) : (
          <EmptyState
            title="Choose an employee"
            description="Type the name or employee code to see their leave balance."
            icon={<CalendarPersonRegular />}
          />
        )}
      </Section>
      <ConfirmDialog
        open={crediting}
        title={`Credit leave for ${year}?`}
        confirmLabel="Credit leave"
        busy={credit.isPending}
        onConfirm={() => credit.mutate()}
        onCancel={() => setCrediting(false)}
      >
        Every current employee gets the yearly quota (pro-rated for people who joined during the year) plus the
        carry-forward from {year - 1}, capped per leave type. It is safe to run again: balances already credited are not
        doubled.
      </ConfirmDialog>
      {employee && (
        <AdjustBalanceDialog open={adjusting} onClose={() => setAdjusting(false)} employee={employee} year={year} />
      )}
    </Page>
  );
}
