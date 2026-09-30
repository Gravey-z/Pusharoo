import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { FaucetChallenge, FaucetClaim, FaucetClaimRequest, FaucetStatus } from '../models/faucet.models';
import { RuntimeConfigService } from './runtime-config.service';

@Injectable({ providedIn: 'root' })
export class FaucetApiService {
  private get apiBaseUrl(): string {
    return this.runtimeConfig.value.apiBaseUrl.replace(/\/$/, '');
  }

  constructor(private readonly http: HttpClient, private readonly runtimeConfig: RuntimeConfigService) {}

  getStatus(address?: string): Promise<FaucetStatus> {
    const query = address ? `?address=${encodeURIComponent(address)}` : '';
    return firstValueFrom(this.http.get<FaucetStatus>(`${this.apiBaseUrl}/faucet/status${query}`));
  }

  createChallenge(address: string): Promise<FaucetChallenge> {
    return firstValueFrom(this.http.post<FaucetChallenge>(`${this.apiBaseUrl}/faucet/challenges`, { address }));
  }

  submitClaim(request: FaucetClaimRequest): Promise<FaucetClaim> {
    return firstValueFrom(this.http.post<FaucetClaim>(`${this.apiBaseUrl}/faucet/claims`, request));
  }

  getClaim(requestId: string): Promise<FaucetClaim> {
    return firstValueFrom(this.http.get<FaucetClaim>(`${this.apiBaseUrl}/faucet/claims/${encodeURIComponent(requestId)}`));
  }
}
