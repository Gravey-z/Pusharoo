import { Injectable } from '@angular/core';
import {
  Artifact,
  Deployment,
  ProjectContract,
  ProjectOverviewViewModel,
  WebhookSubscription
} from '../models/pusharoo.models';
import { DeploymentHistoryService } from './deployment-history.service';

export interface WebhookDeploymentOption {
  key: string;
  contractId: string | null;
  label: string;
  contractHash: string;
  network: string;
  artifact: Artifact | null;
}

@Injectable({ providedIn: 'root' })
export class WebhookContractTargetsService {
  constructor(private readonly history: DeploymentHistoryService) {}

  association(
    subscription: Pick<WebhookSubscription, 'contractHash' | 'network'>,
    deployments: Deployment[],
    contracts: ProjectContract[]
  ): ProjectContract | null {
    const matches = deployments.filter(
      item =>
        this.confirmed(item) &&
        item.network === subscription.network &&
        this.sameHash(item.contractHash, subscription.contractHash)
    );
    const parents = new Set(matches.map(item => item.contractId ?? null));
    if (parents.size !== 1 || parents.has(null)) return null;
    return (
      contracts.find(
        contract =>
          contract.id === matches[0].contractId && contract.projectId === matches[0].projectId
      ) ?? null
    );
  }

  options(
    overview: ProjectOverviewViewModel,
    contracts: ProjectContract[]
  ): WebhookDeploymentOption[] {
    return this.history.latestConfirmedByNetwork(overview.deployments).map(deployment => {
      const contract = contracts.find(
        item => item.id === deployment.contractId && item.projectId === deployment.projectId
      );
      const parents = new Set(
        overview.deployments
          .filter(
            item =>
              this.confirmed(item) &&
              item.network === deployment.network &&
              this.sameHash(item.contractHash, deployment.contractHash)
          )
          .map(item => item.contractId ?? null)
      );
      const artifact =
        parents.size === 1
          ? (overview.artifacts.find(
              item =>
                item.id === deployment.artifactId &&
                item.projectId === deployment.projectId &&
                (item.contractId ?? null) === (deployment.contractId ?? null)
            ) ?? null)
          : null;
      return {
        key: deployment.id,
        contractId: contract?.id ?? null,
        label: `${contract?.name ?? 'Unlinked legacy contract'} - ${deployment.network} - ${deployment.version || 'Recovered deployment'}`,
        contractHash: deployment.contractHash ?? '',
        network: deployment.network,
        artifact
      };
    });
  }

  sameHash(left: string | null | undefined, right: string | null | undefined): boolean {
    return (
      Boolean(left && right) &&
      left!.replace(/^0x/i, '').toLowerCase() === right!.replace(/^0x/i, '').toLowerCase()
    );
  }

  private confirmed(deployment: Deployment): boolean {
    return (
      Boolean(deployment.contractHash) && (!deployment.status || deployment.status === 'confirmed')
    );
  }
}
