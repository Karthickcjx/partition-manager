# Reference application analysis: 4DDiG Partition Manager 4.3.1.8

This is the discovery report for the clean-room reimplementation in this repository. It records what was
**observed** on the installed copy at `C:\Program Files\4DDiG Partition Manager` (2026-11-07) and keeps
**assumptions** separate. No proprietary code, assets, keys or licence logic were copied, and the original
installation was not modified.

## How the analysis was done

| Requested approach | What was available | What was used instead |
|---|---|---|
| Reverse-engineering MCP server, Ghidra | Not present in this environment | PowerShell PE and .NET metadata inspection (`System.Reflection` metadata-only load, `Get-AuthenticodeSignature`, `FileVersionInfo`) |
| Dynamic analysis of every screen | The app runs elevated; Windows UI privilege isolation (UIPI) blocks synthetic input from a non-elevated shell | One screenshot of the home screen; workflows reconstructed from the plain-text localisation file |
| Destructive tests on a VM or VHD | No VM, and attaching a VHDX needs administrator rights | Nothing destructive was run; the new app only simulates writes |

The main executable's embedded resources have obfuscated names, and the install ships an `AntiCrack.dat`. They
were **not** unpacked: doing so would mean defeating a protection mechanism, which is out of bounds for this
project.

## 1. Application architecture (observed)

```
4DDiG Partition Manager.exe   .NET Framework 4.x WPF app (assembly "WindowsPartitionManager" 4.3.1.8), MVVM (TS.MvvmLight)
   │   UI strings: Languages\*.xaml (15 languages, 1,826 keys in en-US)
   │   Managed glue: TS.*.dll, *CLR.dll (C++/CLI bridges), CommonRegister, UpdateCom, FeedbackCommon
   ▼
Native SDK DLLs (C++, MSVC 14.x runtime bundled)          Helper processes (native)
   lib_TSDiskPartitionOptSDK   partition operations          PartitionManagerService.exe
   lib_TSPartTableOpSDK        partition-table edits         PartitionAssistantService.exe
   lib_DiskConverterSDK        MBR↔GPT conversion            PartitionScanSevice.exe  (sic)
   lib_TSFileSystemConvertSDK  NTFS↔FAT32                    DeviceViewerService.exe
   lib_TSDeviceCloneSDK, lib_disk_clone   cloning            DataScanService.exe
   lib_TSDataBackupSDK         backup/restore                PMUpdateService.exe, TSCloudEnhancedService.exe
   lib_TSPartitionRecoverySDK  lost-partition recovery       MsgSupport\MsgSupportService.exe, Monitor\Monitor.exe
   lib_TSDeviceShredSDK        data wipe                     pmnative.exe, PMParseRecord.exe
   lib_TSDiskHealthTestSDK, lib_TSDiskBadBlockDetectSDK, smartctl.exe   disk health / bad sectors
   lib_BitLockerKeySearchSDK   BitLocker unlock/key search
   lib_WinToGo_sdk, lib_TSWindowsUpdateSDK, lib_USBFormatSDK  Windows-to-Go, Windows upgrade, USB format
   lib_TSAppMigrateSDK         application migration
   lib_trash_clean             "Windows Clean"
   TSDeviceIoSDK, DiskUtil, DiskPartitionSDK, FileSystemSDK, DeviceOperatorSDK  low-level I/O
   ▼
pmdiskdriver.sys  kernel driver, registered as a system driver (state: Stopped when inspected)
```

`config\module.ini` maps numeric module IDs to the SDKs (1001 DiskConverter, 1004 DeviceClone, 1005
DiskPartitionOpt, 1006 FileSystemConvert, 1007 PartitionRecovery, 1008 DataBackup, 1010 DeviceShred, 1011 WinToGo,
1012 BitLockerKeySearch, 1013 USBFormat, 1014 WindowsUpdate, 1015 trash_clean, 1016 BadBlockDetect,
1017 DiskHealthTest).

None of the helper executables were registered as Windows services at inspection time (`Get-Service` and
`Win32_Service` queries filtered on the install path returned nothing). **Assumption:** they are started on
demand by the UI, probably with elevation.

### Bundled third-party and Microsoft tools (observed)

