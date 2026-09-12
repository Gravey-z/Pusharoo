import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { DeploymentCapabilities, Project, ProjectAccessAuditEvent, ProjectCollaborator } from '../../models/pusharoo.models';
import { ApiErrorFormatterService } from '../../services/api-error-formatter.service';
import { ProjectOwnershipService } from '../../services/project-ownership.service';
import { PusharooApiService } from '../../services/pusharoo-api.service';
import { ProjectDeploymentAccessService } from '../../services/project-deployment-access.service';
import { ProjectWorkspaceContextService } from '../../services/project-workspace-context.service';
import { WalletService } from '../../services/wallet.service';

@Component({
  selector: 'app-project-collaboration',
  imports: [FormsModule],
  templateUrl: './project-collaboration.component.html',
  styleUrl: './project-collaboration.component.scss'
})
export class ProjectCollaborationComponent implements OnInit {
  projectId = '';
  collaborators: ProjectCollaborator[] = [];
  auditEvents: ProjectAccessAuditEvent[] = [];
  isLoading = true;
  loadError = '';
  auditLoadError = '';
  capabilityLoadError = '';
  actionError = '';
  actionSuccess = '';
  isMutating = false;

  newWalletAddress = '';
  newTestnet = true;
  newMainnet = false;

  editingCollaborator: ProjectCollaborator | null = null;
  editTestnet = false;
  editMainnet = false;

