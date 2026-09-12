import { Injectable } from '@angular/core';
import { Project, ProjectCollaborator } from '../models/pusharoo.models';
import { ProjectOwnershipService } from './project-ownership.service';

export interface ProjectDeploymentAccess {
  isOwner: boolean;
  isCollaborator: boolean;
  allowedNetworks: string[];
}

@Injectable({ providedIn: 'root' })
export class ProjectDeploymentAccessService {
  readonly supportedNetworks = ['neo3:testnet', 'neo3:mainnet'];

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

  description(access: ProjectDeploymentAccess): string {
    if (access.isOwner) {
      return 'This is the owner wallet. It can deploy and update on N3:TestNet and N3:MainNet.';
    }
    if (access.allowedNetworks.length) {
      return `This wallet has Pusharoo deployer access for ${access.allowedNetworks.map((network) => this.networkLabel(network)).join(' and ')}.`;
    }
    return 'This wallet has no Pusharoo deployment access for this project.';
  }

  networkLabel(network: string): string {
    return network === 'neo3:mainnet' ? 'N3:MainNet' : 'N3:TestNet';
  }
}
