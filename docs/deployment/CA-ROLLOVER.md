# Replacing the hospital CA

HC360 makes a private certificate authority, the **hospital CA**, when the server first starts. It is valid for ten
years. Every device that connects to HC360 trusts this one CA:

- **HC360 Desktop** pins its fingerprint, for each Windows user, when the workstation is set up;
- each **lab agent** pins it when it is enrolled, and from then on trusts no other;
- **tablets and browsers** have it installed as a trusted root (MDM or device settings);
- a **warm standby** and any other server node hold a copy, imported with `certificates import-ca`.

From the CA, the server issues itself its **server certificate**, which it renews by itself 30 days before it expires.
The CA is never renewed by itself. Replacing it means setting every one of those devices up again, and until a
device is set up again it cannot connect. This page says when to do it, how to plan it, the steps, how to undo it, and
what to do if the CA has already expired.

It does not apply when the server uses a certificate the hospital supplied (`Network:CertificatePath`): HC360 then
makes no CA, and the hospital replaces that certificate itself.

## When

- **A year before the CA expires.** System health warns a year ahead, and its warning turns critical, which is sent to
  IT on-call by SMS or WhatsApp, in the CA's last 90 days. Administration › System health shows the CA's expiry, amber
  in its last year and red in its last 90 days. The certificate probe (below) says the same from another machine.
- **At once, whenever the CA's private key may have been seen by someone who should not have it.** For example: an
  exported CA file (`certificates export-ca`) was lost together with its password, or the server was compromised.
  Whoever has the key can make certificates every device trusts.

In the CA's last year, the server certificate is issued only up to the CA's expiry, so it cannot outlast it.

## Plan: what trusts the CA

Make the list before the day. The list is the work.

| What | Where to find each one | What setting it up again takes |
| --- | --- | --- |
| HC360 Desktop | Administration › Devices: every enrolled workstation and tablet. Each Windows user of a workstation sets it up for themselves. | For each user: **Change server…**, compare the new fingerprint, and a new enrolment code from Administration › Devices › Enrol a device. Where IT set `-CaFingerprint` (the machine policy), run `Install-HC360Desktop.ps1 -CaFingerprint '<new>'` first. |
| Lab agents | Administration › Devices, type Lab analyser agent | Run `Install-HC360LabAgent.ps1 -ServerUrl … -CaFingerprint '<new>' -EnrolmentCode …` with a new code. The agent is enrolled as a new device: choose it again in Lab setup › Analysers › Connection for each analyser it relays, and check each analyser as at go-live. |
| Tablets, phones and browsers | MDM, or the devices IT keeps a list of | Install the new CA as a trusted root (it is at `https://HC360-SRV:7360/api/v1/system/ca-certificate`). Browsers have seen HC360's HSTS header, so they offer no way past a certificate they do not trust. |
| Warm standby and other server nodes | DEPLOYMENT.md sections 9 and 10 | `certificates export-ca` on the server with the new CA, then `certificates import-ca` on each of them, with their service stopped. |
| Anything else that calls HC360 over HTTPS | BI gateways, partners' systems on the hospital network, the certificate probe's tasks | Its own trust setting, with the new CA or fingerprint. |

These do not change: network analysers and modalities (they do not use HTTPS), the PostgreSQL replication
certificate, and the DSC trust folder.

**Time it.** On a test server, set up one of each again (a workstation user, a tablet, a lab agent with one analyser)
and time each one. Multiply by the list to get the length of the change-over. Choose a quiet time, with the paper
downtime forms ready (the NABH downtime procedure), and with someone at each lab agent.

## The day

1. **Tell staff** when HC360 stops being reachable, and how long it will take.
2. **Keep a copy of the CA you are replacing.** It lets you undo the change even on other hardware:

   ```powershell
   & "$env:ProgramFiles\HyFib\HealthCare360\Server\HyFib.HealthCare360.Server.exe" certificates export-ca --out E:\hc360-ca-before.pfx
   ```

   Keep the file and its password as you keep the backup recovery key, and apart from each other.
3. **Stop the `HC360Server` service.**
4. **Make the new CA.** First run it without `--confirm`. It says which CA it would replace, and changes nothing:

   ```powershell
   & "$env:ProgramFiles\HyFib\HealthCare360\Server\HyFib.HealthCare360.Server.exe" certificates new-ca
   & "$env:ProgramFiles\HyFib\HealthCare360\Server\HyFib.HealthCare360.Server.exe" certificates new-ca --confirm
   ```

   Write down the new fingerprint it prints. The CA it replaced is kept beside it as
   `hospital-ca.pfx.protected.replaced-<time>`.
