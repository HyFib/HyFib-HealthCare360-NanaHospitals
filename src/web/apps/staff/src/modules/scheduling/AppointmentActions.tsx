import {
  Button,
  makeStyles,
  Menu,
  MenuDivider,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
} from '@fluentui/react-components';
import {
  CalendarArrowRightRegular,
  CheckmarkRegular,
  DismissCircleRegular,
  MoreHorizontalRegular,
  PersonArrowRightRegular,
  PersonProhibitedRegular,
  PrintRegular,
} from '@fluentui/react-icons';
import type { ReactElement } from 'react';
import type { AppointmentRef } from './api';
import { appointmentActions, primaryAction, type AppointmentAction } from './appointmentRules';
import type { AppointmentDesk } from './useAppointmentDesk';

const useStyles = makeStyles({
  actions: { display: 'flex', gap: '4px', alignItems: 'center', justifyContent: 'flex-end' },
});

/** The row's likely next step as a button (check in today, confirm future bookings) plus the full menu. */
export function AppointmentRowActions({ appointment, desk }: { appointment: AppointmentRef; desk: AppointmentDesk }) {
  const styles = useStyles();
  const actions = appointmentActions(appointment, desk.context);
  const primary = primaryAction(appointment, desk.context);
  const item = (action: AppointmentAction, icon: ReactElement, label: string) => (
    <MenuItem
      icon={icon}
      disabled={!actions[action].allowed}
      title={actions[action].reason}
      onClick={() => desk.run(action, appointment)}
    >
      {label}
    </MenuItem>
  );

  return (
    <div className={styles.actions}>
      {primary === 'checkIn' && (
        <Button
          size="small"
          appearance="primary"
          icon={<PersonArrowRightRegular />}
          onClick={() => desk.run('checkIn', appointment)}
        >
          Check in
        </Button>
      )}
      {primary === 'confirm' && (
        <Button size="small" icon={<CheckmarkRegular />} onClick={() => desk.run('confirm', appointment)}>
          Confirm
        </Button>
      )}
      <Menu>
        <MenuTrigger disableButtonEnhancement>
          <Button
            size="small"
            appearance="subtle"
            icon={<MoreHorizontalRegular />}
            aria-label={`More actions for ${appointment.patientName}`}
          />
        </MenuTrigger>
        <MenuPopover>
          <MenuList>
            {desk.canManage && (
              <>
                {item('checkIn', <PersonArrowRightRegular />, 'Check in')}
                {item('confirm', <CheckmarkRegular />, 'Confirm')}
                {item('reschedule', <CalendarArrowRightRegular />, 'Reschedule')}
                {item('noShow', <PersonProhibitedRegular />, 'Mark as no-show')}
                <MenuDivider />
              </>
            )}
            <MenuItem icon={<PrintRegular />} onClick={() => desk.run('print', appointment)}>
              Print appointment slip
            </MenuItem>
            {desk.canManage && item('cancel', <DismissCircleRegular />, 'Cancel appointment')}
          </MenuList>
        </MenuPopover>
      </Menu>
    </div>
  );
}
