import { Component, ElementRef, OnDestroy, ViewChild } from '@angular/core';
import { ClipboardService } from '../../services/clipboard.service';
import { WalletService } from '../../services/wallet.service';

type WalletDialogView = 'options' | 'neon';

@Component({
  selector: 'app-wallet-connect',
  templateUrl: './wallet-connect.component.html',
  styleUrl: './wallet-connect.component.scss'
})
export class WalletConnectComponent implements OnDestroy {
  @ViewChild('walletTrigger') private walletTrigger?: ElementRef<HTMLButtonElement>;
  @ViewChild('walletDialog') private walletDialog?: ElementRef<HTMLElement>;

  isDialogOpen = false;
  dialogView: WalletDialogView = 'options';
  copiedMessage = '';
  dialogAnnouncement = '';
  private copiedMessageTimeoutId: number | null = null;
  private bodyOverflow = '';

  constructor(
    private readonly clipboard: ClipboardService,
    readonly wallet: WalletService
  ) {}

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
    await this.wallet.disconnect();
    this.dialogAnnouncement = 'Wallet disconnected.';
    this.closeDialog();
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
      this.dialogAnnouncement = `${label} connected.`;
      this.closeDialog();
    }
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