5. **Start the service.** The server issues itself a certificate from the new CA. Then check the change from another
   machine: System health shows the new fingerprint, valid for ten years, and

   ```powershell
   HyFib.HealthCare360.Server.exe certificates probe https://HC360-SRV:7360/ --ca-fingerprint '<new>'
   ```

   says `OK`.
6. **Do the standby and the other server nodes** (`export-ca` here, `import-ca` there), and probe each one.
7. **Set the devices up again**, the most critical areas first: emergency, ICU, wards, then the lab agents and
   analysers, then the counters.
8. **Give the certificate probe's tasks the new fingerprint.**
9. **Afterwards**, once every device works, remove the old CA from MDM profiles and tablets. When you are sure nothing
   needs it, delete the `.replaced-<time>` file and the exported copy.

## Undo: putting the old CA back

You can do this only before the old CA expires, and only to abandon the change-over. Devices already set up with the
new CA must then be set up again with the old one.

1. Stop the `HC360Server` service.
2. In `%ProgramData%\HyFib\HealthCare360\certs`:
   - rename `hospital-ca.pfx.protected` to `hospital-ca.pfx.protected.withdrawn`;
   - rename `hospital-ca.pfx.protected.replaced-<time>` to `hospital-ca.pfx.protected`;
   - delete `server.pfx.protected` and `server.meta.json`.
3. Start the service. The server issues itself a certificate from the old CA. System health and the probe show the old
   fingerprint.

On other hardware, `certificates import-ca --in E:\hc360-ca-before.pfx` does the same. The automated tests carry out
this undo and check that the server serves a certificate from the old CA again.

## If the CA has already expired

No device connects, and the `HC360Server` service does not start. The Application event log says:

> HC360 cannot start: the hospital CA expired on … Make a new CA with 'HyFib.HealthCare360.Server.exe certificates
> new-ca' and set every device up again …

An expired CA cannot be put back. Use the downtime procedure, then do steps 4 to 8 of [The day](#the-day) at once,
most critical areas first. If the server must serve before then, give it a certificate of the hospital's own
(`Network:CertificatePath`). Devices must trust that certificate's issuer.

## The certificate probe

`certificates probe` checks an address as a device does. It checks that:

- the certificate the server serves is for that name;
- the certificate was issued by the hospital CA;
- neither the certificate nor the CA expires within the periods System health warns in, using the same rule.

It runs from any Windows machine that has a copy of the server's program folder, and needs no database. Unlike System
health, it still reports when HC360 is down, when HC360 cannot send its alerts, and when a device sees something the
server does not.

```powershell
# The staff address: the CA the server hands out must be the one the devices pinned.
HyFib.HealthCare360.Server.exe certificates probe https://HC360-SRV:7360/ --ca-fingerprint '<fingerprint>' --event-log

# The portal's address does not hand out the CA: give its file instead (the server keeps it in certs\hc360-hospital-ca.crt).
HyFib.HealthCare360.Server.exe certificates probe https://HC360-SRV:7361/ --ca-file C:\HC360\hc360-hospital-ca.crt
```

- **Exit code.** It is 0 when all is well. It is 1 when a certificate needs planning. It is 2 when one needs action
  now, or an address cannot be checked. This is how monitoring tools such as Nagios, Zabbix and PRTG read a check.
- **Event log.** `--event-log` also writes a warning or an error to the Application event log, under the source the
  server's installer registers.
- **Where to run it.** Run it daily from the server itself, which catches a server that is not serving. Also run it
  from a machine in IT, which catches the network and the server's name, and still runs when the server is down.
  For example:

  ```powershell
  $exe = "$env:ProgramFiles\HyFib\HealthCare360\Server\HyFib.HealthCare360.Server.exe"
  $probe = New-ScheduledTaskAction -Execute $exe `
      -Argument 'certificates probe https://HC360-SRV:7360/ --ca-fingerprint <fingerprint> --event-log'
  Register-ScheduledTask -TaskPath '\HyFib\' -TaskName 'HC360 certificate probe' -Action $probe `
      -Trigger (New-ScheduledTaskTrigger -Daily -At 07:10) -User 'SYSTEM'
  ```

## Why the change-over is all at once

HC360 Desktop and the lab agents trust exactly one CA fingerprint. So the new CA cannot be handed out to them ahead of
the change, and every device moves on the day. A smoother change-over would need these changes to HC360:

- the server announces the next CA well before the change, signed by the current one;
- devices trust both CAs for a while;
- the server then switches to the next CA.

No device would need setting up again. That is planned as a product change. Until then, follow this procedure.
