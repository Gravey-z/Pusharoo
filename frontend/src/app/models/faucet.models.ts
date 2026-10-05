export interface FaucetStatus {
  available: boolean;
  reason: string | null;
  sponsoredAvailable: boolean;
  sponsoredReason: string | null;
  claimAmount: string | null;
  dailyCap: string | null;
  paused: string | null;
  balance: string | null;
  remainingDailyAllowance: string | null;
  registered: boolean | null;
  sponsoredEligible: boolean | null;
  directEligible: boolean | null;
  nextClaimAt: string | null;
  sponsoredIneligibleReason: string | null;
  directIneligibleReason: string | null;
  dailyResetAt: string | null;
}

export interface FaucetClaimRequest {
  requestId: string;
}

export interface FaucetClaim {
  requestId: string;
  state: string;
  transactionHash: string | null;
  explorerUrl: string | null;
  error: string | null;
}

export interface PendingFaucetClaim {
  accountAddress: string;
  scriptHash: string;
  network: 'neo3:testnet';
  route: 'sponsored' | 'direct';
  requestId?: string;
  transactionHash?: string;
  submittedAt: string;
}
