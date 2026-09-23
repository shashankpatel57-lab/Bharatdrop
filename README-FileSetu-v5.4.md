# FileSetu v5.4 Rebuild

FileSetu is a local-network transfer app for **PC ↔ Phone** and **Phone ↔ Phone** transfers.

## v5.4 changes
- Persistent PackedStream v2 engine with 1 MiB transfer buffers.
- No fixed 20-second payload timeout.
- Automatic reconnect and byte-level resume from `.filesetu.part` files.
- Exact single-file selection: selecting a file never expands to its parent folder.
- Android can both receive and send.
- Windows can send to a phone, browse/pull from a phone, and receive from a phone.
- UDP local peer discovery plus manual IP/PIN pairing.
- Windows Explorer right-click integration: **Send with FileSetu**.
- Completely redesigned FileSetu UI.
- User-visible branding is FileSetu only.
- Android application ID intentionally remains `in.bharatdrop.app` so it can be delivered as an update to the existing installed app.

## Transfer reliability
A partially received file is stored as `<name>.filesetu.part`. If Wi-Fi/hotspot changes, a socket stalls, or a connection drops, the sender reconnects, asks the receiver for the current partial length, seeks to that byte, and continues. The transfer payload itself has no short read timeout.

## Build
GitHub Actions builds:
- Android release APK (unsigned by CI)
- Android AAB (unsigned by CI)
- Windows x64 single-file EXE

The Android release should be signed with the existing FileSetu/BharatDrop upload key before distribution or Play upload.
