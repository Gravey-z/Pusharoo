import { Injectable } from '@angular/core';
import { ProjectOverviewViewModel } from '../models/pusharoo.models';

export const PROJECT_DATA_CACHE_TTL_MS = 5 * 60_000;

@Injectable()
export class ProjectWorkspaceContextService {
  private cachedOverview: ProjectOverviewViewModel | null = null;
  private cachedAt = 0;

  get overview(): ProjectOverviewViewModel | null {
    return this.cachedOverview;
  }

  set overview(value: ProjectOverviewViewModel | null) {
    this.cachedOverview = value;
    this.cachedAt = value ? Date.now() : 0;
  }

  getFreshOverview(projectId: string): ProjectOverviewViewModel | null {
    return this.cachedOverview?.project.id === projectId
      && Date.now() - this.cachedAt < PROJECT_DATA_CACHE_TTL_MS
      ? this.cachedOverview
      : null;
  }
}
