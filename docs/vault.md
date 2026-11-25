# Vault and security

[← Back to the README](../README.md)

- [Encryption](#encryption)
- [Choose where your vault lives](#choose-where-your-vault-lives)
- [Several devices and offline use](#several-devices-and-offline-use)
- [Security in short](#security-in-short)
- [Where your data lives](#where-your-data-lives)
- [Start from zero](#start-from-zero)

## Encryption

The vault uses PBKDF2-SHA256 (600,000 iterations) to derive a key from your master password, and AES-256-GCM to encrypt. Passwords and private keys are never stored in plain text. The data is encrypted before it leaves your PC.

The master password must have at least 8 characters. **There is no recovery.**

In *Settings* you can change the master password, set an auto-lock, back up the vault, or use **Clear vault** to wipe everything (with several confirmation steps).

## Choose where your vault lives

Open **Vault source** in the left menu and pick one of four places:

| Source | What you need |
|---|---|
| **This PC** | Nothing. This is the default. |
| **A folder** | A folder that OneDrive, iCloud, Dropbox or Nextcloud already keeps in step. Choose it, done. |
| **GitHub** | A token with the `gist` permission ([create one here](https://github.com/settings/tokens/new?scopes=gist&description=MultiServerView), the link preselects it). Paste it, done. The vault is kept in a private gist. |
| **Supabase** | A free project, one SQL snippet ([`supabase.sql`](supabase.sql)) and an account. The page guides you through it. |

When you choose a place that is still empty, your vault moves there. When it already contains a vault (for example from your other PC), the app asks you to either:

- **use the vault that is there** (your current one is kept as a backup file), or
- **replace it with yours**.

So a second device simply picks the same place and takes the vault that is already there.

## Several devices and offline use

- Every change is saved to the vault source right away. Other devices pick it up within a minute.
- There is no merging. If two devices change the vault at the same time, you are asked which version to keep, and the other one is saved as a backup file.
- Without a connection you can keep working. Changes wait on this PC and are saved as soon as the place is reachable again.
- The master password never leaves your PC. If you change it on one device, the others ask for the new one the next time they open the vault.

## Security in short

- On Windows the local vault file is `%APPDATA%\MultiServerView\vault.msv`, encrypted with a key derived from your master password.
- Private SSH keys are never sent back to the UI after import. Connections run entirely in the host process.
- SSH host keys use trust on first use. If the key of a known host changes, the connection is **refused** (you can reset the key in the host dialog).
- VNC runs through a local bridge that only listens on `127.0.0.1` and protects every connection with a one-time token.
- The browser development mode (`npm run dev`) stores demo data unencrypted and is only meant for UI development.
- If you use the MCP server, read [AI tools](ai-tools.md#security).

## Where your data lives

The paths below are for Windows. Uninstalling the app does **not** delete your data, so updates and reinstalls never lose your vault.

| What | Location |
|---|---|
| Vault (local copy), source settings, connection history, settings | `%APPDATA%\MultiServerView` (`vault.msv`, `sync.json`, `history.json`, ...) |
| Interface settings (theme, terminal look) | `%LOCALAPPDATA%\MultiServerView\webview` |
| Your vault in a folder, on GitHub or in Supabase | Wherever you chose. The uninstaller never touches it |

## Start from zero

> [!CAUTION]
> This permanently deletes your vault. There is no recovery.

1. Optional: make a backup in *Settings* first.
2. Close MultiServerView and uninstall it (or just close it if you keep it).
3. Delete both folders (Command Prompt):

   ```bat
   rmdir /s /q "%APPDATA%\MultiServerView"
   rmdir /s /q "%LOCALAPPDATA%\MultiServerView"
   ```

4. Delete the vault at its source too (the file `MultiServerView-Vault.msv` in your folder, the gist on GitHub or the row in Supabase), otherwise you can load it again.
5. Reinstall and create a new master password.

The same applies to the portable ZIP. If you only want to wipe the vault, use *Settings → Clear vault* inside the app. It empties the vault but keeps your master password.
