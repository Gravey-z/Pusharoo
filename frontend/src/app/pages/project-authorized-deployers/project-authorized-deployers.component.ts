import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { AuthorizedDeployerAction, Project, ProjectAuthorizedDeployer, WalletActionSignature } from '../../models/pusharoo.models';
import { ApiErrorFormatterService } from '../../services/api-error-formatter.service';
import { ProjectOwnershipService } from '../../services/project-ownership.service';
import { PusharooApiService } from '../../services/pusharoo-api.service';
import { ProjectDeploymentAccessService } from '../../services/project-deployment-access.service';
import { ProjectWorkspaceContextService } from '../../services/project-workspace-context.service';
import { WalletService } from '../../services/wallet.service';

@Component({
  selector: 'app-project-authorized-deployers',
  imports: [FormsModule],
  templateUrl: './project-authorized-deployers.component.html',
  styleUrl: './project-authorized-deployers.component.scss'
})
export class ProjectAuthorizedDeployersComponent implements OnInit {
  projectId = '';
  authorizedDeployers: ProjectAuthorizedDeployer[] = [];
  isLoading = true;
  loadError = '';
  actionError = '';
  actionSuccess = '';
  isMutating = false;
  deploymentAccessRequired = false;

  newWalletAddress = '';
  newTestnet = true;
  newMainnet = false;
  editingAuthorizedDeployer: ProjectAuthorizedDeployer | null = null;
  editTestnet = false;
  editMainnet = false;
  removingAuthorizedDeployer: ProjectAuthorizedDeployer | null = null;
  removalConfirmation = '';

  constructor(
    private readonly route: ActivatedRoute,
    private readonly api: PusharooApiService,
    private readonly errors: ApiErrorFormatterService,
    private readonly ownership: ProjectOwnershipService,
    private readonly workspace: ProjectWorkspaceContextService,
    private readonly deploymentAccess: ProjectDeploymentAccessService,
    readonly wallet: WalletService
  ) {}

  ngOnInit(): void {
    (this.route.parent ?? this.route).paramMap.subscribe((params) => {
      this.projectId = params.get('projectId') ?? '';
      void this.loadAuthorizedDeployers();
    });
    this.route.queryParamMap.subscribe((params) => {
      this.deploymentAccessRequired = params.get('deploymentAccess') === 'required';
    });
  }

  get project(): Project | null {
    return this.workspace.overview?.project ?? null;
  }

  get isOwner(): boolean {
    return this.ownership.canManage(this.project, this.wallet.account()?.address ?? '');
  }

  get selectedNewNetworks(): string[] {
    return this.networksFromSelection(this.newTestnet, this.newMainnet);
  }

  get selectedEditNetworks(): string[] {
    return this.networksFromSelection(this.editTestnet, this.editMainnet);
  }

  get canAddDeployer(): boolean {
    return this.isOwner && !this.isMutating && this.isN3Address(this.newWalletAddress)
      && this.selectedNewNetworks.length > 0;
  }

  get canSaveEdit(): boolean {
    return this.isOwner && Boolean(this.editingAuthorizedDeployer) && !this.isMutating
      && this.selectedEditNetworks.length > 0;
  }

  get canRemoveAuthorizedDeployer(): boolean {
    return this.isOwner && Boolean(this.removingAuthorizedDeployer) && !this.isMutating
      && this.removalConfirmation.trim() === this.removingAuthorizedDeployer?.walletAddress;
  }

  get effectiveAccessMessage(): string {
    const walletAddress = this.wallet.account()?.address?.trim();
    if (!walletAddress) {
      return 'Connect a wallet to see this wallet’s Pusharoo deployment access.';
    }
    if (this.isOwner) {
      return 'Connected wallet owns this project and can deploy on both N3 networks.';
    }

    const authorizedDeployer = this.authorizedDeployers.find((item) => item.walletAddress === walletAddress);
    if (!authorizedDeployer) {
      return 'Connected wallet cannot deploy this project.';
    }

    return this.deploymentAccess.description({
      isOwner: false,
      isAuthorizedDeployer: true,
      allowedNetworks: authorizedDeployer.allowedNetworks
    });
  }

  retryLoad(): void {
    void this.loadAuthorizedDeployers();
  }

  startEdit(authorizedDeployer: ProjectAuthorizedDeployer): void {
    this.clearActionMessages();
    this.removingAuthorizedDeployer = null;
    this.removalConfirmation = '';
    this.editingAuthorizedDeployer = authorizedDeployer;
    this.editTestnet = authorizedDeployer.allowedNetworks.includes('neo3:testnet');
    this.editMainnet = authorizedDeployer.allowedNetworks.includes('neo3:mainnet');
  }

  cancelEdit(): void {
    this.editingAuthorizedDeployer = null;
  }

  openRemove(authorizedDeployer: ProjectAuthorizedDeployer): void {
    this.clearActionMessages();
    this.editingAuthorizedDeployer = null;
    this.removingAuthorizedDeployer = authorizedDeployer;
    this.removalConfirmation = '';
  }

  cancelRemove(): void {
    this.removingAuthorizedDeployer = null;
    this.removalConfirmation = '';
  }

