import { Component } from '@angular/core';
import { ClipboardService } from '../../services/clipboard.service';
import { WalletService } from '../../services/wallet.service';

type WalletDialogView = 'options' | 'neon';

@Component({
  selector: 'app-wallet-connect',
  templateUrl: './wallet-connect.component.html',
  styleUrl: './wallet-connect.component.scss'
})
export class WalletConnectComponent {
  isDialogOpen = false;
  dialogView: WalletDialogView = 'options';
  copiedMessage = '';
  private copiedMessageTimeoutId: number | null = null;

  constructor(
    private readonly clipboard: ClipboardService,
    readonly wallet: WalletService
  ) {}

  openDialog(): void {
    this.isDialogOpen = true;
    this.dialogView = 'options';
    this.clearCopiedMessage();
  }

  closeDialog(): void {
    this.isDialogOpen = false;
    this.dialogView = 'options';
    this.clearCopiedMessage();
  }

  connectNeoLine(): void {
    void this.wallet.connect('neoline');
    this.closeDialog();
  }

  connectOneGate(): void {
    void this.wallet.connect('onegate');
    this.closeDialog();
  }

  connectNeon(): void {
    this.dialogView = 'neon';
    void this.wallet.connect('walletconnect');
  }

  disconnect(): void {
    void this.wallet.disconnect();
    this.closeDialog();
  }

  copyWalletConnectUri(): void {
    const uri = this.wallet.walletConnectUri();
    if (!uri) {
      return;
    }

    void this.clipboard.copy(uri).then(() => {
      this.copiedMessage = 'Copied';
      this.resetCopiedMessageTimer();
    });
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