  removingCollaborator: ProjectCollaborator | null = null;
  removalConfirmation = '';
  deploymentCapabilities: DeploymentCapabilities = ProjectDeploymentAccessService.unavailableCapabilities;

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
    this.route.paramMap.subscribe((params) => {
      this.projectId = params.get('projectId') ?? '';
      void this.loadCollaboration();
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
    return this.isOwner && Boolean(this.editingCollaborator) && !this.isMutating
      && this.selectedEditNetworks.length > 0;
  }

  get canRemoveCollaborator(): boolean {
    return this.isOwner && Boolean(this.removingCollaborator) && !this.isMutating
      && this.removalConfirmation.trim() === this.removingCollaborator?.walletAddress;
  }

  get effectiveAccessMessage(): string {
    const walletAddress = this.wallet.account()?.address?.trim();
    if (!walletAddress) {
      return 'Connect a wallet to see this wallet’s Pusharoo deployment access.';
    }

    if (this.isOwner) {
      return 'This is the owner wallet. It can manage access and deploy on both N3 networks.';
    }

    const collaborator = this.collaborators.find((item) => item.walletAddress === walletAddress);
    if (!collaborator) {
      return 'This wallet has no Pusharoo deployment access for this project.';
    }

    return this.deploymentAccess.description(
      { isOwner: false, isCollaborator: true, allowedNetworks: collaborator.allowedNetworks },
      this.deploymentCapabilities
    );
  }

  retryLoad(): void {
    void this.loadCollaboration();
  }

  startEdit(collaborator: ProjectCollaborator): void {
    this.clearActionMessages();
    this.removingCollaborator = null;
    this.removalConfirmation = '';
    this.editingCollaborator = collaborator;
    this.editTestnet = collaborator.allowedNetworks.includes('neo3:testnet');
    this.editMainnet = collaborator.allowedNetworks.includes('neo3:mainnet');
  }

  cancelEdit(): void {
    this.editingCollaborator = null;
  }

  openRemove(collaborator: ProjectCollaborator): void {
    this.clearActionMessages();
    this.editingCollaborator = null;
    this.removingCollaborator = collaborator;
    this.removalConfirmation = '';
  }

  cancelRemove(): void {
    this.removingCollaborator = null;
    this.removalConfirmation = '';
  }

  async addDeployer(): Promise<void> {
    this.clearActionMessages();
    const walletAddress = this.newWalletAddress.trim();
    const networks = this.selectedNewNetworks;

    if (!this.isN3Address(walletAddress)) {
      this.actionError = 'Enter a valid N3 wallet address. Pusharoo will validate it again before saving.';
      return;
    }
    if (!networks.length) {
      this.actionError = 'Select TestNet, MainNet, or both.';
      return;
    }

    this.isMutating = true;
    try {
      const signature = await this.wallet.signCollaboratorAuthorization(
        this.projectId, 'collaborators.add', walletAddress, networks, 0
      );
      await firstValueFrom(this.api.addCollaborator(this.projectId, {
        walletAddress,
        role: 'deployer',
        allowedNetworks: networks,
        expectedGrantRevision: 0,
        signature
      }));
      this.newWalletAddress = '';
      this.newTestnet = true;
      this.newMainnet = false;
      this.actionSuccess = 'Deployer access was added.';
      await this.loadCollaboration();
    } catch (error) {
      await this.showMutationError(error, 'Could not add this deployer.');
    } finally {
      this.isMutating = false;
    }
  }

  async saveEdit(): Promise<void> {
    const collaborator = this.editingCollaborator;
    const networks = this.selectedEditNetworks;
    this.clearActionMessages();

    if (!collaborator || !networks.length) {
      this.actionError = 'Select at least one deployment network.';
      return;
    }

    this.isMutating = true;
    try {
      const signature = await this.wallet.signCollaboratorAuthorization(
        this.projectId, 'collaborators.update', collaborator.walletAddress, networks, collaborator.grantRevision
      );
      await firstValueFrom(this.api.updateCollaborator(this.projectId, collaborator.walletAddress, {
        role: 'deployer',
        allowedNetworks: networks,
        expectedGrantRevision: collaborator.grantRevision,
        signature
      }));
      this.editingCollaborator = null;
      this.actionSuccess = 'Deployer access was updated.';
      await this.loadCollaboration();
    } catch (error) {
      await this.showMutationError(error, 'Could not update this deployer.');
    } finally {
      this.isMutating = false;
    }
  }

  async removeCollaborator(): Promise<void> {
    const collaborator = this.removingCollaborator;
    this.clearActionMessages();

    if (!collaborator || this.removalConfirmation.trim() !== collaborator.walletAddress) {
      this.actionError = 'Type the exact wallet address to confirm removal.';
      return;
    }

    this.isMutating = true;
    try {
      const signature = await this.wallet.signCollaboratorAuthorization(
        this.projectId,
        'collaborators.remove',
        collaborator.walletAddress,
        collaborator.allowedNetworks,
        collaborator.grantRevision
      );
      await firstValueFrom(this.api.removeCollaborator(this.projectId, collaborator.walletAddress, {
        expectedGrantRevision: collaborator.grantRevision,
        signature
      }));
      this.removingCollaborator = null;
      this.removalConfirmation = '';
      this.actionSuccess = 'Deployer access was removed. The access audit history is retained.';
      await this.loadCollaboration();
    } catch (error) {
      await this.showMutationError(error, 'Could not remove this deployer.');
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

  formatDate(value: string): string {
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? value : date.toLocaleString();
  }

  trackCollaborator(_: number, collaborator: ProjectCollaborator): string {
    return collaborator.walletAddress;
  }

  trackAuditEvent(index: number, event: ProjectAccessAuditEvent): string {
    return `${event.createdAtUtc}-${event.targetWalletAddress}-${event.action}-${index}`;
  }

  private async loadCollaboration(): Promise<void> {
    this.isLoading = true;
    this.loadError = '';
    this.auditLoadError = '';
    this.capabilityLoadError = '';
    try {
      const [collaborators, auditEvents, capabilities] = await Promise.allSettled([
        firstValueFrom(this.api.getCollaborators(this.projectId)),
        firstValueFrom(this.api.getProjectAccessAudit(this.projectId)),
        firstValueFrom(this.api.getDeploymentCapabilities())
      ]);
      if (collaborators.status !== 'fulfilled') {
        throw collaborators.reason;
      }
      this.collaborators = collaborators.value;
      if (auditEvents.status === 'fulfilled') {
        this.auditEvents = auditEvents.value;
      } else {
        this.auditEvents = [];
        this.auditLoadError = 'Could not load the access audit. Current deployer grants are still available.';
      }
      if (capabilities.status === 'fulfilled') {
        this.deploymentCapabilities = capabilities.value;
      } else {
        this.deploymentCapabilities = ProjectDeploymentAccessService.unavailableCapabilities;
        this.capabilityLoadError = 'Deployment availability could not be checked. Collaborator releases remain unavailable.';
      }
      if (this.editingCollaborator) {
        this.editingCollaborator = this.collaborators.find(
          (collaborator) => collaborator.walletAddress === this.editingCollaborator?.walletAddress
        ) ?? null;
      }
      if (this.removingCollaborator) {
        this.removingCollaborator = this.collaborators.find(
          (collaborator) => collaborator.walletAddress === this.removingCollaborator?.walletAddress
        ) ?? null;
      }
    } catch (error) {
      this.loadError = this.errors.format(error, 'Could not load collaboration access.');
    } finally {
      this.isLoading = false;
    }
  }

  private async showMutationError(error: unknown, fallback: string): Promise<void> {
    if (error instanceof HttpErrorResponse && error.status === 409) {
      await this.loadCollaboration();
      this.actionError = 'Collaboration access changed before your signed request was saved. The latest access has been reloaded; review your edits and sign again.';
      return;
    }

    this.actionError = this.errors.format(error, fallback);
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
