# MedQueue

MedQueue is a Windows desktop pilot for clinic downtime. A practice workstation keeps a local copy of upcoming patients' charts and can continue checking patients in, collecting form signatures, and documenting care while a cloud service is unavailable.

## Install

Download the [Windows installer](dist-download/MedQueueSetup.exe) and open it while signed into the designated Windows account. Choose an unlocked fixed NTFS volume with BitLocker protection on and encryption complete. Setup places the executable and vault on that drive and adds a Start menu shortcut. The designated Windows account must have permission to create and write the workspace. The installer is unsigned.

### Choose where MedQueue stores its vault

Selecting a drive in Setup does not encrypt, format, or repartition it. Setup verifies that the selected Windows volume is already fully encrypted with BitLocker and that protection is on. It then creates the MedQueue workspace folder on that volume. The vault has an additional layer of encryption tied to the designated Windows account.

If the drive is locked, open it in File Explorer and unlock it through Windows before running Setup or starting MedQueue. Enter the drive password or recovery key only in Windows. At installation and each app launch, Windows may show an administrator approval prompt for the read-only BitLocker status check. A standard account may need IT to approve this prompt. Keep Setup and MedQueue running under the designated account so the vault uses that account's Windows encryption keys. If Setup verifies BitLocker but cannot create the folder, ask IT to grant the designated account permission to create a folder on the selected volume.

To dedicate a small amount of disk space to MedQueue, have IT provision a separate fixed NTFS volume sized for the practice's charts and queued changes. For example, IT could assign a 10 GB volume the letter `M:`, enable BitLocker on that volume, and wait for encryption to finish before selecting it in Setup. BitLocker protects the selected volume, and the other volumes on the same physical disk keep their existing settings. [Microsoft BitLocker documentation](https://learn.microsoft.com/en-us/windows/security/operating-system-security/data-protection/bitlocker/)

The current installer requires an existing volume. A size-limited virtual disk file is a possible future setup option for allocating dedicated storage without repartitioning the physical disk. Any such workflow would need to verify that the virtual volume can be mounted and unlocked after a restart, including during an outage. [Microsoft virtual disk documentation](https://learn.microsoft.com/en-us/windows-server/storage/disk-management/manage-virtual-hard-disks)

Create the first practice administrator when opening the workspace. Create named provider accounts through **Add provider**. The first account is the practice administrator, so IT should provision the workspace before making it available at the check-in workstation.

## Run a fictional outage drill

1. Sign in as administrator, open **Connect cloud**, and select **Start fictional training clinic**. Two fictional visits enter the vault. A third visit sits outside the download window.
2. Create a form template. Select a visit and issue a one-use access code after checking the patient's identity. MedQueue signs out the provider. In the patient screen, use the code to check in and type the fictional patient's full name to sign the form.
3. Sign in again and use **Toggle training outage** to simulate loss of connectivity. Add a clinical note or document a prescription change. Repeat the check-in steps during the outage to exercise patient forms offline.
4. Close and reopen MedQueue if you want to test restart recovery. Use **Toggle training outage** again to reconnect. Queued changes drain automatically. The status bar reports queued items, conflicts, and the most recent completed sync.
5. Use **Close visit** when treatment is complete. Close active visits promptly to allow expired charts to be removed.

## Link a practice adapter

Install a separate workspace for a live adapter. Under **Connect cloud**, enter its HTTPS base URL and scoped bearer credential. MedQueue verifies the adapter protocol, practice identity, and appointment read endpoint. Check that the displayed practice identity matches the intended practice before approving the link. The workspace is bound to that practice ID, and its credential is stored inside the encrypted vault. The clinic's integration team implements the [adapter contract](ADAPTER.md) against its EHR and verifies write permissions, record mapping, and clinical behavior in staging.

## How downtime works

- While MedQueue is open, it attempts sync at startup and every 15 seconds, including at the locked sign-in screen. Keep the app running on the designated workstation before an outage to populate the cache.
- MedQueue downloads charts for scheduled appointments starting in the next 24 hours. It retains previously downloaded visits for up to 24 hours after the start time and keeps active visits or visits with pending changes until closed or reconciled. An appointment created while disconnected can only appear after the adapter becomes reachable.
- Visit check-ins, signed form snapshots, clinical records, and prescription documentation are persisted with their queued changes. Stable event IDs and revision checks protect retries. Use **Review sync conflict** to compare the cloud and local values and authorize a retry against the reviewed revision. The adapter must implement atomic revision checks and idempotency.
- Audit activity, form templates, credentials, and clinical data are protected with Windows DPAPI under the designated Windows account. The file is replaced atomically. Windows filesystem permissions restrict the workspace to the designated account and Local System. Team access is provided by named logins within a single running workspace on a single workstation.
- Prescription changes are chart documentation. Pharmacy transmission and controlled-substance prescribing require their own validated workflows. Form templates and workstation audit events remain local.

## Production readiness

Use fictional data for this pilot. HIPAA compliance is an organizational determination involving administrative, physical, and technical safeguards. Before introducing patient data, validate the EHR integration and chart completeness, independently review the application and deployment, establish applicable business associate agreements, and drill backup and recovery of the Windows DPAPI profile and vault together. Vet workstation and account management, access reviews, prescribing and signature workflows, and audit retention. This implementation uses a local audit history and does not implement centralized tamper-evident retention, MFA, multi-workstation access, or an unattended Windows service. A workstation failure can interrupt local access. See the [HHS Security Rule summary](https://www.hhs.gov/hipaa/for-professionals/security/laws-regulations/index.html).

## Build and test

From this directory in Windows PowerShell, run `./build.ps1 -Test`. The build uses the Windows .NET Framework 4.8 compiler and libraries. It creates `dist/MedQueue.exe` and `dist/MedQueueSetup.exe`. The integration smoke test uses fictional records and exercises persistence and sync without requiring a BitLocker deployment. The application and installer themselves require BitLocker.
