# SkyVault

SkyVault is a simple private cloud file server written in C#/.NET.

## Requirements

- .NET 10 SDK
- Linux/macOS shell or compatible terminal
- SMTP mailbox for email verification codes

## SMTP configuration

Create a local `.env` file in the project root:

```sh
SMTP_HOST='smtp.gmail.com'
SMTP_PORT='587'
SMTP_USER='skyvaultcloud0@gmail.com'
SMTP_PASS='your-gmail-app-password'
SMTP_FROM='skyvaultcloud0@gmail.com'
SMTP_SSL='true'
PORT='18080'
SKYVAULT_VERSION='0.1.0'
SKYVAULT_DATA_DIR='data'
SKYVAULT_STORAGE_DIRS='data/files'
SKYVAULT_STORAGE_MODE='single'
SKYVAULT_QUOTA_GB='5'
SKYVAULT_MAX_RAM_MB='1024'
SKYVAULT_CPU_CORES='2'
SKYVAULT_MAX_CONNECTIONS='128'
SKYVAULT_MAX_ACTIVE_REQUESTS='64'
SKYVAULT_MAX_UI_REQUESTS='48'
SKYVAULT_MAX_UPLOADS='2'
SKYVAULT_MAX_DOWNLOADS='4'
SKYVAULT_MAX_FILE_OPERATIONS='8'
SKYVAULT_MAX_UPLOADS_PER_USER='1'
SKYVAULT_MAX_DOWNLOADS_PER_USER='2'
SKYVAULT_MAX_FILE_OPERATIONS_PER_USER='3'
SKYVAULT_UI_QUEUE_TIMEOUT_SECONDS='15'
SKYVAULT_UPLOAD_QUEUE_TIMEOUT_SECONDS='3600'
SKYVAULT_DOWNLOAD_QUEUE_TIMEOUT_SECONDS='3600'
SKYVAULT_FILE_OPERATION_QUEUE_TIMEOUT_SECONDS='300'
SKYVAULT_HEADER_READ_TIMEOUT_SECONDS='10'
SKYVAULT_CLIENT_IDLE_TIMEOUT_SECONDS='60'
SKYVAULT_UPLOAD_TIMEOUT_SECONDS='3600'
SKYVAULT_DOWNLOAD_TIMEOUT_SECONDS='3600'
SKYVAULT_MAX_HEADER_BYTES='65536'
SKYVAULT_MAX_MULTIPART_FIELD_BYTES='65536'
SKYVAULT_GRACEFUL_SHUTDOWN_SECONDS='30'
```

Storage modes:

- `single`: write files to the first storage location.
- `spread` / `raid0`: place new files on the storage location with the most free space.
- `mirror` / `raid1`: write each file to every configured storage location.

The `.env` file is ignored by Git, because it contains secrets.

You can copy `.env.example` to `.env` and replace only `SMTP_PASS`.
For Gmail, `SMTP_PASS` must be an app password, not the normal account password.

## Run

To edit runtime settings from the console:

```sh
./SkyVault.sh config
```

```sh
./SkyVault.sh
```

Then open:

```text
http://127.0.0.1:18080
```

## Notes

- Accounts are registered by email.
- Registration requires a verification code sent by SMTP.
- User files are stored locally in `data/files/<email>/`.
