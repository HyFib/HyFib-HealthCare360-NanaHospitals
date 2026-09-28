import { Button } from '@fluentui/react-components';
import { PersonAddRegular } from '@fluentui/react-icons';
import { isApiError } from '@hc360/core';
import { applyServerErrors, Page, PageHeader, useNotify } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import type { UseFormReturn } from 'react-hook-form';
import { hrApi, hrKeys } from '@/modules/hr/api';
import { EmployeeForm } from '@/modules/hr/EmployeeForm';
import { emptyEmployeeForm, formToRequest, type EmployeeFormValues } from '@/modules/hr/employeeModel';
import { HR_PERMISSIONS as P } from '@/modules/hr/labels';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';

export const Route = createFileRoute('/_app/hr/employees/new')({
  component: () => (
    <RequirePermission permission={P.employeesManage} what="adding employees">
      <NewEmployeePage />
    </RequirePermission>
  ),
});

/** Bank details and PAN are part of this form, so it is deliberately not saved as a local draft. */
function NewEmployeePage() {
  const navigate = useNavigate();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [form, setForm] = useState<UseFormReturn<EmployeeFormValues> | null>(null);
  const [defaults] = useState(emptyEmployeeForm);

  const create = useMutation({
    mutationFn: (values: EmployeeFormValues) => hrApi.createEmployee(formToRequest(values, null)),
    onSuccess: (created, values) => {
      notify.success('Employee added', `${values.firstName} ${values.lastName} · ${created.employeeCode}`);
      void queryClient.invalidateQueries({ queryKey: hrKeys.employees });
      void navigate({ to: '/hr/employees/$employeeId', params: { employeeId: created.id } });
    },
    onError: (error) => {
      if (isApiError(error) && error.code === 'employee.bank_account_shared') {
        form?.setError('accountNumber', { type: 'server', message: `${error.userMessage} Save without bank details, then change them from the profile after checking.` });
      } else if (!(form && applyServerErrors(error, form.setError))) {
        notify.error(error, 'Could not add the employee');
      }
    },
  });

  return (
    <Page maxWidth={1400}>
      <PageHeader
        title="Add employee"
        subtitle="The employee code is issued on saving. Press Ctrl+S to save."
        icon={<PersonAddRegular />}
        breadcrumbs={[{ label: 'Employees', onClick: () => void navigate({ to: '/hr/employees' }) }, { label: 'Add employee' }]}
      />
      <EmployeeForm
        defaultValues={defaults}
        onSubmit={(values) => create.mutate(values)}
        busy={create.isPending}
        submitLabel="Add employee"
        onFormReady={setForm}
        secondaryActions={
          <Button appearance="subtle" onClick={() => void navigate({ to: '/hr/employees' })}>
            Cancel
          </Button>
        }
      />
    </Page>
  );
}