  async addDeployer(): Promise<void> {
    this.clearActionMessages();
    const walletAddress = this.newWalletAddress.trim();
    const allowedNetworks = this.selectedNewNetworks;
    if (!this.isN3Address(walletAddress)) {
      this.actionError = 'Enter a valid N3 wallet address. Pusharoo will validate it again before saving.';
      return;
    }
    if (!allowedNetworks.length) {
      this.actionError = 'Select TestNet, MainNet, or both.';
      return;
    }

    this.isMutating = true;
    try {
      const signature = await this.requestAuthorizedDeployerAuthorization(
        'authorized-deployers.add', walletAddress, allowedNetworks
      );
      await firstValueFrom(this.api.addAuthorizedDeployer(this.projectId, {
        walletAddress,
        allowedNetworks,
        signature
      }));
      this.newWalletAddress = '';
      this.newTestnet = true;
      this.newMainnet = false;
      this.actionSuccess = 'Authorized deployer was added.';
      await this.loadAuthorizedDeployers();
    } catch (error) {
      await this.showMutationError(error, 'Could not add this authorized deployer.');
    } finally {
      this.isMutating = false;
    }
  }

  async saveEdit(): Promise<void> {
    const authorizedDeployer = this.editingAuthorizedDeployer;
    const allowedNetworks = this.selectedEditNetworks;
    this.clearActionMessages();
    if (!authorizedDeployer || !allowedNetworks.length) {
      this.actionError = 'Select at least one deployment network.';
      return;
    }

    this.isMutating = true;
    try {
      const signature = await this.requestAuthorizedDeployerAuthorization(
        'authorized-deployers.update', authorizedDeployer.walletAddress, allowedNetworks
      );
      await firstValueFrom(this.api.updateAuthorizedDeployer(this.projectId, authorizedDeployer.walletAddress, {
        allowedNetworks,
        signature
      }));
      this.editingAuthorizedDeployer = null;
      this.actionSuccess = 'Authorized deployer networks were updated.';
      await this.loadAuthorizedDeployers();
    } catch (error) {
      await this.showMutationError(error, 'Could not update this authorized deployer.');
    } finally {
      this.isMutating = false;
    }
  }

  async removeAuthorizedDeployer(): Promise<void> {
    const authorizedDeployer = this.removingAuthorizedDeployer;
    this.clearActionMessages();
    if (!authorizedDeployer || this.removalConfirmation.trim() !== authorizedDeployer.walletAddress) {
      this.actionError = 'Type the exact wallet address to confirm removal.';
      return;
    }

    this.isMutating = true;
    try {
      const signature = await this.requestAuthorizedDeployerAuthorization(
        'authorized-deployers.remove', authorizedDeployer.walletAddress, authorizedDeployer.allowedNetworks
      );
      await firstValueFrom(this.api.removeAuthorizedDeployer(this.projectId, authorizedDeployer.walletAddress, { signature }));
      this.removingAuthorizedDeployer = null;
      this.removalConfirmation = '';
      this.actionSuccess = 'Authorized deployer was removed.';
      await this.loadAuthorizedDeployers();
    } catch (error) {
      await this.showMutationError(error, 'Could not remove this authorized deployer.');
    } finally {
      this.isMutating = false;
    }
  }

  shortAddress(address: string): string {
    return address.length > 13 ? `${address.slice(0, 7)}…${address.slice(-5)}` : address;
  }

  networkLabel(network: string): string {
    return network === 'neo3:mainnet' ? 'N3:MainNet' : 'N3:TestNet';
  }

  trackAuthorizedDeployer(_: number, authorizedDeployer: ProjectAuthorizedDeployer): string {
    return authorizedDeployer.walletAddress;
  }

  private async loadAuthorizedDeployers(): Promise<void> {
    this.isLoading = true;
    this.loadError = '';
    try {
      this.authorizedDeployers = await firstValueFrom(this.api.getAuthorizedDeployers(this.projectId));
      if (this.editingAuthorizedDeployer) {
        this.editingAuthorizedDeployer = this.authorizedDeployers.find(
          (item) => item.walletAddress === this.editingAuthorizedDeployer?.walletAddress
        ) ?? null;
      }
      if (this.removingAuthorizedDeployer) {
        this.removingAuthorizedDeployer = this.authorizedDeployers.find(
          (item) => item.walletAddress === this.removingAuthorizedDeployer?.walletAddress
        ) ?? null;
      }
    } catch (error) {
      this.loadError = this.errors.format(error, 'Could not load authorized deployers.');
    } finally {
      this.isLoading = false;
    }
  }

  private async showMutationError(error: unknown, fallback: string): Promise<void> {
    if (error instanceof HttpErrorResponse && error.status === 409) {
      await this.loadAuthorizedDeployers();
      this.actionError = 'The authorized-deployer list changed before the request was saved. Review the latest list and try again.';
      return;
    }
    this.actionError = this.errors.format(error, fallback);
  }

  private requestAuthorizedDeployerAuthorization(
    action: AuthorizedDeployerAction,
    walletAddress: string,
    allowedNetworks: string[]
  ): Promise<WalletActionSignature> {
    return this.wallet.signAuthorizedDeployerAuthorization(
      this.projectId,
      action,
      walletAddress,
      allowedNetworks
    );
  }

  private clearActionMessages(): void {
    this.actionError = '';
    this.actionSuccess = '';
  }

  private networksFromSelection(testnet: boolean, mainnet: boolean): string[] {
    return [
      ...(testnet ? ['neo3:testnet'] : []),
      ...(mainnet ? ['neo3:mainnet'] : [])
    ];
  }

  private isN3Address(value: string): boolean {
    return /^N[1-9A-HJ-NP-Za-km-z]{33}$/.test(value.trim());
  }
}
