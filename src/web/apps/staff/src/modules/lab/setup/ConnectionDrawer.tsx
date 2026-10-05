import {
  Button,
  Caption1,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  OverlayDrawer,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { DeleteRegular, DismissRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatDateTime, formatRelative, isApiError } from '@hc360/core';
import {
  ChoiceField,
  ConfirmDialog,
  FormGrid,
  NumberField,
  SelectField,
  StatusBadge,
  SwitchField,
  TextAreaField,
  TextField,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useForm, useWatch, type Control } from 'react-hook-form';
import {
  HL7_ORDER_MESSAGES,
  labApi,
  labKeys,
  SERIAL_BAUD_RATES,
  SERIAL_HANDSHAKES,
  SERIAL_PARITIES,
  SERIAL_STOP_BITS,
  type LabAgentDto,
  type LabConnectionsDto,
  type LabInstrumentDto,
} from '../api';
import {
  agentOptionLabel,
  connectionSchema,
  connectionStatus,
  formFieldOf,
  handshakeUsesRts,
  toConnectionForm,
  toSaveRequest,
  type ConnectionForm,
} from './connections';

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '14px' },
  status: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    padding: '12px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  row: { display: 'flex', alignItems: 'center', gap: '8px', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
});

const HANDSHAKE_LABELS: Record<ConnectionForm['handshake'], string> = {
  None: 'None',
  XOnXOff: 'XON/XOFF (software)',
  RequestToSend: 'RTS/CTS (hardware)',
  RequestToSendXOnXOff: 'RTS/CTS and XON/XOFF',
};

const STOP_BIT_LABELS: Record<ConnectionForm['stopBits'], string> = { One: '1', OnePointFive: '1.5', Two: '2' };

/**
 * How one analyser reaches HC360: a network port it connects to, or a serial port on a lab PC whose HC360 lab agent
 * relays it. The fields only; the drawer around them is {@link ConnectionDrawer}.
 */
