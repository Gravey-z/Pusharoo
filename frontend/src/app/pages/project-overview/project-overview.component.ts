import { Component, OnInit } from '@angular/core';
import { AsyncPipe } from '@angular/common';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Observable, map, switchMap } from 'rxjs';
import { Artifact, Deployment, ProjectOverviewViewModel } from '../../models/pusharoo.models';
import { PusharooApiService } from '../../services/pusharoo-api.service';
import { PageShellComponent } from '../page-shell/page-shell.component';

@Component({
  selector: 'app-project-overview',
  imports: [AsyncPipe, PageShellComponent, RouterLink],
  templateUrl: './project-overview.component.html',
  styleUrl: './project-overview.component.scss'
})
export class ProjectOverviewComponent implements OnInit {
  overview$!: Observable<ProjectOverviewViewModel | null>;
  copiedValue = '';

  constructor(
    private readonly route: ActivatedRoute,
    private readonly api: PusharooApiService
  ) {}

  ngOnInit(): void {
    this.overview$ = this.route.paramMap.pipe(
      map((params) => params.get('projectId') ?? ''),
      switchMap((projectId) => this.api.getProjectOverview(projectId))
    );
  }

  artifactDeployments(overview: ProjectOverviewViewModel, artifact: Artifact): Deployment[] {
    return this.latestDeploymentsByNetwork(overview)
      .filter((deployment) => deployment.artifactId === artifact.id);
  }

  artifactNetworks(overview: ProjectOverviewViewModel, artifact: Artifact): string[] {
    return [...new Set(this.artifactDeployments(overview, artifact).map((deployment) => deployment.network))];
  }

  isArtifactDeployed(overview: ProjectOverviewViewModel, artifact: Artifact): boolean {
    return this.artifactDeployments(overview, artifact).length > 0;
  }

  shortText(value: string | null | undefined, leading = 3, trailing = 4): string {
    if (!value) {
      return '-';
    }

    if (value.length <= leading + trailing + 3) {
      return value;
    }

    return `${value.slice(0, leading)}...${value.slice(-trailing)}`;
  }

  shortTransactionId(value: string | null | undefined): string {
    return value ? this.shortText(value, 10, 4) : 'No txid';
  }

  async copyValue(value: string | null | undefined, event: Event): Promise<void> {
    event.preventDefault();
    event.stopPropagation();

    if (!value) {
      return;
    }

    await navigator.clipboard.writeText(value);
    this.copiedValue = value;
    window.setTimeout(() => {
      if (this.copiedValue === value) {
        this.copiedValue = '';
      }
    }, 1400);
  }

  private latestDeploymentsByNetwork(overview: ProjectOverviewViewModel): Deployment[] {
    return [...overview.deployments.reduce((latestByNetwork, deployment) => {
      const current = latestByNetwork.get(deployment.network);
      const deploymentTime = new Date(deployment.createdAt).getTime();
      const currentTime = current ? new Date(current.createdAt).getTime() : 0;

      if (!current || deploymentTime > currentTime) {
        latestByNetwork.set(deployment.network, deployment);
      }

      return latestByNetwork;
    }, new Map<string, Deployment>()).values()];
  }
}
