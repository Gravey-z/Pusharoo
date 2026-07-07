import { Injectable } from '@angular/core';
import { Project } from '../models/pusharoo.models';

@Injectable({ providedIn: 'root' })
export class ProjectOwnershipService {
  canManage(project: Project | null | undefined, walletAddress: string): boolean {
    const creatorAddress = project?.createdByWalletAddress?.trim();

    return !creatorAddress || creatorAddress === walletAddress.trim();
  }

  managementError(project: Project | null | undefined, walletAddress: string): string {
    if (!walletAddress.trim()) {
      return 'Connect the project creator wallet before continuing.';
    }

    return this.canManage(project, walletAddress)
      ? ''
      : 'Only the project creator can manage versions and deployments.';
  }
}
