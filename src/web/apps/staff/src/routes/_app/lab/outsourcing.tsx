import {
  Badge,
  Button,
  Caption1,
  Field,
  makeStyles,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  Select,
  Switch,
  Text,
  tokens,
} from '@fluentui/react-components';
import {
  DocumentArrowDownRegular,
  DocumentBulletListRegular,
  MoreHorizontalRegular,
  VehicleTruckProfileRegular,
} from '@fluentui/react-icons';
import { formatDate, formatDateTime } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { LAB_PERMISSIONS, labQueries, type OutsourcedItem } from '@/modules/lab/api';
import { ItemStatusBadge } from '@/modules/lab/badges';
import { searchFlag, searchId, searchPage } from '@/modules/lab/common/search';
import { ReceiveReportDialog, type ReceiveReportTarget } from '@/modules/lab/ReceiveReportDialog';
import { SendOutDialog, type SendOutTarget } from '@/modules/lab/SendOutDialog';
import { useLabLive } from '@/modules/lab/useLabLive';

interface OutsourcingSearch {
  all?: true;
  labId?: string;
  page?: number;
}

export const Route = createFileRoute('/_app/lab/outsourcing')({
  validateSearch: (s: Record<string, unknown>): OutsourcingSearch => ({
    all: searchFlag(s.all),
    labId: searchId(s.labId),
    page: searchPage(s.page),
  }),
  component: OutsourcingPage,
});

const useStyles = makeStyles({
  muted: { color: tokens.colorNeutralForeground3 },
  mono: { fontFamily: tokens.fontFamilyMonospace },
  cell: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  overdue: { color: tokens.colorPaletteRedForeground1, fontWeight: tokens.fontWeightSemibold },
});

/**
 * Tests sent to reference laboratories (SRS LIS-08): what still has to go, what is on its way and overdue against the
 * laboratory's agreed turnaround, and receipt of the external report.
 */
function OutsourcingPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canSend = usePermission(LAB_PERMISSIONS.receive);
  const canReceive = usePermission(LAB_PERMISSIONS.enter);
  const [sendOut, setSendOut] = useState<SendOutTarget | null>(null);
  const [receive, setReceive] = useState<ReceiveReportTarget | null>(null);
  useLabLive();

  const labs = useQuery(labQueries.referralLabs(true));
  const items = useQuery(
    labQueries.outsourcing({ pendingOnly: !search.all, referralLabId: search.labId, page: search.page }),
  );
  const overdue = items.data?.items.filter((item) => item.isOverdue).length ?? 0;

  return (
    <Page>
      <PageHeader
        title="Outsourced tests"
        subtitle={
          overdue > 0
            ? `${overdue} overdue on this page. Call the reference laboratory for these reports.`
            : 'Tests done by reference laboratories. To send another test out, open its lab order.'
        }
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Reference laboratory">
              <Select
                value={search.labId ?? ''}
                onChange={(_, data) =>
                  void navigate({ search: (prev) => ({ ...prev, labId: data.value || undefined, page: undefined }) })
                }
              >
                <option value="">All laboratories</option>
                {labs.data?.map((lab) => (
                  <option key={lab.id} value={lab.id}>
                    {lab.name}
                  </option>
                ))}
              </Select>
            </Field>
            <Switch
              checked={Boolean(search.all)}
              onChange={(_, data) =>
                void navigate({
                  search: (prev) => ({ ...prev, all: data.checked ? true : undefined, page: undefined }),
                })
              }
              label="Include received reports"
            />
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Outsourced tests"
          rows={items.data?.items}
          loading={items.isLoading}
          error={items.error}
          onRetry={() => void items.refetch()}
          getRowId={(item) => item.itemId}
          size="small"
          columns={[
            {
              key: 'patient',
              header: 'Patient and test',
              render: (item: OutsourcedItem) => (
                <span className={styles.cell}>
                  <Text weight="semibold">{item.testName}</Text>
                  <Caption1 className={styles.muted}>
                    {item.patientName} · {item.uhid}
                  </Caption1>
                </span>
              ),
            },
            {
              key: 'sample',
              header: 'Sample',
              width: 130,
              priority: 'low',
              render: (item: OutsourcedItem) => (
                <span className={styles.cell}>
                  <Text className={styles.mono}>{item.barcode ?? '—'}</Text>
                  <Caption1 className={styles.muted}>
                    {item.collectedAt ? `Collected ${formatDate(item.collectedAt)}` : 'Not collected'}
                  </Caption1>
                </span>
              ),
            },
            { key: 'lab', header: 'Laboratory', render: (item: OutsourcedItem) => item.referralLabName ?? '—' },
            {
              key: 'sent',
              header: 'Sent',
              render: (item: OutsourcedItem) =>
                item.sentOutAt ? (
                  <span className={styles.cell}>
                    <Caption1>{formatDateTime(item.sentOutAt)}</Caption1>
                    {item.dispatchReference && (
                      <Caption1 className={styles.muted}>Docket {item.dispatchReference}</Caption1>
                    )}
                  </span>
                ) : (
                  <Badge appearance="tint" color="warning" shape="rounded">
                    To send
                  </Badge>
                ),
            },
            {
              key: 'due',
              header: 'Report due',
              width: 140,
              render: (item: OutsourcedItem) =>
                item.externalReportReceivedAt ? (
                  <Caption1>Received {formatDate(item.externalReportReceivedAt)}</Caption1>
                ) : item.dueAt ? (
                  <Caption1 className={item.isOverdue ? styles.overdue : undefined}>
                    {item.isOverdue ? 'Overdue since ' : ''}
                    {formatDate(item.dueAt)}
                  </Caption1>
                ) : (
                  '—'
                ),
            },
            {
              key: 'status',
              header: 'Status',
              width: 170,
              priority: 'low',
              render: (item: OutsourcedItem) => <ItemStatusBadge status={item.status} />,
            },
          ]}
          rowActions={(item: OutsourcedItem) => (
            <Menu>
              <MenuTrigger disableButtonEnhancement>
                <Button
                  size="small"
                  appearance="subtle"
                  icon={<MoreHorizontalRegular />}
                  aria-label={`Actions for ${item.testName}`}
                />
              </MenuTrigger>
              <MenuPopover>
                <MenuList>
                  {canSend && !item.sentOutAt && (item.status === 'Collected' || item.status === 'InLab') && (
                    <MenuItem
                      icon={<VehicleTruckProfileRegular />}
                      onClick={() =>
                        setSendOut({
                          itemId: item.itemId,
                          testName: item.testName,
                          patientName: item.patientName,
                          barcode: item.barcode,
                          referralLabId: item.referralLabId,
                        })
                      }
                    >
                      Mark as sent
                    </MenuItem>
                  )}
                  {canReceive && item.sentOutAt && !item.externalReportReceivedAt && item.status !== 'Cancelled' && (
                    <MenuItem
                      icon={<DocumentArrowDownRegular />}
                      onClick={() =>
                        setReceive({
                          itemId: item.itemId,
                          testName: item.testName,
                          patientId: item.patientId,
                          patientName: item.patientName,
                          referralLabName: item.referralLabName,
                        })
                      }
                    >
                      Receive report
                    </MenuItem>
                  )}
                  <MenuItem
                    icon={<DocumentBulletListRegular />}
                    onClick={() => void navigate({ to: '/lab/orders/$orderId', params: { orderId: item.labOrderId } })}
                  >
                    Open the order
                  </MenuItem>
                </MenuList>
              </MenuPopover>
            </Menu>
          )}
          emptyTitle={search.all ? 'No outsourced tests' : 'Nothing waiting for a reference laboratory'}
          emptyDescription="Tests set up to go to a reference laboratory appear here once ordered."
          page={search.page ?? 1}
          pageSize={25}
          totalCount={items.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
        />
      </Section>
      {sendOut && <SendOutDialog target={sendOut} onClose={() => setSendOut(null)} />}
      {receive && <ReceiveReportDialog target={receive} onClose={() => setReceive(null)} />}
    </Page>
  );
}
