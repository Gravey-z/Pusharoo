import { Component, ElementRef, OnDestroy, ViewChild, effect } from '@angular/core';
import { DatePipe } from '@angular/common';
import { ClipboardService } from '../../services/clipboard.service';
import { WalletService } from '../../services/wallet.service';
import { WalletAuthService } from '../../services/wallet-auth.service';

type WalletDialogView = 'options' | 'neon';

@Component({
  selector: 'app-wallet-connect',
  imports: [DatePipe],
  templateUrl: './wallet-connect.component.html',
  styleUrl: './wallet-connect.component.scss'
})
export class WalletConnectComponent implements OnDestroy {
  @ViewChild('walletTrigger') private walletTrigger?: ElementRef<HTMLButtonElement>;
  @ViewChild('walletDialog') private walletDialog?: ElementRef<HTMLElement>;
  @ViewChild('loginSection') private loginSection?: ElementRef<HTMLElement>;

  isDialogOpen = false;
  dialogView: WalletDialogView = 'options';
  copiedMessage = '';
  dialogAnnouncement = '';
  private copiedMessageTimeoutId: number | null = null;
  private bodyOverflow = '';

  constructor(
    private readonly clipboard: ClipboardService,
    readonly wallet: WalletService,
    readonly auth: WalletAuthService
  ) {
    effect(() => {
      if (this.wallet.account() && this.wallet.session()?.provider === 'walletconnect' && this.isDialogOpen) {
        this.dialogAnnouncement = 'Neon Wallet connected. Pusharoo login is available.';
        this.focusLoginSection();
      }
    });
  }

  openDialog(): void {
    this.isDialogOpen = true;
    this.dialogView = 'options';
    this.dialogAnnouncement = '';
    this.clearCopiedMessage();
    this.lockBackground();
    requestAnimationFrame(() => this.focusFirstDialogElement());
  }

  closeDialog(): void {
    this.isDialogOpen = false;
    this.dialogView = 'options';
    this.clearCopiedMessage();
    this.unlockBackground();
    requestAnimationFrame(() => this.walletTrigger?.nativeElement.focus());
  }

  async connectNeoLine(): Promise<void> {
    await this.connectExtensionWallet('neoline', 'NeoLine');
  }

  async connectOneGate(): Promise<void> {
    await this.connectExtensionWallet('onegate', 'OneGate');
  }

  connectNeon(): void {
    this.dialogView = 'neon';
    this.dialogAnnouncement = 'Preparing Neon Wallet connection.';
    requestAnimationFrame(() => this.walletDialog?.nativeElement.focus());
    void this.wallet.connect('walletconnect').then(() => {
      if (this.wallet.walletConnectUri()) {
        this.dialogAnnouncement = 'WalletConnect URI is ready. Scan the QR code or open Neon Wallet.';
      }
    });
  }

  async disconnect(): Promise<void> {
    const revoke = this.auth.logout();
    await this.wallet.disconnect();
    await revoke.catch(() => {});
    this.dialogAnnouncement = 'Wallet disconnected.';
    this.closeDialog();
  }

  async signIn(): Promise<void> {
    try {
      await this.auth.ensureAuthenticated();
      this.dialogAnnouncement = 'Signed in to Pusharoo.';
    } catch {
      this.dialogAnnouncement = this.auth.errorMessage() || 'Could not sign in to Pusharoo.';
    }
  }

  async signOut(): Promise<void> {
    try {
      await this.auth.logout();
      this.dialogAnnouncement = 'Signed out of Pusharoo. The wallet remains connected.';
    } catch {
      this.dialogAnnouncement = this.auth.errorMessage();
    }
  }

  copyWalletConnectUri(): void {
    const uri = this.wallet.walletConnectUri();
    if (!uri) {
      return;
    }

    void this.clipboard.copy(uri).then(() => {
      this.copiedMessage = 'Copied';
      this.dialogAnnouncement = 'WalletConnect URI copied.';
      this.resetCopiedMessageTimer();
    });
  }

  onDialogKeydown(event: KeyboardEvent): void {
    if (event.key === 'Escape') {
      event.preventDefault();
      this.closeDialog();
      return;
    }

    if (event.key !== 'Tab') {
      return;
    }

    const focusable = this.focusableDialogElements();
    if (!focusable.length) {
      event.preventDefault();
      this.walletDialog?.nativeElement.focus();
      return;
    }

    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    const activeElement = document.activeElement as HTMLElement | null;

    if (event.shiftKey && (activeElement === first || !this.walletDialog?.nativeElement.contains(activeElement))) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && activeElement === last) {
      event.preventDefault();
      first.focus();
    }
  }

  ngOnDestroy(): void {
    this.clearCopiedMessage();
    this.unlockBackground();
  }

  private async connectExtensionWallet(provider: 'neoline' | 'onegate', label: string): Promise<void> {
    this.dialogAnnouncement = `Connecting to ${label}.`;
    await this.wallet.connect(provider);

    if (this.wallet.account()) {
      this.dialogAnnouncement = `${label} connected. Pusharoo login is available.`;
      this.focusLoginSection();
    }
  }

  private focusLoginSection(): void {
    requestAnimationFrame(() => {
      if (this.isDialogOpen) this.loginSection?.nativeElement.focus();
    });
  }

  private focusFirstDialogElement(): void {
    this.focusableDialogElements()[0]?.focus() ?? this.walletDialog?.nativeElement.focus();
  }

  private focusableDialogElements(): HTMLElement[] {
    const dialog = this.walletDialog?.nativeElement;
    if (!dialog) {
      return [];
    }

    return Array.from(dialog.querySelectorAll<HTMLElement>(
      'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])'
    )).filter((element) => element.offsetParent !== null);
  }

  private lockBackground(): void {
    this.bodyOverflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
  }

  private unlockBackground(): void {
    document.body.style.overflow = this.bodyOverflow;
  }

  private resetCopiedMessageTimer(): void {
    if (this.copiedMessageTimeoutId !== null) {
      window.clearTimeout(this.copiedMessageTimeoutId);
    }

    this.copiedMessageTimeoutId = window.setTimeout(() => {
      this.copiedMessage = '';
      this.copiedMessageTimeoutId = null;
    }, 1600);
  }

  private clearCopiedMessage(): void {
    this.copiedMessage = '';

    if (this.copiedMessageTimeoutId !== null) {
      window.clearTimeout(this.copiedMessageTimeoutId);
      this.copiedMessageTimeoutId = null;
    }
  }
}
