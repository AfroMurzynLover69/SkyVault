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
