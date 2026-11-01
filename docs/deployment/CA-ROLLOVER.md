# Rolling over or replacing the hospital CA

HC360 makes a private certificate authority, the **hospital CA**, when the server first starts. It is valid for ten
years. Every device that connects to HC360 trusts this one CA:

- **HC360 Desktop** pins its fingerprint, for each Windows user, when the workstation is set up;
- each **lab agent** pins it when it is enrolled, and from then on trusts no other;
- **tablets and browsers** have it installed as a trusted root (MDM or device settings);
- a **warm standby** and any other server node hold a copy, imported with `certificates import-ca`.

From the CA, the server issues itself its **server certificate**, which it renews by itself 30 days before it expires.
The CA is never renewed by itself. There are two ways to change it:

- **Roll it over** (`certificates roll-ca`), in its last year. No device stops connecting and none is set up on the
  day. This is the normal way. See [Rolling the CA over](#rolling-the-ca-over).
- **Replace it** (`certificates new-ca`). Every device must be set up again, and until it is it cannot connect. Do this
  only when the CA's private key may have been seen by someone who should not have it, or when the CA has already
  expired. See [Replacing the CA](#replacing-the-ca-every-device-set-up-again).

Neither applies when the server uses a certificate the hospital supplied (`Network:CertificatePath`): HC360 then makes
no CA, and the hospital replaces that certificate itself.

## When

- **In the CA's last year: roll it over.** System health warns a year ahead, and its warning turns critical, which is
  sent to IT on-call by SMS or WhatsApp, in the CA's last 90 days. Administration › System health shows the CA's expiry,
  amber in its last year and red in its last 90 days. The certificate probe (below) says the same from another machine.
- **At once, whenever the CA's private key may have been seen by someone who should not have it: replace it.** For
  example: an exported CA file (`certificates export-ca`) was lost together with its password, or the server was
  compromised. Whoever has the key can make certificates every device trusts. Do not roll such a CA over: whoever has
  the old key could sign a "new" CA of their own the same way, and every device would trust it.

In the CA's last year, the server certificate is issued only up to the CA's expiry, so it cannot outlast it.

## Rolling the CA over

### How it works

`certificates roll-ca` makes a new CA with the old one's name and a new key, valid for ten years. The old CA then signs
a certificate for the new key: the same name as both, so it changes nothing about what the CA may issue (the CA's
path length of 0 still holds, RFC 5280 4.2.1.9). Windows certificate services renew a CA with a new key the same way.
Until the old CA expires, the server sends that certificate with its own:

- a device that trusts the **old** CA follows it to the old CA, and keeps trusting the server;
- a device that has the **new** CA trusts the server through the new CA itself;
- a device that trusts **another** CA trusts the server no more than before.

The server also hands out the old CA at `/api/v1/system/ca-certificate/previous`, and announces its fingerprint on the
network (mDNS `fpprev`) next to the new one's (`fp`), so a workstation that pinned the old CA still finds the server.
A warm standby that imports the CA after the rollover sends the same chain.

The rollover ends when the old CA expires: the server stops sending the old CA's certificate, and a device that still
has only the old CA stops connecting. Until then System health warns that devices which have not moved stop trusting
the server on that day (critical in its last 90 days). Every device must have the new CA before then:

| What | What it needs before the old CA expires |
| --- | --- |
| Tablets, phones and browsers | The new CA installed as a trusted root, beside the old one (MDM). Remove the old one after it has expired. |
| The certificate probe's tasks | The new fingerprint (`--ca-fingerprint`). Until then the probe warns that the CA was rolled over, and names the new CA. |
| Anything else that calls HC360 over HTTPS | BI gateways, partners' systems: the new CA or fingerprint. |
| HC360 Desktop and lab agents | Today: each set up again with the new fingerprint, one at a time, at any time before the old CA expires: no downtime, nothing on the day. A later version of HC360 Desktop and the lab agent takes the new CA by itself over its pinned connection. |
| IT's `-CaFingerprint` policy for HC360 Desktop | The new fingerprint, on each workstation as it is set up again, not before: a workstation whose policy names another CA than the one it pinned drops its pin, and must be set up again. |

### The day of a rollover

1. **Keep a copy of the CA you are rolling over**, as for a replacement (step 2 of [the day of a
   replacement](#the-day-of-a-replacement)).
2. **Stop the `HC360Server` service.**
3. **Roll the CA over.** First run it without `--confirm`. It says which CA it would roll over, and changes nothing:

   ```powershell
   & "$env:ProgramFiles\HyFib\HealthCare360\Server\HyFib.HealthCare360.Server.exe" certificates roll-ca
   & "$env:ProgramFiles\HyFib\HealthCare360\Server\HyFib.HealthCare360.Server.exe" certificates roll-ca --confirm
   ```

   Write down the new fingerprint it prints, and the day the old CA vouches for it until. The old CA is kept beside it as
   `hospital-ca.pfx.protected.replaced-<time>`. It refuses a CA that has expired (replace it then), and a second
   rollover while the last one still runs.
4. **Start the service.** The server issues itself a certificate from the new CA.
5. **Check it from another machine, with the OLD fingerprint**, as the devices that have not moved see the server:

   ```powershell
   HyFib.HealthCare360.Server.exe certificates probe https://HC360-SRV:7360/ --ca-fingerprint '<old>'
   ```

   It must warn that the CA was rolled over and name the new CA. If it says the certificate "was not issued by the
   hospital CA", the server is not sending the old CA's certificate: devices that have the old CA cannot connect. Put the
   old CA back at once (below).

   Windows builds the chain it sends with the server's certificate from the Server Node's own certificate stores, not
   from what HC360 hands it, and sends it only when it ends at a root the Server Node trusts. Otherwise the server's
   certificate goes alone. So `roll-ca`, run as an administrator, puts:
   - the certificate the old CA signed in the computer's **Intermediate Certification Authorities** store;
   - the old CA among its **Trusted Root Certification Authorities**, trusted for **Server Authentication only**. In the
     Certificates console (Local Computer), its Properties show "Enable only the following purposes" with Server
     Authentication alone.

   The old CA's private key is never there: the Server Node trusts it only to check a TLS server's certificate, until it
   expires. The running server checks that both are in place. Once the old CA has expired they vouch for nothing; the
   next `roll-ca`, or a `new-ca`, takes them out. If `roll-ca` says Windows was not given them, run it again from an
   elevated prompt: while the rollover runs it is refused, and gives Windows the certificates. Do not install the
   **new** hospital CA among the Server Node's trusted roots: Windows would then build the chain to it and send the
   server's certificate alone. A domain policy that lets the Server Node trust only enterprise root CAs has the same
   effect; the probe with the old fingerprint shows it.
6. **Probe again with the new fingerprint.** It says `OK`.
7. **Do the standby and the other server nodes** (`certificates export-ca` here, `certificates import-ca` there), and
   probe each one with the old fingerprint and the new one.
8. **Try one device of each kind** that has only the old CA: a workstation, a lab agent, and one tablet and one browser
   of each make the hospital uses. Each must connect as before. Phones and tablets check certificates in their own way;
   only the hospital's own devices can show that each one follows the old CA's certificate.
9. **Over the following months**, give the new CA to everything in the table above. System health keeps warning until
   the old CA has expired.

### Undo

Before the old CA expires you can put it back, as for a replacement ([Undo](#undo-putting-the-old-ca-back)), and also
delete `hospital-ca-cross.crt` and `hc360-hospital-ca-previous.crt` in the same folder. Devices that already have only
the new CA must then be given the old one again.

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

An expired CA cannot be put back, nor rolled over: it can no longer vouch for the next one. Use the downtime
procedure, then do steps 4 to 8 of [the day of a replacement](#the-day-of-a-replacement) at once,
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

## What HC360 does not do yet

- **HC360 Desktop and the lab agents** trust exactly one CA fingerprint. After a rollover they keep working through
  the old CA's certificate, but each must still be set up again with the new fingerprint before the old CA expires. A
  later version takes the new CA by itself over the connection it already trusts, and reports which CA each device
  holds, so System health can name the devices that have not moved.
- **The certificate probe's scheduled task** is registered by hand (above). A later installer registers it.
