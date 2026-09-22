# BharatDrop — Google Play listing draft

## App name
BharatDrop

## Short description
Fast local Android-to-Windows folder transfer. No cloud, no data retention.

## Full description
BharatDrop is a Made-in-India local file-transfer utility designed for fast Android-to-Windows backups over your own Wi-Fi or phone hotspot.

Key features:
- Packed-folder transfer engine for folders containing thousands of files
- High sustained local-network throughput
- Select the exact phone folder you want to transfer
- Pause and resume from BharatDrop Desktop
- Automatic reconnect and resume after interrupted connections
- Optional SHA-256 post-transfer verification
- Optional skipped/error CSV report
- Preserves folder structure and file timestamps
- Read-only phone transfer server
- Temporary session PIN
- No BharatDrop cloud account
- No developer-operated file storage
- No data retention on developer-controlled servers

How it works:
1. Install BharatDrop on Android.
2. Start BharatDrop and note the temporary session PIN.
3. Connect the Windows PC to the same Wi-Fi/hotspot.
4. Run BharatDrop Desktop, choose the phone folder and PC destination, and transfer.

Privacy:
Transfers occur directly between your devices on the local network. BharatDrop does not upload your files to a developer-controlled cloud service.

Developer: Shashank Patel
Developed in India.

## Suggested category
Tools

## Data Safety draft
- Developer-operated collection of transferred file contents: No
- Developer-operated sharing of transferred file contents: No
- Advertising: No
- Analytics SDK: No
- Account required: No
- Data deletion request for cloud-stored transfer data: Not applicable because BharatDrop does not retain transferred files on developer-controlled servers.

## All files access declaration rationale
BharatDrop's core functionality is user-directed file/folder transfer and backup from shared Android storage to a Windows PC. The app must be able to enumerate and read user-selected folders across shared storage, including nested subfolders and arbitrary file types. This broad file-management/backup functionality is the primary purpose of the app.
