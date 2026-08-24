import { Injectable } from '@angular/core';
import { ProjectOverviewViewModel } from '../models/pusharoo.models';

@Injectable()
export class ProjectWorkspaceContextService {
  overview: ProjectOverviewViewModel | null = null;
}
