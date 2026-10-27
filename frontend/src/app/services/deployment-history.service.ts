import { Injectable } from '@angular/core';
import { Artifact, Deployment, ProjectOverviewViewModel } from '../models/pusharoo.models';

@Injectable({ providedIn: 'root' })
export class DeploymentHistoryService {
  private confirmed(deployments: Deployment[]): Deployment[] {
    return deployments.filter(
      deployment =>
        Boolean(deployment.contractHash) &&
        (!deployment.status || deployment.status === 'confirmed')
    );
  }

  latestByNetwork(deployments: Deployment[]): Deployment[] {
    return [
      ...deployments
        .reduce((latestByNetwork, deployment) => {
          const current = latestByNetwork.get(
            `${deployment.projectId}:${deployment.contractId ?? 'legacy'}:${deployment.network}`
          );
          const deploymentTime = new Date(deployment.createdAt).getTime();
          const currentTime = current ? new Date(current.createdAt).getTime() : 0;

          if (!current || deploymentTime > currentTime) {
            latestByNetwork.set(
              `${deployment.projectId}:${deployment.contractId ?? 'legacy'}:${deployment.network}`,
              deployment
            );
          }

          return latestByNetwork;
        }, new Map<string, Deployment>())
        .values()
    ];
  }

  latestConfirmedByNetwork(deployments: Deployment[]): Deployment[] {
    return this.latestByNetwork(this.confirmed(deployments));
  }

  latestForNetwork(
    deployments: Deployment[],
    network: string,
    contractId: string | null
  ): Deployment | null {
    if (!network) {
      return null;
    }

    return (
      this.latestConfirmedByNetwork(deployments).find(
        deployment =>
          deployment.network === network && (deployment.contractId ?? null) === contractId
      ) ?? null
    );
  }

  forNetwork(deployments: Deployment[], network: string, contractId: string | null): Deployment[] {
    return deployments.filter(
      deployment => deployment.network === network && (deployment.contractId ?? null) === contractId
    );
  }

  latestForArtifact(overview: ProjectOverviewViewModel, artifact: Artifact): Deployment[] {
    return this.latestConfirmedByNetwork(overview.deployments).filter(
      deployment => deployment.artifactId === artifact.id
    );
  }

  networksForLatestArtifact(overview: ProjectOverviewViewModel, artifact: Artifact): string[] {
    return [
      ...new Set(this.latestForArtifact(overview, artifact).map(deployment => deployment.network))
    ];
  }
}
