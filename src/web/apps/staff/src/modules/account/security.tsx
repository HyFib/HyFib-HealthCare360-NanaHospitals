import {
  Body1,
  Button,
  Caption1,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { CopyRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { auth, type AuthResult, type AuthUser, isApiError, useSession } from '@hc360/core';
import { applyServerErrors, TextField, useNotify } from '@hc360/ui';
import { useMutation } from '@tanstack/react-query';
import { QRCodeSVG } from 'qrcode.react';
import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { api } from '@/lib/api';

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '12px', maxWidth: '420px' },
  qr: { padding: '12px', backgroundColor: '#fff', borderRadius: tokens.borderRadiusMedium, alignSelf: 'flex-start' },
  secret: { fontFamily: tokens.fontFamilyMonospace, wordBreak: 'break-all' },
  row: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
});

export async function reloadCurrentUser(): Promise<void> {
  useSession.getState().setUser(await api.get<AuthUser>('/api/v1/auth/me'));
}

const passwordSchema = z
  .object({
    currentPassword: z.string().min(1, 'Enter your current password.'),
    // Length is checked here for instant feedback; the server also rejects breached, common and personal passwords.
    newPassword: z
      .string()
      .min(12, 'Use at least 12 characters. A short sentence works well.')
      .max(128, 'Use at most 128 characters.'),
    confirmPassword: z.string(),
  })
  .refine((v) => v.newPassword === v.confirmPassword, {
    path: ['confirmPassword'],
    message: 'The passwords do not match.',
  })
  .refine((v) => v.newPassword !== v.currentPassword, {
    path: ['newPassword'],
    message: 'Choose a password you have not used here.',
  });

type PasswordForm = z.infer<typeof passwordSchema>;

/** Change password; the server enforces the full policy (length, breach and deny lists, names, reuse) and returns fresh tokens. */
export function ChangePasswordForm({ onDone }: { onDone?: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const form = useForm<PasswordForm>({
    resolver: zodResolver(passwordSchema),
    defaultValues: { currentPassword: '', newPassword: '', confirmPassword: '' },
  });

  const save = useMutation({
    mutationFn: (values: PasswordForm) =>
      api.post<AuthResult>('/api/v1/auth/change-password', {
        currentPassword: values.currentPassword,
        newPassword: values.newPassword,
      }),
    onSuccess: (result) => {
      auth.apply(result);
      form.reset();
      notify.success('Password changed', 'Other devices signed in with your account have been signed out.');
      onDone?.();
    },
    onError: (error) => {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not change the password');
      }
    },
  });

  return (
    <form className={styles.form} onSubmit={form.handleSubmit((values) => save.mutate(values))} noValidate>
      <TextField
        control={form.control}
        name="currentPassword"
        label="Current password"
        type="password"
        autoComplete="current-password"
        required
      />
      <TextField
        control={form.control}
        name="newPassword"
        label="New password"
        type="password"
        autoComplete="new-password"
        hint="At least 12 characters. A short sentence you can remember works well."
        required
      />
      <TextField
        control={form.control}
        name="confirmPassword"
        label="Confirm new password"
        type="password"
        autoComplete="new-password"
        required
      />
      <div>
        <Button
          type="submit"
          appearance="primary"
          disabled={save.isPending}
          icon={save.isPending ? <Spinner size="tiny" /> : undefined}
        >
          Change password
        </Button>
      </div>
    </form>
  );
}

