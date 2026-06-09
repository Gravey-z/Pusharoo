import { computed, Injectable, signal } from '@angular/core';
import { WalletKit } from 'neo-n3-walletkit';
import { toDataURL } from 'qrcode';
import type {
  ConnectedAccount,
  ContractArgs,
  NetworkType,
  WalletProvider,
  WalletSession
} from 'neo-n3-walletkit';
import { walletConfig } from '../config/wallet.config';

type WalletStatus = 'idle' | 'connecting' | 'connected' | 'error';
type ConnectableWalletProvider = Extract<WalletProvider, 'neoline' | 'onegate' | 'walletconnect'>;

@Injectable({ providedIn: 'root' })
export class WalletService {
  private walletKit: WalletKit | null = null;
  private unsubscribeSession: (() => void) | null = null;

  readonly account = signal<ConnectedAccount | null>(null);
  readonly session = signal<WalletSession | null>(null);
  readonly status = signal<WalletStatus>('idle');
  readonly errorMessage = signal('');
  readonly selectedProvider = signal<ConnectableWalletProvider | null>(null);
  readonly walletConnectUri = signal('');
  readonly walletConnectQrCode = signal('');
  readonly neonConnectUrl = computed(() => {
    const uri = this.walletConnectUri();
    if (!uri) {
      return '';
    }

    const neonUrl = new URL('https://neon.coz.io/connect');
    neonUrl.searchParams.set('uri', uri);

    return neonUrl.toString();
  });
  readonly isBusy = computed(() => this.status() === 'connecting');
  readonly shortAddress = computed(() => {
    const address = this.account()?.address;

    return address ? `${address.slice(0, 6)}...${address.slice(-4)}` : '';
  });

  async connect(provider: ConnectableWalletProvider): Promise<void> {
    this.status.set('connecting');
    this.errorMessage.set('');
    this.selectedProvider.set(provider);
    this.walletConnectUri.set('');
    this.walletConnectQrCode.set('');

    try {
      const walletKit = provider === 'neoline'
        ? await WalletKit.initNeoLine({ network: walletConfig.network })
        : provider === 'onegate'
          ? await WalletKit.initOneGate({ network: walletConfig.network })
          : await this.startWalletConnectApproval();

      const session = provider === 'walletconnect'
        ? null
        : await walletKit.connect();
      this.setWalletKit(walletKit);

      if (session) {
        this.setSession(session);
      }
    } catch (error) {
      this.status.set('error');
      this.account.set(null);
      this.session.set(null);
      this.errorMessage.set(this.getErrorMessage(error, provider));
    }
  }

  async disconnect(): Promise<void> {
    this.errorMessage.set('');

    try {
      await this.walletKit?.disconnect();
    } finally {
      this.unsubscribeSession?.();
      this.unsubscribeSession = null;
      this.walletKit = null;
      this.account.set(null);
      this.session.set(null);
      this.walletConnectUri.set('');
      this.walletConnectQrCode.set('');
      this.status.set('idle');
      this.selectedProvider.set(null);
    }
  }

  async deployContract(
    network: NetworkType,
    nefHex: string,
    manifestJson: string,
    contractName: string
  ): Promise<string> {
    const session = this.session();
    const walletKit = this.walletKit;

    if (!walletKit || !session) {
      throw new Error('Connect a wallet before deploying.');
    }

    if (session.network !== network) {
      throw new Error(`Connected wallet is on ${session.network}. Select ${session.network} or reconnect on ${network}.`);
    }

    const contractManagementHash = walletConfig.contractManagement[network];
    const contractManagement = walletKit.contract(contractManagementHash);
    const nefValue = session.provider === 'onegate'
      ? nefHex
      : this.hexToBase64(nefHex);
    const args: ContractArgs = [
      { type: 'ByteArray', value: nefValue },
      { type: 'String', value: manifestJson },
      { type: 'Any', value: null }
    ];

    return await contractManagement.invoke(
      'deploy',
      args,
      { context: `Deploy ${contractName} with Pusharoo` }
    );
  }

  private hexToBase64(hex: string): string {
    const cleanHex = hex.trim().replace(/^0x/i, '');
    const bytes: string[] = [];

    for (let index = 0; index < cleanHex.length; index += 2) {
      bytes.push(String.fromCharCode(Number.parseInt(cleanHex.slice(index, index + 2), 16)));
    }

    return btoa(bytes.join(''));
  }

  private setWalletKit(walletKit: WalletKit): void {
    this.unsubscribeSession?.();
    this.walletKit = walletKit;
    this.unsubscribeSession = walletKit.onSessionChange((session) => {
      this.session.set(session);
      this.account.set(walletKit.account);
      this.status.set(session ? 'connected' : 'idle');
    });
  }

  private setSession(session: WalletSession): void {
    this.session.set(session);
    this.account.set(this.walletKit?.account ?? null);
    this.status.set('connected');
  }

  private getErrorMessage(error: unknown, provider: ConnectableWalletProvider): string {
    if (error instanceof Error && error.message) {
      return error.message;
    }

    if (provider === 'neoline') {
      return 'Could not connect NeoLine. Make sure the extension is installed, unlocked, and on Neo N3 testnet.';
    }

    if (provider === 'onegate') {
      return 'Could not connect OneGate. Open Pusharoo inside a OneGate browser with Neo N3 testnet selected.';
    }

    return 'Could not connect Neon Wallet. Make sure WalletConnect is configured and Neon is on Neo N3 testnet.';
  }

  private getWalletConnectProjectId(): string {
    const projectId = walletConfig.walletConnectProjectId.trim();

    if (!projectId) {
      throw new Error('Add a WalletConnect project ID in frontend/src/app/config/wallet.config.ts to enable Neon Wallet.');
    }

    return projectId;
  }

  private async startWalletConnectApproval(): Promise<WalletKit> {
    const walletKit = await WalletKit.init({
      projectId: this.getWalletConnectProjectId(),
      relayUrl: 'wss://relay.walletconnect.com',
      metadata: {
        name: 'Pusharoo',
        description: 'Neo smart contract artifact workspace',
        url: window.location.origin,
        icons: [`${window.location.origin}/pusharoo-logo.png`]
      },
      network: walletConfig.network
    });
    const { uri, approval } = await walletKit.createConnection();

    if (!uri) {
      throw new Error('Neon Wallet did not return a WalletConnect URI.');
    }

    this.walletConnectUri.set(uri);
    this.walletConnectQrCode.set(await toDataURL(uri, {
      errorCorrectionLevel: 'M',
      margin: 1,
      width: 220
    }));
    void this.waitForWalletConnectApproval(walletKit, approval);

    return walletKit;
  }

  private async waitForWalletConnectApproval(
    walletKit: WalletKit,
    approval: () => Promise<WalletSession>
  ): Promise<void> {
    try {
      const session = await approval();
      this.walletConnectUri.set('');
      this.walletConnectQrCode.set('');
      this.setWalletKit(walletKit);
      this.setSession(session);
    } catch (error) {
      this.status.set('error');
      this.errorMessage.set(this.getErrorMessage(error, 'walletconnect'));
    }
  }
}
