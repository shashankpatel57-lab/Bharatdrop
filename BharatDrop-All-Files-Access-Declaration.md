# BharatDrop MANAGE_EXTERNAL_STORAGE declaration draft

BharatDrop is a file-transfer and backup utility whose core purpose requires access to files and folders outside app-specific storage.

The user explicitly starts the local transfer server and chooses which folder to transfer from BharatDrop Desktop. BharatDrop then enumerates nested folders and reads arbitrary user files so they can be copied to the user's PC while preserving folder structure.

All-files access is used only for this core user-requested file-management/backup functionality. BharatDrop does not use this permission for advertising, analytics, profiling, or undisclosed background collection.

The transferred content is sent directly to the user's PC over the user's local network. The developer does not retain transferred files on developer-controlled servers.
