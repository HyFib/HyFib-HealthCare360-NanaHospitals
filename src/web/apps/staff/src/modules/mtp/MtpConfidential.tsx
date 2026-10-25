import { MessageBar, MessageBarBody, MessageBarTitle } from '@fluentui/react-components';
import { ShieldLockRegular } from '@fluentui/react-icons';

/** The note every MTP screen shows: the woman's name and particulars are revealed to no one (MTP Act s.5A). */
export function MtpConfidential() {
  return (
    <MessageBar intent="warning" icon={<ShieldLockRegular />} layout="multiline">
      <MessageBarBody>
        <MessageBarTitle>Confidential (MTP Act s.5A)</MessageBarTitle>
        The woman&apos;s name and particulars are revealed to no one but a person authorised by law. Every case opened,
        printed or exported is recorded in the audit log with your name.
      </MessageBarBody>
    </MessageBar>
  );
}
