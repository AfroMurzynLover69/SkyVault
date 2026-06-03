# SkyVault

SkyVault is a simple private cloud file server written in C#/.NET.

## Requirements

- .NET 10 SDK
- Linux/macOS shell or compatible terminal
- SMTP mailbox for email verification codes

## SMTP configuration

Create a local `.env` file in the project root:

```sh
SMTP_HOST='smtp.example.com'
SMTP_PORT='465'
SMTP_USER='your-email@example.com'
SMTP_PASS='your-password-or-app-password'
SMTP_FROM='your-email@example.com'
SMTP_SSL='true'
PORT='18080'
```

The `.env` file is ignored by Git, because it contains secrets.

## Run

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
