import {
  Badge,
  Button,
  Caption1,
  makeStyles,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  Text,
  tokens,
  Tooltip,
  useRestoreFocusTarget,
} from '@fluentui/react-components';
import {
  ArrowSyncRegular,
  BeakerDismissRegular,
  BeakerEditRegular,
  DismissCircleRegular,
  DocumentArrowDownRegular,
  DocumentTextRegular,
  DropRegular,
  MoreHorizontalRegular,
  PrintRegular,
  VehicleTruckProfileRegular,
} from '@fluentui/react-icons';
import { formatDateTime, isApiError } from '@hc360/core';
import { DataTable, ErrorState, LoadingState, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState, type ReactNode } from 'react';
import { usePermission } from '@/lib/permissions';
import {
  LAB_PERMISSIONS,
  labApi,
  labKeys,
  labQueries,
  type LabOrderDetail,
  type LabOrderItemDto,
  type LabSampleDto,
} from '@/modules/lab/api';
import {
  ItemStatusBadge,
  PatientClassBadge,
  PriorityBadge,
  ReportStatusBadge,
  SampleStatusBadge,
  TubeChip,
} from '@/modules/lab/badges';
import { PatientBannerWithFlags } from '@/modules/emr/PatientBannerWithFlags';
import { CollectDialog, type CollectTarget } from '@/modules/lab/CollectDialog';
import { bannerFor } from '@/modules/lab/common/banner';
import { ReasonDialog } from '@/modules/lab/common/ReasonDialog';
import { searchFlag } from '@/modules/lab/common/search';
import { rejectionReasonLabel, sectionLabel, sourceLabel, specimenLabel } from '@/modules/lab/labels';
import { printSampleLabels } from '@/modules/lab/print';
import { ReceiveReportDialog, type ReceiveReportTarget } from '@/modules/lab/ReceiveReportDialog';
import { RejectSampleDialog, type RejectTarget } from '@/modules/lab/RejectSampleDialog';
import { SendOutDialog, type SendOutTarget } from '@/modules/lab/SendOutDialog';
import { useLabLive } from '@/modules/lab/useLabLive';

export const Route = createFileRoute('/_app/lab/orders/$orderId')({
  validateSearch: (s: Record<string, unknown>): { collect?: true } => ({ collect: searchFlag(s.collect) }),
  component: LabOrderPage,
});

const useStyles = makeStyles({
  facts: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(220px, 1fr))', gap: '12px 24px' },
  fact: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  label: { color: tokens.colorNeutralForeground3 },
  muted: { color: tokens.colorNeutralForeground3 },
  mono: { fontFamily: tokens.fontFamilyMonospace },
  cell: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  error: { color: tokens.colorPaletteRedForeground1 },
});

function Fact({ label, children }: { label: string; children: ReactNode }) {
  const styles = useStyles();
  return (
    <div className={styles.fact}>
      <Caption1 className={styles.label}>{label}</Caption1>
      <Text>{children || '—'}</Text>
    </div>
  );
}

function collectTarget(order: LabOrderDetail): CollectTarget {
  const pending = order.samples.filter((sample) => sample.status === 'Pending');
  return {
    orderId: order.id,
    accessionNumber: order.accessionNumber,
    patientId: order.patientId,
    patientName: order.patientName,
    uhid: order.uhid,
    ageSex: `${order.age ?? '—'} / ${order.gender.charAt(0)}`,
    priority: order.priority,
    tubes: pending.map((sample) => ({
      sampleId: sample.id,
      barcode: sample.barcode,
      container: sample.container,
      specimenType: sample.specimenType,
      section: sample.section,
      tests: sample.tests,
      isRecollection: sample.recollectionOfSampleId !== null,
    })),
  };
}

/** One lab order (accession): tests with charge and referral status, tubes with their journey, and every action on them. */
function LabOrderPage() {
  const { orderId } = Route.useParams();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const order = useQuery(labQueries.order(orderId));
  useLabLive();

  if (order.isLoading) {
    return <LoadingState label="Opening the order…" />;
  }
  if (order.error || !order.data) {
    return (
      <Page maxWidth={900}>
        <ErrorState
          error={order.error}
          onRetry={() => void order.refetch()}
          title={isApiError(order.error) && order.error.status === 404 ? 'Order not found' : undefined}
        />
      </Page>
    );
  }
  return (
    <OrderView
      order={order.data}
      openCollect={Boolean(search.collect)}
      onCollectShown={() => void navigate({ search: {}, replace: true })}
    />
  );
}

