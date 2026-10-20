import { Injectable } from '@angular/core';

@Injectable({ providedIn: 'root' })
export class DeploymentAttemptCapabilityService {
  private readonly prefix = 'pusharoo.deployment-attempt-capability.';

  get(deploymentId: string): string | null {
    try {
      return sessionStorage.getItem(`${this.prefix}${deploymentId}`);
    } catch {
      return null;
    }
  }

  set(deploymentId: string, capability: string): void {
    try {
      sessionStorage.setItem(`${this.prefix}${deploymentId}`, capability);
    } catch {}
  }

  remove(deploymentId: string): void {
    try {
      sessionStorage.removeItem(`${this.prefix}${deploymentId}`);
    } catch {}
    try {
      localStorage.removeItem(`${this.prefix}${deploymentId}.transaction`);
    } catch {}
  }

  getTransactionId(deploymentId: string): string | null {
    try {
      return localStorage.getItem(`${this.prefix}${deploymentId}.transaction`);
    } catch {
      return null;
    }
  }

  setTransactionId(deploymentId: string, transactionId: string): void {
    try {
      localStorage.setItem(`${this.prefix}${deploymentId}.transaction`, transactionId);
    } catch {}
  }
}
