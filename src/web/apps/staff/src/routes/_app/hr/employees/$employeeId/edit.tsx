import { Button, MessageBar, MessageBarBody } from '@fluentui/react-components';
import { EditRegular } from '@fluentui/react-icons';
import { isApiError } from '@hc360/core';
import { applyServerErrors, ErrorState, LoadingState, Page, PageHeader, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import type { UseFormReturn } from 'react-hook-form';
import { hrApi, hrKeys, hrQueries, type EmployeeDetail } from '@/modules/hr/api';
import { EmployeeForm } from '@/modules/hr/EmployeeForm';
import { employeeToForm, formToRequest, type EmployeeFormValues } from '@/modules/hr/employeeModel';
import { HR_PERMISSIONS as P } from '@/modules/hr/labels';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';

export const Route = createFileRoute('/_app/hr/employees/$employeeId/edit')({
  component: () => (
    <RequirePermission permission={P.employeesManage} what="editing employees">
      <EditEmployeePage />
    </RequirePermission>
  ),
});

function EditEmployeePage() {
  const { employeeId } = Route.useParams();
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
  return <EditForm key={`${employee.data.id}:${employee.data.version}`} employee={employee.data} />;
}

function EditForm({ employee }: { employee: EmployeeDetail }) {
  const navigate = useNavigate();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [form, setForm] = useState<UseFormReturn<EmployeeFormValues> | null>(null);
  const [defaults] = useState(() => employeeToForm(employee));
  const back = () => void navigate({ to: '/hr/employees/$employeeId', params: { employeeId: employee.id } });

  const save = useMutation({
    mutationFn: (values: EmployeeFormValues) => hrApi.updateEmployee(employee.id, { ...formToRequest(values, employee.version), photoFileId: employee.photoFileId }),
    onSuccess: () => {
      notify.success('Employee saved', employee.fullName);
      void queryClient.invalidateQueries({ queryKey: hrKeys.employees });
      back();
    },
    onError: (error) => {
      if (isApiError(error) && error.code === 'concurrency') {
        notify.error(error, 'Someone else changed this employee');
        void queryClient.invalidateQueries({ queryKey: hrKeys.employees });
      } else if (!(form && applyServerErrors(error, form.setError))) {
        notify.error(error, 'Could not save the employee');
      }
    },
  });

  if (employee.status === 'Separated') {
    return (
      <Page maxWidth={900}>
        <MessageBar intent="info">
          <MessageBarBody>Separated employees are kept for records and cannot be edited.</MessageBarBody>
        </MessageBar>
        <Button onClick={back}>Back to the profile</Button>
      </Page>
    );
  }

  return (
    <Page maxWidth={1400}>
      <PageHeader
        title={`Edit ${employee.fullName}`}
        subtitle={`${employee.employeeCode} · Press Ctrl+S to save.`}
        icon={<EditRegular />}
        breadcrumbs={[
          { label: 'Employees', onClick: () => void navigate({ to: '/hr/employees' }) },
          { label: employee.fullName, onClick: back },
          { label: 'Edit' },
        ]}
      />
      <EmployeeForm
        defaultValues={defaults}
        onSubmit={(values) => save.mutate(values)}
        busy={save.isPending}
        submitLabel="Save changes"
        onFile={{ pan: employee.panMasked }}
        onFormReady={setForm}
        employeeId={employee.id}
        secondaryActions={
          <Button appearance="subtle" onClick={back}>
            Cancel
          </Button>
        }
      />
    </Page>
  );
}
