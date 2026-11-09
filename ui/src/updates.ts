import { api, type UpdateStatus } from './bridge';
import { askConfirm } from './Confirm';

/** Shows "new version available" and, if the user agrees, installs it (installed app) or opens the download page (portable). */
export async function offerUpdate(u: UpdateStatus, toast: (msg: string, kind?: 'ok' | 'error') => void): Promise<void> {
  const notes = u.notes.trim().split('\n').slice(0, 8).join('\n');
  if (u.canInstall) {
    const yes = await askConfirm(`Version ${u.version} is available (you have ${u.current}).${notes ? '\n\n' + notes : ''}\n\nInstall it now? MultiServerView closes and starts again by itself.`, {
      title: 'Update available',
      confirmLabel: 'Install and restart',
    });
    if (!yes) return;
    toast('Downloading the update…');
    try {
      await api.updateInstall();
    } catch (e) {
      toast((e as Error).message, 'error');
    }
    return;
  }
  const yes = await askConfirm(`Version ${u.version} is available (you have ${u.current}).${notes ? '\n\n' + notes : ''}\n\nThis copy cannot update itself (portable version). Open the download page?`, {
    title: 'Update available',
    confirmLabel: 'Open download page',
  });
  if (yes && u.pageUrl) window.open(u.pageUrl, '_blank');
}
