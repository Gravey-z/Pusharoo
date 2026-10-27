import { ActivatedRoute } from '@angular/router';
import { combineLatest, distinctUntilChanged, map } from 'rxjs';

export function workspaceRoute(route: ActivatedRoute) {
  return combineLatest(route.pathFromRoot.map(part => part.paramMap)).pipe(
    map(parts => ({
      projectId: parts.map(part => part.get('projectId')).find(Boolean) ?? '',
      contractId: parts.map(part => part.get('contractId')).find(Boolean) ?? null
    })),
    distinctUntilChanged(
      (left, right) => left.projectId === right.projectId && left.contractId === right.contractId
    )
  );
}

export function contractLink(
  projectId: string,
  contractId: string | null,
  ...segments: string[]
): string[] {
  return contractId
    ? ['/projects', projectId, 'contracts', contractId, ...segments]
    : ['/projects', projectId, ...segments];
}