export function ConnectionFields({
  control,
  isHl7,
  agents,
  disabled,
}: {
  control: Control<ConnectionForm>;
  isHl7: boolean;
  agents: LabAgentDto[];
  disabled: boolean;
}) {
  const transport = useWatch({ control, name: 'transport' });
  const handshake = useWatch({ control, name: 'handshake' });
  return (
    <>
      <ChoiceField
        control={control}
        name="transport"
        label="How the analyser is connected"
        disabled={disabled}
        options={[
          { value: 'Tcp', label: 'Network: the analyser connects to the HC360 server' },
          { value: 'Serial', label: 'Serial cable to a lab PC, relayed by its HC360 lab agent' },
        ]}
      />
      {transport === 'Tcp' ? (
        <FormGrid columns={2}>
          <NumberField
            control={control}
            name="port"
            label="Port"
            required
            min={1}
            max={65535}
            disabled={disabled}
            hint="Set the same port on the analyser, for example 5100"
          />
          <TextAreaField
            control={control}
            name="allowedIps"
            label="Analyser's IP address"
            disabled={disabled}
            hint="Only this address may connect. Give the analyser a fixed address. One per line."
          />
        </FormGrid>
      ) : (
        <>
          {agents.length === 0 && (
            <MessageBar intent="warning">
              <MessageBarBody>
                <MessageBarTitle>No lab agent is enrolled</MessageBarTitle>
                Install one on the PC the analyser is cabled to: create a code in Administration › Devices (device type:
                Lab analyser agent), then run Install-HC360LabAgent.ps1 on that PC.
              </MessageBarBody>
            </MessageBar>
          )}
          <SelectField
            control={control}
            name="agentDeviceId"
            label="Lab agent"
            required
            disabled={disabled}
            placeholder="Choose the lab PC's agent"
            options={agents.map((agent) => ({ value: agent.id, label: agentOptionLabel(agent) }))}
          />
          <FormGrid columns={3}>
            <TextField
              control={control}
              name="portName"
              label="COM port"
              required
              disabled={disabled}
              placeholder="COM3"
              maxLength={6}
            />
            <SelectField
              control={control}
              name="baudRate"
              label="Baud rate"
              disabled={disabled}
              options={SERIAL_BAUD_RATES.map((rate) => ({ value: String(rate), label: String(rate) }))}
            />
            <SelectField
              control={control}
              name="dataBits"
              label="Data bits"
              disabled={disabled}
              options={[
                { value: '8', label: '8' },
                { value: '7', label: '7' },
              ]}
            />
            <SelectField
              control={control}
              name="parity"
              label="Parity"
              disabled={disabled}
              options={SERIAL_PARITIES.map((value) => ({ value, label: value }))}
            />
            <SelectField
              control={control}
              name="stopBits"
              label="Stop bits"
              disabled={disabled}
              options={SERIAL_STOP_BITS.map((value) => ({ value, label: STOP_BIT_LABELS[value] }))}
            />
            <SelectField
              control={control}
              name="handshake"
              label="Flow control"
              disabled={disabled}
              options={SERIAL_HANDSHAKES.map((value) => ({ value, label: HANDSHAKE_LABELS[value] }))}
            />
          </FormGrid>
          <FormGrid columns={2}>
            <SwitchField
              control={control}
              name="dtrEnable"
              label="Raise DTR (data terminal ready)"
              disabled={disabled}
            />
            <SwitchField
              control={control}
              name="rtsEnable"
              label="Raise RTS (request to send)"
              disabled={disabled || handshakeUsesRts(handshake)}
              hint={handshakeUsesRts(handshake) ? 'The flow control drives RTS.' : undefined}
            />
          </FormGrid>
          <Caption1>
            The analyser&apos;s interface manual gives these settings. Most analysers use 9600 baud, 8 data bits, no
            parity and 1 stop bit.
          </Caption1>
        </>
      )}
      <FormGrid columns={2}>
        <SelectField
          control={control}
          name="encoding"
          label="Text encoding"
          disabled={disabled}
          options={[
            { value: 'latin1', label: 'Latin-1 (most analysers)' },
            { value: 'utf-8', label: 'UTF-8' },
          ]}
        />
        {isHl7 && (
          <SelectField
            control={control}
            name="orderMessage"
            label="Orders are sent as"
            disabled={disabled}
            options={HL7_ORDER_MESSAGES.map((value) => ({
              value,
              label: value === 'OML^O21' ? 'OML^O21 (HL7 2.5)' : 'ORM^O01 (HL7 2.3)',
            }))}
          />
        )}
      </FormGrid>
      {isHl7 && (
        <TextField
          control={control}
          name="sampleIdFields"
          label="Where the analyser puts the sample barcode"
          disabled={disabled}
          placeholder="SPM-2, SAC-3, OBR-3, OBR-2"
          hint="Leave empty for the usual places. Change it only when the analyser's manual says otherwise."
        />
      )}
      <SwitchField control={control} name="enabled" label="Receive results from this analyser" disabled={disabled} />
    </>
  );
}