| Area | Files |
|---|---|
| Boot / WinPE | `bcdboot.exe`, `bootsect.exe`, `bootx64.efi`, `bootia32.efi`, `DISM\` (dism, wimgapi, wimlib-imagex, oscdimg, vhdprovider), `syslinux\` |
| File-system templates | `NTFSFormat`, `FATFormat`, `exfat_dbr` (boot-sector images) |
| Disk health | `smartctl.exe` (smartmontools) |
| Archives / images | `7z\`, `esd2iso.exe`, `SharpCompress`, `liblz4`, `zlibwapi` |
| Networking / cloud | `libcurl`, OpenSSL 1.1, `AWSSDK.S3`, `TS.Downloader` |
| Data | `System.Data.SQLite` + Entity Framework 6, `protobuf-net`, `Newtonsoft.Json` |
| Diagnostics | NLog, BugSplat crash reporting (`BsSndRpt*.exe`), `msdia140.dll` |
| UI | `Microsoft.Expression.Drawing`, `System.Windows.Interactivity`, `XamlAnimatedGif`, `Microsoft.Windows.Shell`, `pdfium` |

### Metadata and signatures (observed)

* `4DDiG Partition Manager.exe`: FileVersion and ProductVersion 4.3.1.8, company "Tenorshare", Authenticode
  status **Valid**, signer *Tenorshare (Hongkong) Limited*.
* Installer and uninstaller: Inno Setup (`unins000.exe`, `.dat`, `.msg`). Updates come from `PMUpdateService.exe`
  plus `UpdaterCLR.dll`, and the `.exe.Config` points at vendor download URLs (hot-update config, WinPE/ISO
  manifests).
* Logs: `Logs\yyyy-MM-dd.log`, plus per-service logs in the same folder.

## 2. Technology stack summary

| Layer | Reference app (observed) | This project |
|---|---|---|
| UI | WPF on .NET Framework 4.x, MVVM, XAML resource dictionaries for localisation | WPF on .NET 8, hand-written MVVM, theme dictionaries |
| Disk logic | Proprietary native C++ SDKs and a kernel driver | Documented Windows APIs only: the Storage Management API through WMI (`MSFT_Disk`, `MSFT_Partition`, `MSFT_Volume`, `MSFT_PhysicalDisk`), `Win32_EncryptableVolume`, and read-only `\\.\PhysicalDriveN` |
| Privilege model | Whole app elevated | Runs as the invoking user; reads that need admin rights degrade gracefully |
| Packaging | Inno Setup | Inno Setup (`installer\PartitionManager.iss`) |

## 3. UI observations

Home screen, measured from one capture at about 125 % display scale (1525×900 physical pixels, roughly
1220×720 DIP):

* **Frame:** custom title bar, no system chrome. App mark and name on the left; account, feedback, menu,
  minimise, maximise and close on the right.
* **Background:** a very light blue-lavender diagonal gradient. Content sits on one white card with roughly 12 px
  corner radius.
* **Sidebar** (about 270 DIP wide, rows about 60 DIP tall, icon plus semibold label): Clone Disk *(default
  page)*, Backup, Convert Disk, Windows Clean, Partition Management, Partition Recovery, Windows Download and
  Upgrade (with an orange "New Windows version" badge), Toolkit. The selected row is a white rounded tile.
* **Clone Disk page:** one wide hero card ("Clone OS Disk (System Migration)", three bullet points) above three
  cards: Clone Data Disk, Clone Partition, Application Migration.
* **Palette** (from `CommonRegisterColorAndFont.json` plus the capture): primary blue `#4F80FD`, link
  `#2F69FF`, purchase orange `#F9AC0D`, border `#EDF1FB`, secondary text `#6C7BA1`, body text near-black.
  Body text is 14 px.

From the en-US strings, the Partition Management workflow is: select a disk or partition on a disk map, then
choose an operation (Resize/Move with *front/rear space* fields, Split, Merge, Delete, Create, Format, Change
Drive Letter, Hide/Show, Delete all partitions, Convert primary/logical, NTFS↔FAT32, Initialize disk
MBR/GPT). Each operation becomes an entry in a **Task List / Pending Operations** panel. **Execute** runs the
list with step messages (for example "Shrinking partition...", "Moving file data...") and an elapsed-time
display formatted `{0}h{1}m{2}s`. Errors map to numbered codes (1400–1459 partition, 2700–2711 backup).

**What the new UI reproduces:** the layout (title bar, icon-and-label sidebar, a single rounded content card on
a gradient), the palette family, the task-list-then-Execute interaction, the front/size/rear resize model,
progress with elapsed time, and the text style of the error messages. No artwork, icons, fonts or branding were
copied. Icons come from the Segoe Fluent and MDL2 system fonts, and the app icon is generated by
`scripts/make-icon.ps1`.

Screenshots of the reference application are kept out of the repository, because they contain its artwork
(`docs/reference-screenshots/` is git-ignored). Screenshots of this app are in [`docs/screenshots`](screenshots).

## 4. Blockers and gaps

1. **No reverse-engineering MCP server or Ghidra.** Static analysis was limited to metadata. Native import and
   export tables of the SDKs were not walked.
2. **UIPI** blocked automated navigation of the elevated reference app. Running the inspection tooling elevated
   would capture the remaining screens.
3. **No disposable test disk.** Attaching VHDX files needs administrator rights, so no destructive behaviour of
   the reference app was exercised, and real write operations in this project stay unimplemented.
4. **Obfuscated resources** were deliberately left alone (see above).
