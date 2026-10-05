import { Injectable } from '@angular/core';
import { defaultWalletConfig } from '../config/wallet.config';

export interface RuntimeConfig {
  apiBaseUrl: string;
  walletSignatureAudience: string;
  eventRelayHealthUrl: string;
  eventRelays?: Record<string, { healthUrl: string }>;
  faucet: {
    testnetContractHash: string;
  };
  wallet: {
    network: string;
    walletConnectProjectId: string;
    contractManagement: Record<string, string>;
    rpc: Record<string, string>;
  };
}

const defaultConfig: RuntimeConfig = {
  apiBaseUrl: '/api',
  walletSignatureAudience: 'pusharoo-web',
  eventRelayHealthUrl: 'http://localhost:5001/health',
  eventRelays: {
    'neo3:testnet': { healthUrl: 'http://localhost:5001/health' },
    'neo3:mainnet': { healthUrl: 'http://localhost:5002/health' }
  },
  faucet: { testnetContractHash: '' },
  wallet: {
    network: defaultWalletConfig.network,
    walletConnectProjectId: defaultWalletConfig.walletConnectProjectId,
    contractManagement: { ...defaultWalletConfig.contractManagement },
    rpc: { ...defaultWalletConfig.rpc }
  }
};

@Injectable({ providedIn: 'root' })
export class RuntimeConfigService {
  private config: RuntimeConfig = defaultConfig;

  get value(): RuntimeConfig {
    return this.config;
  }

  async load(): Promise<void> {
    try {
      const response = await fetch('/runtime-config.json', { cache: 'no-store' });
      if (!response.ok) {
        return;
      }

      const loaded = await response.json() as Partial<RuntimeConfig>;
      this.config = {
        ...defaultConfig,
        ...loaded,
        faucet: {
          ...defaultConfig.faucet,
          ...loaded.faucet
        },
        wallet: {
          ...defaultConfig.wallet,
          ...loaded.wallet,
          contractManagement: {
            ...defaultConfig.wallet.contractManagement,
            ...loaded.wallet?.contractManagement
          },
          rpc: {
            ...defaultConfig.wallet.rpc,
            ...loaded.wallet?.rpc
          }
        }
      };
    } catch {
      this.config = defaultConfig;
    }
  }
}