/** TOTP enrolment: scan the QR in Microsoft/Google Authenticator, confirm with a code. */
export function MfaSetup({ onDone }: { onDone?: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const [password, setPassword] = useState('');
  const [code, setCode] = useState('');

  // The password is asked again before the sign-in method changes (ASVS V7.5.1).
  const begin = useMutation({
    mutationFn: () => api.post<{ secret: string; otpAuthUri: string }>('/api/v1/auth/mfa/setup', { password }),
    onSuccess: () => setPassword(''),
    onError: (error) => {
      if (!(isApiError(error) && error.fieldErrors.password)) {
        notify.error(error, 'Could not start set-up');
      }
    },
  });
  const passwordError = isApiError(begin.error) ? begin.error.fieldErrors.password?.[0] : undefined;

  const enable = useMutation({
    mutationFn: () => api.post<AuthResult>('/api/v1/auth/mfa/enable', { code }),
    onSuccess: (result) => {
      auth.apply(result);
      notify.success('Two-step verification is on');
      onDone?.();
    },
    onError: (error) => notify.error(error, 'That code did not work'),
  });

  if (!begin.data) {
    return (
      <form
        className={styles.form}
        onSubmit={(event) => {
          event.preventDefault();
          if (password && !begin.isPending) {
            begin.mutate();
          }
        }}
      >
        <Body1>
          Two-step verification asks for a 6-digit code from an authenticator app on your phone when you sign in, so a
          stolen password alone cannot open patient records. Your other devices are signed out once it is on.
        </Body1>
        <Field
          label="Your password (to start)"
          validationMessage={passwordError}
          validationState={passwordError ? 'error' : 'none'}
        >
          <Input
            type="password"
            value={password}
            onChange={(_, d) => setPassword(d.value)}
            autoComplete="current-password"
          />
        </Field>
        <div>
          <Button appearance="primary" type="submit" disabled={!password || begin.isPending}>
            Set up two-step verification
          </Button>
        </div>
      </form>
    );
  }

  return (
    <div className={styles.form}>
      <Body1>1. Open Microsoft Authenticator or Google Authenticator and scan this code.</Body1>
      <div className={styles.qr}>
        <QRCodeSVG value={begin.data.otpAuthUri} size={176} level="M" />
      </div>
      <Caption1 className={styles.muted}>Can't scan? Enter this key in the app instead:</Caption1>
      <div className={styles.row}>
        <Text className={styles.secret}>{begin.data.secret.replace(/(.{4})/g, '$1 ').trim()}</Text>
        <Button
          size="small"
          icon={<CopyRegular />}
          onClick={() => void navigator.clipboard?.writeText(begin.data.secret)}
          aria-label="Copy key"
        />
      </div>
      <Field label="2. Enter the 6-digit code the app shows">
        <Input
          value={code}
          onChange={(_, d) => setCode(d.value.replace(/\D/g, '').slice(0, 6))}
          inputMode="numeric"
          autoComplete="one-time-code"
        />
      </Field>
      <div>
        <Button appearance="primary" disabled={code.length !== 6 || enable.isPending} onClick={() => enable.mutate()}>
          Turn on
        </Button>
      </div>
    </div>
  );
}

const pinSchema = z
  .object({
    pin: z.string().regex(/^\d{4,8}$/, 'Use 4 to 8 digits.'),
    confirmPin: z.string(),
    password: z.string().min(1, 'Enter your password to confirm.'),
  })
  .refine((v) => v.pin === v.confirmPin, { path: ['confirmPin'], message: 'The PINs do not match.' });

type PinForm = z.infer<typeof pinSchema>;

/** Quick-unlock PIN for the idle lock screen (never used to sign in). */
export function PinForm() {
  const styles = useStyles();
  const notify = useNotify();
  const hasPin = useSession((s) => s.user?.hasPin);
  const form = useForm<PinForm>({
    resolver: zodResolver(pinSchema),
    defaultValues: { pin: '', confirmPin: '', password: '' },
  });

  const save = useMutation({
    mutationFn: (values: PinForm) => api.post('/api/v1/auth/pin', { pin: values.pin, password: values.password }),
    onSuccess: async () => {
      form.reset();
      await reloadCurrentUser();
      notify.success(hasPin ? 'PIN changed' : 'PIN set', 'Use it to unlock this screen after a break.');
    },
    onError: (error) => {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the PIN');
      }
    },
  });

  return (
    <form className={styles.form} onSubmit={form.handleSubmit((values) => save.mutate(values))} noValidate>
      <TextField
        control={form.control}
        name="pin"
        label={hasPin ? 'New PIN' : 'PIN'}
        type="password"
        autoComplete="off"
        required
      />
      <TextField
        control={form.control}
        name="confirmPin"
        label="Confirm PIN"
        type="password"
        autoComplete="off"
        required
      />
      <TextField
        control={form.control}
        name="password"
        label="Your password"
        type="password"
        autoComplete="current-password"
        required
      />
      <div>
        <Button type="submit" appearance="primary" disabled={save.isPending}>
          {hasPin ? 'Change PIN' : 'Set PIN'}
        </Button>
      </div>
    </form>
  );
}

export function RestrictionNotice({ children }: { children: string }) {
  return (
    <MessageBar intent="info" layout="multiline">
      <MessageBarBody>{children}</MessageBarBody>
    </MessageBar>
  );
}
