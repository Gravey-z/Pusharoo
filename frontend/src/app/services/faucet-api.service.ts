import { HttpClient, HttpContext } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { FaucetClaim, FaucetClaimRequest, FaucetStatus } from '../models/faucet.models';
import { RuntimeConfigService } from './runtime-config.service';
import { REQUIRE_WALLET_SESSION } from './wallet-auth.interceptor';

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

  submitClaim(request: FaucetClaimRequest): Promise<FaucetClaim> {
    return firstValueFrom(this.http.post<FaucetClaim>(`${this.apiBaseUrl}/faucet/claims`, request,
      { context: new HttpContext().set(REQUIRE_WALLET_SESSION, true) }));
  }

  getClaim(requestId: string): Promise<FaucetClaim> {
    return firstValueFrom(this.http.get<FaucetClaim>(`${this.apiBaseUrl}/faucet/claims/${encodeURIComponent(requestId)}`));
  }
}
