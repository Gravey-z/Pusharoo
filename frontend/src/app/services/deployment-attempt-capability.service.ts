import { Injectable } from '@angular/core';

@Injectable({ providedIn: 'root' })
export class DeploymentAttemptCapabilityService {
  private readonly prefix = 'pusharoo.deployment-attempt-capability.';

  get(deploymentId: string): string | null {
    return sessionStorage.getItem(`${this.prefix}${deploymentId}`);
  }

  set(deploymentId: string, capability: string): void {
    sessionStorage.setItem(`${this.prefix}${deploymentId}`, capability);
  }

  remove(deploymentId: string): void {
    sessionStorage.removeItem(`${this.prefix}${deploymentId}`);
  }
}
