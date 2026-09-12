import { Injectable } from '@angular/core';
import { DeploymentCapabilities, Project, ProjectCollaborator } from '../models/pusharoo.models';
import { ProjectOwnershipService } from './project-ownership.service';

export interface ProjectDeploymentAccess {
  isOwner: boolean;
  isCollaborator: boolean;
  allowedNetworks: string[];
}

@Injectable({ providedIn: 'root' })
export class ProjectDeploymentAccessService {
  readonly supportedNetworks = ['neo3:testnet', 'neo3:mainnet'];
  static readonly unavailableCapabilities: DeploymentCapabilities = {
    collaboratorDeploymentsEnabled: false,
    collaboratorDeploymentUnavailableReason: 'Collaborator deployment is not available yet. Pusharoo must verify a prepared transaction before it can safely enable collaborator broadcasts.',
    unboundRecoveryEnabled: false,
    unboundRecoveryUnavailableReason: 'Unbound transaction recovery is not available yet. Resume an authorized submitted attempt instead.'
  };

  constructor(private readonly ownership: ProjectOwnershipService) {}

  resolve(
    project: Project | null | undefined,
    collaborators: ProjectCollaborator[],
    walletAddress: string | null | undefined
  ): ProjectDeploymentAccess {
    const normalizedWallet = walletAddress?.trim() ?? '';
    const isOwner = this.ownership.canManage(project, normalizedWallet);
    if (isOwner) {
      return { isOwner: true, isCollaborator: false, allowedNetworks: [...this.supportedNetworks] };
    }

    const collaborator = collaborators.find((item) => item.walletAddress === normalizedWallet);
    return {
      isOwner: false,
      isCollaborator: Boolean(collaborator),
      allowedNetworks: collaborator?.allowedNetworks.filter((network) => this.supportedNetworks.includes(network)) ?? []
    };
  }

  canDeployToNetwork(access: ProjectDeploymentAccess, network: string | null | undefined): boolean {
    return Boolean(network) && access.allowedNetworks.includes(network!);
  }

  canStartDeployment(
    access: ProjectDeploymentAccess,
    network: string | null | undefined,
    capabilities: DeploymentCapabilities
  ): boolean {
    return this.canDeployToNetwork(access, network)
      && (access.isOwner || capabilities.collaboratorDeploymentsEnabled);
  }

  description(access: ProjectDeploymentAccess, capabilities?: DeploymentCapabilities): string {
    if (access.isOwner) {
      return 'This is the owner wallet. It can deploy and update on N3:TestNet and N3:MainNet.';
    }
    if (access.allowedNetworks.length) {
      const grant = `This wallet has a Pusharoo deployer grant for ${access.allowedNetworks.map((network) => this.networkLabel(network)).join(' and ')}.`;
      return capabilities?.collaboratorDeploymentsEnabled
        ? `${grant} Collaborator deployment is available on those networks.`
        : `${grant} ${capabilities?.collaboratorDeploymentUnavailableReason ?? ProjectDeploymentAccessService.unavailableCapabilities.collaboratorDeploymentUnavailableReason}`;
    }
    return 'This wallet has no Pusharoo deployment access for this project.';
  }

  networkLabel(network: string): string {
    return network === 'neo3:mainnet' ? 'N3:MainNet' : 'N3:TestNet';
  }
}
