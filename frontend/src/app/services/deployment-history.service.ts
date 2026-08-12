import { Injectable } from '@angular/core';
import { Artifact, Deployment, ProjectOverviewViewModel } from '../models/pusharoo.models';

@Injectable({ providedIn: 'root' })
export class DeploymentHistoryService {
  private confirmed(deployments: Deployment[]): Deployment[] {
    return deployments.filter((deployment) =>
      Boolean(deployment.contractHash)
      && (!deployment.status || deployment.status === 'confirmed')
    );
  }

  latestByNetwork(deployments: Deployment[]): Deployment[] {
    return [...deployments.reduce((latestByNetwork, deployment) => {
      const current = latestByNetwork.get(deployment.network);
      const deploymentTime = new Date(deployment.createdAt).getTime();
      const currentTime = current ? new Date(current.createdAt).getTime() : 0;

      if (!current || deploymentTime > currentTime) {
        latestByNetwork.set(deployment.network, deployment);
      }

      return latestByNetwork;
    }, new Map<string, Deployment>()).values()];
  }

  latestForNetwork(deployments: Deployment[], network: string): Deployment | null {
    if (!network) {
      return null;
    }

    return this.latestByNetwork(this.confirmed(deployments))
      .find((deployment) => deployment.network === network) ?? null;
  }

  forNetwork(deployments: Deployment[], network: string): Deployment[] {
    return deployments.filter((deployment) => deployment.network === network);
  }

  latestForArtifact(overview: ProjectOverviewViewModel, artifact: Artifact): Deployment[] {
    return this.latestByNetwork(this.confirmed(overview.deployments))
      .filter((deployment) => deployment.artifactId === artifact.id);
  }

  networksForLatestArtifact(overview: ProjectOverviewViewModel, artifact: Artifact): string[] {
    return [...new Set(this.latestForArtifact(overview, artifact).map((deployment) => deployment.network))];
  }
}
