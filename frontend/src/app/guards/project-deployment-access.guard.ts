import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { catchError, forkJoin, map, of } from 'rxjs';
import { ProjectDeploymentAccessService } from '../services/project-deployment-access.service';
import { PusharooApiService } from '../services/pusharoo-api.service';
import { WalletService } from '../services/wallet.service';

export const projectDeploymentAccessGuard: CanActivateFn = route => {
  const projectId =
    route.pathFromRoot.map(part => part.paramMap.get('projectId')).find(Boolean) ?? '';
  const wallet = inject(WalletService);
  const walletAddress = wallet.account()?.address;
  const walletNetwork = wallet.session()?.network;

  if (!projectId || !walletAddress || !walletNetwork) {
    return true;
  }

  const api = inject(PusharooApiService);
  const access = inject(ProjectDeploymentAccessService);
  const router = inject(Router);

  return forkJoin({
    overview: api.getProjectSummary(projectId),
    authorizedDeployers: api.getAuthorizedDeployers(projectId)
  }).pipe(
    map(
      ({ overview, authorizedDeployers }) =>
        access.canStartDeployment(
          access.resolve(overview.project, authorizedDeployers, walletAddress),
          walletNetwork
        ) ||
        router.createUrlTree(['/projects', projectId, 'authorized-deployers'], {
          queryParams: { deploymentAccess: 'required' }
        })
    ),

    catchError(() => of(true))
  );
};
