import { Injectable } from '@angular/core';
import { Project, ProjectAuthorizedDeployer } from '../models/pusharoo.models';
import { ProjectOwnershipService } from './project-ownership.service';

export interface ProjectDeploymentAccess {
  isOwner: boolean;
  isAuthorizedDeployer: boolean;
  allowedNetworks: string[];
}

@Injectable({ providedIn: 'root' })
export class ProjectDeploymentAccessService {
  readonly supportedNetworks = ['neo3:testnet', 'neo3:mainnet'];
  constructor(private readonly ownership: ProjectOwnershipService) {}

  resolve(
    project: Project | null | undefined,
    authorizedDeployers: ProjectAuthorizedDeployer[],
    walletAddress: string | null | undefined
  ): ProjectDeploymentAccess {
    const normalizedWallet = walletAddress?.trim() ?? '';
    const isOwner = this.ownership.canManage(project, normalizedWallet);
    if (isOwner) {
      return { isOwner: true, isAuthorizedDeployer: false, allowedNetworks: [...this.supportedNetworks] };
    }

    const authorizedDeployer = authorizedDeployers.find((item) => item.walletAddress === normalizedWallet);
    return {
      isOwner: false,
      isAuthorizedDeployer: Boolean(authorizedDeployer),
      allowedNetworks: authorizedDeployer?.allowedNetworks.filter((network) => this.supportedNetworks.includes(network)) ?? []
    };
  }

  canDeployToNetwork(access: ProjectDeploymentAccess, network: string | null | undefined): boolean {
    return Boolean(network) && access.allowedNetworks.includes(network!);
  }

  canStartDeployment(
    access: ProjectDeploymentAccess,
    network: string | null | undefined
  ): boolean {
    return this.canDeployToNetwork(access, network);
  }

  description(access: ProjectDeploymentAccess): string {
    if (access.isOwner) {
      return 'Connected wallet is the project owner. It can deploy on N3:TestNet and N3:MainNet.';
    }
    if (access.allowedNetworks.length) {
      return `Connected wallet can deploy on ${access.allowedNetworks.map((network) => this.networkLabel(network)).join(' and ')}.`;
    }
    return 'Deployment access is managed by the project owner.';
  }

  networkLabel(network: string): string {
    return network === 'neo3:mainnet' ? 'N3:MainNet' : 'N3:TestNet';
  }
}
