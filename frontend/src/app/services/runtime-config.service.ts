import { Injectable } from '@angular/core';
import { defaultWalletConfig } from '../config/wallet.config';

export interface RuntimeConfig {
  apiBaseUrl: string;
  eventRelayBaseUrl: string;
  eventRelayHealthUrl: string;
  wallet: {
    network: string;
    walletConnectProjectId: string;
    contractManagement: Record<string, string>;
    rpc: Record<string, string>;
  };
}

const defaultConfig: RuntimeConfig = {
  apiBaseUrl: 'http://localhost:5000/api',
  eventRelayBaseUrl: 'http://localhost:5001/api',
  eventRelayHealthUrl: 'http://localhost:5001/health',
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