function OrderView({
  order,
  openCollect,
  onCollectShown,
}: {
  order: LabOrderDetail;
  openCollect: boolean;
  onCollectShown: () => void;
}) {
  const styles = useStyles();
  // Closing a dialog opened from a row's menu returns focus to that row's menu button.
  const restoreFocusTarget = useRestoreFocusTarget();
  const notify = useNotify();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const canCreate = usePermission(LAB_PERMISSIONS.create);
  const canCollect = usePermission(LAB_PERMISSIONS.collect);
  const canReceive = usePermission(LAB_PERMISSIONS.receive);
  const canLabels = usePermission([LAB_PERMISSIONS.collect, LAB_PERMISSIONS.receive]);
  const canEnter = usePermission([LAB_PERMISSIONS.enter, LAB_PERMISSIONS.verify]);
  const canReceiveReport = usePermission(LAB_PERMISSIONS.enter);
  const referralLabs = useQuery({ ...labQueries.referralLabs(true), retry: false });
  const pendingTubes = order.samples.filter((sample) => sample.status === 'Pending');
  const [collecting, setCollecting] = useState(openCollect && pendingTubes.length > 0);
  const [cancelling, setCancelling] = useState<LabOrderItemDto | null>(null);
  const [sendOut, setSendOut] = useState<SendOutTarget | null>(null);
  const [receiveReport, setReceiveReport] = useState<ReceiveReportTarget | null>(null);
  const [rejecting, setRejecting] = useState<RejectTarget | null>(null);
  const [printing, setPrinting] = useState<string | null>(null);
  const failedCharges = order.items.filter((item) => item.chargeError && item.status !== 'Cancelled');
  const labName = (id: string | null) =>
    id ? (referralLabs.data?.find((lab) => lab.id === id)?.name ?? 'Reference lab') : null;
  const sampleById = new Map(order.samples.map((sample) => [sample.id, sample]));

  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: labKeys.orders });
    void queryClient.invalidateQueries({ queryKey: labKeys.samples });
  };

  const retryCharges = useMutation({
    mutationFn: () => labApi.retryCharges(order.id),
    onSuccess: (outcome) => {
      if (outcome.failures.length === 0) {
        notify.success(
          outcome.posted === 1 ? 'Charge posted to the bill' : `${outcome.posted} charges posted to the bill`,
        );
      } else {
        notify.warning('Some charges still failed', outcome.failures.join(' · '));
      }
      refresh();
    },
    onError: (error) => notify.error(error, 'Could not post the charges'),
  });

  const cancelItem = useMutation({
    mutationFn: ({ item, reason }: { item: LabOrderItemDto; reason: string }) => labApi.cancelItem(item.id, reason),
    onSuccess: (_, { item }) => {
      setCancelling(null);
      notify.success(`${item.testName} cancelled`, item.chargeId ? 'Its charge is reversed on the bill.' : undefined);
      refresh();
    },
    onError: (error) => notify.error(error, 'Could not cancel the test'),
  });

  const recollect = useMutation({
    mutationFn: (sample: LabSampleDto) => labApi.recollect(sample.id),
    onSuccess: (result) => {
      notify.success(
        'New sample requested',
        result.recollectionBarcode ? `New tube ${result.recollectionBarcode} is on the collection list.` : undefined,
      );
      refresh();
    },
    onError: (error) => notify.error(error, 'Could not request a new sample'),
  });

  const printLabels = async (sampleId?: string) => {
    setPrinting(sampleId ?? 'all');
    try {
      const labels = await labApi.labels(order.id, sampleId);
      await printSampleLabels(sampleId ? labels : labels.filter((label) => label.collectedAt !== null));
    } catch (error) {
      notify.error(error, 'The labels could not be printed');
    } finally {
      setPrinting(null);
    }
  };

  const collectedCount = order.samples.filter((sample) => sample.collectedAt).length;

  return (
    <Page>
      <PatientBannerWithFlags
        patientId={order.patientId}
        patient={bannerFor(order)}
        actions={
          <>
            {canCollect && pendingTubes.length > 0 && (
              <Button appearance="primary" icon={<DropRegular />} onClick={() => setCollecting(true)}>
                Collect {pendingTubes.length} {pendingTubes.length === 1 ? 'tube' : 'tubes'}
              </Button>
            )}
            {order.reportId && (
              <Button
                icon={<DocumentTextRegular />}
                onClick={() => void navigate({ to: '/lab/reports/$reportId', params: { reportId: order.reportId! } })}
              >
                Report
              </Button>
            )}
            {canLabels && collectedCount > 0 && (
              <Button
                icon={printing === 'all' ? <Spinner size="tiny" aria-hidden /> : <PrintRegular />}
                // While printing it stays focusable (aria-disabled) and ignores clicks: a disabled button drops focus.
                disabledFocusable={printing !== null}
                onClick={() => void printLabels()}
              >
                Reprint labels
              </Button>
            )}
          </>
        }
      />

      <PageHeader
        title={`Lab order ${order.accessionNumber}`}
        subtitle={`${sourceLabel[order.source]} · ordered ${formatDateTime(order.orderedAt)}`}
        breadcrumbs={[
          { label: 'Lab orders', onClick: () => void navigate({ to: '/lab/orders' }) },
          { label: order.accessionNumber },
        ]}
      />

      {failedCharges.length > 0 && (
        <MessageBar intent="warning" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>
              {failedCharges.length === 1
                ? 'A charge was not posted to the bill'
                : `${failedCharges.length} charges were not posted to the bill`}
            </MessageBarTitle>
            {failedCharges.map((item) => `${item.testName}: ${item.chargeError}`).join(' · ')}
          </MessageBarBody>
          {canCreate && (
            <MessageBarActions>
              <Button
                icon={retryCharges.isPending ? <Spinner size="tiny" aria-hidden /> : <ArrowSyncRegular />}
                disabled={retryCharges.isPending}
                onClick={() => retryCharges.mutate()}
              >
                Retry charges
              </Button>
            </MessageBarActions>
          )}
        </MessageBar>
      )}

      <Section>
        <div className={styles.facts}>
          <Fact label="Status">
            {order.reportStatus ? (
              <ReportStatusBadge status={order.reportStatus} />
            ) : (
              <Badge appearance="tint">{order.status}</Badge>
            )}
          </Fact>
          <Fact label="Priority">
            <PriorityBadge priority={order.priority} showRoutine />
          </Fact>
          <Fact label="Patient type">
            <PatientClassBadge patientClass={order.patientClass} />
          </Fact>
          <Fact label="Ordered by">{order.orderingDoctor ?? order.referringDoctorName}</Fact>
          {order.referringDoctorName && order.orderingDoctor && (
            <Fact label="Referred by">{order.referringDoctorName}</Fact>
          )}
          {order.sourceOrderNumber && <Fact label="Clinical order">{order.sourceOrderNumber}</Fact>}
          {order.completedAt && <Fact label="Completed">{formatDateTime(order.completedAt)}</Fact>}
          {order.clinicalNotes && <Fact label="Clinical notes">{order.clinicalNotes}</Fact>}
        </div>
      </Section>

      <Section title="Tests" flush>
        <DataTable
          ariaLabel="Tests on this order"
          rows={order.items}
          getRowId={(item) => item.id}
          size="small"
          columns={[
            {
              key: 'test',
              header: 'Test',
              render: (item: LabOrderItemDto) => (
                <span className={styles.cell}>
                  <Text weight="semibold" strikethrough={item.status === 'Cancelled'}>
                    {item.testName}
                  </Text>
                  <Caption1 className={styles.muted}>
                    {item.testCode} · {sectionLabel[item.section]}
                    {item.quantity > 1 ? ` · ×${item.quantity}` : ''}
                  </Caption1>
                  {item.cancelReason && <Caption1 className={styles.muted}>Cancelled: {item.cancelReason}</Caption1>}
                </span>
              ),
            },
            {
              key: 'status',
              header: 'Status',
              width: 170,
              render: (item: LabOrderItemDto) => <ItemStatusBadge status={item.status} />,
            },
            {
              key: 'sample',
              header: 'Sample',
              width: 130,
              priority: 'low',
              render: (item: LabOrderItemDto) => {
                const sample = item.sampleId ? sampleById.get(item.sampleId) : undefined;
                return sample ? <Text className={styles.mono}>{sample.barcode}</Text> : '—';
              },
            },
            {
              key: 'charge',
              header: 'Billing',
              width: 150,
              priority: 'low',
              render: (item: LabOrderItemDto) =>
                item.packageBookingId ? (
                  <Caption1>In health package</Caption1>
                ) : item.chargeError ? (
                  <Tooltip content={item.chargeError} relationship="description">
                    <Caption1 className={styles.error}>Not posted</Caption1>
                  </Tooltip>
                ) : item.chargeId ? (
                  <Caption1>On the bill</Caption1>
                ) : (
                  <Caption1 className={styles.muted}>—</Caption1>
                ),
            },
            {
              key: 'referral',
              header: 'Outsourced',
              width: 170,
              priority: 'low',
              render: (item: LabOrderItemDto) =>
                item.referralLabId ? (
                  <span className={styles.cell}>
                    <Caption1>{labName(item.referralLabId)}</Caption1>
                    <Caption1 className={styles.muted}>
                      {item.externalReportReceivedAt
                        ? `Report received ${formatDateTime(item.externalReportReceivedAt)}`
                        : item.sentOutAt
                          ? `Sent ${formatDateTime(item.sentOutAt)}`
                          : 'Not sent yet'}
                    </Caption1>
                  </span>
                ) : (
                  '—'
                ),
            },
          ]}
          rowActions={(item: LabOrderItemDto) => {
            const sample = item.sampleId ? sampleById.get(item.sampleId) : undefined;
            const actions = [
              canEnter && sample && ['Received', 'InProcess', 'Completed'].includes(sample.status) && (
                <MenuItem
                  key="results"
                  icon={<BeakerEditRegular />}
                  onClick={() => void navigate({ to: '/lab/worklist', search: { sampleId: sample.id } })}
                >
                  Results
                </MenuItem>
              ),
              canReceive && (item.status === 'Collected' || item.status === 'InLab') && !item.sentOutAt && (
                <MenuItem
                  key="send"
                  icon={<VehicleTruckProfileRegular />}
                  onClick={() =>
                    setSendOut({
                      itemId: item.id,
                      testName: item.testName,
                      patientName: order.patientName,
                      barcode: sample?.barcode,
                      referralLabId: item.referralLabId,
                    })
                  }
                >
                  Send to a reference lab
                </MenuItem>
              ),
              canReceiveReport && item.sentOutAt && !item.externalReportReceivedAt && item.status !== 'Cancelled' && (
                <MenuItem
                  key="receive"
                  icon={<DocumentArrowDownRegular />}
                  onClick={() =>
                    setReceiveReport({
                      itemId: item.id,
                      testName: item.testName,
                      patientId: order.patientId,
                      patientName: order.patientName,
                      referralLabName: labName(item.referralLabId),
                    })
                  }
                >
                  Receive external report
                </MenuItem>
              ),
              canCreate && !['Verified', 'Released', 'Cancelled'].includes(item.status) && (
                <MenuItem key="cancel" icon={<DismissCircleRegular />} onClick={() => setCancelling(item)}>
                  Cancel test
                </MenuItem>
              ),
            ].filter(Boolean);
            return actions.length > 0 ? (
              <Menu>
                <MenuTrigger disableButtonEnhancement>
                  <Button
                    size="small"
                    appearance="subtle"
                    icon={<MoreHorizontalRegular />}
                    aria-label={`Actions for ${item.testName}`}
                    {...restoreFocusTarget}
                  />
                </MenuTrigger>
                <MenuPopover>
                  <MenuList>{actions}</MenuList>
                </MenuPopover>
              </Menu>
            ) : null;
          }}
        />
      </Section>

      <Section title="Samples" description="Each tube with its journey from collection to the lab" flush>
        <DataTable
          ariaLabel="Samples of this order"
          rows={order.samples}
          getRowId={(sample) => sample.id}
          size="small"
          emptyTitle="No tubes"
          columns={[
            {
              key: 'tube',
              header: 'Tube',
              render: (sample: LabSampleDto) => (
                <span className={styles.cell}>
                  <span>
                    <TubeChip container={sample.container} detail={specimenLabel[sample.specimenType]} />{' '}
                    <Text className={styles.mono}>{sample.barcode}</Text>
                  </span>
                  <Caption1 className={styles.muted}>
                    {sectionLabel[sample.section]} · {sample.tests.join(', ')}
                  </Caption1>
                </span>
              ),
            },
            {
              key: 'status',
              header: 'Status',
              width: 120,
              render: (sample: LabSampleDto) => (
                <span className={styles.cell}>
                  <SampleStatusBadge status={sample.status} />
                  {sample.recollectionOfSampleId && <Caption1 className={styles.muted}>Repeat sample</Caption1>}
                </span>
              ),
            },
            {
              key: 'collected',
              header: 'Collected',
              priority: 'low',
              render: (sample: LabSampleDto) =>
                sample.collectedAt ? (
                  <span className={styles.cell}>
                    <Caption1>{formatDateTime(sample.collectedAt)}</Caption1>
                    <Caption1 className={styles.muted}>
                      {[sample.collectedBy, sample.collectionSite].filter(Boolean).join(' · ')}
                    </Caption1>
                  </span>
                ) : (
                  '—'
                ),
            },
            {
              key: 'received',
              header: 'Received',
              priority: 'low',
              render: (sample: LabSampleDto) =>
                sample.rejectedAt ? (
                  <span className={styles.cell}>
                    <Caption1 className={styles.error}>
                      Rejected:{' '}
                      {sample.rejectionReason ? rejectionReasonLabel[sample.rejectionReason] : 'reason not given'}
                    </Caption1>
                    <Caption1 className={styles.muted}>
                      {sample.rejectionRemarks ?? formatDateTime(sample.rejectedAt)}
                    </Caption1>
                  </span>
                ) : sample.receivedAt ? (
                  <span className={styles.cell}>
                    <Caption1>{formatDateTime(sample.receivedAt)}</Caption1>
                    <Caption1 className={styles.muted}>{sample.receivedBy}</Caption1>
                  </span>
                ) : (
                  '—'
                ),
            },
          ]}
          rowActions={(sample: LabSampleDto) => {
            const actions = [
              canLabels && sample.status !== 'Cancelled' && (
                <MenuItem
                  key="label"
                  icon={<PrintRegular />}
                  disabled={printing !== null}
                  onClick={() => void printLabels(sample.id)}
                >
                  Reprint label
                </MenuItem>
              ),
              canReceive && ['Collected', 'Received', 'InProcess'].includes(sample.status) && (
                <MenuItem
                  key="reject"
                  icon={<BeakerDismissRegular />}
                  onClick={() =>
                    setRejecting({
                      sampleId: sample.id,
                      barcode: sample.barcode,
                      patientName: order.patientName,
                      tests: sample.tests,
                    })
                  }
                >
                  Reject sample
                </MenuItem>
              ),
              canReceive && sample.status === 'Rejected' && !sample.recollectedAsSampleId && (
                <MenuItem
                  key="recollect"
                  icon={<DropRegular />}
                  disabled={recollect.isPending}
                  onClick={() => recollect.mutate(sample)}
                >
                  Request a new sample
                </MenuItem>
              ),
            ].filter(Boolean);
            return actions.length > 0 ? (
              <Menu>
                <MenuTrigger disableButtonEnhancement>
                  <Button
                    size="small"
                    appearance="subtle"
                    icon={<MoreHorizontalRegular />}
                    aria-label={`Actions for tube ${sample.barcode}`}
                    {...restoreFocusTarget}
                  />
                </MenuTrigger>
                <MenuPopover>
                  <MenuList>{actions}</MenuList>
                </MenuPopover>
              </Menu>
            ) : null;
          }}
        />
      </Section>

      {collecting && pendingTubes.length > 0 && (
        <CollectDialog
          target={collectTarget(order)}
          onClose={() => {
            setCollecting(false);
            if (openCollect) {
              onCollectShown();
            }
          }}
        />
      )}

      {cancelling && (
        <ReasonDialog
          key={cancelling.id}
          open
          title={`Cancel ${cancelling.testName}?`}
          description={
            <Text>
              The test is removed from the order and its charge is reversed on the bill. This cannot be undone; order
              the test again if needed.
            </Text>
          }
          suggestions={[
            'Patient declined',
            'Ordered twice',
            'Doctor cancelled the test',
            'Sample could not be collected',
          ]}
          confirmLabel="Cancel test"
          destructive
          busy={cancelItem.isPending}
          onCancel={() => setCancelling(null)}
          onConfirm={(reason) => cancelItem.mutate({ item: cancelling, reason })}
        />
      )}
      {sendOut && <SendOutDialog target={sendOut} onClose={() => setSendOut(null)} />}
      {receiveReport && <ReceiveReportDialog target={receiveReport} onClose={() => setReceiveReport(null)} />}
      {rejecting && <RejectSampleDialog target={rejecting} onClose={() => setRejecting(null)} />}
    </Page>
  );
}
