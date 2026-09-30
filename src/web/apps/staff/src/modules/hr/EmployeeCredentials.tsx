import { Button, Menu, MenuItem, MenuList, MenuPopover, MenuTrigger } from '@fluentui/react-components';
import {
  AddRegular,
  ArrowDownloadRegular,
  DeleteRegular,
  EditRegular,
  MoreHorizontalRegular,
} from '@fluentui/react-icons';
import { formatDateOnly } from '@hc360/core';
import { ConfirmDialog, DataTable, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { hrApi, hrKeys, hrQueries, type CredentialDto } from './api';
import { CredentialDialog } from './CredentialDialog';
import { credentialExpiry, credentialTypeLabel, HR_PERMISSIONS } from './labels';
import { downloadStoredFile } from './shared/files';
import { Toolbar } from './shared/layout';

/** Licences and certifications of one employee (NABH HRM: valid registration for every clinician). */
export function EmployeeCredentials({
  employeeId,
  employeeName,
  readOnly,
}: {
  employeeId: string;
  employeeName: string;
  readOnly?: boolean;
}) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canManage = usePermission(HR_PERMISSIONS.credentialsManage) && !readOnly;
  const credentials = useQuery(hrQueries.employeeCredentials(employeeId));
  const [editing, setEditing] = useState<CredentialDto | 'new' | null>(null);
  const [deleting, setDeleting] = useState<CredentialDto | null>(null);

  const remove = useMutation({
    mutationFn: (id: string) => hrApi.deleteCredential(id),
    onSuccess: () => {
      setDeleting(null);
      notify.success('Credential removed');
      void queryClient.invalidateQueries({ queryKey: hrKeys.credentials });
    },
    onError: (error) => notify.error(error, 'Could not remove the credential'),
  });

  const download = (fileId: string) =>
    void downloadStoredFile(fileId).catch((error: unknown) => notify.error(error, 'Could not open the certificate'));

  return (
    <>
      {canManage && (
        <Toolbar>
          <Button icon={<AddRegular />} onClick={() => setEditing('new')}>
            Add credential
          </Button>
        </Toolbar>
      )}
      <DataTable
        ariaLabel="Credentials"
        rows={credentials.data}
        loading={credentials.isLoading}
        error={credentials.error}
        onRetry={() => void credentials.refetch()}
        getRowId={(c) => c.id}
        onRowClick={canManage ? (c) => setEditing(c) : undefined}
        columns={[
          { key: 'type', header: 'Credential', render: (c) => `${credentialTypeLabel[c.type]} · ${c.name}` },
          { key: 'number', header: 'Number', render: (c) => c.number },
          { key: 'issuer', header: 'Issued by', priority: 'low', render: (c) => c.issuingAuthority ?? '—' },
          { key: 'issued', header: 'Issued', priority: 'low', render: (c) => formatDateOnly(c.issuedOn) },
          {
            key: 'expires',
            header: 'Valid until',
            render: (c) => (c.expiresOn ? formatDateOnly(c.expiresOn) : 'Lifetime'),
          },
          {
            key: 'stage',
            header: 'Status',
            render: (c) => {
              const expiry = credentialExpiry(c.stage, c.daysToExpiry);
              return <StatusBadge status={c.stage} tone={expiry.tone} label={expiry.label} />;
            },
          },
        ]}
        rowActions={(c) => (
          <Menu>
            <MenuTrigger disableButtonEnhancement>
              <Button
                size="small"
                appearance="subtle"
                icon={<MoreHorizontalRegular />}
                aria-label={`Actions for ${c.name}`}
              />
            </MenuTrigger>
            <MenuPopover>
              <MenuList>
                {c.fileId && (
                  <MenuItem icon={<ArrowDownloadRegular />} onClick={() => download(c.fileId!)}>
                    Download certificate
                  </MenuItem>
                )}
                {canManage && (
                  <MenuItem icon={<EditRegular />} onClick={() => setEditing(c)}>
                    Edit
                  </MenuItem>
                )}
                {canManage && (
                  <MenuItem icon={<DeleteRegular />} onClick={() => setDeleting(c)}>
                    Remove
                  </MenuItem>
                )}
              </MenuList>
            </MenuPopover>
          </Menu>
        )}
        emptyTitle="No credentials recorded"
        emptyDescription={
          canManage
            ? 'Add the council registration and BLS/ACLS certificates so HR is reminded before they expire.'
            : undefined
        }
        size="small"
      />
      <CredentialDialog
        open={editing !== null}
        onClose={() => setEditing(null)}
        employeeId={employeeId}
        employeeName={employeeName}
        credential={editing === 'new' ? null : editing}
      />
      <ConfirmDialog
        open={deleting !== null}
        title="Remove this credential?"
        confirmLabel="Remove"
        destructive
        busy={remove.isPending}
        onConfirm={() => deleting && remove.mutate(deleting.id)}
        onCancel={() => setDeleting(null)}
      >
        {deleting
          ? `${credentialTypeLabel[deleting.type]} ${deleting.number} will no longer be tracked for expiry.`
          : null}
      </ConfirmDialog>
    </>
  );
}
