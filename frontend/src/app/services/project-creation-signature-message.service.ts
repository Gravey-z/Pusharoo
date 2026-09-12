import { Injectable } from '@angular/core';
import type { ConnectedAccount, WalletSession } from 'neo-n3-walletkit';
import { RuntimeConfigService } from './runtime-config.service';

export interface ProjectCreationSignatureChallenge {
  origin: string;
  audience: string;
  issuedAtUtc: string;
  nonce: string;
  message: string;
}

export type WalletActionSignatureChallenge = ProjectCreationSignatureChallenge;

@Injectable({ providedIn: 'root' })
export class ProjectCreationSignatureMessageService {
  constructor(private readonly runtimeConfig: RuntimeConfigService) {}

  async create(
    projectName: string,
    projectDescription: string,
    account: ConnectedAccount,
    session: WalletSession
  ): Promise<ProjectCreationSignatureChallenge> {
    const origin = window.location.origin;
    const audience = this.runtimeConfig.value.walletSignatureAudience;
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
      `Audience: ${audience}`,
      `Issued at UTC: ${issuedAtUtc}`,
      `Nonce: ${nonce}`
    ].join('\n');

    return {
      origin,
      audience,
      issuedAtUtc,
      nonce,
      message
    };
  }

  async createArtifactUpload(
    projectId: string,
    version: string,
    notes: string,
    nefFile: File,
    manifestFile: File,
    account: ConnectedAccount,
    session: WalletSession
  ): Promise<WalletActionSignatureChallenge> {
    const origin = window.location.origin;
    const audience = this.runtimeConfig.value.walletSignatureAudience;
    const issuedAtUtc = new Date().toISOString();
    const nonce = this.createNonce();
    const nefHash = await this.sha256File(nefFile);
    const manifestHash = await this.sha256Hex(await manifestFile.text());
    const message = [
      'Pusharoo artifact upload',
      `Project ID: ${projectId.trim()}`,
      `Version: ${version.trim()}`,
      `Notes SHA-256: ${await this.sha256Hex(notes.trim())}`,
      `NEF SHA-256: ${nefHash}`,
      `Manifest SHA-256: ${manifestHash}`,
      `Wallet: ${account.address}`,
      `Script hash: ${account.scriptHash}`,
      `Network: ${session.network}`,
      `Origin: ${origin}`,
      `Audience: ${audience}`,
      `Issued at UTC: ${issuedAtUtc}`,
      `Nonce: ${nonce}`
    ].join('\n');

    return {
      origin,
      audience,
      issuedAtUtc,
      nonce,
      message
    };
  }

  createWebhookAdministration(
    projectId: string,
    operation: string,
    requestHash: string,
    account: ConnectedAccount,
    session: WalletSession
  ): WalletActionSignatureChallenge {
    const origin = window.location.origin;
    const audience = this.runtimeConfig.value.walletSignatureAudience;
    const issuedAtUtc = new Date().toISOString();
    const nonce = this.createNonce();
    const message = [
      'Pusharoo webhook administration',
      `Project ID: ${projectId.trim()}`,
      `Operation: ${operation.trim()}`,
      `Request SHA-256: ${requestHash.trim().toLowerCase()}`,
      `Wallet: ${account.address}`,
      `Script hash: ${account.scriptHash}`,
      `Network: ${session.network}`,
      `Origin: ${origin}`,
      `Audience: ${audience}`,
      `Issued at UTC: ${issuedAtUtc}`,
      `Nonce: ${nonce}`
    ].join('\n');

    return {
      origin,
      audience,
      issuedAtUtc,
      nonce,
      message
    };
  }

  createProjectDeletion(
    projectId: string,
    projectName: string,
    account: ConnectedAccount,
    session: WalletSession
  ): WalletActionSignatureChallenge {
    const origin = window.location.origin;
    const audience = this.runtimeConfig.value.walletSignatureAudience;
    const issuedAtUtc = new Date().toISOString();
    const nonce = this.createNonce();
    const message = [
      'Pusharoo project deletion',
      `Project ID: ${projectId.trim()}`,
      `Project: ${projectName.trim()}`,
      'Confirmation: DELETE',
      `Wallet: ${account.address}`,
      `Script hash: ${account.scriptHash}`,
      `Network: ${session.network}`,
      `Origin: ${origin}`,
      `Audience: ${audience}`,
      `Issued at UTC: ${issuedAtUtc}`,
      `Nonce: ${nonce}`
    ].join('\n');

    return { origin, audience, issuedAtUtc, nonce, message };
  }

  createCollaboratorAuthorization(
    projectId: string,
    action: 'collaborators.add' | 'collaborators.update' | 'collaborators.remove',
    targetWalletAddress: string,
    allowedNetworks: string[],
    expectedGrantRevision: number,
    account: ConnectedAccount,
    session: WalletSession
  ): WalletActionSignatureChallenge {
    const origin = window.location.origin;
    const audience = this.runtimeConfig.value.walletSignatureAudience;
    const issuedAtUtc = new Date().toISOString();
    const nonce = this.createNonce();
    const networks = [...new Set(allowedNetworks.map((network) => network.trim()))].sort().join(',');
    const message = [
      'Pusharoo collaborator authorization',
      'Schema: pusharoo.collaborator.v1',
      `Action: ${action}`,
      `Project ID: ${projectId.trim()}`,
      `Target wallet: ${targetWalletAddress.trim()}`,
      'Role: deployer',
      `Allowed networks: ${networks}`,
      `Expected grant revision: ${expectedGrantRevision}`,
      `Audience: ${audience}`,
      `Origin: ${origin}`,
      `Issued at UTC: ${issuedAtUtc}`,
      `Nonce: ${nonce}`
    ].join('\n');

    return { origin, audience, issuedAtUtc, nonce, message };
  }

  private createNonce(): string {
    const bytes = new Uint8Array(16);
    crypto.getRandomValues(bytes);

    return this.bytesToHex(bytes);
  }

  private async sha256Hex(value: string): Promise<string> {
    const bytes = new TextEncoder().encode(value);

    return this.sha256Bytes(bytes);
  }

  private async sha256File(file: File): Promise<string> {
    return this.sha256Bytes(new Uint8Array(await file.arrayBuffer()));
  }

  private async sha256Bytes(bytes: Uint8Array): Promise<string> {
    const input = new ArrayBuffer(bytes.byteLength);
    new Uint8Array(input).set(bytes);
    const hash = await crypto.subtle.digest('SHA-256', input);

    return this.bytesToHex(new Uint8Array(hash));
  }

  private bytesToHex(bytes: Uint8Array): string {
    return [...bytes]
      .map((value) => value.toString(16).padStart(2, '0'))
      .join('');
  }
}
