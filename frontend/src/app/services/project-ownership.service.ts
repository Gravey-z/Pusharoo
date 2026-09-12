import { Injectable } from '@angular/core';
import { Project } from '../models/pusharoo.models';

@Injectable({ providedIn: 'root' })
export class ProjectOwnershipService {
  canManage(project: Project | null | undefined, walletAddress: string): boolean {
    const creatorAddress = project?.createdByWalletAddress?.trim();

    return Boolean(creatorAddress) && creatorAddress === walletAddress.trim();
  }

  managementError(project: Project | null | undefined, walletAddress: string): string {
    if (!walletAddress.trim()) {
      return 'Connect the project creator wallet before continuing.';
    }

    if (!project?.createdByWalletAddress?.trim()) {
      return 'This legacy project has no verified creator. Recover ownership before managing it.';
    }

    return this.canManage(project, walletAddress)
      ? ''
      : 'Only the project creator can manage versions and deployments.';
  }
}