/** Lab setup › Analysers › Connection: the analyser's connection, its live state, and the form to change it. */
export function ConnectionDrawer({
  instrument,
  list,
  canEdit,
  onClose,
}: {
  instrument: LabInstrumentDto;
  list: LabConnectionsDto;
  canEdit: boolean;
  onClose: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [removing, setRemoving] = useState(false);
  const code = instrument.code.toUpperCase();
  const connection = list.connections.find((c) => c.code.toUpperCase() === code);
  const isHl7 = instrument.protocol === 'Hl7';
  const status = connectionStatus(connection, list);
  const form = useForm<ConnectionForm>({
    resolver: zodResolver(connectionSchema),
    defaultValues: toConnectionForm(connection),
  });

  const reloadAfterConflict = (error: unknown) => {
    if (isApiError(error) && error.isConflict) {
      notify.error(
        error,
        'Someone else changed the analyser connections. They have been reloaded; check and save again.',
      );
      void queryClient.invalidateQueries({ queryKey: labKeys.connections });
      return true;
    }
    return false;
  };

  const save = useMutation({
    mutationFn: (values: ConnectionForm) => labApi.saveConnection(code, toSaveRequest(values, isHl7, list.listVersion)),
    onSuccess: () => {
      notify.success('Connection saved', 'HC360 applies it within a minute.');
      void queryClient.invalidateQueries({ queryKey: labKeys.connections });
      onClose();
    },
    onError: (error) => {
      if (reloadAfterConflict(error)) {
        return;
      }
      if (isApiError(error) && error.isValidation) {
        for (const [field, messages] of Object.entries(error.fieldErrors)) {
          const name = formFieldOf(field);
          if (name) {
            form.setError(name, { type: 'server', message: messages[0] });
          }
        }
        return;
      }
      notify.error(error, 'Could not save the connection');
    },
  });

  const remove = useMutation({
    mutationFn: () => labApi.removeConnection(code, list.listVersion),
    onSuccess: () => {
      notify.success('Connection removed', 'Results from this analyser are entered by hand until it is set up again.');
      void queryClient.invalidateQueries({ queryKey: labKeys.connections });
      setRemoving(false);
      onClose();
    },
    onError: (error) => {
      setRemoving(false);
      if (!reloadAfterConflict(error)) {
        notify.error(error, 'Could not remove the connection');
      }
    },
  });

  return (
    <OverlayDrawer open position="end" size="medium" onOpenChange={(_, data) => !data.open && onClose()}>
      <form onSubmit={form.handleSubmit((values) => save.mutate(values))} noValidate style={{ display: 'contents' }}>
        <DrawerHeader>
          <DrawerHeaderTitle
            action={<Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={onClose} />}
          >
            Connection: {instrument.name}
          </DrawerHeaderTitle>
        </DrawerHeader>
        <DrawerBody>
          <div className={styles.form}>
            <div className={styles.status} aria-live="polite">
              <span className={styles.row}>
                <StatusBadge status={status.label} tone={status.tone} label={status.label} />
                <Text weight="semibold">
                  {instrument.code} · {isHl7 ? 'HL7' : 'ASTM'}
                </Text>
              </span>
              <Text>{status.detail}</Text>
              {connection?.status?.lastMessageAt && (
                <Caption1 className={styles.muted}>
                  Last message {formatRelative(connection.status.lastMessageAt)} (
                  {formatDateTime(connection.status.lastMessageAt)})
                </Caption1>
              )}
              {connection && connection.pendingOrders > 0 && (
                <Caption1 className={styles.muted}>
                  {connection.pendingOrders} {connection.pendingOrders === 1 ? 'order is' : 'orders are'} waiting to be
                  sent to the analyser.
                </Caption1>
              )}
            </div>
            <Caption1>
              {instrument.isBidirectional
                ? 'Bidirectional is on in the analyser’s details: it can ask which tests to run, and HC360 sends it the orders of each sample received in the lab.'
                : 'Bidirectional is off in the analyser’s details: it only sends results. Turn it on there for host queries and orders.'}
            </Caption1>
            {connection?.hostQueryRetiredOff && (
              <MessageBar intent="warning">
                <MessageBarBody>
                  An older setting on this connection keeps host queries and orders off. Save the connection to clear
                  it; Bidirectional then decides.
                </MessageBarBody>
              </MessageBar>
            )}
            {!canEdit && (
              <MessageBar intent="info">
                <MessageBarBody>Only an administrator can change how analysers connect.</MessageBarBody>
              </MessageBar>
            )}
            <ConnectionFields control={form.control} isHl7={isHl7} agents={list.agents} disabled={!canEdit} />
          </div>
        </DrawerBody>
        <DrawerFooter>
          {canEdit && (
            <Button
              type="submit"
              appearance="primary"
              disabled={save.isPending}
              icon={save.isPending ? <Spinner size="tiny" /> : undefined}
            >
              Save connection
            </Button>
          )}
          {canEdit && connection && (
            <Button icon={<DeleteRegular />} onClick={() => setRemoving(true)} disabled={remove.isPending}>
              Remove
            </Button>
          )}
          <Button onClick={onClose}>{canEdit ? 'Cancel' : 'Close'}</Button>
        </DrawerFooter>
      </form>
      <ConfirmDialog
        open={removing}
        title={`Remove the connection of ${instrument.name}?`}
        confirmLabel="Remove"
        destructive
        busy={remove.isPending}
        onConfirm={() => remove.mutate()}
        onCancel={() => setRemoving(false)}
      >
        HC360 stops receiving results from this analyser. Orders waiting for it are not sent. The analyser stays in Lab
        setup.
      </ConfirmDialog>
    </OverlayDrawer>
  );
}
