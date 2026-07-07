import { Injectable } from '@angular/core';
import type { ConnectedAccount, WalletSession } from 'neo-n3-walletkit';

export interface ProjectCreationSignatureChallenge {
  origin: string;
  issuedAtUtc: string;
  nonce: string;
  message: string;
}

@Injectable({ providedIn: 'root' })
export class ProjectCreationSignatureMessageService {
  async create(
    projectName: string,
    projectDescription: string,
    account: ConnectedAccount,
    session: WalletSession
  ): Promise<ProjectCreationSignatureChallenge> {
    const origin = window.location.origin;
    const issuedAtUtc = new Date().toISOString();
    const nonce = this.createNonce();
    const descriptionHash = await this.sha256Hex(projectDescription.trim());
    const message = [
      'Pusharoo project creation',
      `Project: ${projectName.trim()}`,
      `Description SHA-256: ${descriptionHash}`,
      `Wallet: ${account.address}`,
      `Script hash: ${account.scriptHash}`,
      `Network: ${session.network}`,
      `Origin: ${origin}`,
      `Issued at UTC: ${issuedAtUtc}`,
      `Nonce: ${nonce}`
    ].join('\n');

    return {
      origin,
      issuedAtUtc,
      nonce,
      message
    };
  }

  private createNonce(): string {
    const bytes = new Uint8Array(16);
    crypto.getRandomValues(bytes);

    return this.bytesToHex(bytes);
  }

  private async sha256Hex(value: string): Promise<string> {
    const bytes = new TextEncoder().encode(value);
    const hash = await crypto.subtle.digest('SHA-256', bytes);

    return this.bytesToHex(new Uint8Array(hash));
  }

  private bytesToHex(bytes: Uint8Array): string {
    return [...bytes]
      .map((value) => value.toString(16).padStart(2, '0'))
      .join('');
  }
}
