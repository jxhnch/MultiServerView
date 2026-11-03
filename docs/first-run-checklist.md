# First run checklist

## Getting the app
1. Download the latest installer (`MultiServerView-Setup-x.y.z.exe`) or the portable ZIP from the
   [Releases page](https://github.com/jxhnch/MultiServerView/releases).
2. Install and start it. Nothing else has to be installed (.NET is bundled; WebView2 is already part of Windows 10/11 in most cases).
3. On first start: set a **master password** (at least 8 characters, there is no recovery!).

## What is checked automatically on Windows (every build)
Around 40 checks on a real Windows machine, among them: window and interface start, create/lock/unlock the vault,
change the master password, Windows encryption (DPAPI), saving settings, **RDP control in a tab** (including quick closing),
**SSH terminal** against a real OpenSSH server (command, window size, host key, OS detection), **SFTP** with folders and
Windows paths, **jump host**, **port forwarding**, terminal and SFTP in the real interface, closing without a hanging process.

## What only you can check (5 minutes)
- [ ] **Your real servers**: create a first SSH host (try password **and** key), connect, type commands, resize the window.
- [ ] **RDP to your Windows PC/server**: save the password, connect, resize the window, test the clipboard.
- [ ] **VNC** (if you have one).
- [ ] **Sync**: choose a sync folder (or set up Supabase, see README), then use "Load vault" on a second PC.
- [ ] **Import**: your mRemoteNG file (`confCons.xml`), PuTTY sessions, a Proxmox server or VMware Workstation VMs.
- [ ] **Feel**: does hosts → connect → switch feel smooth? What annoys you?

## If something does not work
Take a screenshot and note **what you clicked** and **what happened instead**. For connection errors the cause is usually shown in the terminal or in the red notice.
