import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { map } from 'rxjs';
import { PusharooApiService } from '../services/pusharoo-api.service';
import { contractLink } from '../services/workspace-route';

export const legacyContractRedirectGuard: CanActivateFn = route => {
  const projectId =
    route.pathFromRoot.map(part => part.paramMap.get('projectId')).find(Boolean) ?? '';
  const destination = route.data['contractDestination'] as string;
  const api = inject(PusharooApiService);
  const router = inject(Router);
  return api.getProjectSummary(projectId).pipe(
    map(summary => {
      const queryParams = { ...route.queryParams };
      delete queryParams['contractDestination'];
      const requested = route.queryParamMap.get('contractId');
      const explicit = summary.contracts.find(item => item.contract.id === requested);
      const contract = requested
        ? (explicit?.contract ?? null)
        : summary.contracts.length === 1
          ? summary.contracts[0].contract
          : null;
      if (contract) {
        delete queryParams['contractId'];
        return router.createUrlTree(
          contractLink(projectId, contract.id, ...destination.split('/')),
          { queryParams }
        );
      }
      return router.createUrlTree(['/projects', projectId], {
        queryParams: { ...queryParams, contractDestination: destination }
      });
    })
  );
};
