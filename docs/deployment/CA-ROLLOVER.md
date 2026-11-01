# Rolling over or replacing the hospital CA

HC360 makes a private certificate authority, the **hospital CA**, when the server first starts. It is valid for ten
years. Every device that connects to HC360 trusts this one CA:

- **HC360 Desktop** pins its fingerprint, for each Windows user, when the workstation is set up;
- each **lab agent** pins it when it is enrolled;
- **tablets and browsers** have it installed as a trusted root (MDM or device settings);
- a **warm standby** and any other server node hold a copy, imported with `certificates import-ca`.

From the CA, the server issues itself its **server certificate**, which it renews by itself 30 days before it expires.
The CA is never renewed by itself. There are two ways to change it:

- **Roll it over**, in its last year, in two steps: `certificates prepare-ca`, then, once every device holds the next
  CA, `certificates switch-ca`. No device stops connecting and none is set up again. This is the normal way. See
  [Rolling the CA over](#rolling-the-ca-over).
- **Replace it** (`certificates new-ca`). Every device must be set up again, and until it is it cannot connect. Do this
  only when the CA's private key may have been seen by someone who should not have it, or when the CA has already
  expired with no next CA prepared. See [Replacing the CA](#replacing-the-ca-every-device-set-up-again).

Neither applies when the server uses a certificate the hospital supplied (`Network:CertificatePath`): HC360 then makes
no CA, and the hospital replaces that certificate itself.

## When

- **In the CA's last year: roll it over.** System health warns a year ahead, and its warning turns critical, which is
  sent to IT on-call by SMS or WhatsApp, in the CA's last 90 days. Administration › System health shows the CA's expiry,
  amber in its last year and red in its last 90 days. The certificate probe (below) says the same from another machine.
  Prepare the next CA early in that year: devices take it over the following days, and the switch waits until they all
  hold it.
- **At once, whenever the CA's private key may have been seen by someone who should not have it: replace it.** For
  example: an exported CA file (`certificates export-ca`) was lost together with its password, or the server was
  compromised. Whoever has the key can make certificates every device trusts. Do not roll such a CA over: whoever has
  the key could sign a "next" CA of their own the same way, and every device would take it.

In the CA's last year, the server certificate is issued only up to the CA's expiry, so it cannot outlast it.

## Rolling the CA over

### How it works

**Step 1, `certificates prepare-ca`.** It makes the next CA: the current one's name, a new key, valid for ten years. The
current CA signs a certificate for the next CA's key, in the same name, so it changes nothing about what a CA may issue
(the CA's path length of 0 still holds, RFC 5280 4.2.1.9; Windows certificate services renew a CA with a new key the
same way). The server **keeps serving with the current CA**: nothing changes for any device. It hands out the next CA,
followed by the current CA's word for it, at `/api/v1/system/ca-certificate/next`, and its fingerprint in
`/api/v1/system/info` (`nextCaFingerprint`).

**HC360 Desktop and the lab agent take the next CA by themselves.** A minute after HC360 Desktop starts (at the lab
agent's first heartbeat) and every six hours after, each asks the server for the next CA over the connection it already
trusts, and holds it as a second pin only on the current CA's word: the certificate the current CA signed for the next
key. A CA in the hospital's name without that word is refused, whoever hands it out. Each says in its heartbeat which CAs
it holds.

**While the next CA is prepared, System health names the devices that do not hold it** (`cert.next_ca_devices`): each
workstation and lab agent seen in the last 30 days that holds other CAs, and each that does not say, because it runs a
version from before devices took a next CA. Revoked devices are left out, and so are devices not seen for 30 days. The
warning turns critical in the current CA's last 90 days, which tells IT on-call.

**Step 2, `certificates switch-ca`.** It is refused while System health would name any device: after the switch a device
without the next CA no longer trusts the server. Once none is left, it makes the next CA the server's. The current CA is
kept beside it, never deleted, and the server hands it out at `/api/v1/system/ca-certificate/previous` until it expires.
The server only ever sends its own certificate, issued by its CA; nothing else goes into the handshake or into the Server
Node's certificate stores.

**After the switch each HC360 Desktop and lab agent pins the new CA**, within six hours, and keeps the CA it replaced
as a second pin until that expires. So a warm standby that has not been given the new CA yet still serves every device
through it, and a device that switched never goes back to the old CA.

| What | What it needs before the switch |
| --- | --- |
| HC360 Desktop and lab agents | Nothing: each takes the next CA by itself (above). Update those System health names as not saying, which run an older version. Look into those it names as without the next CA: each must reach the server; its log says when it took it ("The server prepared the next hospital CA …: … holds it for the switch"). Revoke, in Administration › Devices, a device that is gone for good: it then does not hold up the switch. |
| Tablets, phones and browsers | The next CA installed as a trusted root, beside the current one (MDM): it is at `https://HC360-SRV:7360/api/v1/system/ca-certificate/next`. They cannot say which CAs they hold, so `switch-ca` cannot wait for them: give it to them first. Remove the old one after it has expired. |
| The certificate probe's tasks | Nothing before the switch. After it, the new fingerprint (`--ca-fingerprint`): until then the probe says, critically, that the CA was switched and names the new CA. The installer gives its own task the new fingerprint when it is run again. |
| Anything else that calls HC360 over HTTPS | BI gateways, partners' systems: the next CA or fingerprint, beside the current one. |
| IT's `-CaFingerprint` policy for HC360 Desktop | Nothing: a workstation that switched keeps its setup under a policy that names the CA it replaced, since it keeps that CA's signature on the new key. Change the policy's fingerprint to the new CA after the switch, and in any case before the next rollover, ten years on. A workstation whose policy names the next CA before it switched pins that CA alone at its next start. |

### The day you prepare the next CA

1. **Keep a copy of the current CA**, as for a replacement (step 2 of [the day of a
   replacement](#the-day-of-a-replacement)).
2. **Stop the `HC360Server` service.**
3. **Prepare the next CA.** First run it without `--confirm`. It says which CA it would prepare the next one for, and
   changes nothing:

   ```powershell
   & "$env:ProgramFiles\HyFib\HealthCare360\Server\HyFib.HealthCare360.Server.exe" certificates prepare-ca
   & "$env:ProgramFiles\HyFib\HealthCare360\Server\HyFib.HealthCare360.Server.exe" certificates prepare-ca --confirm
   ```

   Write down the next CA's fingerprint it prints. Its key is kept beside the current CA as
   `hospital-ca-next.pfx.protected`, protected for this machine. It refuses a CA that has expired (replace it then), a
   second next CA while one is prepared, and a new rollover while devices still keep the CA the last switch replaced.
4. **Start the service.** It serves with the current CA, as before.
5. **Check it from another machine** with the current fingerprint:

   ```powershell
   HyFib.HealthCare360.Server.exe certificates probe https://HC360-SRV:7360/ --ca-fingerprint '<current>'
   ```

   In the CA's last year it warns, and says the next CA is prepared and to switch to it once every device holds it.
6. **Give the next CA to tablets, browsers and other systems** (the table above).
7. **The next day**, look at System health. It names the workstations and lab agents that do not hold the next CA yet.

### The day you switch

1. **Check that System health names no device** without the next CA (`cert.next_ca_devices` is gone).
2. **Stop the `HC360Server` service**, and switch. First run it without `--confirm`. It reads from the database which
   devices hold the next CA, says so, and changes nothing:

   ```powershell
   & "$env:ProgramFiles\HyFib\HealthCare360\Server\HyFib.HealthCare360.Server.exe" certificates switch-ca
   & "$env:ProgramFiles\HyFib\HealthCare360\Server\HyFib.HealthCare360.Server.exe" certificates switch-ca --confirm
   ```

   It refuses while any device seen in the last 30 days, and not revoked, does not hold the next CA, and names each one;
   and when it cannot reach the database to tell (exit code 3). The CA it replaces is kept as
   `hospital-ca.pfx.protected.replaced-<time>`.
3. **Start the service.** The server issues itself a certificate from the new CA.
4. **Probe from another machine with the new fingerprint.** It says `OK`. With the old fingerprint it says, critically,
   that the CA was switched, and names the new CA: give every probe task the new fingerprint.
5. **Do the standby and the other server nodes** (`certificates export-ca` here, `certificates import-ca` there): each
   then serves with the new CA and hands out the CA it replaced, as this one does. Until then they serve with the old
   CA, which every device that switched keeps as a second pin. Probe each one with the new fingerprint.
6. **Try one device of each kind**: a workstation, a lab agent, and one tablet and one browser of each make the hospital
   uses. Each must connect as before. Within six hours each HC360 Desktop and lab agent logs that it switched ("The
   server switched to the hospital CA …"). Phones and tablets check certificates in their own way; only the hospital's
   own devices can show that each one trusts the new CA.

### If the CA expires before the switch

The service does not start, and says so: "the hospital CA expired on …". When the next CA is prepared, it also says to
switch to it: do [the day you switch](#the-day-you-switch) at once. Every device that holds the next CA trusts the server
again; set up the others again, as for a replacement. Without a next CA, see [If the CA has already
expired](#if-the-ca-has-already-expired).

### Undo

**Before the switch**, nothing has changed for any device: stop the service and delete `hospital-ca-next.pfx.protected`
and `hospital-ca-cross.crt` in `%ProgramData%\HyFib\HealthCare360\certs`. Devices keep the next CA they hold, which no
longer issues anything, until the next rollover replaces it.

**After the switch**, before the old CA expires, you can put it back, as for a replacement ([Undo](#undo-putting-the-old-ca-back)),
and also delete `hospital-ca-cross.crt` and `hc360-hospital-ca-previous.crt` in the same folder. Workstations and lab
agents that switched keep the old CA as a second pin, so they keep connecting; tablets and browsers that have only the
new CA must be given the old one again.

## Replacing the CA (every device set up again)

### Plan: what trusts the CA

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

### The day of a replacement

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

### Undo: putting the old CA back

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

When the next CA was prepared before the CA expired, the message says to switch to it instead: see [If the CA expires
before the switch](#if-the-ca-expires-before-the-switch). Otherwise an expired CA cannot be put back, nor rolled over: it
can no longer vouch for the next one. Use the downtime procedure, then do steps 4 to 8 of [the day of a
replacement](#the-day-of-a-replacement) at once, most critical areas first. If the server must serve before then, give it a certificate of the hospital's own
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

## What HC360 does not do yet

- **The certificate probe's scheduled task** is registered by hand (above). A later installer registers it.
